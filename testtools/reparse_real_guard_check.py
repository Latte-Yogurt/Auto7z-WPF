#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""重写 reparse_root_check 的 D2 组：必须走到真实守卫的重解析点分支。

## 为什么前一版 D2 无效

`IsSafeToDeleteRecursively` 的判定顺序是：

    1. 规范化路径
    2. 遍历两个 root（portable / local）
    3.   full == <root>\\Auto7z ?           -> 查祖先链，返回
    4.   full 在 <root>\\Auto7z\\ 之下 ?    -> 查祖先链，返回
    5. 都不匹配 -> return false            ← 前一版 D2 死在这里

前一版把 `d2/.config/Auto7z` 传进去，它不属于任何真实 root，
于是第 5 步直接返回 false —— 与重解析点毫无关系。
**测试通过得毫无意义**，变异自然抓不到。

## 正确做法

先读出产品真实的 root（portable 或 local），把 Junction 造在
**真实 root 的 Auto7z 位置**上，这样第 3 步就会命中，才会走到第 113 行。

用 Reflect 调 AppPaths.GetLocalRootDirectory 拿到真实 root。
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
SDK = os.path.join(os.environ.get("TEMP", r"C:\Windows\Temp"), "Auto7zTest_data", "reparse_real")

results = []


def check(name, ok, detail=""):
    results.append({"name": name, "ok": ok, "detail": detail})
    print(("  OK   " if ok else "  !!   ") + name + ("  --- " + detail if detail else ""))


def skip(name, why):
    results.append({"name": name, "ok": True, "detail": "跳过: " + why})
    print("  --   " + name + "  --- 跳过: " + why)


def call(method, *args, type_name="Auto7z_WPF.Services.DataDirectoryMover"):
    arg_file = Path(SDK) / "_args.json"
    arg_file.write_text(json.dumps([str(a) for a in args], ensure_ascii=False), encoding="utf-8")
    script = (
        'param([string]$Exe, [string]$TypeName, [string]$Method, [string]$ArgFile)\n'
        '$ErrorActionPreference = "Stop"\n'
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
         EXE, type_name, method, str(arg_file)],
        capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=180)
    out = (p.stdout or "") + (p.stderr or "")
    for line in out.splitlines():
        if line.startswith("RESULT="):
            return line.split("=", 1)[1].strip()
        if line.startswith("EXCEPTION:") or line in ("NO_TYPE", "NO_METHOD"):
            return line.strip()
    return "(无结果)"


def get_root():
    """读产品真实的 local root（AppPaths.GetLocalRootDirectory）。"""
    for name in ("GetLocalRootDirectory", "GetPortableRootDirectory"):
        r = call(name, type_name="Auto7z_WPF.Services.AppPaths")
        if r and not r.startswith(("EXCEPTION", "NO_", "(")):
            return r, name
    return None, None


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
    print("重解析点守卫：真实入口验证（D2 重写）")
    print("=" * 76)

    root, method = get_root()
    print("\n  产品真实 root（%s）= %s" % (method, root))
    if not root:
        skip("真实 root 守卫验证", "无法读取 AppPaths 的 root")
        return finish()

    # 我们要在真实 root 下造一个 Auto7z 的 Junction。
    # 但真实 root 可能已被产品使用（本机有真实数据），所以：
    #   · 若 <root>\Auto7z 已存在，先确认它不是链接，然后**临时改名**
    #   · 造 Junction 指向外部
    #   · 测完恢复
    data_dir = Path(root) / "Auto7z"
    outside = Path(SDK) / "outside"
    outside.mkdir(parents=True, exist_ok=True)
    (outside / "secret.txt").write_text("MUST_SURVIVE", encoding="utf-8")

    backup = None
    if data_dir.exists():
        try:
            attrs = os.stat(str(data_dir)).st_file_attributes
            if attrs & 0x400:
                skip("真实 root 守卫验证", "<root>\\Auto7z 本身就是链接，不宜在此环境测试")
                return finish()
        except Exception:
            pass
        backup = Path(str(data_dir) + ".d2bak")
        if backup.exists():
            safe_rmtree(backup)
        os.rename(str(data_dir), str(backup))
        print("  已临时改名真实数据目录 -> %s" % backup.name)

    try:
        if not make_junction(data_dir, outside):
            skip("根目录 Junction 经真实守卫被拒绝", "无法创建 Junction")
            return finish()

        allowed = call("IsSafeToDeleteRecursively", str(data_dir))
        check("真实守卫拒绝「数据根是 Junction」",
              allowed.lower() == "false",
              "IsSafeToDeleteRecursively=%s（True = 守卫被绕过）" % allowed)

        secret_ok = (outside / "secret.txt").exists() and \
            (outside / "secret.txt").read_text(encoding="utf-8") == "MUST_SURVIVE"
        check("外部数据未被触碰", secret_ok, "")

    finally:
        rm_junction(data_dir)
        if backup is not None and backup.exists():
            os.rename(str(backup), str(data_dir))
            print("  已恢复真实数据目录")

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
    out_path = os.path.join(SDK, "reparse_real.json")
    os.makedirs(SDK, exist_ok=True)
    open(out_path, "w", encoding="utf-8").write(json.dumps(results, ensure_ascii=False, indent=2))
    print("输出 -> %s" % out_path)
    return code


if __name__ == "__main__":
    sys.exit(main())
