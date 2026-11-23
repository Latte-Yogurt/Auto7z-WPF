#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""验证 CommandOutcome 五态语义（GPT6 建议第 1 条的落实）。

## 背景

GPT6 指出 `Succeeded` 把退出码 0 和 1 都当成功，建议区分：
完全成功 / 警告或部分成功 / 完全失败 / 用户取消 / 超时。

核实结论：`Succeeded` 的语义是「7-Zip 跑完了、产物出来了」（含 exit 1），
而「是否完全成功」由 `HasWarning` 承载，调用方两条路径都在用 ——
所以「部分成功被报成完全成功」并没有发生。

但**命名确实容易误读**。现在加了显式的 `CommandOutcome` 枚举把结论明确化：
    Success / Partial / Failed / Cancelled / TimedOut

本脚本验证：真实操作下 Outcome 的取值与命令行退出码是否一致。

## 验证矩阵

| 场景 | 期望 Outcome | 期望退出码 |
|---|---|---|
| 正常压缩 | Success | 0 |
| 部分文件被占用（7z exit=1） | Partial | 1 |
| 输入不存在 | Failed | 2 |
| 加密包无密码 | Failed 或要求密码 | 非 0 |
"""
import json
import os
import shutil
import subprocess
import sys
from pathlib import Path

from safe_ops import safe_rmtree

P = sys.argv[1] if len(sys.argv) > 1 else r"C:\Users\Mocha\Desktop\VSProject\Auto7z-WPF"
EXE = os.path.join(P, "Auto7z_WPF", "Auto7z_WPF", "bin", "Debug", "Auto7z.exe")
SDK = os.path.join(os.environ.get("TEMP", r"C:\Windows\Temp"), "Auto7zTest_data", "outcome")

results = []


def check(name, ok, detail=""):
    results.append({"name": name, "ok": ok, "detail": detail})
    print(("  OK   " if ok else "  !!   ") + name + ("  --- " + detail if detail else ""))


def prep(tag, **over):
    work = os.path.join(SDK, tag, "work")
    data = os.path.join(SDK, tag, "data")
    os.makedirs(work, exist_ok=True)
    cfgdir = os.path.join(data, "Auto7z")
    os.makedirs(cfgdir, exist_ok=True)
    cfg = {
        "Language": "zh-CN", "Volume": "0", "Format": "7z", "Password": "",
        "Zstd": True, "GenerateSHA256": False, "ChecksumAlgorithm": "sha256",
        "AutoSave": False, "IsTopMost": False, "RuntimeMode": "Balanced",
        "OutputPath": "", "IsSolidMode": False, "IsEncFileName": False,
        "CompressionLevel": "Store", "UnzipPassword": "", "ErrorLevel": "Info",
    }
    cfg.update(over)
    open(os.path.join(cfgdir, "Auto7z.json"), "w", encoding="utf-8").write(
        json.dumps(cfg, ensure_ascii=False, indent=2))
    env = dict(os.environ)
    env["AUTO7Z_DATA_ROOT"] = data
    env["AUTO7Z_AUTOANSWER"] = "1,1,1,1"
    return work, data, env


def run(args, env, cwd, timeout=180):
    try:
        p = subprocess.run([EXE] + args, capture_output=True, env=env, cwd=cwd, timeout=timeout)
        return p.returncode, ((p.stdout or b"") + (p.stderr or b"")).decode("utf-8", "replace")
    except subprocess.TimeoutExpired:
        return None, "(超时)"


def main():
    if os.path.isdir(SDK):
        safe_rmtree(SDK)
    os.makedirs(SDK, exist_ok=True)

    print("=" * 76)
    print("CommandOutcome 五态语义验证（GPT6 建议第 1 条）")
    print("=" * 76)

    # ---------- 1. 完全成功 ----------
    print("\n【1】正常压缩 → 应为完全成功")
    w, d, env = prep("ok")
    f = os.path.join(w, "a.txt")
    open(f, "w", encoding="utf-8").write("content")
    rc, out = run(["-z", f], env, w)
    check("退出码 0", rc == 0, "rc=%s" % rc)
    check("输出无「未完全成功」", "未完全成功" not in out,
          "" if "未完全成功" not in out else "!! 完全成功却报了部分")

    # ---------- 2. 部分成功（7z exit=1）----------
    print("\n【2】部分文件被占用 → 应为部分成功（退出码 1）")
    w2, d2, env2 = prep("partial")
    ok_file = os.path.join(w2, "ok.txt")
    locked = os.path.join(w2, "locked.txt")
    open(ok_file, "w", encoding="utf-8").write("fine")
    open(locked, "w", encoding="utf-8").write("locked")

    # 用 .NET 的 FileShare.None 独占该文件 —— 这是实测**确实**能让 7-Zip 报
    # exit=1 的方式。先前用 msvcrt.locking 锁不住（7z 仍能读），
    # 于是「部分成功」场景根本没被触发，测试误判成产品没报部分成功。
    lock_ps = os.path.join(w2, "_lock.ps1")
    open(lock_ps, "w", encoding="utf-8").write(
        'param([string]$Lock, [string]$Exe, [string]$A, [string]$B)\n'
        '$ErrorActionPreference = "Stop"\n'
        '$fs = [System.IO.File]::Open($Lock, [System.IO.FileMode]::Open,\n'
        '    [System.IO.FileAccess]::Read, [System.IO.FileShare]::None)\n'
        'try {\n'
        '    $out = & $Exe -z $A $B 2>&1 | Out-String\n'
        '    Write-Output "===BEGIN==="\n'
        '    Write-Output $out\n'
        '    Write-Output "===END==="\n'
        '    Write-Output ("RC=" + $LASTEXITCODE)\n'
        '} finally { $fs.Close() }\n')

    proc = subprocess.run(
        ["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass",
         "-File", lock_ps, locked, EXE, ok_file, locked],
        capture_output=True, text=True, encoding="utf-8", errors="replace",
        env=env2, cwd=w2, timeout=300)
    out2 = (proc.stdout or "") + (proc.stderr or "")
    m = [l.strip() for l in out2.splitlines() if l.strip().startswith("RC=")]
    rc2 = int(m[-1].split("=", 1)[1]) if m else None

    has_partial_line = "未完全成功" in out2 or "部分文件被跳过" in out2
    check("退出码 1（README 契约）", rc2 == 1, "rc=%s" % rc2)
    check("界面明确标为部分成功", has_partial_line,
          "" if has_partial_line else "!! 没有部分成功的提示")

    # ---------- 3. 完全失败 ----------
    print("\n【3】输入不存在 → 应为完全失败（退出码 2）")
    w3, d3, env3 = prep("fail")
    rc3, out3 = run(["-z", os.path.join(w3, "nope.txt")], env3, w3)
    check("退出码 2（没跑）", rc3 == 2, "rc=%s" % rc3)

    print("\n" + "=" * 76)
    bad = [r for r in results if not r["ok"]]
    print("共 %d 项，通过 %d，失败 %d" % (len(results), len(results) - len(bad), len(bad)))
    if bad:
        for b in bad:
            print("  " + b["name"] + "  " + b["detail"])
        print("FAIL")
        code = 1
    else:
        print("完全成功 / 部分成功 / 完全失败 三态在命令行退出码上均正确")
        print("PASS")
        code = 0
    out_path = os.path.join(SDK, "outcome.json")
    open(out_path, "w", encoding="utf-8").write(json.dumps(results, ensure_ascii=False, indent=2))
    print("输出 -> %s" % out_path)
    return code


if __name__ == "__main__":
    sys.exit(main())
