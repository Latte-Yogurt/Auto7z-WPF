#!/usr/bin/env python3
# -*- coding: utf-8 -*-
r"""命令行参数转义与格式矩阵测试（2.5.1 二次审查 §三-1、§五 核心功能）。

审查报告 §三-1 的原话：
    「SevenZipService.Quote() 有专门处理反斜杠和引号的逻辑。这是 Windows
      命令行编程中很容易出现问题的地方，当前实现方向正确。建议补充参数
      边界测试：路径中有空格 / 中文 / 括号 / 多个引号转义 / 路径以反斜杠
      结尾 / 密码中包含特殊字符。特别要测试包含双引号的密码。」

这个脚本分两部分：

一、Quote() 转义表（纯函数，直接反射调用，逐条断言输出）
    Windows 命令行转义规则：反斜杠只在「紧跟引号」时才需要翻倍，
    「在结尾」时必须翻倍成对，否则会吃掉收尾引号。

二、端到端：真实文件 + 真实压缩解压
    §五 核心功能清单要求「ZIP / 7z / TAR 格式测试」与「中文密码、
    特殊字符密码测试」。这里对三种输出格式各跑一次压缩→解压→内容比对，
    并用特殊字符文件名验证命令行没有把路径切断。

不做的事：不替代 Windows 实机 DPI/键盘验收（那需要真显示器与真焦点）。

退出码：0 = 全部通过；1 = 有失败；2 = 环境问题。
"""
import argparse
import os
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

from projectpaths import project_root
from safe_ops import safe_rmtree

ROOT = project_root()


def find_exe(cfg="Release"):
    for c in (cfg, "Release", "Debug"):
        p = ROOT / "Auto7z_WPF" / "Auto7z_WPF" / "bin" / c / "Auto7z.exe"
        if p.is_file():
            return p
    sys.stderr.write("错误: 找不到 Auto7z.exe，请先构建\n")
    raise SystemExit(2)


EXE = find_exe()

results = []


def record(name, ok, detail=""):
    results.append((name, ok, detail))
    print("  %s %-46s %s" % ("[通过]" if ok else "[失败]", name, detail))


# ---------------------------------------------------------------------------
# 第一部分：Quote() 转义表
# ---------------------------------------------------------------------------
def load_quote():
    """用 PowerShell 反射拿到 private static Quote 方法。

    不引 pythonnet：项目测试一律不引入额外依赖，反射走 pwsh 最省事。
    """
    ps = r'''
$exe = "%s"
$asm = [System.Reflection.Assembly]::LoadFrom($exe)
$t = $asm.GetType("Auto7z_WPF.Services.SevenZipService")
$m = $t.GetMethod("Quote", [System.Reflection.BindingFlags]::NonPublic -bor [System.Reflection.BindingFlags]::Static)
$cases = $args | ForEach-Object { $_ }
$out = @()
foreach ($c in %s) {
    $r = $m.Invoke($null, [object[]]@([string]$c))
    $out += ($r -replace "`r","\r" -replace "`n","\n")
}
$out -join "`n"''' % (str(EXE).replace('"', '""'), '%CASES%')
    return ps


def run_quote_cases(cases):
    """批量调用 Quote()，返回结果列表（与 cases 等长）。"""
    # 通过一个临时 PowerShell 脚本传参，避免命令行长度与转义问题
    script = r'''
param([string]$ExePath, [string]$JsonPath)
$ErrorActionPreference = "Stop"
$asm = [System.Reflection.Assembly]::LoadFrom($ExePath)
$t = $asm.GetType("Auto7z_WPF.Services.SevenZipService")
$m = $t.GetMethod("Quote", [System.Reflection.BindingFlags]::NonPublic -bor [System.Reflection.BindingFlags]::Static)
$cases = Get-Content -LiteralPath $JsonPath -Raw -Encoding UTF8 | ConvertFrom-Json
$out = @()
foreach ($c in $cases) {
    $r = [string]$m.Invoke($null, [object[]]@([string]$c))
    $out += [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($r))
}
$out -join "`n"
'''
    import base64
    import json

    base = Path(tempfile.mkdtemp(prefix="Auto7zTest_quote_"))
    try:
        ps1 = base / "quote.ps1"
        ps1.write_text(script, encoding="utf-8")
        js = base / "cases.json"
        js.write_text(json.dumps(cases, ensure_ascii=False), encoding="utf-8")

        r = subprocess.run(
            ["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass",
             "-File", str(ps1), "-ExePath", str(EXE), "-JsonPath", str(js)],
            capture_output=True, text=True, encoding="utf-8", errors="replace")
        if r.returncode != 0:
            sys.stderr.write(r.stdout + "\n" + r.stderr + "\n")
            raise SystemExit(2)
        outs = []
        for line in r.stdout.strip().splitlines():
            line = line.strip()
            if not line:
                continue
            outs.append(base64.b64decode(line).decode("utf-8"))
        return outs
    finally:
        safe_rmtree(base)


def test_quote():
    """逐条核对转义结果。

    期望值按 Windows 命令行（CommandLineToArgvW）规则推导：
      - 整体用双引号包裹
      - 内部 " 变成 \"
      - 紧邻引号之前的反斜杠翻倍
      - 结尾的反斜杠全部翻倍（因为它们紧邻收尾引号）
    """
    cases = [
        # (输入, 期望输出, 说明)
        ("C:\\plain\\a.txt",
         '"C:\\plain\\a.txt"',
         "普通路径"),
        ("C:\\with space\\a b.txt",
         '"C:\\with space\\a b.txt"',
         "路径含空格"),
        ("C:\\中文目录\\文件.txt",
         '"C:\\中文目录\\文件.txt"',
         "路径含中文"),
        ("C:\\dir (x)\\a[1]{2}.txt",
         '"C:\\dir (x)\\a[1]{2}.txt"',
         "路径含括号与方括号"),
        ("C:\\trailing\\",
         '"C:\\trailing\\\\"',
         "路径以单个反斜杠结尾 -> 翻倍"),
        ("C:\\trailing\\\\",
         '"C:\\trailing\\\\\\\\"',
         "路径以两个反斜杠结尾 -> 翻倍成四个"),
        ('say "hi"',
         '"say \\"hi\\""',
         "值内含双引号"),
        ('a"b',
         '"a\\"b"',
         "双引号在中间"),
        ("",
         '""',
         "空值也必须产出成对引号（否则参数错位）"),
        ("C:\\dir with space\\and\\",
         '"C:\\dir with space\\and\\\\"',
         "空格 + 结尾反斜杠组合"),
        ("p@ss#w!rd$%^&*()",
         '"p@ss#w!rd$%^&*()"',
         "密码含 shell 特殊字符"),
        ('pw"with"quotes',
         '"pw\\"with\\"quotes"',
         "密码含双引号（报告特别点名的场景）"),
        ("pw\\\\\"mix",
         '"pw\\\\\\\\\\"mix"',
         "反斜杠紧邻双引号"),
        ("tab\there",
         '"tab\there"',
         "值含制表符"),
    ]

    got = run_quote_cases([c[0] for c in cases])
    if len(got) != len(cases):
        record("Quote() 转义表", False,
               "返回 %d 条，期望 %d 条" % (len(got), len(cases)))
        return

    bad = []
    for (inp, want, why), actual in zip(cases, got):
        if actual != want:
            bad.append("%s\n        输入: %r\n        期望: %r\n        实际: %r"
                       % (why, inp, want, actual))

    record("Quote() 转义表（%d 条）" % len(cases), not bad,
           ("%d 条全部正确" % len(cases)) if not bad else "%d 条不符" % len(bad))
    for b in bad:
        print("       " + b)


# ---------------------------------------------------------------------------
# 第二部分：端到端格式矩阵
# ---------------------------------------------------------------------------
def run(cmd, cwd, data_root, timeout=240):
    env = dict(os.environ)
    env["AUTO7Z_DATA_ROOT"] = str(data_root)
    return subprocess.run(cmd, cwd=str(cwd), env=env, capture_output=True,
                          text=True, encoding="utf-8", errors="replace",
                          timeout=timeout)


def find_output(root, exts, exclude):
    """在 root 下找指定后缀的产物，排除 exclude 子树。

    用 startswith 而不是精确后缀集合：tar 格式在 Zstd 开启时产出的是
    「.tar.zst」而不是「.tar」，只匹配 .tar 会漏检（实测踩到过）。
    """
    found = []
    for p in Path(root).rglob("*"):
        if not p.is_file():
            continue
        name = p.name.lower()
        if not any(name.endswith(e) for e in exts):
            continue
        if any(e in p.parents for e in exclude):
            continue
        found.append(p)
    return sorted(found)


def test_format(ext_list, label):
    """对一种输出格式跑压缩→解压→内容比对。

    格式选择：产品通过配置文件里的 Format 键决定，没有命令行开关。
    所以这里先写一份带指定 Format 的配置，再跑压缩。
    """
    base = Path(tempfile.mkdtemp(prefix="Auto7zTest_fmt_"))
    try:
        src = base / "src"
        src.mkdir()
        payload = ("格式测试内容 %s\n" % label) * 50
        (src / "a.txt").write_text(payload, encoding="utf-8")
        (src / "sub").mkdir()
        (src / "sub" / "b bin.dat").write_bytes(bytes(range(256)) * 8)
        (src / "中文 名.txt").write_text("中文文件名", encoding="utf-8")

        data = base / "data"
        cfgdir = data / "Auto7z"
        cfgdir.mkdir(parents=True)
        fmt = ext_list[0].lstrip(".")
        cfg = {
            "Language": "zh-CN", "Volume": 2000, "Format": fmt,
            "Password": "", "Zstd": True, "GenerateSHA256": True,
            "ChecksumAlgorithm": "sha256", "AutoSave": True,
            "IsTopMost": False, "RuntimeMode": "Balanced", "OutputPath": "",
            "IsSolidMode": False, "IsEncFileName": False,
            "CompressionLevel": "Normal", "UnzipPassword": "",
            "ErrorLevel": "Warning",
        }
        import json
        (cfgdir / "Auto7z.json").write_text(json.dumps(cfg, ensure_ascii=False),
                                           encoding="utf-8")

        work = base / "work"
        work.mkdir()

        r = run([str(EXE), "-z", str(src)], work, data)
        if r.returncode != 0:
            record("格式 %-4s 压缩" % fmt, False,
                   "退出码 %d: %s" % (r.returncode,
                                     (r.stdout + r.stderr).strip()[:160]))
            return

        produced = find_output(base, set(ext_list),
                               exclude=[src, work, data, cfgdir])
        if not produced:
            allfiles = [p.relative_to(base).as_posix()
                        for p in base.rglob("*") if p.is_file()]
            record("格式 %-4s 压缩产物" % fmt, False,
                   "未找到 %s（base 内: %s）"
                   % ("/".join(ext_list), allfiles[:8] or "空"))
            return
        archive = produced[0]
        record("格式 %-4s 压缩" % fmt, True, "%s (%d B)" % (archive.name,
                                                          archive.stat().st_size))

        # 解压
        outdir = base / "out"
        outdir.mkdir()
        r = run([str(EXE), "-uz", str(archive)], outdir, data)
        if r.returncode != 0:
            record("格式 %-4s 解压" % fmt, False,
                   "退出码 %d: %s" % (r.returncode,
                                     (r.stdout + r.stderr).strip()[:160]))
            return

        # 内容比对：找到解出来的 a.txt
        hits = [p for p in outdir.rglob("a.txt")
                if p.is_file() and p.parent != src]
        if not hits:
            hits = [p for p in base.rglob("a.txt")
                    if p.is_file() and p.parent != src]
        if not hits:
            record("格式 %-4s 内容" % fmt, False, "解压后找不到 a.txt")
            return
        got = hits[0].read_text(encoding="utf-8", errors="replace")
        ok = got == payload
        record("格式 %-4s 内容一致" % fmt, ok,
               "逐字符一致" if ok else "内容不符")
    finally:
        if base.exists() and base.name.startswith("Auto7zTest_fmt_"):
            safe_rmtree(base)


def test_special_paths():
    """带特殊字符的真实文件路径能否正确压缩解压。

    这条直接对应报告 §三-1「路径中有空格 / 中文 / 括号 / 路径以反斜杠结尾」。
    只要 Quote() 有任何一处漏翻，这里就会失败。
    """
    base = Path(tempfile.mkdtemp(prefix="Auto7zTest_spec_"))
    try:
        dirname = "有 空格 (括号) +加号 目录"
        src = base / dirname
        src.mkdir()
        fname = "文件 with spaces (1) +plus.txt"
        payload = "special path content\n" * 20
        (src / fname).write_text(payload, encoding="utf-8")

        data = base / "data"
        work = base / "work"
        work.mkdir()

        r = run([str(EXE), "-z", str(src)], work, data)
        if r.returncode != 0:
            record("特殊字符路径压缩", False,
                   "退出码 %d: %s" % (r.returncode,
                                     (r.stdout + r.stderr).strip()[:160]))
            return

        arcs = [p for p in base.rglob("*.7z")]
        if not arcs:
            record("特殊字符路径压缩", False, "无 7z 产物")
            return
        record("特殊字符路径压缩", True, arcs[0].name)

        outdir = base / "out"
        outdir.mkdir()
        r = run([str(EXE), "-uz", str(arcs[0])], outdir, data)
        if r.returncode != 0:
            record("特殊字符路径解压", False,
                   "退出码 %d: %s" % (r.returncode,
                                     (r.stdout + r.stderr).strip()[:160]))
            return

        hits = [p for p in base.rglob(fname) if p.parent != src]
        if not hits:
            record("特殊字符路径解压", False, "解压后找不到目标文件")
            return
        got = hits[0].read_text(encoding="utf-8", errors="replace")
        record("特殊字符路径内容一致", got == payload,
               "逐字符一致" if got == payload else "内容不符")
    finally:
        if base.exists() and base.name.startswith("Auto7zTest_spec_"):
            safe_rmtree(base)


def test_password_special_chars():
    """密码含特殊字符时的往返（报告点名「特别要测试包含双引号的密码」）。

    产品通过配置驱动密码，命令行不直接收 -p。这里写配置含特殊字符密码，
    压缩后用同一密码解压，验证密码链路没被转义破坏。

    注意：如果产品把密码以 -p"..." 传给 7z，含双引号的密码会在 7z 侧
    被拆开。这类失败是真实缺陷，必须暴露而不是掩盖。
    """
    import json

    cases = [
        ("simple123", "纯字母数字"),
        ("中文密码测试", "中文密码"),
        ("p@ss w0rd!", "含空格与标点"),
        ('pw"quote"x', "含双引号"),
        ("back\\slash", "含反斜杠"),
    ]

    for pw, why in cases:
        base = Path(tempfile.mkdtemp(prefix="Auto7zTest_pw_"))
        try:
            src = base / "src"
            src.mkdir()
            (src / "secret.txt").write_text("protected content", encoding="utf-8")

            data = base / "data"
            cfgdir = data / "Auto7z"
            cfgdir.mkdir(parents=True)
            cfg = {
                "Language": "zh-CN", "Volume": 2000, "Format": "7z",
                "Password": pw, "Zstd": True, "GenerateSHA256": True,
                "ChecksumAlgorithm": "sha256", "AutoSave": True,
                "IsTopMost": False, "RuntimeMode": "Balanced", "OutputPath": "",
                "IsSolidMode": False, "IsEncFileName": False,
                "CompressionLevel": "Normal",
                # 解压用的是 UnzipPassword（不是 Password）。产品在非交互环境下
                # 遇到加密包会走「提示输入密码」分支，而不是回落到配置里的
                # Password —— 所以这里必须两个键都填，否则解压会被判为取消。
                "UnzipPassword": pw,
                "ErrorLevel": "Warning",
            }
            (cfgdir / "Auto7z.json").write_text(
                json.dumps(cfg, ensure_ascii=False), encoding="utf-8")

            work = base / "work"
            work.mkdir()

            r = run([str(EXE), "-z", str(src)], work, data)
            if r.returncode != 0:
                record("密码 %-18s 压缩" % why, False,
                       "退出码 %d" % r.returncode)
                continue

            arcs = [p for p in base.rglob("*.7z")]
            if not arcs:
                record("密码 %-18s 压缩" % why, False, "无产物")
                continue

            # 用同一份配置（含同一密码）解压
            outdir = base / "out"
            outdir.mkdir()
            r = run([str(EXE), "-uz", str(arcs[0])], outdir, data)
            ok = r.returncode == 0
            if ok:
                hits = [p for p in base.rglob("secret.txt") if p.parent != src]
                ok = len(hits) > 0 and \
                    hits[0].read_text(encoding="utf-8", errors="replace") == "protected content"
                detail = "解出且内容一致" if ok else "解出但内容不符/未找到"
            else:
                detail = "解压失败 退出码 %d" % r.returncode
            record("密码 %-18s 往返" % why, ok, detail)
        finally:
            if base.exists() and base.name.startswith("Auto7zTest_pw_"):
                safe_rmtree(base)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--config", default="Release")
    a = ap.parse_args()

    print("=" * 78)
    print("命令行转义与格式矩阵测试")
    print("被测程序: %s" % EXE)
    print("=" * 78)

    print("\n=== 一、Quote() 参数转义 ===")
    test_quote()

    print("\n=== 二、格式矩阵（7z / zip / tar）===")
    test_format([".7z"], "7z")
    test_format([".zip"], "zip")
    test_format([".tar", ".tar.zst", ".tar.gz"], "tar")

    print("\n=== 三、特殊字符路径 ===")
    test_special_paths()

    print("\n=== 四、密码特殊字符 ===")
    test_password_special_chars()

    passed = sum(1 for _, ok, _ in results if ok)
    failed = len(results) - passed
    print()
    print("=" * 78)
    print("转义与格式测试汇总: %d 项, 通过 %d, 失败 %d"
          % (len(results), passed, failed))
    print("=" * 78)
    if failed:
        for name, ok, detail in results:
            if not ok:
                print("  - %s  %s" % (name, detail))
        return 1
    print("全部通过 ✓")
    return 0


if __name__ == "__main__":
    sys.exit(main())
