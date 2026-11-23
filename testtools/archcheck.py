#!/usr/bin/env python3
# -*- coding: utf-8 -*-
r"""架构约束检查：防止「同一份知识写多遍」的耦合回潮。

背景
----
2026-11-22 LTS 轮合并了三处重复，每处都是真实隐患：

1. **组件路径算了三遍**（`ComponentsPath` / `ComponentsDirectory` / `SevenZipExePath`
   各自 Path.Combine 一次）。当时算出来一样，但将来认定「组件该放别处」要改三处，
   漏一处就出现「检查的路径」与「启动的路径」不是同一个 —— 文件明明在，程序却说找不到。

2. **模式分派写了两遍**（`ConsoleTaskRunner` 与 `ShellMenuTaskRunner` 各一个 switch）。
   将来加模式开关漏改一处，就出现「命令行能用、右键菜单不认」。

3. **开关→枚举映射写了两遍且兜底方向相反**
   （`App.ParseMode` 未知→Zip vs `MainWindow.ParseHandedMode` 未知→Auto）。
   眼下不可达，但两条路径的默认值互相矛盾，是典型的埋雷。

本检查把「这些知识只能有一处」变成可判定的规则，防止后续改动又把它们复制出去。

注意边界：**不是禁止所有重复**。像 Hash 核心族那种「每个算法一个薄封装类」
是刻意的解耦（工厂模式），不能合并成巨型类 —— 那不是原子化而是「原子服务」，
用户明确反对。本检查只盯**同一语义映射被复制**这一类。
"""
import io
import os
import re
import sys

P = sys.argv[1] if len(sys.argv) > 1 else r"C:\Users\Mocha\Desktop\VSProject\Auto7z-WPF"
if os.path.isdir(os.path.join(P, "Auto7z_WPF", "Auto7z_WPF")):
    SRC = os.path.join(P, "Auto7z_WPF", "Auto7z_WPF")
else:
    SRC = P

problems = []


def collect():
    out = {}
    for root, dirs, fs in os.walk(SRC):
        dirs[:] = [d for d in dirs if d not in ("obj", "objgui", "bin")]
        for n in fs:
            if n.endswith(".cs"):
                full = os.path.join(root, n)
                out[os.path.relpath(full, SRC).replace("\\", "/")] = io.open(
                    full, encoding="utf-8", errors="replace").read()
    return out


def fail(code, msg):
    problems.append((code, msg))


print("=" * 74)
print("架构约束：同一份知识只能有一处")
print("=" * 74)

files = collect()
print("源文件 %d 个" % len(files))

# ---------------------------------------------------------------- A1 组件路径
print("\n【A1】组件路径只从 ComponentsDirectory 派生")
# 允许出现 Path.Combine(..., "7z.exe") 的位置只有 SevenZipService 里基于 ComponentsDirectory 的那一处
stray = []
for rel, text in files.items():
    for i, line in enumerate(text.splitlines(), 1):
        s = line.strip()
        if s.startswith("//") or s.startswith("///"):
            continue
        if re.search(r'Path\.Combine\([^)]*Auto7z_WPF\.Services\.AppPaths\.ComponentsDirectory', s):
            continue
        # 直接用 AppPaths.ComponentsDirectory 拼组件文件的，只允许在 SevenZipService 的定义处
        if "AppPaths.ComponentsDirectory" in s and "SevenZipService.cs" not in rel:
            stray.append("%s:%d" % (rel, i))
        # 组件目录被重新计算（不是引用 ComponentsDirectory）也算
        if re.search(r'Path\.Combine\([^)]*"7-Zip"[^)]*\)', s) and rel != "Services/AppPaths.cs":
            stray.append("%s:%d (重算 7-Zip 目录)" % (rel, i))
if stray:
    fail("A1", "组件路径被重复计算: " + ", ".join(stray[:4]))
    print("  !!   " + ", ".join(stray[:4]))
else:
    print("  OK   组件路径只在 SevenZipService / AppPaths 各定义一次")

# ---------------------------------------------------------------- A2 模式分派
print("\n【A2】模式分派只有一处（ShellCommandService.RunByMode）")
# 在 Runner 里出现 switch/if 链把 SwitchZip/SwitchUnZip 映射到 Run*AndWait 就算重复
dispatchers = []
for rel, text in files.items():
    if rel == "Services/ShellCommandService.cs":
        continue
    # 找「同一个方法体里同时出现 SwitchUnZip 与 RunExtractAndWait」
    for m in re.finditer(r'(private|public)[^{;]*\b\w+\([^)]*\)\s*\{', text):
        start = m.start()
        depth, end = 0, start
        for k in range(text.find("{", start), len(text)):
            if text[k] == "{":
                depth += 1
            elif text[k] == "}":
                depth -= 1
                if depth == 0:
                    end = k
                    break
        body = text[start:end]
        if ("SwitchUnZip" in body and "RunExtractAndWait" in body) or \
           ("SwitchZip" in body and "RunCompressAndWait" in body):
            dispatchers.append(rel)
            break
if dispatchers:
    fail("A2", "模式分派重复出现在: " + ", ".join(sorted(set(dispatchers))))
    print("  !!   " + ", ".join(sorted(set(dispatchers))))
else:
    print("  OK   分派逻辑只在 ShellCommandService")

# ---------------------------------------------------------------- A3 开关→枚举
print("\n【A3】开关→枚举映射只有一处（ParseOperationMode）")
parsers = []
for rel, text in files.items():
    if rel == "Services/ShellCommandService.cs":
        continue
    for m in re.finditer(r'private\s+static\s+OperationMode\s+(\w+)\s*\(', text):
        parsers.append("%s:%s" % (rel, m.group(1)))
if parsers:
    fail("A3", "又出现了开关→枚举映射: " + ", ".join(parsers))
    print("  !!   " + ", ".join(parsers))
else:
    print("  OK   只有 ShellCommandService.ParseOperationMode")

# ---------------------------------------------------------------- A4 兜底一致性
print("\n【A4】ParseOperationMode 的兜底方向")
svc = files.get("Services/ShellCommandService.cs", "")
if "ParseOperationMode" in svc:
    m = re.search(r'public static OperationMode ParseOperationMode\(string mode\).*?\n        \}', svc, re.S)
    body = m.group(0) if m else ""
    returns_auto = re.search(r'return\s+OperationMode\.Auto', body) is not None
    returns_zip_fallback = len(re.findall(r'return\s+OperationMode\.Zip', body)) > 1
    if returns_auto and not returns_zip_fallback:
        print("  OK   未知/ null → Auto，与「不带开关的默认行为」一致")
    else:
        fail("A4", "ParseOperationMode 兜底不明确：应只有 -z 一条返回 Zip")
        print("  !!   兜底不明确")
else:
    fail("A4", "找不到 ParseOperationMode")
    print("  !!   找不到 ParseOperationMode")

# ---------------------------------------------------------------- A6 全静态类必须声明 static
#
# 「21 个成员全是静态、0 个实例成员」的类如果只写成 public class，
# 任何人都能用 new 造出一个毫无用处的实例 —— 编译器本可以拦住这类误用。
# 本项要求这类类必须声明为 static class（IL 层面即 abstract+sealed）。
#
# 2026-11-23 发现两处：ShellRegistrationService（21 静态/0 实例）、
# ShellCommandService（9 静态/0 实例），两者都有私有构造器作为人工防线，
# 但那是「约定」而不是「编译器强制」。改成 static class 后误用直接编译不过。
print("\n【A6】全静态类必须声明为 static class")
all_static_violations = []
for rel, text in files.items():
    m = re.search(r'public\s+(static\s+)?class\s+(\w+)', text)
    if not m or m.group(1):
        continue                      # 已是 static class，或本文件没有 public class
    cls = m.group(2)
    inst = stat = 0
    for line in text.splitlines():
        s = line.strip()
        mm = re.match(r'(public|private|internal|protected)\s+(static\s+)?([\w<>\[\],\.\?]+)\s+(\w+)\s*[\(\{]', s)
        if not mm:
            continue
        if mm.group(4) in ('if', 'for', 'while', 'switch', 'return', cls):
            continue
        if mm.group(2):
            stat += 1
        else:
            inst += 1
    if stat > 0 and inst == 0:
        all_static_violations.append('%s(%s): %d 静态/0 实例' % (rel, cls, stat))

if all_static_violations:
    fail("A6", "以下类全是静态成员却没声明 static class（可以 new 出空壳）: "
         + "; ".join(all_static_violations))
else:
    print("  OK   没有「全静态却不是 static class」的类型")

# ---------------------------------------------------------------- A5 刻意重复的黑名单
print("\n【A5】确认没有误伤刻意解耦的部分")
# Hash 核心族每个算法一个薄封装类 —— 这是工厂模式，不是耦合
cores = [r for r in files if r.startswith("Services/Hash/Cores/")]
if len(cores) >= 20:
    print("  OK   Hash 核心族保持 %d 个独立薄封装（刻意的解耦，未合并）" % len(cores))
else:
    fail("A5", "Hash 核心族文件数异常: %d" % len(cores))
    print("  !!   Hash 核心族只剩 %d 个文件" % len(cores))

# ---------------------------------------------------------------- 汇总
print("\n" + "=" * 74)
if problems:
    print("发现 %d 项架构约束违规：" % len(problems))
    for c, m in problems:
        print("  [%s] %s" % (c, m))
    print("FAIL")
    sys.exit(1)

print("同一份知识都只有一处，且刻意的解耦未被误伤")
print("PASS")
sys.exit(0)
