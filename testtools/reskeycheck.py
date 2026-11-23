# -*- coding: utf-8 -*-
"""资源键一致性检查（可全自动化，正对应用户说的"颜色映射这种你完全可以检查的"）。

检查四件事：
  A. 三语键集合是否完全一致（缺一个 → 某种语言下界面出现裸键名）
  B. 代码里引用的键是否都存在（缺 → 静默回落到 fallback，用户看到英文）
  C. 定义了但从未被引用的键（死资源）
  D. **结果三态的颜色映射**是否与文档/预期一致（成功绿/警告黄/失败红）
"""
import io
import os
import re

import sys

# 调用方可能传项目根（含 Auto7z_WPF/Auto7z_WPF 两级），
# 也可能直接传产品源码目录（run_regress.py 的 PROJECT 就是后者）。
# 两种都要能跑，否则接进回归时会因为路径重复而找不到 Resources 目录，
# 表现为「检查未跑完」——本轮踩过一次。
_arg = sys.argv[1] if len(sys.argv) > 1 else r"C:\Users\Mocha\Desktop\VSProject\Auto7z-WPF"
if os.path.isdir(os.path.join(_arg, "Auto7z_WPF", "Auto7z_WPF")):
    P = _arg
    SRC = os.path.join(P, "Auto7z_WPF", "Auto7z_WPF")
elif os.path.isdir(os.path.join(_arg, "Resources", "Languages")):
    SRC = _arg
    P = os.path.dirname(os.path.dirname(_arg))
else:
    P = _arg
    SRC = os.path.join(P, "Auto7z_WPF", "Auto7z_WPF")
LANGS = ("zh-CN", "zh-TW", "en-US")


def load_keys():
    keys = {}
    for lang in LANGS:
        f = os.path.join(SRC, "Resources", "Languages", lang + ".xaml")
        t = io.open(f, encoding="utf-8", errors="replace").read()
        keys[lang] = set(re.findall(r'x:Key="([A-Za-z0-9_]+)"', t))
    return keys


def collect_code():
    out = {}
    for root, dirs, fs in os.walk(SRC):
        dirs[:] = [d for d in dirs if d not in ("obj", "objgui", "bin")]
        for n in fs:
            if n.endswith((".cs", ".xaml")):
                full = os.path.join(root, n)
                out[os.path.relpath(full, SRC).replace("\\", "/")] = io.open(
                    full, encoding="utf-8", errors="replace").read()
    return out


def main():
    keys = load_keys()
    code = collect_code()

    print("=" * 78)
    print("资源键一致性检查")
    print("=" * 78)
    for lang in LANGS:
        print("  %-6s %3d 键" % (lang, len(keys[lang])))

    # ---- A. 三语键集合一致性 ----
    print("\n【A】三语键集合一致性")
    base = keys["zh-CN"]
    allok = True
    for lang in LANGS:
        miss = sorted(base - keys[lang])
        extra = sorted(keys[lang] - base)
        if miss:
            allok = False
            print("  [缺失] %s 少了 %d 个: %s" % (lang, len(miss), ", ".join(miss[:8])))
        if extra:
            print("  [多余] %s 多了 %d 个: %s" % (lang, len(extra), ", ".join(extra[:8])))
    if allok:
        print("  三语键集合完全一致 ✓")

    # ---- B. 代码引用的键是否存在 ----
    used = set()
    use_sites = {}

    # 取键 API 必须列全：实测全仓共 124 次取键调用，其中 GetFormattedText 有 3 次。
    # 早先这里只列了 GetResourceText / GetErrorText，那 3 次调用从未被检查 ——
    # 若有人用 GetFormattedText 取一个不存在的键，本检查会漏报，
    # 界面会静默回落到硬编码文案（这正是本文件开头说要防的缺陷）。
    pat = re.compile(r'(?:GetResourceText|GetErrorText|GetFormattedText)\(\s*"([A-Za-z][A-Za-z0-9_]*)"')
    pat2 = re.compile(r'SetToolTip\([^,]+,\s*"([A-Za-z][A-Za-z0-9_]*)"')
    for rel, text in code.items():
        for m in pat.finditer(text):
            used.add(m.group(1))
            use_sites.setdefault(m.group(1), []).append(rel)
        for m in pat2.finditer(text):
            used.add(m.group(1))
            use_sites.setdefault(m.group(1), []).append(rel)

    print("\n【B】代码引用的键是否都存在（共引用 %d 个）" % len(used))
    missing = []
    for lang in LANGS:
        miss = sorted(used - keys[lang])
        if miss:
            missing.append((lang, miss))
    if missing:
        for lang, ms in missing:
            print("  [缺失] %s 缺 %d 个: %s" % (lang, len(ms), ", ".join(ms[:10])))
    else:
        print("  全部引用键在三语中都存在 ✓")

    # ---- C. 死资源 ----
    allkeys = set().union(*keys.values())
    blob = "\n".join(code.values())
    unused = []
    for k in sorted(allkeys):
        # 出现次数 == 1 说明只在它自己的定义处出现过
        if blob.count(k) <= 1:
            unused.append(k)
    print("\n【C】定义但从未被引用（死资源）: %d 个" % len(unused))
    for k in unused[:20]:
        print("  ? " + k)

    # ---- D. 结果三态颜色映射 ----
    print("\n【D】结果三态颜色映射")
    mainwin = code.get("MainWindow.xaml.cs", "")
    checks = [
        ("Failed",  (0xC0, 0x39, 0x2B), "红"),
        ("Warning", (0xB8, 0x86, 0x0B), "黄"),
        ("Success", (0x1E, 0x88, 0x3B), "绿"),
    ]
    for name, rgb, label in checks:
        hexpat = "Color.FromRgb(0x%02X, 0x%02X, 0x%02X)" % rgb
        found = hexpat in mainwin
        print("  %-8s %s  %s" % (name, hexpat, "✓" if found else "✗ 未找到"))

    # 顺序断言：Failed 分支必须在 Warning 之前，Warning 在 default 之前
    idx_failed = mainwin.find("case ResultSeverity.Failed:")
    idx_warn = mainwin.find("case ResultSeverity.Warning:")
    if idx_failed >= 0 and idx_warn >= 0:
        print("  分支顺序: Failed(%d) < Warning(%d) → %s" % (
            idx_failed, idx_warn, "✓" if idx_failed < idx_warn else "✗ 顺序异常"))

    # 三态必须是三个分支，不能合并成两个
    n_cases = mainwin.count("case ResultSeverity.")
    print("  switch 分支数: %d → %s" % (n_cases, "✓ 三态齐全" if n_cases == 2 else "? 检查 default 分支"))

    return 0 if allok and not missing else 1


if __name__ == "__main__":
    raise SystemExit(main())
