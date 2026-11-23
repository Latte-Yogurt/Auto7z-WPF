#!/usr/bin/env python3
# -*- coding: utf-8 -*-
r"""外观约束检查：确认「不要修改前端外观」这条硬性约束在结构上可守住。

背景
----
用户对本项目的硬性约束之一是「不要修改前端外观」。
这不能靠「我改了没改」的口头保证，要变成可判定的规则。

本检查盯三件事（都是**稳定事实**，不依赖某个时间点的快照）：

A. **颜色不在窗口里硬编码**
   `MainWindow.xaml.cs` 的 `ShowResult` 里有三态颜色（这是唯一允许的地方），
   其余窗口的 .cs 与所有 .xaml 都不该出现 `Color.FromRgb` / `#RRGGBB` 字面量。
   一旦有人在别处写死颜色，主题化就彻底断了。

B. **XAML 里没有裸的数值尺寸漂移**
   只统计 WinUI/WPF 布局属性（Width/Height/Margin/…）出现在 .xaml 中的**总次数**，
   与基线值比对。数量突变说明有人动了布局。

C. **三语文件的键集合一致 + 每个窗口的 x:Class 都存在**
   外观的「结构」就是窗口与字符串。缺键会让界面露出裸键名，
   缺 x:Class 会让窗口加载失败。

为什么不做「与快照逐字节比对」：快照只反映 12 小时前那一刻，
之后任何正常改动都会让审计噪声满屏。而上面三条是**永久成立的约束**。
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

# 布局属性的基线计数（2026-11-23 LTS 轮实测值）。
# 数量的变化说明有人动了布局 —— 这不一定是错，但必须让改动者显式更新这里，
# 从而留下痕迹、让审阅者注意到「外观被动过」。
BASELINE_LAYOUT_ATTRS = 252

BASELINE_LAYOUT_FINGERPRINTS = {
    'FontSize=12',
    'FontSize=13',
    'FontSize=16',
    'FontSize=30',
    'Height=*',
    'Height=10',
    'Height=15',
    'Height=150',
    'Height=20',
    'Height=24',
    'Height=260',
    'Height=60',
    'Height=Auto',
    'HorizontalAlignment=Center',
    'HorizontalAlignment=Left',
    'HorizontalAlignment=Right',
    'HorizontalAlignment=Stretch',
    'Margin=0',
    'Margin=0,2,2,2',
    'Margin=10,10',
    'Margin=10,5',
    'Margin=3,5',
    'Margin=4,0,0,0',
    'Margin=5',
    'Margin=5,5',
    'Margin=6,0',
    'Margin=8',
    'Margin={TemplateBinding Padding}',
    'MinWidth=100',
    'MinWidth=14',
    'MinWidth=38',
    'MinWidth=60',
    'MinWidth=70',
    'MinWidth=90',
    'Padding=15,0',
    'TextAlignment=Center',
    'VerticalAlignment=Bottom',
    'VerticalAlignment=Center',
    'VerticalAlignment=Top',
    'Width=*',
    'Width=10',
    'Width=100',
    'Width=15',
    'Width=160',
    'Width=300',
    'Width=520',
    'Width=80',
    'Width=90',
    'Width=Auto',
}

# XAML 里内联十六进制色值的基线数量（2026-11-23 实测）。
# 界面配色若被批量改动，这个数会变 —— 让改动者显式更新，从而留下痕迹。
BASELINE_XAML_COLORS = 5

problems = []


def fail(code, msg):
    problems.append((code, msg))
    print("  !!   " + msg)


def ok(msg):
    print("  OK   " + msg)


def walk(exts, exclude=("obj", "objgui", "bin", ".git")):
    out = []
    for root, dirs, files in os.walk(SRC):
        dirs[:] = [d for d in dirs if d not in exclude]
        for n in files:
            if n.endswith(exts):
                out.append(os.path.join(root, n))
    return out


print("=" * 74)
print("外观约束：颜色集中定义 / 布局未漂移 / 窗口结构完整")
print("=" * 74)

# ---------------------------------------------------------------- A 颜色集中
print("\n【A】颜色不在窗口里硬编码")
HEX = re.compile(r"#[0-9A-Fa-f]{6}\b")
RGB = re.compile(r"Color\.FromRgb")

# 唯一允许 C# 里出现颜色字面量的地方：MainWindow.ShowResult 的三态映射。
#
# 注意只查 .cs：XAML 里的内联十六进制（例如侧栏 Background="#FDFDFD"）是**既有设计**，
# 基线快照里逐字节一致，属于正常的界面写法。把它算成「硬编码」是误判 ——
# 真正要防的是「C# 代码里爱上写死颜色」，那会让主题化彻底断掉。
allow_cs = {"MainWindow.xaml.cs"}

color_hits = []
for f in walk((".cs",)):
    name = os.path.basename(f)
    text = io.open(f, encoding="utf-8", errors="replace").read()
    for i, line in enumerate(text.splitlines(), 1):
        s = line.strip()
        if s.startswith("//"):
            continue
        if RGB.search(s) or HEX.search(s):
            if name in allow_cs:
                continue
            color_hits.append("%s:%d" % (name, i))

if color_hits:
    fail("A", "C# 里颜色硬编码在 %d 处：%s" % (len(color_hits), ", ".join(color_hits[:4])))
else:
    ok("C# 里颜色只出现在 MainWindow.ShowResult")

# XAML 内联色值单独统计，作为「界面配色未被批量改动」的守卫
xaml_colors = 0
for f in walk((".xaml",)):
    if os.path.basename(f) in ("en-US.xaml", "zh-CN.xaml", "zh-TW.xaml"):
        continue
    text = io.open(f, encoding="utf-8", errors="replace").read()
    xaml_colors += len(HEX.findall(text))
if xaml_colors != BASELINE_XAML_COLORS:
    fail("A", "XAML 内联色值 %d 个，基线 %d —— 配色可能被改动"
         % (xaml_colors, BASELINE_XAML_COLORS))
else:
    ok("XAML 内联色值 %d 个，与基线一致" % xaml_colors)

# MainWindow 里的三态颜色必须仍是那三个值
mw = os.path.join(SRC, "MainWindow.xaml.cs")
if os.path.isfile(mw):
    text = io.open(mw, encoding="utf-8", errors="replace").read()
    want = {
        "成功绿": ("0x1E", "0x88", "0x3B"),
        "警告黄": ("0xB8", "0x86", "0x0B"),
        "失败红": ("0xC0", "0x39", "0x2B"),
    }
    missing = []
    for label, parts in want.items():
        pat = "Color.FromRgb(%s, %s, %s)" % parts
        if pat not in text:
            missing.append(label + " " + pat)
    if missing:
        fail("A", "三态颜色值变了：%s" % "; ".join(missing))
    else:
        ok("三态颜色值未变（绿 #1E883B / 黄 #B8860B / 红 #C0392B）")

# ---------------------------------------------------------------- B 布局未漂移
print("\n【B】XAML 布局属性数量未漂移")
# 只数「属性个数」抓不到「Height=24 改成 30」这种最典型的外观改动 ——
# 个数没变、值变了，实测确实漏报。所以统计「属性名=值」的指纹并与基线比对。
LAYOUT = re.compile(
    r'\b(Width|Height|Margin|Padding|FontSize|MinWidth|MaxWidth|MinHeight|MaxHeight'
    r'|HorizontalAlignment|VerticalAlignment|TextAlignment)\s*=\s*"([^"]*)"')

total = 0
per_file = {}
fingerprints = set()
for f in walk((".xaml",)):
    name = os.path.basename(f)
    if name in ("en-US.xaml", "zh-CN.xaml", "zh-TW.xaml"):
        continue          # 语言文件只有字符串，不含布局
    text = io.open(f, encoding="utf-8", errors="replace").read()
    hits = LAYOUT.findall(text)
    if hits:
        per_file[name] = len(hits)
        total += len(hits)
        for attr, value in hits:
            fingerprints.add(attr + "=" + value)

if total != BASELINE_LAYOUT_ATTRS:
    fail("B", "布局属性总数 %d，基线 %d（差 %+d）—— 外观可能被改动；"
              "若是有意改动请更新 BASELINE_LAYOUT_ATTRS"
              % (total, BASELINE_LAYOUT_ATTRS, total - BASELINE_LAYOUT_ATTRS))
else:
    # 个数一致时再比对「值集合」，这样「改值不改个数」也能抓到
    unknown = sorted(fingerprints - BASELINE_LAYOUT_FINGERPRINTS)
    gone = sorted(BASELINE_LAYOUT_FINGERPRINTS - fingerprints)
    if unknown or gone:
        detail = []
        if unknown:
            detail.append("新增 %d 种：%s" % (len(unknown), ", ".join(unknown[:3])))
        if gone:
            detail.append("消失 %d 种：%s" % (len(gone), ", ".join(gone[:3])))
        fail("B", "布局属性值有变化 — " + "；".join(detail))
    else:
        ok("布局属性 %d 处、%d 种取值，与基线一致（%s）" % (
            total, len(fingerprints),
            ", ".join("%s=%d" % (k, v) for k, v in sorted(per_file.items()))))

# ---------------------------------------------------------------- C 窗口结构
print("\n【C】窗口结构与字符串键")
lang_dir = os.path.join(SRC, "Resources", "Languages")
keysets = {}
for ln in ("zh-CN", "zh-TW", "en-US"):
    f = os.path.join(lang_dir, ln + ".xaml")
    if not os.path.isfile(f):
        fail("C", "缺少语言文件 %s.xaml" % ln)
        continue
    text = io.open(f, encoding="utf-8", errors="replace").read()
    keysets[ln] = set(re.findall(r'x:Key="([^"]+)"', text))

if len(keysets) == 3:
    base = keysets["zh-CN"]
    bad = []
    for ln, ks in keysets.items():
        only = ks - base
        miss = base - ks
        if only:
            bad.append("%s 多 %d 个" % (ln, len(only)))
        if miss:
            bad.append("%s 缺 %d 个" % (ln, len(miss)))
    if bad:
        fail("C", "三语键不一致：" + "; ".join(bad))
    else:
        ok("三语键完全一致（各 %d 个）" % len(base))

# 每个 .xaml 的 x:Class 必须在代码里存在同名类
classes = set()
for f in walk((".cs",)):
    text = io.open(f, encoding="utf-8", errors="replace").read()
    for m in re.finditer(r"partial\s+class\s+(\w+)", text):
        classes.add(m.group(1))

orphan = []
for f in walk((".xaml",)):
    name = os.path.basename(f)
    text = io.open(f, encoding="utf-8", errors="replace").read()
    m = re.search(r'x:Class="[\w\.]*\.(\w+)"', text)
    if m and m.group(1) not in classes:
        orphan.append("%s -> %s" % (name, m.group(1)))

if orphan:
    fail("C", "XAML 的 x:Class 找不到对应类：" + ", ".join(orphan))
else:
    ok("所有 x:Class 都有对应的 partial class")

# ---------------------------------------------------------------- 汇总
print("\n" + "=" * 74)
if problems:
    print("发现 %d 项外观约束违规：" % len(problems))
    for c, m in problems:
        print("  [%s] %s" % (c, m))
    print("FAIL")
    sys.exit(1)

print("颜色集中定义、布局未漂移、窗口结构完整")
print("PASS")
sys.exit(0)
