#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""重解析点守卫：**父目录 Junction** 的真实入口验证（BUG-01 的核心）。

## 为什么需要这一版

前面的变异测试暴露了两个「测试设计无效」的问题：

1. **D2 无效**：把 `d2/.config/Auto7z` 传给守卫，它不是任何真实 root 下的路径，
   判定在第 5 步（都不匹配 → return false）就结束了，**根本没走到重解析点分支**。
   测试通过得毫无意义。

2. **根目录 Junction 无法区分两种实现**：当 `<root>/Auto7z` 自身是 Junction 时，
   `ExistsAsReparsePoint(full)` 和 `HasReparsePointAncestor(full)` **都返回 true**。
   所以拿它做变异测试，改回旧实现也照样通过。

## BUG-01 真正的差异点

GPT6 说的是「检查完整路径的**每一个现存祖先目录**」。
差异只在**父目录是 Junction、而数据根自身不是**时才显现：

    <root>  ->  Junction  ->  D:\\outside
      └ Auto7z            ← 自身是普通目录（Directory 属性，无 ReparsePoint 位）

此时：
    ExistsAsReparsePoint(<root>/Auto7z)        => false   ← 旧实现放行（BUG）
    HasReparsePointAncestor(<root>/Auto7z)     => true    ← 新实现拒绝

用 `AUTO7Z_DATA_ROOT` 把 root 指到受控目录，就能精确构造这个场景，
并让守卫在前面几步正常通过、走到祖先链检查。
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
SDK = os.path.join(os.environ.get("TEMP", r"C:\Windows\Temp"), "Auto7zTest_data", "parentjunc")

results = []


def check(name, ok, detail=""):
    results.append({"name": name, "ok": ok, "detail": detail})
    print(("  OK   " if ok else "  !!   ") + name + ("  --- " + detail if detail else ""))


def skip(name, why):
    results.append({"name": name, "ok": True, "detail": "跳过: " + why})
    print("  --   " + name + "  --- 跳过: " + why)


def call(method, *args, type_name="Auto7z_WPF.Services.DataDirectoryMover", data_root=None):
    arg_file = Path(SDK) / "_args.json"
    arg_file.write_text(json.dumps([str(a) for a in args], ensure_ascii=False), encoding="utf-8")
    script = (
        'param([string]$Exe, [string]$TypeName, [string]$Method, [string]$ArgFile, [string]$Root)\n'
        '$ErrorActionPreference = "Stop"\n'
        'if ($Root -ne "") { $env:AUTO7Z_DATA_ROOT = $Root }\n'
        '$asm = [System.Reflection.Assembly]::LoadFrom($Exe)\n'
        '$t = $asm.GetType($TypeName)\n'
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
        'try { Write-Output ("RESULT=" + $m.Invoke($null, $arr)) }\n'
        'catch {\n'
        '    $inner = $_.Exception.InnerException\n'
        '    if ($inner -eq $null) { $inner = $_.Exception }\n'
        '    Write-Output ("EXCEPTION: " + $inner.GetType().Name)\n'
        '}\n')
    f = Path(SDK) / "_invoke.ps1"
    f.write_text(script, encoding="utf-8")
    p = subprocess.run(
        ["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", str(f),
         EXE, type_name, method, str(arg_file), data_root or ""],
        capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=180)
    out = (p.stdout or "") + (p.stderr or "")
    for line in out.splitlines():
        if line.startswith("RESULT="):
            return line.split("=", 1)[1].strip()
        if line.startswith("EXCEPTION:") or line in ("NO_TYPE", "NO_METHOD"):
            return line.strip()
    return "(无结果)"


def make_junction(link, target):
    subprocess.run(["cmd", "/c", "mklink", "/J", str(link), str(target)],
                   capture_output=True, text=True, encoding="utf-8", errors="replace")
    return Path(link).exists()


def rm_junction(link):
    if Path(link).exists():
        subprocess.run(["cmd", "/c", "rmdir", str(link)], capture_output=True)


def main():
    if os.path.isdir(SDK):
        safe_rmtree(SDK)
    os.makedirs(SDK, exist_ok=True)

    print("=" * 76)
    print("重解析点守卫：父目录 Junction（BUG-01 核心差异点）")
    print("=" * 76)

    # 布局：
    #   SDK/root_holder/Auto7z            <- 数据根（普通目录）
    #   SDK/root_holder  -> Junction -> SDK/real_root
    #   SDK/real_root/Auto7z              <- 实际位置（外部）
    #
    # AUTO7Z_DATA_ROOT 指向 SDK/root_holder，于是 root = SDK/root_holder，
    # 数据根 = SDK/root_holder/Auto7z，它自身是普通目录，但父目录是 Junction。
    holder = Path(SDK) / "root_holder"
    real_root = Path(SDK) / "real_root"
    real_root.mkdir(parents=True, exist_ok=True)
    (real_root / "Auto7z").mkdir(exist_ok=True)
    (real_root / "Auto7z" / "secret.txt").write_text("MUST_SURVIVE", encoding="utf-8")

    if not make_junction(holder, real_root):
        skip("父目录 Junction 被真实守卫拒绝", "无法创建 Junction")
        return finish()

    try:
        # 数据根路径（字符串上在 holder 下，实际在 real_root 下）
        data_dir = holder / "Auto7z"

        # --- 前置确认：数据根自身不是链接，父目录才是 ---
        try:
            attrs = os.lstat(str(data_dir)).st_file_attributes
            self_is_link = bool(attrs & 0x400)
        except Exception as e:
            self_is_link = None
            print("     stat 失败: %s" % e)

        check("前置：数据根自身不是链接（父目录才是）", self_is_link is False,
              "自身 ReparsePoint=%s" % self_is_link)

        parent_is_link = bool(os.lstat(str(holder)).st_file_attributes & 0x400)
        check("前置：父目录确实是 Junction", parent_is_link, "父目录 ReparsePoint=%s" % parent_is_link)

        # --- 关键断言：守卫必须拒绝 ---
        allowed = call("IsSafeToDeleteRecursively", str(data_dir), data_root=str(holder))
        check("真实守卫拒绝「父目录是 Junction」的数据根",
              allowed.lower() == "false",
              "IsSafeToDeleteRecursively=%s（True = BUG-01 未修复）" % allowed)

        # --- 对照：同一实现下，纯普通路径必须放行（证明不是无脑拒绝） ---
        plain_holder = Path(SDK) / "plain_holder"
        (plain_holder / "Auto7z").mkdir(parents=True, exist_ok=True)
        allowed_plain = call("IsSafeToDeleteRecursively",
                             str(plain_holder / "Auto7z"), data_root=str(plain_holder))
        check("对照：普通路径被放行（不误报）",
              allowed_plain.lower() == "true",
              "IsSafeToDeleteRecursively=%s" % allowed_plain)

        # --- 外部数据未被动 ---
        secret_ok = ((real_root / "Auto7z" / "secret.txt").exists()
                     and (real_root / "Auto7z" / "secret.txt").read_text(encoding="utf-8") == "MUST_SURVIVE")
        check("外部数据未被触碰", secret_ok, "")

    finally:
        rm_junction(holder)

    return finish()


def finish():
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
    os.makedirs(SDK, exist_ok=True)
    out_path = os.path.join(SDK, "parentjunc.json")
    open(out_path, "w", encoding="utf-8").write(json.dumps(results, ensure_ascii=False, indent=2))
    print("输出 -> %s" % out_path)
    return code


if __name__ == "__main__":
    sys.exit(main())
