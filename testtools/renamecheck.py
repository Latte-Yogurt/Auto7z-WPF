#!/usr/bin/env python3
# -*- coding: utf-8 -*-
r"""改名残留检查（B04 的回归保护，以及 §4 命名阶段的收口）。

改名必须同步改掉全部引用点，否则运行时 FindName 拿到 null，
或者 XAML 绑定断掉。人工 grep 容易漏，这里做成工具。

两类检查：
  1) **旧名残留**：本轮改名涉及的旧名字，在源码里一个都不该剩下
  2) **新名一致**：新名字在 XAML 里定义一次、在代码里引用，两边对得上

用法:
    python renamecheck.py                      # 查默认工程
    python renamecheck.py "<源码目录>"          # 查任意一份（变异测试用）

退出码：0 = 没有残留且新名一致；1 = 有残留或不一致。
"""
import re
import sys
from pathlib import Path

DEFAULT_SRC = Path(r"C:\Users\Mocha\Desktop\VSProject\Auto7z Project\Auto7z_WPF\Auto7z_WPF")

if len(sys.argv) > 1:
    SRC = Path(sys.argv[1])
else:
    SRC = DEFAULT_SRC

# 本轮改名：(旧名, 新名, 说明)
# 旧名必须彻底消失；新名必须在 XAML 有定义、且在代码里有引用
#
# 白名单：PasswordWindow.xaml 的 PasswordTitle 是**有意保留**的 ——
# 那个窗口确实是密码窗口，语义正确。提示词明确要求保留它。
# 所以检查旧名残留时按「文件」豁免，不是按名字豁免。
ALLOWED_OLD = {
    # (旧名, 允许残留的文件名)
    ("PasswordTitle", "PasswordWindow.xaml"),   # 密码窗口自己那份，文件名按前缀匹配
    ("PasswordTitle", "PasswordWindow.g.cs"),  # 上面那份的生成代码，见 collect() 的过滤
}

RENAMES = [
    ("PasswordTitle", "MessageTitle",
     "MessageWindow 的标题控件原叫 PasswordTitle，名不副实"),
    ("PasswordTitle", "ProgressTitle",
     "ProcessWindow 的标题控件原叫 PasswordTitle，名不副实"),
    ("PasswordBox_Zip", "ZipPasswordInput",
     "MainWindow 的压缩密码框实际是 TextBox，名字里的类型词不符"),
    ("PasswordBox_UnZip", "UnZipPasswordInput",
     "MainWindow 的解压密码框实际是 TextBox，名字里的类型词不符"),
    ("PasswordBox_Zip_Text", "ZipPasswordInputText",
     "对应的视图模型属性"),
]


def read(p):
    raw = p.read_bytes()
    if raw[:2] in (b"\xff\xfe", b"\xfe\xff"):
        return raw.decode("utf-16", errors="replace")
    return raw.decode("utf-8", errors="replace")


def collect():
    files = []
    for p in sorted(SRC.rglob("*")):
        if not p.is_file():
            continue
        if p.suffix not in (".cs", ".xaml"):
            continue
        if any(x in p.parts for x in ("bin", "obj", "objgui", "build")):
            continue
        # 生成代码（*.g.cs / *.g.i.cs / *.Designer.cs）不参与改名检查：
        # 它们由 XAML 编译器按 x:Name 生成，名字对不对要看源文件。
        if p.name.endswith(".g.cs") or p.name.endswith(".g.i.cs") or p.name.endswith(".Designer.cs"):
            continue
        files.append(p)
    return files


def main():
    if not SRC.is_dir():
        print("源码目录不存在: %s" % SRC)
        return 2

    files = collect()
    texts = {p: read(p) for p in files}
    all_text = "\n".join(texts.values())

    print("=" * 74)
    print("改名残留检查: %s" % SRC)
    print("扫描文件 %d 个" % len(files))
    print("=" * 74)

    failures = []

    # ---- 1) 旧名残留 ----
    for old, new, desc in RENAMES:
        # PasswordBox_Zip_Text 里含 PasswordBox_Zip，先查长的
        hits = []
        for p, t in texts.items():
            for i, l in enumerate(t.splitlines(), 1):
                if old in l and (old, p.name) not in ALLOWED_OLD:
                    hits.append("%s:%d  %s" % (p.name, i, l.strip()[:90]))
        if hits:
            print("  !! RN1 旧名「%s」仍有 %d 处残留（应改为 %s）"
                  % (old, len(hits), new))
            for h in hits[:6]:
                print("        %s" % h)
            failures.append("旧名 %s 残留 %d 处" % (old, len(hits)))
        else:
            allowed = [f for (o, f) in ALLOWED_OLD if o == old]
            extra = ("（%s 属有意保留）" % "、".join(allowed)) if allowed else ""
            print("  ✓  RN1 旧名「%s」已无残留%s —— %s" % (old, extra, desc))

    # ---- 2) 新名一致：XAML 有定义 + 代码有引用 ----
    for old, new, desc in RENAMES:
        if new == "ZipPasswordInputText":
            # 视图模型属性，不在 XAML 里
            hits = [(p.name, i) for p, t in texts.items()
                    for i, l in enumerate(t.splitlines(), 1)
                    if new in l]
            if hits:
                print("  ✓  RN2 新名「%s」在代码里有 %d 处引用" % (new, len(hits)))
            else:
                print("  !! RN2 新名「%s」在代码里一处都没有 —— 改名可能漏了" % new)
                failures.append("新名 %s 无引用" % new)
            continue

        defined = re.search(r'x:Name="%s"' % re.escape(new), all_text)
        xaml_def = [(p.name, i) for p, t in texts.items()
                    if p.suffix == ".xaml"
                    for i, l in enumerate(t.splitlines(), 1)
                    if 'x:Name="%s"' % re.escape(new) in l]
        code_ref = [(p.name, i) for p, t in texts.items()
                    if p.suffix == ".cs"
                    for i, l in enumerate(t.splitlines(), 1)
                    if re.search(r'\b%s\b' % re.escape(new), l)]
        # XAML 内的引用（WindowChrome 的 CaptionHeight 绑定等）
        xaml_ref = [(p.name, i) for p, t in texts.items()
                    if p.suffix == ".xaml"
                    for i, l in enumerate(t.splitlines(), 1)
                    if re.search(r'\b%s\b' % re.escape(new), l)
                    and 'x:Name="%s"' % re.escape(new) not in l]

        if not xaml_def:
            print("  !! RN2 新名「%s」在 XAML 里没有 x:Name 定义" % new)
            failures.append("新名 %s 无 XAML 定义" % new)
        elif not code_ref and not xaml_ref:
            print("  !! RN2 新名「%s」在 XAML 有定义，但任何地方都没有引用 —— 改名可能漏了" % new)
            failures.append("新名 %s 无引用" % new)
        else:
            print("  ✓  RN2 新名「%s」：XAML 定义 %s，代码引用 %d 处，XAML 内引用 %d 处"
                  % (new, ", ".join("%s:%d" % x for x in xaml_def),
                     len(code_ref), len(xaml_ref)))

    # ---- 3) 类型词残留：名字里含类型词但元素类型不符 ----
    # 委托给 cs_check 的思路，但这里独立做一遍（交叉验证用）
    TYPE_WORDS = ["PasswordBox", "CheckBox", "RadioButton", "ComboBox", "TextBox",
                  "TextBlock", "ProgressBar", "ListBox", "Slider", "ToggleButton"]
    for p in files:
        if p.suffix != ".xaml":
            continue
        t = texts[p]
        for m in re.finditer(r'<(\w+)([^>]*?x:Name="([^"]+)"[^>]*?)/?>', t, re.S):
            elem, name = m.group(1), m.group(3)
            for w in TYPE_WORDS:
                if w in name and elem != w:
                    if w == "RadioButton" and elem == "RadioButton":
                        continue
                    print("  !! RN3 %s:%d  名字「%s」含类型词 %s，实际是 <%s>"
                          % (p.name, t[:m.start()].count("\n") + 1, name, w, elem))
                    failures.append("名不副实: %s" % name)

    print()
    if failures:
        print("改名检查不通过 %d 项：" % len(failures))
        for f in failures:
            print("  !! %s" % f)
        return 1
    print("改名检查通过 ✓")
    print("B04 有回归保护（旧名无残留、新名定义与引用一致、名字不含类型词）")
    return 0


if __name__ == "__main__":
    sys.exit(main())
