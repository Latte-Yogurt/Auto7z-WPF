# -*- coding: utf-8 -*-
"""检查 ConfigService 的键表与 Save/ApplyValues 是否漂移。

这是典型的「同一份知识写两遍」缺陷：
  `_keys` 数组声明了 16 个键，用于「缺键补齐」判断；
  `Save()` 里逐行 Set(...) 写了另一份清单；
  `ApplyValues()` 里又逐行读了一份清单。

三者只要有一处漏掉/多写，就会出现：
  - 键表有、Save 没有 → 每次加载都判定「缺键」，反复重写配置文件
  - Save 有、键表没有 → 该键永远不会被补齐，旧文件里缺它时读到默认值
  - ApplyValues 漏键 → 配置里写了但程序不生效（最隐蔽）
"""
import io
import os
import re

import sys
P = sys.argv[1] if len(sys.argv) > 1 else r"C:\Users\Mocha\Desktop\VSProject\Auto7z-WPF"
if os.path.isdir(os.path.join(P, "Auto7z_WPF", "Auto7z_WPF")):
    P = os.path.join(P, "Auto7z_WPF", "Auto7z_WPF")
F = os.path.join(P, "Services", "ConfigService.cs")
text = io.open(F, encoding="utf-8", errors="replace").read()

# 1) 键表
m = re.search(r'private static readonly ConfigKey\[\] _keys\s*=\s*\{(.*?)\};', text, re.S)
table = re.findall(r'new ConfigKey\("(\w+)"', m.group(1)) if m else []

# 2) Save 里的 Set
ms = re.search(r'public bool Save\(AppConfig config\).*?\n        \}', text, re.S)
save_block = ms.group(0) if ms else ""
save_keys = re.findall(r'Set\(values,\s*"(\w+)"', save_block)

# 3) ApplyValues 里的读取
ma = re.search(r'private static void ApplyValues\(AppConfig config, Dictionary<string, string> values\).*?\n        \}', text, re.S)
apply_block = ma.group(0) if ma else ""
apply_keys = re.findall(r'(?:GetValue|ParseBool)\(values,\s*"(\w+)"', apply_block)

print("=" * 74)
print("ConfigService 键表一致性")
print("=" * 74)
print("  _keys         %d 个: %s" % (len(table), ", ".join(table)))
print("  Save 写出     %d 个" % len(save_keys))
print("  ApplyValues  %d 个" % len(apply_keys))

ok = True


def diff(name, a, b):
    global ok
    only_a = [x for x in a if x not in b]
    only_b = [x for x in b if x not in a]
    if only_a:
        ok = False
        print("  [%s] 只在 %s 有: %s" % ("✗", name[0], ", ".join(only_a)))
    if only_b:
        ok = False
        print("  [%s] 只在 %s 有: %s" % ("✗", name[1], ", ".join(only_b)))


diff(("_keys", "Save"), table, save_keys)
diff(("_keys", "ApplyValues"), table, apply_keys)
diff(("Save", "ApplyValues"), save_keys, apply_keys)

# 键序是否与键表一致（配置文件人可读，顺序影响 diff 噪音）
seq_mismatch = [k for k in table if k in save_keys and save_keys.index(k) != table.index(k)]
if seq_mismatch:
    print("  [提示] Save 写出顺序与键表不同: %s" % ", ".join(seq_mismatch[:6]))

if ok:
    print("  三处键集合完全一致 ✓")

# 4) AppConfig 里的属性是否都被覆盖
cfg = io.open(os.path.join(P, "Services", "AppConfig.cs"),
              encoding="utf-8", errors="replace").read()
props = re.findall(r'public \w+\??\s+(\w+)\s*\{\s*get;\s*set;\s*\}', cfg)
persisted = [p for p in props if p != "TotalSize"]  # TotalSize 是运行时状态，不持久化
not_saved = [p for p in persisted if p not in save_keys]
not_applied = [p for p in persisted if p not in apply_keys]

print("\n  AppConfig 持久化属性 %d 个" % len(persisted))
if not_saved:
    ok = False
    print("  [✗] 未写入配置: %s" % ", ".join(not_saved))
if not_applied:
    ok = False
    print("  [✗] 未从配置读取: %s" % ", ".join(not_applied))
if not not_saved and not not_applied:
    print("  全部属性都参与了读写 ✓")

print("\n" + ("PASS" if ok else "FAIL"))
raise SystemExit(0 if ok else 1)
