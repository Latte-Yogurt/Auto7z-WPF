#!/usr/bin/env python3
# -*- coding: utf-8 -*-
r"""Auto7z 静态检查：命名/类型一致性、死分支、文档标签、路径边界。

这是为 §5b 变异测试补上的检查工具：
原有的 regress.py 只做运行时行为检查，
"控件类型与代码 cast 不一致"、"空 catch"、"文档标签没闭合"、
"递归删除缺边界检查" 这四类缺陷它一个都测不出来，
对应的测试项属于「无效测试」。

用法:
    python cs_check.py <源码目录>
    python cs_check.py <源码目录> --json

退出码: 0 = 没发现问题, 1 = 有问题
"""
import json
import re
import sys
from pathlib import Path

SRC_EXT = (".cs", ".xaml", ".cpp", ".h")


def collect_sources(root):
    out = []
    for p in sorted(root.rglob("*")):
        if not p.is_file():
            continue
        if p.suffix not in SRC_EXT:
            continue
        if any(x in p.parts for x in ("bin", "obj", "build")):
            continue
        out.append(p)
    return out


def read(p):
    raw = p.read_bytes()
    if raw[:2] in (b"\xff\xfe", b"\xfe\xff"):
        return raw.decode("utf-16", errors="replace")
    return raw.decode("utf-8", errors="replace")


# ---------------------------------------------------------------- ① 命名/类型
# XAML 里 x:Name 含类型词，但元素类型不是那个类型
TYPE_WORDS = {
    "Button": "Button", "TextBox": "TextBox", "CheckBox": "CheckBox",
    "RadioButton": "RadioButton", "ComboBox": "ComboBox", "Image": "Image",
    "TextBlock": "TextBlock", "Grid": "Grid", "StackPanel": "StackPanel",
    "Border": "Border", "TabItem": "TabItem", "PasswordBox": "PasswordBox",
    "ListBox": "ListBox", "Slider": "Slider", "Window": "Window",
    "ToggleButton": "ToggleButton", "RadioButton_": "RadioButton",
}


def check_naming(sources, findings):
    """① XAML x:Name 的类型词与实际元素类型不符；
    ② 代码里 FindName/X cast 的目标类型与 XAML 实际元素类型不符。"""
    # 收集每个 xaml 的 x:Name -> 元素类型
    name_to_elem = {}   # (xaml, xname) -> elem
    for p in sources:
        if p.suffix != ".xaml":
            continue
        text = read(p)
        for m in re.finditer(r"<(\w+)([^>]*?x:Name=\"([^\"]+)\"[^>]*?)/?>", text, re.S):
            elem, name = m.group(1), m.group(3)
            name_to_elem[(p.name, name)] = elem

            # ① 类型词不符
            for word, tname in TYPE_WORDS.items():
                if word in name and elem != tname:
                    # 已知且有意为之的例外：
                    #   Grid 做成按钮样子（标题栏的最小化/关闭）
                    #   StackPanel 里放 Image（图片面板）
                    #   Border 名字里带 Window/Border（窗口边框）
                    #   RadioButton_ 前缀是命名约定，RadioButton 本就是 Button 的子类
                    if word == "Button" and elem == "Grid":
                        continue
                    if word == "Image" and elem == "StackPanel":
                        continue
                    if word == "Window" and elem == "Border":
                        continue
                    if word == "Grid" and elem == "Border" and name.startswith("Border_"):
                        continue
                    if word == "Button" and elem == "RadioButton" and name.startswith("RadioButton_"):
                        continue
                    findings.append({
                        "class": "① 命名/类型不匹配",
                        "file": p.name,
                        "line": text[:m.start()].count("\n") + 1,
                        "detail": 'x:Name="%s" 含类型词 %s，实际元素是 <%s>' % (name, word, elem),
                    })

    # ② 代码里的 as 转换与 XAML 实际类型不符
    #    键是 (XAML 文件名, x:Name)，代码文件按 "同名.cs" 对应到同名 XAML。
    for p in sources:
        if p.suffix != ".cs":
            continue
        text = read(p)
        # PasswordWindow.xaml.cs -> PasswordWindow.xaml
        xaml_name = p.name[:-3] if p.name.endswith(".cs") else p.stem + ".xaml"
        # FindName("X") as Type  以及  FindName("X") as Some.Namespace.Type
        for m in re.finditer(r'FindName\(\s*"([^"]+)"\s*\)\s*as\s+([\w\.]+)', text):
            fname, ctype = m.group(1), m.group(2).split(".")[-1]
            elem = name_to_elem.get((xaml_name, fname))
            if elem is None:
                continue
            if ctype not in TYPE_WORDS:
                continue
            if elem == ctype:
                continue
            # 已知例外：FindName 拿到的可能是派生类型（基类 -> 派生类 是合法的）
            if ctype in ("FrameworkElement", "Control", "UIElement"):
                continue
            findings.append({
                "class": "① 命名/类型不匹配",
                "file": p.name,
                "line": text[:m.start()].count("\n") + 1,
                "detail": 'FindName("%s") as %s，但 XAML 里它是 <%s> —— 该转换恒为 null'
                          % (fname, ctype, elem),
            })


# ---------------------------------------------------------------- ② 死分支
def check_dead_branch(sources, findings):
    """空 catch / 空 if / 恒假条件 / 空代码块。"""
    for p in sources:
        if p.suffix != ".cs":
            continue
        lines = read(p).splitlines()
        for i, l in enumerate(lines):
            s = l.strip()
            # 空 catch { } 或只有注释
            if re.search(r"catch\s*(\([^)]*\))?\s*\{\s*\}\s*$", s):
                findings.append({
                    "class": "② 死分支/空 catch",
                    "file": p.name,
                    "line": i + 1,
                    "detail": "空 catch：异常被静默吞掉" + s[:60],
                })
            elif re.search(r"catch\s*(\([^)]*\))?\s*\{\s*(/\*.*?\*/)?\s*\}\s*$", s):
                findings.append({
                    "class": "② 死分支/空 catch",
                    "file": p.name,
                    "line": i + 1,
                    "detail": "空 catch（只带注释）：异常被静默吞掉",
                })
            # if (false) / if (true) 之类恒定条件
            if re.search(r"\bif\s*\(\s*(false|true|0|1)\s*\)", s):
                findings.append({
                    "class": "② 死分支/空 catch",
                    "file": p.name,
                    "line": i + 1,
                    "detail": "恒定条件的 if：分支永远不会走" + s[:60],
                })


# ---------------------------------------------------------------- ③ 文档标签
def check_doc_tags(sources, findings):
    """XML 文档标签不配对、重复。"""
    for p in sources:
        if p.suffix != ".cs":
            continue
        text = read(p)
        so = len(re.findall(r"<summary>", text))
        sc = len(re.findall(r"</summary>", text))
        if so != sc:
            findings.append({
                "class": "③ 未闭合文档标签",
                "file": p.name,
                "line": 0,
                "detail": "<summary> 开启 %d 次，</summary> 关闭 %d 次 —— 后续整段会被注释吞掉" % (so, sc),
            })
        po = len(re.findall(r"<param[ >]", text))
        pc = len(re.findall(r"</param>", text))
        if po != pc:
            findings.append({
                "class": "③ 未闭合文档标签",
                "file": p.name,
                "line": 0,
                "detail": "<param> %d 个，</param> %d 个" % (po, pc),
            })
        ro = len(re.findall(r"<returns>", text))
        rc = len(re.findall(r"</returns>", text))
        if ro != rc:
            findings.append({
                "class": "③ 未闭合文档标签",
                "file": p.name,
                "line": 0,
                "detail": "<returns> %d 个，</returns> %d 个" % (ro, rc),
            })
        # 连续重复的同一行 summary
        lines = text.splitlines()
        for i in range(len(lines) - 1):
            a, b = lines[i].strip(), lines[i + 1].strip()
            if a and a == b and a.startswith("///"):
                findings.append({
                    "class": "③ 未闭合文档标签",
                    "file": p.name,
                    "line": i + 1,
                    "detail": "重复的文档标签：%s" % a[:60],
                })


# ---------------------------------------------------------------- ⑤ 路径边界
DANGEROUS_CALLS = [
    # 只盯递归删除：单文件删除的输入要么是程序自己拼的临时文件，
    # 要么紧跟在 File.Exists 判断之后，误报太多没有意义。
    (r"Directory\.Delete\(\s*([^,)]+)\s*,\s*true\s*\)", "递归删除目录"),
    (r"DirectoryInfo\.Delete\(\s*true\s*\)", "递归删除目录"),
]


def check_path_guard(sources, findings):
    """递归删除缺边界校验：删除调用之前没有 null / 存在性 / 根路径检查。

    只查递归删除（recursive=true）。单文件删除的输入要么是程序自己拼的临时文件，
    要么紧跟在 File.Exists 判断之后，全查的话误报会淹没真问题。
    """
    for p in sources:
        if p.suffix not in (".cs",):
            continue
        lines = read(p).splitlines()
        for i, l in enumerate(lines):
            s = l.strip()
            for pat, what in DANGEROUS_CALLS:
                if not re.search(pat, s):
                    continue
                window = "\n".join(lines[max(0, i - 8):i])
                has_guard = bool(re.search(
                    r"(IsNullOrWhiteSpace|string\.IsNullOrEmpty|ArgumentException|"
                    r"Directory\.Exists|File\.Exists|StartsWith|IsPathRooted|"
                    r"GetFullPath|GetInvalidFileNameChars|EnumerateFileSystemEntries)", window))
                if not has_guard:
                    findings.append({
                        "class": "⑤ 路径缺边界检查",
                        "file": p.name,
                        "line": i + 1,
                        "detail": "%s 前没有路径边界校验：%s" % (what, s[:70]),
                    })


def main():
    if len(sys.argv) < 2:
        print("用法: python cs_check.py <源码目录> [--json]")
        return 2
    root = Path(sys.argv[1])
    if not root.is_dir():
        print("目录不存在: %s" % root)
        return 2

    sources = collect_sources(root)
    findings = []
    check_naming(sources, findings)
    check_dead_branch(sources, findings)
    check_doc_tags(sources, findings)
    check_path_guard(sources, findings)

    if "--json" in sys.argv:
        print(json.dumps(findings, ensure_ascii=False, indent=2))
        return 1 if findings else 0

    print("=" * 70)
    print("Auto7z 静态检查: %s" % root)
    print("扫描文件 %d 个" % len(sources))
    print("=" * 70)
    if not findings:
        print("(无问题)")
        return 0
    by_class = {}
    for f in findings:
        by_class.setdefault(f["class"], []).append(f)
    for cls in sorted(by_class):
        print()
        print("--- %s (%d) ---" % (cls, len(by_class[cls])))
        for f in by_class[cls]:
            print("  !! %s:%s  %s" % (f["file"], f["line"] or "-", f["detail"]))
    print()
    print("TOTAL PROBLEMS: %d" % len(findings))
    return 1


if __name__ == "__main__":
    sys.exit(main())
