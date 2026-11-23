#!/usr/bin/env python3
# -*- coding: utf-8 -*-
r"""变异测试夹具：把「变异 -> 构建 -> 检查器」三步做成一条不会骗人的流水线。

## 为什么需要这个夹具（本轮第二次踩同一个坑）

我在做 BUG-04 的变异测试时手写成这样：

    改源码 -> 构建 -> 跑检查器

构建其实**失败了**（CS1010 常量中有换行符），但我没看构建返回码，
检查器对着**上一轮的旧二进制**跑出 PASS。差一点就得寸进尺地宣布
「变异没被抓住 → 检查器无效」，而真相是检查器根本没跑被测代码。

上一轮的 `fake-green-incident.md` 记的正是这类事故，这次换了张脸又来一次。
所以把它变成工具：**构建失败 = 整次变异无效**，直接判错，不给「用旧产物蒙混」留缝。

用法
----
    python mutation_runner.py --patch <patch.json>

patch.json:
    {
      "name": "bug04-去掉发布前复查",
      "file": "Auto7z_WPF/Auto7z_WPF/Services/SingleInstanceService.cs",
      "find": "精确子串",
      "replace": "替换成",
      "config": "Debug",
      "checkers": [["lifecycle_race_check.py"], ["shutdown_check.py"]],
      "expect": "fail"          # 变异后检查器应当 FAIL
    }

判据（三者全中才算变异有效）：
    1. 变异确实改变了文件内容（find 子串存在、替换后内容不同）
    2. 构建**成功**（返回码 0，且输出里没有 error）
    3. 各检查器的实际结果与 expect 一致

任何一条不满足 → 退出码 1，并在报告里写清是哪一条。
"""
import json
import os
import subprocess
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
PROJECT = HERE.parent

MSBUILD = r"C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe"
CSPROJ = PROJECT / "Auto7z_WPF" / "Auto7z_WPF" / "Auto7z_WPF.csproj"
GUIPROJ = PROJECT / "Auto7z_WPF" / "Auto7z_WPF" / "Auto7z_WPF.Gui.csproj"
SHELL_TOOL = PROJECT / "Auto7zShell" / "buildtool" / "buildtool.exe"
SHELL_DIR = PROJECT / "Auto7zShell"
VCVARS = r"C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat"


def build_shell():
    """构建 C++ 外壳扩展。返回 (ok, 摘要)。

    单独一支是因为它不走 MSBuild —— 走的是项目自带的 buildtool.exe。
    变异测试必须能覆盖它，否则 BUG-05 这类「改成动态缓冲」的修补
    就完全落在变异测试的覆盖范围之外，那正是最需要变异测试的地方。
    """
    if not SHELL_TOOL.is_file():
        return False, "找不到 buildtool.exe: %s" % SHELL_TOOL

    r = subprocess.run([str(SHELL_TOOL), "--vcvars", VCVARS], cwd=str(SHELL_DIR),
                       capture_output=True, text=True, encoding="utf-8", errors="replace")
    out = (r.stdout or "") + (r.stderr or "")
    low = out.lower()
    # cl 的失败形态：返回码非 0，或输出里有 "error C"。
    if r.returncode != 0 or "error c" in low:
        return False, "外壳构建失败 rc=%s\n%s" % (r.returncode, out.strip()[-1500:])
    return True, "外壳构建成功"


def build(config):
    """构建并**严格判定成功**。返回 (ok, 摘要)。

    只信返回码是不够的：MSBuild 在部分错误下仍可能返回 0，
    而 /v:minimal 里的 "error CS" 才是真信号。两个都看。
    """
    procs = [CSPROJ]
    if config == "Debug":
        procs.append(GUIPROJ)

    for proj in procs:
        r = subprocess.run([MSBUILD, str(proj), "/t:Build",
                            "/p:Configuration=" + config, "/v:minimal", "/nologo"],
                           capture_output=True, text=True, encoding="utf-8", errors="replace")
        out = (r.stdout or "") + (r.stderr or "")
        low = out.lower()
        if r.returncode != 0 or "error cs" in low or "error msb" in low:
            return False, "构建失败 rc=%s\n%s" % (r.returncode, out.strip()[-1500:])

    # 目标文件在 C++ 侧时，也要把外壳重新构建一遍。
    return True, "构建成功（0 错）"


def main():
    if len(sys.argv) < 3 or sys.argv[1] != "--patch":
        print("用法: python mutation_runner.py --patch <patch.json>")
        return 2

    spec = json.loads(Path(sys.argv[2]).read_text(encoding="utf-8"))
    name = spec.get("name", "未命名变异")
    target = PROJECT / spec["file"]
    config = spec.get("config", "Debug")
    expect = spec.get("expect", "fail")

    print("=" * 76)
    print("变异测试: %s" % name)
    print("=" * 76)

    if not target.is_file():
        print("  !! 目标文件不存在: %s" % target)
        return 1

    original = target.read_text(encoding="utf-8")
    find = spec["find"]
    replace = spec["replace"]

    if find not in original:
        print("  !! 变异点未命中：find 子串在源文件里找不到。")
        print("     这说明源码已被改动（或补丁过期），变异无效 —— 不能当作「没抓到」。")
        return 1

    mutated = original.replace(find, replace, 1)
    if mutated == original:
        print("  !! 替换后内容没有变化，变异无效。")
        return 1

    problems = []
    is_shell = target.suffix.lower() in (".cpp", ".h", ".rc", ".def")
    try:
        target.write_text(mutated, encoding="utf-8")
        print("  .. 已应用变异")

        # C++ 目标要额外构建外壳扩展；C# 目标走 MSBuild。
        if is_shell:
            ok_shell, shell_detail = build_shell()
            print("  " + ("OK  " if ok_shell else "!!  ") + shell_detail.splitlines()[0])
            ok_build, build_detail = (True, "C# 侧未改动") if ok_shell \
                else (False, shell_detail)
            if not ok_shell:
                problems.append("外壳构建失败，变异测试不成立（检查器没机会跑被测代码）")
                print(shell_detail)
        else:
            ok_build, build_detail = build(config)
            print("  " + ("OK  " if ok_build else "!!  ") + build_detail.splitlines()[0])
        if not ok_build:
            problems.append("构建失败，变异测试不成立（检查器没机会跑被测代码）")
            print(build_detail)
        else:
            for cmd in spec["checkers"]:
                r = subprocess.run([sys.executable] + cmd, cwd=str(HERE),
                                   capture_output=True, text=True,
                                   encoding="utf-8", errors="replace")
                out = (r.stdout or "") + (r.stderr or "")
                verdict = "PASS" if r.returncode == 0 else "FAIL"
                print("  .. %-34s -> %s (rc=%s)" % (" ".join(cmd), verdict, r.returncode))
                if expect == "fail" and r.returncode == 0:
                    problems.append("%s 在变异后仍报 PASS —— 检查器抓不住这个缺陷"
                                    % " ".join(cmd))
                    print("     末尾输出: " + out.strip()[-600:])
                elif expect == "pass" and r.returncode != 0:
                    problems.append("%s 在变异后报 FAIL，但预期应为 PASS" % " ".join(cmd))
                    print("     末尾输出: " + out.strip()[-600:])
    finally:
        target.write_text(original, encoding="utf-8")
        print("  .. 已还原源码")

        ok_restore = target.read_text(encoding="utf-8") == original
        if not ok_restore:
            problems.append("源码还原失败！")
        else:
            if is_shell:
                ok_build, build_detail = build_shell()
            else:
                ok_build, build_detail = build(config)
            print("  " + ("OK  " if ok_build else "!!  ") + "还原后重建: "
                  + build_detail.splitlines()[0])
            if not ok_build:
                problems.append("还原后重建失败")

    print("\n" + "=" * 76)
    if problems:
        for p in problems:
            print("  !! " + p)
        print("变异结论: 无效 / 未按预期表现")
        print("FAIL")
        return 1

    print("变异结论: 检查器%s了这个缺陷" % ("抓住" if expect == "fail" else "正确放行"))
    print("PASS")
    return 0


if __name__ == "__main__":
    sys.exit(main())
