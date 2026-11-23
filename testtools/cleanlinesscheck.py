#!/usr/bin/env python3
# -*- coding: utf-8 -*-
r"""代码清洁度守卫：把「清洁」从一次性动作变成可回归的约束。

只守**能机械判定、且违反了一定更差**的四项：

K1 连续空行超过 2 个      —— 删代码留下的痕迹
K2 行尾多余空白            —— 无意义 diff 噪声
K3 Tab 与空格混用          —— 同一文件内不一致
K4 大括号风格混用          —— K&R 与 Allman 混在同一个文件

不守的（并且说明为什么）：
  - **长注释块**：本项目注释密度高是刻意的，用来记录决策理由
    （例如「为什么必须在这里而不是 OnClosing」「为什么用 HKLM 而不是 HKCR」）。
    把这些压掉等于丢掉了最有价值的部分。当前有 28 处超过 12 行的注释块，全部保留。
  - **超长行**：只剩 1 处，在 `Properties/Resources.Designer.cs`（自动生成，不该手改）。
  - **重复代码**：已由 `archcheck.py` 的 A1–A6 专门管，不在这里重复。

判据细节（都是踩过坑才写对的）：
  K4 必须用 `[ \t]` 而不是 `\s` —— `\s` 含换行，会让 Allman 的 ") \n {"
  也被 K&R 的 ")\s*{" 匹配到，两个计数几乎相等，40 个文件全被误报成混用。
"""
import io
import os
import re
import sys

P = sys.argv[1] if len(sys.argv) > 1 else r"C:\Users\Mocha\Desktop\VSProject\Auto7z-WPF"
SRC = os.path.join(P, "Auto7z_WPF", "Auto7z_WPF") if os.path.isdir(
    os.path.join(P, "Auto7z_WPF", "Auto7z_WPF")) else P
EXCLUDE = ("obj", "objgui", "bin", ".git")

# 自动生成文件不参与风格检查
GENERATED = {"Resources.Designer.cs", "Settings.Designer.cs", "AssemblyInfo.cs"}

problems = []


def fail(code, msg):
    problems.append((code, msg))
    print("  !!   " + msg)


def ok(msg):
    print("  OK   " + msg)


def walk():
    out = []
    for root, dirs, files in os.walk(SRC):
        dirs[:] = [d for d in dirs if d not in EXCLUDE]
        for n in files:
            if n.endswith(".cs") and n not in GENERATED:
                out.append(os.path.join(root, n))
    return out


def rel(p):
    return os.path.relpath(p, SRC).replace("\\", "/")


files = walk()
print("=" * 74)
print("代码清洁度守卫")
print("=" * 74)
print("参与检查 %d 个 .cs（排除 %d 个自动生成文件）" % (len(files), len(GENERATED)))
print()

# ---------------------------------------------------------------- K1 连续空行
print("【K1】连续空行不超过 2 个")
bad = []
for f in files:
    lines = io.open(f, encoding="utf-8", errors="replace").read().split("\n")
    run = 0
    for i, l in enumerate(lines):
        if not l.strip():
            run += 1
        else:
            if run > 2:
                bad.append("%s:%d (%d 空行)" % (rel(f), i - run + 1, run))
            run = 0
if bad:
    fail("K1", "%d 处：%s" % (len(bad), ", ".join(bad[:4])))
else:
    ok("无超过 2 个的连续空行")

# ---------------------------------------------------------------- K2 行尾空白
print("\n【K2】行尾无多余空白")
bad = []
for f in files:
    lines = io.open(f, encoding="utf-8", errors="replace").read().replace("\r", "").split("\n")
    for i, l in enumerate(lines):
        if l != l.rstrip() and l.strip():
            bad.append("%s:%d" % (rel(f), i + 1))
if bad:
    fail("K2", "%d 处：%s" % (len(bad), ", ".join(bad[:4])))
else:
    ok("无行尾多余空白")

# ---------------------------------------------------------------- K3 Tab 混用
print("\n【K3】不使用 Tab")
bad = []
for f in files:
    text = io.open(f, encoding="utf-8", errors="replace").read()
    if "\t" in text:
        n = text.count("\t")
        bad.append("%s (%d 个 Tab)" % (rel(f), n))
if bad:
    fail("K3", "%d 个文件含 Tab：%s" % (len(bad), ", ".join(bad[:4])))
else:
    ok("全部文件只用空格缩进")

# ---------------------------------------------------------------- K4 大括号风格
print("\n【K4】大括号风格统一（本项目统一用 Allman）")
bad = []
for f in files:
    text = io.open(f, encoding="utf-8", errors="replace").read()
    knr = len(re.findall(r"\)[ \t]*\{", text))
    allman = len(re.findall(r"\)[ \t]*\r?\n[ \t]*\{", text))
    if knr > 3 and allman > 3:
        bad.append("%s (K&R %d / Allman %d)" % (rel(f), knr, allman))
if bad:
    fail("K4", "%d 个文件风格混用：%s" % (len(bad), ", ".join(bad[:4])))
else:
    ok("所有文件风格一致")

# ---------------------------------------------------------------- 说明保留项
print("")
print("-" * 74)
print("刻意保留、不做清理的：")
longc = 0
for f in files:
    lines = io.open(f, encoding="utf-8", errors="replace").read().splitlines()
    run = 0
    for l in lines:
        if l.strip().startswith("//"):
            run += 1
        else:
            if run > 12:
                longc += 1
            run = 0
print("  长注释块 %d 处 —— 记录决策理由，是资产不是负债" % longc)
print("  （若把它们压成一行，下次改代码的人就得重新踩一遍同样的坑）")

# ---------------------------------------------------------------- 汇总
print("\n" + "=" * 74)
if problems:
    print("发现 %d 项清洁度问题：" % len(problems))
    for c, m in problems:
        print("  [%s] %s" % (c, m))
    print("FAIL")
    sys.exit(1)

print("空行/空白/Tab/大括号风格全部合规")
print("PASS")
sys.exit(0)
