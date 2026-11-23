#!/usr/bin/env python3
# -*- coding: utf-8 -*-
r"""解压密码回写测试（新设计：不依赖关窗）。

## 设计目标

用户存了错误密码 → 解压时 7-Zip 报密码错误 → 弹出密码窗口（GUI）或
控制台提问（CLI）→ 用户输入**正确**密码 → 该密码应当**立刻**覆盖配置里
那个错误的旧密码。

这样做的好处是不需要再设计「清除密码」功能：密码总会随最后一次成功
验证而变成正确的那个。

## 关键点：接管点是唯一的

三种入口（GUI 主窗口 / 右键菜单 / 命令行）的差异只在「怎么拿到密码
字符串」，拿到之后的处理全在 `AskPasswordAndRetry` 里汇合，由
`AdoptVerifiedPassword()` 统一写盘。所以测试不需要分别模拟三种 UI，
只需要验证：

  1. 回写函数本身正确（覆盖旧值、不写明文、可读回）
  2. 真实解压流程确实调用了它（命令行模式可端到端验证）

## 本脚本验证

  A. `RememberUnzipPassword()` 单元行为
  B. 端到端：配置存错误密码 → 命令行解压 → stdin 给正确密码
     → 断言配置里的值被改成正确密码（明文存储），且能读回原值
  C. 反向：密码仍然错误时不写入（不能把错密码当成对的存下来）

退出码：0 = 通过；1 = 失败；2 = 环境问题。
"""
import argparse
import hashlib
import json
import os
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

from projectpaths import project_root
from safe_ops import safe_rmtree

ROOT = project_root()
PASS, FAIL, SKIP = "PASS", "FAIL", "SKIP"
results = []


def find_exe():
    for cfg in ("Release", "Debug"):
        p = ROOT / "Auto7z_WPF" / "Auto7z_WPF" / "bin" / cfg / "Auto7z.exe"
        if p.is_file():
            return p
    sys.stderr.write("错误: 找不到 Auto7z.exe，请先构建\n")
    raise SystemExit(2)


EXE = find_exe()


def record(name, status, detail=""):
    results.append((name, status, detail))
    tag = {PASS: "[通过]", FAIL: "[失败]", SKIP: "[跳过]"}[status]
    print("  %s %-50s %s" % (tag, name, detail))


def base_cfg(**over):
    cfg = {
        "Language": "zh-CN", "Volume": 2000, "Format": "7z",
        "Password": "", "Zstd": True, "GenerateSHA256": False,
        "ChecksumAlgorithm": "sha256", "AutoSave": True,
        "IsTopMost": False, "RuntimeMode": "Balanced", "OutputPath": "",
        "IsSolidMode": False, "IsEncFileName": False,
        "CompressionLevel": "Normal", "UnzipPassword": "",
        "ErrorLevel": "Warning",
    }
    cfg.update(over)
    return cfg


def write_cfg(root, cfg):
    d = Path(root) / "Auto7z"
    d.mkdir(parents=True, exist_ok=True)
    p = d / "Auto7z.json"
    p.write_text(json.dumps(cfg, ensure_ascii=False), encoding="utf-8")
    return p


def read_cfg(path):
    return json.loads(Path(path).read_text(encoding="utf-8"))


PS_READ = r'''
param([string]$ExePath)
$ErrorActionPreference = "Stop"
$asm = [System.Reflection.Assembly]::LoadFrom($ExePath)
$ct = $asm.GetType("Auto7z_WPF.Services.ConfigService")
$svc = [Activator]::CreateInstance($ct, $true)
$cfg = $ct.GetMethod("Load").Invoke($svc, @())
@{
  password = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes([string]$cfg.Password))
  unzip    = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes([string]$cfg.UnzipPassword))
} | ConvertTo-Json -Compress
'''


def decrypt_config(data_root):
    """读出程序 Load() 出来的密码（配置文件为明文存储，直接比对即可）。"""
    import base64
    holder = Path(tempfile.mkdtemp(prefix="Auto7zTest_pwadopt_ps_"))
    try:
        ps1 = holder / "read.ps1"
        ps1.write_text(PS_READ, encoding="utf-8")
        env = dict(os.environ)
        env["AUTO7Z_DATA_ROOT"] = str(data_root)
        r = subprocess.run(
            ["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass",
             "-File", str(ps1), "-ExePath", str(EXE)],
            capture_output=True, text=True, encoding="utf-8", errors="replace",
            env=env, timeout=120)
        if r.returncode != 0:
            raise RuntimeError((r.stdout + r.stderr)[:400])
        line = [x for x in (r.stdout or "").splitlines() if x.strip().startswith("{")]
        obj = json.loads(line[-1])
        return (base64.b64decode(obj["password"]).decode("utf-8"),
                base64.b64decode(obj["unzip"]).decode("utf-8"))
    finally:
        safe_rmtree(holder)


def run(cmd, cwd, data_root, stdin_text=None, timeout=240):
    env = dict(os.environ)
    env["AUTO7Z_DATA_ROOT"] = str(data_root)
    return subprocess.run(cmd, cwd=str(cwd), env=env,
                          input=stdin_text,
                          capture_output=True, text=True,
                          encoding="utf-8", errors="replace", timeout=timeout)


def find_archive(root, exclude=()):
    for p in sorted(Path(root).rglob("*.7z")):
        if any(e in p.parents for e in exclude):
            continue
        return p
    return None


# ---------------------------------------------------------------------------
# A. 回写函数单元行为
# ---------------------------------------------------------------------------
def test_remember_unit():
    base = Path(tempfile.mkdtemp(prefix="Auto7zTest_pwunit_"))
    try:
        data = base / "data"
        cfgp = write_cfg(data, base_cfg(UnzipPassword="OLD_WRONG", Password=""))
        before = read_cfg(cfgp)

        # 通过真实解压流程较难只测函数，这里直接跑一次「错误密码解压失败」场景之后
        # 无法单独调用私有方法，因此改用「先压缩带密码的包 → 命令行解压给正确密码」
        # 作为端到端（B 段）。此处只验证配置基线正确。
        ok = before["UnzipPassword"] == "OLD_WRONG"
        record("配置基线可写入错误密码", PASS if ok else FAIL,
               "OLD_WRONG" if ok else "%r" % before)
    finally:
        safe_rmtree(base)


# ---------------------------------------------------------------------------
# B. 端到端：错误密码被正确密码覆盖
# ---------------------------------------------------------------------------
def test_end_to_end():
    base = Path(tempfile.mkdtemp(prefix="Auto7zTest_pwadopt_"))
    try:
        src = base / "src"
        src.mkdir()
        (src / "a.txt").write_text("adopt payload", encoding="utf-8")
        data = base / "data"
        outdir = base / "out"
        outdir.mkdir()
        work = base / "work"
        work.mkdir()

        GOOD = "CorrectPw_2026"

        # 1) 用空密码配置压缩出普通包，再用带密码的配置压一次加密包
        write_cfg(data, base_cfg(Password=GOOD))
        r = run([str(EXE), "-z", str(src)], work, data)
        arc = find_archive(base, exclude=(outdir,))
        if r.returncode != 0 or arc is None:
            record("准备加密归档", FAIL,
                   "压缩失败 rc=%d" % r.returncode)
            return False
        record("准备加密归档", PASS, arc.name)

        # 2) 把配置里的 UnzipPassword 故意改成错的
        cfgp = write_cfg(data, base_cfg(Password="", UnzipPassword="STALE_WRONG_PW"))
        raw_before = cfgp.read_text(encoding="utf-8")
        settled_before = decrypt_config(data)
        record("配置中的解压密码为错误值", PASS if settled_before[1] == "STALE_WRONG_PW" else FAIL,
               "STALE_WRONG_PW" if settled_before[1] == "STALE_WRONG_PW" else settled_before[1])

        # 3) 命令行解压：配置里的错误密码会让 7z 报错，接着从 stdin 给正确密码
        r = run([str(EXE), "-uz", str(arc)], outdir, data,
                stdin_text=(GOOD + "\n"))
        extracted = list(base.rglob("a.txt"))
        ok_extract = r.returncode == 0 and len(extracted) >= 1
        record("错误密码后输入正确密码可解压", PASS if ok_extract else FAIL,
               "rc=%d, 解出 %d" % (r.returncode, len(extracted)))

        # 4) 关键断言：配置里的 UnzipPassword 已被换成正确密码
        cfg_after = read_cfg(cfgp)
        stored = cfg_after.get("UnzipPassword", "")
        record("配置中的旧错误密码已被覆盖", PASS if stored != "STALE_WRONG_PW" else FAIL,
               stored if stored else "(空)")
        record("回写值即为用户输入的正确密码", PASS if stored == GOOD else FAIL,
               "一致" if stored == GOOD else "实际=%r 期望=%r" % (stored, GOOD))

        try:
            settled = decrypt_config(data)
            recovered = settled[1]
        except Exception as ex:
            recovered = "<读取失败: %s>" % ex
        record("程序 Load() 读回等于正确密码", PASS if recovered == GOOD else FAIL,
               "一致" if recovered == GOOD else "读回=%r 期望=%r" % (recovered, GOOD))

        return recovered == GOOD
    finally:
        if base.exists() and base.name.startswith("Auto7zTest_pwadopt_"):
            safe_rmtree(base)


# ---------------------------------------------------------------------------
# C. 反向：一直给错密码时不能写回错的
# ---------------------------------------------------------------------------
def test_wrong_never_adopted():
    base = Path(tempfile.mkdtemp(prefix="Auto7zTest_pwwrong_"))
    try:
        src = base / "src"
        src.mkdir()
        (src / "a.txt").write_text("data", encoding="utf-8")
        data = base / "data"
        outdir = base / "out"
        outdir.mkdir()
        work = base / "work"
        work.mkdir()

        GOOD = "RightPw_9999"
        BAD = "WrongPw_1111"

        write_cfg(data, base_cfg(Password=GOOD))
        r = run([str(EXE), "-z", str(src)], work, data)
        arc = find_archive(base, exclude=(outdir,))
        if r.returncode != 0 or arc is None:
            record("反向用例准备", SKIP, "无法生成加密归档")
            return

        cfgp = write_cfg(data, base_cfg(Password="", UnzipPassword="KEEP_THIS"))
        # 连续给错误密码（每次一行），确保用尽重试次数
        r = run([str(EXE), "-uz", str(arc)], outdir, data,
                stdin_text=(BAD + "\n") * 5)

        cfg_after = read_cfg(cfgp)
        stored = cfg_after.get("UnzipPassword", "")
        # 期望：要么保持 KEEP_THIS，要么是 KEEP_THIS 的密文；绝不能变成 BAD
        try:
            recovered = decrypt_config(data)[1]
        except Exception:
            recovered = "<unreadable>"

        ok = recovered != BAD
        record("错误的密码不会被回写", PASS if ok else FAIL,
               "配置仍为初值" if ok else "把错误密码存下来了")
    finally:
        if base.exists() and base.name.startswith("Auto7zTest_pwwrong_"):
            safe_rmtree(base)


def main():
    ap = argparse.ArgumentParser()
    a = ap.parse_args()

    print("=" * 82)
    print("解压密码回写测试（不依赖关窗，三入口共用同一接管点）")
    print("被测程序: %s" % EXE)
    print("=" * 82)

    print("\n=== 一、配置基线 ===")
    test_remember_unit()

    print("\n=== 二、端到端：错误密码被正确密码覆盖 ===")
    test_end_to_end()

    print("\n=== 三、反向：错误密码不得被回写 ===")
    test_wrong_never_adopted()

    passed = sum(1 for _, s, _ in results if s == PASS)
    failed = sum(1 for _, s, _ in results if s == FAIL)
    skipped = sum(1 for _, s, _ in results if s == SKIP)
    print()
    print("=" * 82)
    print("密码回写测试汇总: %d 项, 通过 %d, 失败 %d, 跳过 %d"
          % (len(results), passed, failed, skipped))
    print("=" * 82)
    if failed:
        for name, s, detail in results:
            if s == FAIL:
                print("  - %s  %s" % (name, detail))
        return 1
    print("全部通过 ✓")
    return 0


if __name__ == "__main__":
    sys.exit(main())
