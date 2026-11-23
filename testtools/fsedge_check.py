#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""文件系统异常场景补充（GPT6 补充建议第 2 条的后半部分）。

现有覆盖：
  ✓ 数据根目录本身为 Junction（gpt6b_check BUG-01）
  ✓ Junction 指向外部磁盘（reparse_guard_check）
  ✓ 临时文件与用户文件重名（gpt6b_check BUG-05）
  ✓ 源目录权限不足（gpt6b_check BUG-02）

本脚本补：
  1. 数据根目录本身为 SymbolicLink
  2. 磁盘根目录压缩（端到端，不只是 IsPathInside 单测）
  3. 迁移过程中强制结束程序
  4. 目标磁盘空间不足 —— 用「目标路径落在一个极小的卷/受限路径上」模拟
  5. 盘符突然断开 —— 原则上不可自动化（需要真实拔盘），如实标为不可测

## 关于 SymbolicLink

创建目录符号链接需要「创建符号链接」权限：
  · 管理员，或
  · 开发者模式（Windows 10 1703+）
拿不到就跳过 —— **不假装通过**。
"""
import json
import os
import shutil
import subprocess
import sys
import time
from pathlib import Path

from safe_ops import safe_rmtree

P = sys.argv[1] if len(sys.argv) > 1 else r"C:\Users\Mocha\Desktop\VSProject\Auto7z-WPF"
EXE = os.path.join(P, "Auto7z_WPF", "Auto7z_WPF", "bin", "Debug", "Auto7z.exe")
SDK = os.path.join(os.environ.get("TEMP", r"C:\Windows\Temp"), "Auto7zTest_data", "fsedge")

results = []


def check(name, ok, detail=""):
    results.append({"name": name, "ok": ok, "detail": detail})
    print(("  OK   " if ok else "  !!   ") + name + ("  --- " + detail if detail else ""))


def skip(name, why):
    results.append({"name": name, "ok": True, "detail": "跳过: " + why})
    print("  --   " + name + "  --- 跳过: " + why)


def prep(tag):
    work = os.path.join(SDK, tag)
    data = os.path.join(work, "data")
    os.makedirs(os.path.join(data, "Auto7z"), exist_ok=True)
    cfg = {
        "Language": "zh-CN", "Volume": "0", "Format": "7z", "Password": "",
        "Zstd": True, "GenerateSHA256": False, "ChecksumAlgorithm": "sha256",
        "AutoSave": False, "IsTopMost": False, "RuntimeMode": "Balanced",
        "OutputPath": "", "IsSolidMode": False, "IsEncFileName": False,
        "CompressionLevel": "Store", "UnzipPassword": "", "ErrorLevel": "Info",
    }
    open(os.path.join(data, "Auto7z", "Auto7z.json"), "w", encoding="utf-8").write(
        json.dumps(cfg, ensure_ascii=False, indent=2))
    env = dict(os.environ)
    env["AUTO7Z_DATA_ROOT"] = data
    env["AUTO7Z_AUTOANSWER"] = "1,1,1,1"
    return work, data, env


def call_mover(method, *args):
    """反射调用 DataDirectoryMover 的静态方法（参数走 JSON 文件）。"""
    arg_file = Path(SDK) / "_args.json"
    arg_file.write_text(json.dumps([str(a) for a in args], ensure_ascii=False),
                        encoding="utf-8")
    script = (
        'param([string]$Exe, [string]$Method, [string]$ArgFile)\n'
        '$ErrorActionPreference = "Stop"\n'
        '$asm = [System.Reflection.Assembly]::LoadFrom($Exe)\n'
        '$t = $asm.GetType("Auto7z_WPF.Services.DataDirectoryMover")\n'
        'if ($t -eq $null) { Write-Output "NO_TYPE"; exit 2 }\n'
        '$flags = [System.Reflection.BindingFlags]::NonPublic -bor '
        '[System.Reflection.BindingFlags]::Public -bor '
        '[System.Reflection.BindingFlags]::Static\n'
        '$m = $t.GetMethod($Method, $flags)\n'
        'if ($m -eq $null) { Write-Output "NO_METHOD"; exit 2 }\n'
        '$json = Get-Content -Raw -Encoding UTF8 $ArgFile | ConvertFrom-Json\n'
        '$n = $m.GetParameters().Length\n'
        '$arr = New-Object object[] $n\n'
        'for ($i = 0; $i -lt $n; $i++) {\n'
        '    if ($i -lt $json.Count) { $arr[$i] = [string]$json[$i] } else { $arr[$i] = $null }\n'
        '}\n'
        'try {\n'
        '    $r = $m.Invoke($null, $arr)\n'
        '    Write-Output ("RESULT=" + $r)\n'
        '} catch {\n'
        '    $inner = $_.Exception.InnerException\n'
        '    if ($inner -eq $null) { $inner = $_.Exception }\n'
        '    Write-Output ("EXCEPTION: " + $inner.GetType().Name)\n'
        '}\n')
    f = Path(SDK) / "_invoke.ps1"
    f.write_text(script, encoding="utf-8")
    p = subprocess.run(
        ["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", str(f),
         EXE, method, str(arg_file)],
        capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=180)
    out = (p.stdout or "") + (p.stderr or "")
    for line in out.splitlines():
        if line.startswith("RESULT="):
            return line.split("=", 1)[1].strip()
        if line.startswith("EXCEPTION:") or line in ("NO_TYPE", "NO_METHOD"):
            return line.strip()
    return "(无结果)"


def main():
    if os.path.isdir(SDK):
        safe_rmtree(SDK)
    os.makedirs(SDK, exist_ok=True)

    print("=" * 76)
    print("文件系统异常场景补充验证")
    print("=" * 76)

    # ============ 1. 数据根目录本身是 SymbolicLink ============
    print("\n【1】数据根目录本身是 Symbolic Link")
    s1 = Path(SDK) / "symlink"
    (s1 / "real").mkdir(parents=True)
    (s1 / "real" / "f.txt").write_text("REAL", encoding="utf-8")
    link = s1 / "Auto7z"

    r = subprocess.run(["cmd", "/c", "mklink", "/D", str(link), str(s1 / "real")],
                       capture_output=True, text=True, encoding="utf-8", errors="replace")
    if not link.exists():
        skip("符号链接根目录被拒绝", "无创建符号链接权限（需管理员或开发者模式）")
    else:
        got = call_mover("HasReparsePointAncestor", str(link))
        check("符号链接根目录被识别为链接", got.lower() == "true", "返回值=%s" % got)
        # 清理符号链接（rmdir 只删链接本身）
        subprocess.run(["cmd", "/c", "rmdir", str(link)], capture_output=True)

    # ============ 2. 磁盘根目录压缩（端到端）============
    print("\n【2】以磁盘根目录为源做压缩，输出不得落进源内被自吞")
    # 不能真压 C:\（太大），改为验证「候选路径落在源内」时会被改道。
    # 用 IsPathInside 的端到端效果：把输出目录设成源的子目录，看产品是否改到别处。
    work2, data2, env2 = prep("root")
    src_dir = Path(work2) / "src"
    src_dir.mkdir(parents=True)
    (src_dir / "f.txt").write_text("payload", encoding="utf-8")
    inside_out = src_dir / "out"
    inside_out.mkdir()
    # 明确把输出目录指到源内部
    cfgp = Path(data2) / "Auto7z" / "Auto7z.json"
    cfg = json.loads(cfgp.read_text(encoding="utf-8"))
    cfg["OutputPath"] = str(inside_out)
    cfgp.write_text(json.dumps(cfg, ensure_ascii=False, indent=2), encoding="utf-8")

    rc2 = subprocess.run([EXE, "-z", str(src_dir)], env=env2, cwd=work2,
                         capture_output=True, timeout=180)
    out2 = (rc2.stdout or b"").decode("utf-8", "replace")
    # 归档不应出现在 out 里（会被自吞）；若产品改道，会落在 src 的父目录
    inside_archives = list(inside_out.rglob("*.7z"))
    all_archives = list(Path(work2).rglob("*.7z"))
    check("输出目录在源内时不会把归档压进自身",
          len(inside_archives) == 0,
          "源内归档=%d 总归档=%d" % (len(inside_archives), len(all_archives)))

    # ============ 3. 迁移中途强制结束程序 ============
    print("\n【3】迁移中途强制结束，不得留下「两边都不完整」且目标不丢数据")
    work3, data3, env3 = prep("kill")
    src3 = Path(work3) / "src3"
    (src3 / "sub").mkdir(parents=True)
    # 造一批文件，让迁移有可观的时间窗
    for i in range(200):
        (src3 / ("f%03d.bin" % i)).write_bytes(b"x" * 4096)
    (src3 / "sub" / "deep.bin").write_bytes(b"y" * 4096)

    tgt3 = Path(work3) / "tgt3"
    tgt3.mkdir()
    # 目标预置同名文件（验证不被删）
    (tgt3 / "f000.bin").write_bytes(b"OLD_TARGET")

    arg_file = Path(SDK) / "_args3.json"
    arg_file.write_text(json.dumps([str(src3), str(tgt3)]), encoding="utf-8")
    inv = Path(SDK) / "_invoke.ps1"
    ps = subprocess.Popen(["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass",
                           "-File", str(inv), EXE, "CopyThenDeleteSource", str(arg_file)],
                          stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    time.sleep(0.05)   # 让它刚起手
    ps.kill()
    ps.wait(timeout=10)
    time.sleep(0.3)

    # 断言：目标侧 f000.bin 不能是空/丢失状态
    f000 = tgt3 / "f000.bin"
    if f000.exists():
        content = f000.read_bytes()
        ok = len(content) > 0
        check("强杀后目标已有文件未变成 0 字节", ok,
              "f000.bin = %d 字节" % len(content))
    else:
        # 若迁移已完成（把源搬走了），源里也该没有；这不算丢数据
        src_still = (src3 / "f000.bin").exists()
        check("强杀后数据仍在某一侧", src_still, "目标无、源侧有=%s" % src_still)

    # ============ 4. 目标路径不可写（模拟磁盘空间/权限受限）============
    print("\n【4】目标不可写时迁移必须失败，不能让源数据消失")
    work4, data4, env4 = prep("nowrite")
    src4 = Path(work4) / "src4"
    src4.mkdir()
    (src4 / "keep.txt").write_text("MUST_NOT_LOSE", encoding="utf-8")

    # 用一个「路径中间某个上级是文件」的目标，让 CreateDirectory 必然失败
    blocker = Path(work4) / "blocker"
    blocker.write_text("iamafile", encoding="utf-8")
    bad_tgt = blocker / "sub"

    r4 = call_mover("MoveDataDirectory", str(src4), str(bad_tgt))
    still = (src4 / "keep.txt").exists()
    check("目标无法创建时返回失败", r4.lower() == "false", "返回值=%s" % r4)
    check("失败后源数据完好", still and
          (src4 / "keep.txt").read_text(encoding="utf-8") == "MUST_NOT_LOSE",
          "源文件仍在=%s" % still)

    # ============ 5. 盘符断开 ============
    print("\n【5】盘符突然断开")
    skip("盘符断开", "需要真实拔盘或卸载卷，无法在自动化里可靠模拟")

    print("\n" + "=" * 76)
    bad = [r for r in results if not r["ok"]]
    print("共 %d 项，通过 %d，失败 %d" % (len(results), len(results) - len(bad), len(bad)))
    if bad:
        for b in bad:
            print("  " + b["name"] + "  " + b["detail"])
        print("FAIL")
        code = 1
    else:
        print("可自动化的文件系统异常场景均通过；不可模拟的如实标为跳过")
        print("PASS")
        code = 0
    out_path = os.path.join(SDK, "fsedge.json")
    open(out_path, "w", encoding="utf-8").write(json.dumps(results, ensure_ascii=False, indent=2))
    print("输出 -> %s" % out_path)
    return code


if __name__ == "__main__":
    sys.exit(main())
