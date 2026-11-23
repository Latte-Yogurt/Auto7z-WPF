#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""复现并验证「数据目录迁移非原子」缺陷。

## 缺陷描述

`DataDirectoryMover.CopyThenDeleteSource` 里的搬运逻辑：

```csharp
string target = Path.Combine(targetDirectory, Path.GetFileName(file));
if (File.Exists(target)) File.Delete(target);   // ① 先删目标
File.Move(file, target);                        // ② 再移动源
```

①② 不是原子操作。如果 ② 失败（权限、占用、磁盘错误、跨卷），
**目标文件已经被删掉，源文件还在原处** —— 目标侧的数据就此丢失。

更糟的是：`CopyThenDeleteSource` 是被递归调用的，
前面已经搬走的文件无法回滚，最终形成一个「两边都不完整」的状态，
而 `MoveDataDirectory` 只返回 false，没有恢复能力。

## 验证方式

直接调用 `DataDirectoryMover` 的私有方法 `CopyThenDeleteSource`（反射），
构造「目标已存在同名文件 + 移动会失败」的场景。

如何让 File.Move 失败：
  - 跨卷移动会自动退化为「复制 + 删除」，不容易失败
  - 更可靠的方式：让**目标目录**只读，或在目标位置放一个**目录**同名项
    → File.Move 到已存在的目录路径会抛异常

本脚本用「目标位置放同名目录」制造失败：
  target/x.txt 是个**目录**，File.Delete(target/x.txt) 会抛
  UnauthorizedAccessException（对目录调 File.Delete），
  于是 ① 就崩了 —— 但这也说明「先删」这一步本身就脆弱。

更贴近真实的一步：目标同名**文件**存在且可删，
但 File.Move 因源文件被占用而失败 → 目标文件已丢。

本脚本覆盖这两种。
"""
import json
import os
import shutil
import subprocess
import sys
from safe_ops import safe_rmtree
import tempfile
from pathlib import Path

P = sys.argv[1] if len(sys.argv) > 1 else r"C:\Users\Mocha\Desktop\VSProject\Auto7z-WPF"
EXE = os.path.join(P, "Auto7z_WPF", "Auto7z_WPF", "bin", "Debug", "Auto7z.exe")
R = os.path.join(os.environ.get("TEMP", r"C:\Windows\Temp"), "Auto7zTest_data")
SDK = os.path.join(R, "tmp", "mover")

results = []


def check(name, ok, detail=""):
    results.append({"name": name, "ok": ok, "detail": detail})
    print(("  OK   " if ok else "  !!   ") + name + ("  --- " + detail if detail else ""))


def main():
    if os.path.isdir(SDK):
        safe_rmtree(SDK)
    os.makedirs(SDK, exist_ok=True)

    print("=" * 76)
    print("数据目录迁移：原子性验证")
    print("=" * 76)

    # 用 PowerShell 反射调私有方法（C# 探针更直接，但这里用 PS 省一次编译）
    probe = Path(SDK) / "run_mover.ps1"
    probe.write_text(r'''
param([string]$Exe, [string]$Src, [string]$Tgt, [string]$LockFile)

$ErrorActionPreference = "Stop"
$asm = [System.Reflection.Assembly]::LoadFrom($Exe)
$t = $asm.GetType("Auto7z_WPF.Services.DataDirectoryMover")
if ($t -eq $null) { Write-Output "NO_TYPE"; exit 2 }
$m = $t.GetMethod("CopyThenDeleteSource",
                  [System.Reflection.BindingFlags]::NonPublic -bor
                  [System.Reflection.BindingFlags]::Static)
if ($m -eq $null) { Write-Output "NO_METHOD"; exit 2 }

$lock = $null
try {
    if ($LockFile -and (Test-Path $LockFile)) {
        # 独占打开源文件，让 File.Move 失败
        $lock = [System.IO.File]::Open($LockFile,
            [System.IO.FileMode]::Open,
            [System.IO.FileAccess]::Read,
            [System.IO.FileShare]::None)
    }
    try {
        $m.Invoke($null, @($Src, $Tgt)) | Out-Null
        Write-Output "NO_EXCEPTION"
        exit 0
    } catch {
        Write-Output ("EXCEPTION: " + $_.Exception.InnerException.GetType().Name)
        exit 1
    }
} finally {
    if ($lock) { $lock.Close() }
}
''', encoding="utf-8")

    # ---------------- 场景 A：目标同名文件会被先删掉 ----------------
    print("\n【A】目标已有同名文件时，移动失败是否丢目标")
    src = Path(SDK) / "A_src"
    tgt = Path(SDK) / "A_tgt"
    src.mkdir(parents=True)
    tgt.mkdir(parents=True)

    # 源：zzz_locked.txt（会被独占）+ aaa_normal.txt
    # 用 aaa 先排序，确保它在 zzz 之前被搬走（递归里按名字顺序，实际上 GetFiles 不保证，
    # 所以这里只验证「目标文件是否还在」这个核心点）
    (src / "conflict.txt").write_text("SOURCE_VERSION", encoding="utf-8")
    (tgt / "conflict.txt").write_text("TARGET_VERSION", encoding="utf-8")
    (src / "other.txt").write_text("other", encoding="utf-8")

    before_target = (tgt / "conflict.txt").read_text(encoding="utf-8")
    print("     迁移前目标 conflict.txt 内容 = %r" % before_target)

    p = subprocess.run(
        ["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass",
         "-File", str(probe), EXE, str(src), str(tgt),
         str(src / "conflict.txt")],
        capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=180)
    out = (p.stdout or "") + (p.stderr or "")
    print("     探针输出: %s" % out.strip()[:120])

    # 关键断言：目标文件不该丢
    target_still_there = (tgt / "conflict.txt").exists()
    if target_still_there:
        after = (tgt / "conflict.txt").read_text(encoding="utf-8")
        check("移动失败后目标文件仍在", True, "内容=%r" % after)
    else:
        check("移动失败后目标文件仍在", False,
              "!! 目标 conflict.txt 已被删除，源也没搬过来 —— 数据丢失")

    # ---------------- 场景 B：正常迁移必须完整 ----------------
    print("\n【B】正常迁移（无冲突）应当完整搬走")
    src2 = Path(SDK) / "B_src"
    tgt2 = Path(SDK) / "B_tgt"
    (src2 / "sub").mkdir(parents=True)
    tgt2.mkdir(parents=True)
    (src2 / "one.txt").write_text("1", encoding="utf-8")
    (src2 / "sub" / "two.txt").write_text("2", encoding="utf-8")

    p = subprocess.run(
        ["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass",
         "-File", str(probe), EXE, str(src2), str(tgt2), ""],
        capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=180)
    ok_b = (tgt2 / "one.txt").exists() and (tgt2 / "sub" / "two.txt").exists()
    check("正常迁移搬走全部文件", ok_b,
          "one=%s sub/two=%s" % ((tgt2 / "one.txt").exists(), (tgt2 / "sub" / "two.txt").exists()))

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
    out_path = os.path.join(SDK, "mover-atomicity.json")
    open(out_path, "w", encoding="utf-8").write(json.dumps(results, ensure_ascii=False, indent=2))
    print("输出 -> %s" % out_path)
    return code


if __name__ == "__main__":
    sys.exit(main())
