#!/usr/bin/env python3
"""排查语言词典与配置文件里的废键, 以及与之对应的废方法"""
import io
import os
import re
import subprocess
import xml.etree.ElementTree as ET
from pathlib import Path

PROJECT = Path(r"C:\Users\Mocha\Desktop\VSProject\Auto7z_WPF\Auto7z_WPF")
LANG_DIR = PROJECT / "Resources" / "Languages"
CFG = Path(os.environ["LOCALAPPDATA"]) / "Auto7z" / "Auto7z.xml"

# 收集全部 C# 源码文本
sources = []
for p in PROJECT.rglob("*.cs"):
    if "bin" in p.parts or "obj" in p.parts:
        continue
    sources.append((p, io.open(p, encoding="utf-8").read()))
for p in PROJECT.rglob("*.xaml"):
    if "bin" in p.parts or "obj" in p.parts:
        continue
    sources.append((p, io.open(p, encoding="utf-8").read()))

all_text = "\n".join(t for _, t in sources)

print("=" * 68)
print("一、语言词典里的废键")
print("=" * 68)

lang_keys = {}
for f in sorted(LANG_DIR.glob("*.xaml")):
    txt = io.open(f, encoding="utf-8").read()
    keys = re.findall(r'x:Key="([^"]+)"', txt)
    lang_keys[f.name] = keys
    dups = [k for k in set(keys) if keys.count(k) > 1]
    if dups:
        print("  !! %s 有重复键: %s" % (f.name, ", ".join(dups)))

# 词典键是否被引用
unused = []
for name, keys in lang_keys.items():
    for k in keys:
        # 在源码里查 "key" 的字面量引用
        if ('"' + k + '"') in all_text:
            continue
        # XAML 里用 {DynamicResource key} 形式
        if ("{DynamicResource " + k + "}") in all_text:
            continue
        unused.append((name, k))

if unused:
    for name, k in unused:
        print("  !! 无人引用: %-40s (%s)" % (k, name))
else:
    print("  (无)")

print()
print("=" * 68)
print("二、词典键之间的一致性")
print("=" * 68)
names = list(lang_keys)
for i in range(len(names)):
    for j in range(i + 1, len(names)):
        a, b = set(lang_keys[names[i]]), set(lang_keys[names[j]])
        only_a = a - b
        only_b = b - a
        if only_a:
            print("  !! %s 多出: %s" % (names[i], ", ".join(sorted(only_a))))
        if only_b:
            print("  !! %s 多出: %s" % (names[j], ", ".join(sorted(only_b))))
print("  各语言键数: " + ", ".join("%s=%d" % (n, len(lang_keys[n])) for n in names))

print()
print("=" * 68)
print("三、配置文件里的键")
print("=" * 68)

if CFG.exists():
    root = ET.parse(CFG).getroot()
    cfg_keys = [c.tag for c in root]
    print("  当前配置键: " + ", ".join(cfg_keys))
    print()
    for k in cfg_keys:
        # 是否被代码读取
        read = ('"' + k + '"') in all_text
        mark = "" if read else "   <== 代码里没有引用"
        print("  %-20s %s%s" % (k, "被代码引用" if read else "无人引用", mark))
else:
    print("  (配置不存在)")

print()
print("=" * 68)
print("四、代码里声明但没人调用的 public 方法")
print("=" * 68)

# 找出 public 方法名, 再统计全项目出现次数
method_names = {}
for p, txt in sources:
    if p.suffix != ".cs":
        continue
    for m in re.finditer(r'^\s*public\s+(?:static\s+)?(?:async\s+)?[\w<>\[\],\.\?]+\s+(\w+)\s*\(', txt, re.M):
        method_names.setdefault(m.group(1), []).append(p)

for name, files in sorted(method_names.items()):
    if name in ("Main", "get", "set"):
        continue
    # 统计声明之外的出现次数
    count = len(re.findall(r'\b' + re.escape(name) + r'\b', all_text))
    decl = len(files)
    if count <= decl:
        print("  !! %-32s 声明 %d 处, 全项目只出现 %d 次" % (name, decl, count))
