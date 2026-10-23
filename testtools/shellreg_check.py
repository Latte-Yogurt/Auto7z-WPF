#!/usr/bin/env python3
# -*- coding: utf-8 -*-
r"""外壳扩展注册/注销的注册表契约检查（B02 的回归保护）。

背景
----
注册表里 Approved 列表要写在 HKEY_LOCAL_MACHINE ——
写在 HKEY_CURRENT_USER 下资源管理器不认，扩展会被静默禁用。
但 DllUnregisterServer 原来删的是 HKEY_CURRENT_USER 那一份，
跟注册时写入的键对不上，于是 HKLM 下那份永远留着。

这个脚本检查「注销代码删的键」跟「注册代码写的键」是不是同一个。
只看最终状态不够 —— 残留的 Approved 值可能在跑测试之前就已经在了
（本机跑前快照里就有），那是历史遗留，不是本轮代码造成的。

用法:
    python shellreg_check.py                      # 查默认工程
    python shellreg_check.py "<工程根目录>"        # 查任意一份（变异测试用）

退出码：0 = 契约成立；1 = 不成立。
"""
import re
import sys
from pathlib import Path

DEFAULT_ROOT = Path(r"C:\Users\Mocha\Desktop\VSProject\Auto7z Project")

if len(sys.argv) > 1:
    ROOT = Path(sys.argv[1])
else:
    ROOT = DEFAULT_ROOT

CPP = ROOT / "Auto7zShell" / "src" / "ShellExtension.cpp"

# 注册时写的 Approved 路径（源码里的字面量）
APPROVED_SUBPATH = r"SOFTWARE\Microsoft\Windows\CurrentVersion\Shell Extensions\Approved"


def read(p):
    return p.read_text(encoding="utf-8", errors="replace")


def main():
    if not CPP.exists():
        print("找不到 ShellExtension.cpp: %s" % CPP)
        return 2

    text = read(CPP)
    failures = []

    print("=" * 74)
    print("外壳扩展注册表契约检查")
    print("源码: %s" % CPP)
    print("=" * 74)

    # ---- 1) 注册点：必须写 HKEY_LOCAL_MACHINE ----
    # 源码里的路径是宽字符串字面量，反斜杠写成了 \\，正则要按这个形态匹配。
    m = re.search(
        r"WriteStringValue\(\s*HKEY_(LOCAL_MACHINE|CURRENT_USER)\s*,\s*"
        r'L"([^"]*Shell Extensions\\\\Approved)"',
        text)
    if not m:
        print("  !! RC1  找不到 Approved 的注册写入点")
        failures.append("RC1 注册写入点缺失")
    else:
        hive, sub = m.group(1), m.group(2)
        print("  ✓  RC1  注册写入 Approved：HKEY_%s" % hive)
        print("        路径: %s" % sub)
        if hive != "LOCAL_MACHINE":
            print("  !! RC1  注册写的是 HKEY_%s —— 资源管理器不认这一份，扩展会被静默禁用" % hive)
            failures.append("RC1 注册写错 hive")
        reg_sub = sub

    # ---- 2) 注销点：必须删同一个 hive、同一段路径 ----
    m2 = re.search(
        r"DeleteRegValue\(\s*HKEY_(LOCAL_MACHINE|CURRENT_USER)\s*,\s*"
        r'L"([^"]*Shell Extensions\\\\Approved)"',
        text)
    if not m2:
        print("  !! RC2  找不到 Approved 的注销删除点")
        failures.append("RC2 注销删除点缺失")
    else:
        hive2, sub2 = m2.group(1), m2.group(2)
        print("  ✓  RC2  注销删除 Approved：HKEY_%s" % hive2)
        print("        路径: %s" % sub2)
        if m and hive2 != m.group(1):
            print("  !! RC2  注销删的是 HKEY_%s，注册写的是 HKEY_%s —— 两边对不上，"
                  "注册时写的那份永远删不掉" % (hive2, m.group(1)))
            failures.append("RC2 注销与注册的 hive 不一致")
        if m and sub2 != reg_sub:
            print("  !! RC2  注销的路径与注册的路径不同")
            failures.append("RC2 注销与注册的路径不一致")

    # ---- 3) 注销点不能出现在注册点之前（顺序检查）----
    if m and m2:
        if m.start() > m2.start():
            print("  !! RC3  Approved 的写入点在删除点之后（源码顺序异常）")
            failures.append("RC3 写入点/删除点顺序异常")
        else:
            print("  ✓  RC3  Approved 的写入点在删除点之前（注册在前、注销在后）")

    # ---- 4) Approved 的子路径大小写与形式 ----
    # 源码里写的是 C++ 字面量，反斜杠是双写的；比较时要还原成真实路径。
    if m:
        want = r"SOFTWARE\Microsoft\Windows\CurrentVersion\Shell Extensions\Approved"
        got = m.group(2).replace("\\\\", "\\")
        if got != want:
            print("  !! RC4  Approved 路径与预期不一致")
            print("        预期: %s" % want)
            print("        实际: %s" % got)
            failures.append("RC4 Approved 路径不符")
        else:
            print("  ✓  RC4  Approved 路径写法与约定一致")

    print()
    if failures:
        print("契约不成立 %d 项 —— B02 没有回归保护" % len(failures))
        for f in failures:
            print("  !! %s" % f)
        return 1
    print("注册/注销的 Approved 契约成立 ✓")
    print("B02 有回归保护")
    return 0


if __name__ == "__main__":
    sys.exit(main())
