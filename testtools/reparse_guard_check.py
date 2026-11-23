#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""验证「重解析点不跟随」的防护是否生效。

## 缺陷（GPT6 报告第 4 条，已确认成立）

`DataDirectoryMover` 的搬运逻辑用 `Directory.GetFiles` / `GetDirectories` 递归。
这两个 API **会穿过 Junction / SymbolicLink 进入外部目录**。

实测：在 `src\\sub\\link` 建一个指向 `outside` 的 Junction 后，
`Directory.GetFiles("src\\sub\\link")` 能读到 outside 里的文件。

于是「路径字符串在数据目录内」的边界检查会通过，但实际读写的是外部磁盘 ——
可能把 `D:\\ImportantData` 的内容搬进数据目录，或者反向搬出去。

## 验证方式

构造：
    work/src/sub/link  ->  Junction 指向 work/outside
    work/outside/secret.txt   内容 SECRET_OUTSIDE

调用 CopyThenDeleteSource(src, tgt)，然后断言：
    A. outside/secret.txt **仍然存在且内容不变**（外部数据没被搬走）
    B. tgt 下**没有** secret.txt（没有把外部内容搬进来）
    C. 普通文件仍能正常搬运（防护没误伤正常路径）
"""
import json
import os
import shutil
import subprocess
import sys
from safe_ops import safe_rmtree
from pathlib import Path

P = sys.argv[1] if len(sys.argv) > 1 else r"C:\Users\Mocha\Desktop\VSProject\Auto7z-WPF"
EXE = os.path.join(P, "Auto7z_WPF", "Auto7z_WPF", "bin", "Debug", "Auto7z.exe")
R = os.path.join(os.environ.get("TEMP", r"C:\Windows\Temp"), "Auto7zTest_data")
SDK = os.path.join(R, "tmp", "reparse")

results = []


def check(name, ok, detail=""):
    results.append({"name": name, "ok": ok, "detail": detail})
    print(("  OK   " if ok else "  !!   ") + name + ("  --- " + detail if detail else ""))


PROBE_PS = r'''
param([string]$Exe, [string]$Src, [string]$Tgt)

$ErrorActionPreference = "Stop"
$asm = [System.Reflection.Assembly]::LoadFrom($Exe)
$t = $asm.GetType("Auto7z_WPF.Services.DataDirectoryMover")
if ($t -eq $null) { Write-Output "NO_TYPE"; exit 2 }
$m = $t.GetMethod("CopyThenDeleteSource",
                  [System.Reflection.BindingFlags]::NonPublic -bor
                  [System.Reflection.BindingFlags]::Static)
if ($m -eq $null) { Write-Output "NO_METHOD"; exit 2 }

try {
    $m.Invoke($null, @($Src, $Tgt)) | Out-Null
    Write-Output "OK"
    exit 0
} catch {
    Write-Output ("EXCEPTION: " + $_.Exception.InnerException.GetType().Name)
    exit 1
}
'''


def main():
    if os.path.isdir(SDK):
        # 含 junction 的目录 rmtree 可能出问题，先删 junction
        link = os.path.join(SDK, "work", "src", "sub", "link")
        if os.path.isdir(link):
            subprocess.run(["cmd", "/c", "rmdir", link], capture_output=True)
        safe_rmtree(SDK)
    os.makedirs(SDK, exist_ok=True)

    probe = Path(SDK) / "run_mover.ps1"
    probe.write_text(PROBE_PS, encoding="utf-8")

    print("=" * 76)
    print("重解析点防护验证")
    print("=" * 76)

    work = Path(SDK) / "work"
    src = work / "src"
    tgt = work / "tgt"
    outside = work / "outside"

    (src / "sub").mkdir(parents=True)
    tgt.mkdir(parents=True)
    outside.mkdir(parents=True)

    # 正常文件（应当被搬走）
    (src / "normal.txt").write_text("NORMAL", encoding="utf-8")
    (src / "sub" / "deep.txt").write_text("DEEP", encoding="utf-8")

    # 外部数据（绝不能被搬走）
    (outside / "secret.txt").write_text("SECRET_OUTSIDE", encoding="utf-8")

    # 建 Junction
    link = src / "sub" / "link"
    proc = subprocess.run(["cmd", "/c", "mklink", "/J", str(link), str(outside)],
                          capture_output=True, text=True, encoding="utf-8", errors="replace")
    junction_ok = link.exists() and (link / "secret.txt").exists()
    check("Junction 创建成功（且确实能看到外部文件）", junction_ok,
          "" if junction_ok else (proc.stdout or "")[:80])
    if not junction_ok:
        print("\n无法建立 Junction，本项无法验证（可能需要权限）")
        return 0

    print("\n【执行搬运】")
    p = subprocess.run(
        ["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass",
         "-File", str(probe), EXE, str(src), str(tgt)],
        capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=180)
    out = (p.stdout or "") + (p.stderr or "")
    print("     探针输出: %s" % out.strip()[:100])

    print("\n【断言】")
    # A. 外部数据必须原样保留
    secret_still = (outside / "secret.txt").exists()
    secret_content = (outside / "secret.txt").read_text(encoding="utf-8") if secret_still else "(已丢失)"
    check("A 外部数据未被搬走", secret_still and secret_content == "SECRET_OUTSIDE",
          "存在=%s 内容=%r" % (secret_still, secret_content))

    # B. 目标目录不得出现外部文件
    leaked = (tgt / "secret.txt").exists() or any(
        f.name == "secret.txt" for f in tgt.rglob("*.txt"))
    check("B 外部内容未泄漏进目标目录", not leaked,
          "目标里出现了 secret.txt" if leaked else "干净")

    # C. 正常文件仍要搬走
    normal_moved = (tgt / "normal.txt").exists()
    deep_moved = (tgt / "sub" / "deep.txt").exists()
    check("C 正常文件仍被搬走", normal_moved and deep_moved,
          "normal=%s sub/deep=%s" % (normal_moved, deep_moved))

    # 清理 junction（避免后续 rmtree 跟随）
    if link.exists():
        subprocess.run(["cmd", "/c", "rmdir", str(link)], capture_output=True)

    print("\n" + "=" * 76)
    bad = [r for r in results if not r["ok"]]
    print("共 %d 项，通过 %d，失败 %d" % (len(results), len(results) - len(bad), len(bad)))
    if bad:
        for b in bad:
            print("  " + b["name"] + "  " + b["detail"])
        print("FAIL")
        code = 1
    else:
        print("PASS")
        code = 0
    out_path = os.path.join(SDK, "reparse-guard.json")
    open(out_path, "w", encoding="utf-8").write(json.dumps(results, ensure_ascii=False, indent=2))
    print("输出 -> %s" % out_path)
    return code


if __name__ == "__main__":
    sys.exit(main())
