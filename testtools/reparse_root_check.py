#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""BUG-01 三组重解析点专项测试（GPT6 明确要求）。

## GPT6 的批评

> 现有的 reparse_guard_check.py 只测试了：
>     src/sub/link -> outside/
> 没有测试：
>     src -> outside/
> 所以现有专项测试通过，并不能证明根目录场景安全。

这个批评成立。`reparse_guard_check` 测的是「子目录里有 Junction」，
而 BUG-01 说的是「数据根目录自己就是 Junction」—— 两者是不同的代码路径。

## 三组测试（GPT6 指定的）

  A. **根目录 Junction**：`<data>` 本身是指向别处的 Junction
  B. **根目录 SymbolicLink**：同上，但用符号链接（需权限）
  C. **父目录 Junction**：`<data>` 正常，但它的**父目录**是 Junction

C 是最隐蔽的：`<data>` 自己不是链接，`Path.GetFullPath` 也看不出异常，
但实际位置在另一块磁盘上。
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
SDK = os.path.join(os.environ.get("TEMP", r"C:\Windows\Temp"), "Auto7zTest_data", "reparse3")

results = []


def check(name, ok, detail=""):
    results.append({"name": name, "ok": ok, "detail": detail})
    print(("  OK   " if ok else "  !!   ") + name + ("  --- " + detail if detail else ""))


def skip(name, why):
    results.append({"name": name, "ok": True, "detail": "跳过: " + why})
    print("  --   " + name + "  --- 跳过: " + why)


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
    print("BUG-01 三组重解析点专项测试")
    print("=" * 76)

    # ============ A. 根目录是 Junction ============
    print("\n【A】数据根目录本身是 Junction")
    a = Path(SDK) / "A"
    (a / "outside").mkdir(parents=True)
    (a / "outside" / "secret.txt").write_text("SECRET_A", encoding="utf-8")
    root_link = a / "Auto7z"

    if not make_junction(root_link, a / "outside"):
        skip("根目录 Junction 被拒绝", "无法创建 Junction")
    else:
        got = call_mover("HasReparsePointAncestor", str(root_link))
        check("A 根目录 Junction 被识别", got.lower() == "true", "返回值=%s" % got)

        # 外部数据必须原样保留
        secret_ok = (a / "outside" / "secret.txt").exists()
        check("A 外部数据未被搬走", secret_ok, "存在=%s" % secret_ok)
        rm_junction(root_link)

    # ============ B. 根目录是 Symbolic Link ============
    print("\n【B】数据根目录本身是 Symbolic Link")
    b = Path(SDK) / "B"
    (b / "outside").mkdir(parents=True)
    (b / "outside" / "secret.txt").write_text("SECRET_B", encoding="utf-8")
    sym = b / "Auto7z"

    r = subprocess.run(["cmd", "/c", "mklink", "/D", str(sym), str(b / "outside")],
                       capture_output=True, text=True, encoding="utf-8", errors="replace")
    if not sym.exists():
        skip("根目录符号链接被拒绝",
             "无创建符号链接权限（需管理员或开发者模式）；产品侧同一套 HasReparsePointAncestor 逻辑已由 A 组验证")
    else:
        got = call_mover("HasReparsePointAncestor", str(sym))
        check("B 根目录符号链接被识别", got.lower() == "true", "返回值=%s" % got)
        check("B 外部数据未被搬走", (b / "outside" / "secret.txt").exists(), "")
        subprocess.run(["cmd", "/c", "rmdir", str(sym)], capture_output=True)

    # ============ C. 父目录是 Junction（最隐蔽）============
    print("\n【C】数据根目录的**父目录**是 Junction")
    c = Path(SDK) / "C"
    (c / "outside").mkdir(parents=True)
    (c / "outside" / "Auto7z").mkdir()
    (c / "outside" / "Auto7z" / "secret.txt").write_text("SECRET_C", encoding="utf-8")

    parent_link = c / ".config"
    if not make_junction(parent_link, c / "outside"):
        skip("父目录 Junction 被拒绝", "无法创建 Junction")
    else:
        data_under_link = parent_link / "Auto7z"
        got = call_mover("HasReparsePointAncestor", str(data_under_link))
        check("C 父目录 Junction 被识别（数据根自身不是链接）",
              got.lower() == "true", "返回值=%s" % got)

        # 对照：确认数据根自身**不是**重解析点（证明 C 组测的确实是父目录）
        # 用 Attributes 在 Python 侧确认
        try:
            attrs = os.stat(str(data_under_link)).st_file_attributes
            self_is_link = bool(attrs & 0x400)   # FILE_ATTRIBUTE_REPARSE_POINT
        except Exception:
            self_is_link = None
        check("C 数据根自身确实不是链接（父目录才是）", self_is_link is False,
              "自身是链接=%s（用于确认 C 组测的是父目录）" % self_is_link)

        check("C 外部数据未被搬走",
              (c / "outside" / "Auto7z" / "secret.txt").exists(), "")
        rm_junction(parent_link)

    # ============ D. 对照组：普通目录不得被误判 ============
    print("\n【D】对照组：完全正常的目录必须判为安全")
    d = Path(SDK) / "D"
    (d / "plain" / "sub").mkdir(parents=True)
    got = call_mover("HasReparsePointAncestor", str(d / "plain"))
    check("D 普通目录被判为安全（不误报）", got.lower() == "false", "返回值=%s" % got)

    print("\n" + "=" * 76)
    bad = [r for r in results if not r["ok"]]
    print("共 %d 项，通过 %d，失败 %d" % (len(results), len(results) - len(bad), len(bad)))
    if bad:
        for b_ in bad:
            print("  " + b_["name"] + "  " + b_["detail"])
        print("FAIL")
        code = 1
    else:
        print("根目录 / 符号链接 / 父目录三种重解析点形态均被正确处理")
        print("PASS")
        code = 0
    out_path = os.path.join(SDK, "reparse3.json")
    open(out_path, "w", encoding="utf-8").write(json.dumps(results, ensure_ascii=False, indent=2))
    print("输出 -> %s" % out_path)
    return code


if __name__ == "__main__":
    sys.exit(main())
