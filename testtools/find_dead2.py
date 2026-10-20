#!/usr/bin/env python3
r"""审查弃用方法与废键值:
- 词典里的键有没有被引用
- public 方法有没有人调用
- 配置文件里的键有没有被代码读取
"""
import io
import os
import re
import xml.etree.ElementTree as ET
from pathlib import Path

PROJECT = Path(r"C:\Users\Mocha\Desktop\VSProject\Auto7z Project\Auto7z_WPF\Auto7z_WPF")
LANG_DIR = PROJECT / "Resources" / "Languages"
CFG = Path(os.environ["LOCALAPPDATA"]) / "Auto7z" / "Auto7z.xml"

# 收集全部源码
sources = []
for p in PROJECT.rglob("*"):
    if p.suffix not in (".cs", ".xaml"):
        continue
    if "bin" in p.parts or "obj" in p.parts:
        continue
    sources.append((p, io.open(p, encoding="utf-8").read()))
all_text = "\n".join(t for _, t in sources)

print("=" * 66)
print("一、语言词典里的废键")
print("=" * 66)

lang_keys = {}
for f in sorted(LANG_DIR.glob("*.xaml")):
    txt = io.open(f, encoding="utf-8").read()
    keys = re.findall(r'x:Key="([^"]+)"', txt)
    lang_keys[f.name] = keys
    dups = [k for k in set(keys) if keys.count(k) > 1]
    if dups:
        print("  !! %s 重复键: %s" % (f.name, ", ".join(dups)))

unused = []
for name, keys in lang_keys.items():
    for k in keys:
        if ('"%s"' % k) in all_text:
            continue
        if ("{DynamicResource %s}" % k) in all_text:
            continue
        unused.append((name, k))

if unused:
    for name, k in unused:
        print("  !! 无人引用: %-34s (%s)" % (k, name))
else:
    print("  (无)")

print("")
print("=" * 66)
print("二、三语词典一致性")
print("=" * 66)
names = list(lang_keys)
for i in range(len(names)):
    for j in range(i + 1, len(names)):
        a, b = set(lang_keys[names[i]]), set(lang_keys[names[j]])
        if a - b:
            print("  !! %s 多出: %s" % (names[i], ", ".join(sorted(a - b))))
        if b - a:
            print("  !! %s 多出: %s" % (names[j], ", ".join(sorted(b - a))))
print("  各语言键数: " + ", ".join("%s=%d" % (n, len(lang_keys[n])) for n in names))

print("")
print("=" * 66)
print("三、配置文件里的键")
print("=" * 66)
if CFG.exists():
    root = ET.parse(CFG).getroot()
    for c in root:
        read = ('"%s"' % c.tag) in all_text
        print("  %-20s %s" % (c.tag, "被代码引用" if read else "!! 无人引用"))
else:
    print("  (配置不存在)")

print("")
print("=" * 66)
print("四、没人调用的 public 方法")
print("=" * 66)

method_names = {}
for p, txt in sources:
    if p.suffix != ".cs":
        continue
    for m in re.finditer(
            r'^\s*public\s+(?:static\s+)?(?:async\s+)?[\w<>\[\],\.\?]+\s+(\w+)\s*\(',
            txt, re.M):
        method_names.setdefault(m.group(1), []).append(p)

found = False
for name, files in sorted(method_names.items()):
    if name in ("Main", "get", "set", "Dispose", "Equals", "GetHashCode", "ToString"):
        continue
    count = len(re.findall(r'\b%s\b' % re.escape(name), all_text))
    decl = len(files)
    if count <= decl:
        print("  !! %-32s 声明 %d 处, 全项目出现 %d 次" % (name, decl, count))
        found = True

if not found:
    print("  (无)")

print("")
print("=" * 66)
print("五、导出但没用的 C++ 导出函数")
print("=" * 66)
dll = PROJECT / "bin" / "Debug" / "Auto7zShell.dll"
if dll.exists():
    cs = Path(r"C:\Users\Mocha\Desktop\VSProject\Auto7z Project\Auto7zShell\src\ShellExtension.cpp")
    cpp = io.open(cs, encoding="utf-8").read()
    for m in re.finditer(r'extern "C"[^;{]*?(\w+)\s*\(', cpp):
        fn = m.group(1)
        used = fn in all_text
        print("  %-24s %s" % (fn, "被主程序使用" if used else "(仅组件内部)"))
