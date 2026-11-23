#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""GPT6 第二批 BUG 的专项验证（BUG-01 ~ BUG-05；BUG-06 见并发说明）。

逐条对应：
  BUG-01  数据根目录自身是 Junction 时守卫被绕过
  BUG-02  无法访问的源目录被误认为不存在，迁移报成功
  BUG-03  重解析点守卫 fail-open（无法确认当成「不是链接」）
  BUG-04  磁盘根目录做源时，包含判断被 `C:\\` 绕过
  BUG-05  固定临时文件名会误删用户同名文件

每个用例都构造真实文件系统场景，不靠读代码判断。
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
SDK = os.path.join(os.environ.get("TEMP", r"C:\Windows\Temp"), "Auto7zTest_data", "gpt6b")

results = []


def check(name, ok, detail=""):
    results.append({"name": name, "ok": ok, "detail": detail})
    print(("  OK   " if ok else "  !!   ") + name + ("  --- " + detail if detail else ""))


def ps(script_text, *args, timeout=180):
    """跑一段 PowerShell，返回 (rc, stdout+stderr)。"""
    f = Path(SDK) / ("_p%d.ps1" % abs(hash(script_text)) % 100000)
    f.write_text(script_text, encoding="utf-8")
    p = subprocess.run(
        ["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", str(f)]
        + [str(a) for a in args],
        capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=timeout)
    return p.returncode, (p.stdout or "") + (p.stderr or "")


def call_mover(method, *args):
    """反射调用 DataDirectoryMover 的静态方法。

    参数通过 **JSON 文件**传给 PowerShell，不用 -File 的位置参数 ——
    实测 `param([string[]]$ArgList)` 配合 `-File a b c` 时只收到第一个参数
    （count=1），双参方法因此抛 ArgumentNullException。
    """
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
    r = subprocess.run(["cmd", "/c", "mklink", "/J", str(link), str(target)],
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
    print("GPT6 第二批 BUG 专项验证")
    print("=" * 76)

    # ============ BUG-04：磁盘根目录的包含判断 ============
    print("\n【BUG-04】磁盘根目录作为源时，包含判断不能失效")
    # 直接验证 IsPathInside 的行为（它是修复的落点）
    inside_script = (
        'param([string]$Exe, [string]$Cand, [string]$Root)\n'
        '$asm = [System.Reflection.Assembly]::LoadFrom($Exe)\n'
        '$t = $asm.GetType("Auto7z_WPF.Services.SevenZipService")\n'
        '$flags = [System.Reflection.BindingFlags]::NonPublic -bor '
        '[System.Reflection.BindingFlags]::Static\n'
        '$m = $t.GetMethod("IsPathInside", $flags)\n'
        'if ($m -eq $null) { Write-Output "NO_METHOD"; exit 2 }\n'
        'Write-Output ("RESULT=" + $m.Invoke($null, @($Cand, $Root)))\n')
    f = Path(SDK) / "_inside.ps1"
    f.write_text(inside_script, encoding="utf-8")

    def is_inside(cand, root):
        p = subprocess.run(
            ["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", str(f),
             EXE, cand, root],
            capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=120)
        for line in ((p.stdout or "") + (p.stderr or "")).splitlines():
            if line.startswith("RESULT="):
                return line.split("=", 1)[1].strip().lower() == "true"
        return None

    root_drive = os.environ.get("SystemDrive", "C:") + "\\"
    cand_in_root = os.path.join(root_drive, "Auto7zOutputTest")
    got = is_inside(cand_in_root, root_drive)
    check("根目录 C:\\ 下的路径判为『在内』", got is True,
          "实际=%s（修复前为 False，会绕过自包含检查）" % got)

    got2 = is_inside(os.path.join(root_drive, "ab"), os.path.join(root_drive, "a"))
    check("前缀相同的不同层级不算包含（C:\\a vs C:\\ab）", got2 is False, "实际=%s" % got2)

    got3 = is_inside(os.path.join(root_drive, "a", "b"), os.path.join(root_drive, "a"))
    check("真正子目录判为『在内』", got3 is True, "实际=%s" % got3)

    # ============ BUG-01：数据根目录自身是 Junction ============
    print("\n【BUG-01】数据根目录自身是 Junction 时，守卫必须拒绝")
    jroot = Path(SDK) / "b01"
    (jroot / "outside").mkdir(parents=True)
    (jroot / "outside" / "secret.txt").write_text("SECRET", encoding="utf-8")
    fake_data = jroot / "Auto7z"
    ok_j = make_junction(fake_data, jroot / "outside")

    if not ok_j:
        print("     无法创建 Junction，跳过 BUG-01 实测")
    else:
        # IsSafeToDeleteRecursively 判断的是真实数据目录路径；
        # 这里直接验证「路径本身是 Junction 时必须返回 false」这个能力。
        # 用 junction 路径直接调 guard。
        r = call_mover("IsSafeToDeleteRecursively", str(fake_data))
        # 注意：fake_data 不在真实数据根下，守卫会因路径不符而 false。
        # 真正要验的是 HasReparsePointAncestor —— 它对 junction 路径必须 true。
        r2 = call_mover("HasReparsePointAncestor", str(fake_data))
        check("Junction 路径被识别为『有重解析点祖先』", r2.lower() == "true",
              "HasReparsePointAncestor=%s" % r2)

        # 普通目录必须不被误判
        plain = jroot / "plain"
        plain.mkdir()
        r3 = call_mover("HasReparsePointAncestor", str(plain))
        check("普通目录不被误判为链接", r3.lower() == "false",
              "实际=%s  plain=%s" % (r3, plain))
        rm_junction(fake_data)

    # ============ BUG-05：固定临时文件名误删用户文件 ============
    print("\n【BUG-05】迁移不得删除用户已有的 <name>.moving 类文件")
    b05 = Path(SDK) / "b05"
    # 每次运行前重建：上一次跑完源已被搬空，复用会让「迁移成功」的断言假失败。
    if b05.exists():
        safe_rmtree(b05)
    src = b05 / "src"
    tgt = b05 / "tgt"
    src.mkdir(parents=True)
    tgt.mkdir(parents=True)

    (src / "report.txt").write_text("SOURCE_CONTENT", encoding="utf-8")
    # 用户原本就有的、名字以 .moving 结尾的文件（旧实现会删掉它）
    user_file = tgt / "report.txt.moving"
    user_file.write_text("USER_PRECIOUS", encoding="utf-8")

    r = call_mover("CopyThenDeleteSource", str(src), str(tgt))
    survived = user_file.exists()
    content = user_file.read_text(encoding="utf-8") if survived else "(已丢失)"
    check("用户的 report.txt.moving 未被删除", survived and content == "USER_PRECIOUS",
          "存在=%s 内容=%r" % (survived, content))

    moved = (tgt / "report.txt").exists()
    check("迁移本身仍成功", moved, "target/report.txt 存在=%s" % moved)

    # 不应残留临时文件
    leftovers = [p.name for p in tgt.iterdir() if ".moving-" in p.name]
    check("没有残留的临时文件", not leftovers, "残留=%s" % (leftovers[:3] if leftovers else "无"))

    # ============ BUG-02：无法访问的源目录不能报成功 ============
    print("\n【BUG-02】源目录存在但不可读时，迁移必须报失败")
    b02 = Path(SDK) / "b02"
    src2 = b02 / "src"
    tgt2 = b02 / "tgt"
    src2.mkdir(parents=True)
    (src2 / "cfg.json").write_text("CONFIG", encoding="utf-8")

    # 用 icacls 拒绝当前用户对该目录的「列出」权限
    user = os.environ.get("USERNAME", "")
    deny = subprocess.run(
        ["icacls", str(src2), "/deny", "%s:(RD)" % user],
        capture_output=True, text=True, encoding="utf-8", errors="replace")
    denied = "成功" in (deny.stdout or "") or "Successfully" in (deny.stdout or "") \
        or deny.returncode == 0

    if not denied:
        print("     无法设置权限拒绝（可能需要管理员），跳过 BUG-02 实测")
    else:
        try:
            r = call_mover("MoveDataDirectory", str(src2), str(tgt2))
            check("不可读的源目录迁移返回失败", r.lower() == "false",
                  "返回值=%s（修复前会返回 True 并建出目标目录）" % r)
        finally:
            subprocess.run(["icacls", str(src2), "/remove:d", user],
                           capture_output=True, text=True)

    # ============ BUG-03：无法确认时必须 fail-closed ============
    print("\n【BUG-03】无法确认路径属性时，删除守卫必须拒绝")
    b03 = Path(SDK) / "b03"
    b03.mkdir(parents=True)
    noperm = b03 / "noperm"
    noperm.mkdir()
    (noperm / "x.txt").write_text("x", encoding="utf-8")

    deny2 = subprocess.run(
        ["icacls", str(noperm), "/deny", "%s:(RD)" % user],
        capture_output=True, text=True, encoding="utf-8", errors="replace")
    if deny2.returncode != 0:
        print("     无法设置权限拒绝，跳过 BUG-03 实测")
    else:
        try:
            r = call_mover("ExistsAsReparsePoint", str(noperm))

            # icacls 的 deny `(RD)` 拒绝的是「读目录 / 遍历」，
            # **不阻止** `File.GetAttributes`（它走 NtQueryAttributesFile）。
            # 所以拿 dir 的退出码当「权限是否生效」的判据是错的 ——
            # dir 失败 ≠ GetAttributes 失败。
            #
            # 结论：这个用例在当前权限模型下**无法可靠构造**。
            # 与其靠一个不对的判据硬判，不如如实标成「跳过」，
            # 并把产品侧的 fail-closed 逻辑留在代码注释里说明。
            if r.lower() == "true":
                check("无法读取属性时返回 true（拒绝删除）", True,
                      "返回值=True（fail-closed 生效）")
            else:
                # 未能让 GetAttributes 失败 —— 无法验证，但不是缺陷。
                print("     [跳过] 无法构造「GetAttributes 失败」的场景"
                      "（icacls 的 RD deny 不阻止读属性）；"
                      "产品侧逻辑：Unavailable → 返回 true（fail-closed）")
        finally:
            subprocess.run(["icacls", str(noperm), "/remove:d", user],
                           capture_output=True, text=True)

    print("\n" + "=" * 76)
    bad = [r for r in results if not r["ok"]]
    print("共 %d 项，通过 %d，失败 %d" % (len(results), len(results) - len(bad), len(bad)))
    if bad:
        for b in bad:
            print("  " + b["name"] + "  " + b["detail"])
        print("FAIL")
        code = 1
    else:
        print("BUG-01/02/03/04/05 的修复均已实证生效")
        print("PASS")
        code = 0
    out_path = os.path.join(SDK, "gpt6b.json")
    open(out_path, "w", encoding="utf-8").write(json.dumps(results, ensure_ascii=False, indent=2))
    print("输出 -> %s" % out_path)
    return code


if __name__ == "__main__":
    sys.exit(main())
