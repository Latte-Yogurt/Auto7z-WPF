#!/usr/bin/env python3
# -*- coding: utf-8 -*-
r"""提示文案检查：把 Tip_* 文案当成"产品界面的一部分"来验，而不是只验键在不在。

为什么需要这个工具
==================
`langkeycheck.py` 只验三语键集合一致、`uiwidth` 只验控件上的可见文案宽度，
两边都不管 **tooltip**。结果是英文提示涨到 150–180 字符也没人发现：
WPF 对纯字符串调 `ToolTipService.SetToolTip` 时套默认模板，里面是 `TextWrapping=NoWrap`，
于是一个提示会拉成很宽的一条长条，读起来很累。产品侧已改成显式 `TextBlock + Wrap + MaxWidth`，
本工具负责**守住这条线**，防止以后又有人把长文案塞回来而无人察觉。

检查项
======
1. **过长**：按"显示宽度"算（CJK 记 2、ASCII 记 1），超过阈值即报。
   阈值默认 **200**，可用 `--max-width` 调整。
   为什么是 200 而不是更小：同一个提示的中英文长度天然不同（「便携模式」那条例子里
   中文 81、英文 173，是翻译膨胀而非啰嗦），阈值太紧只会逼人去删有效信息。
   200 对应"约两行半 80 列文本"，正好是 `MaxWidth=360` 下 WPF 折行后的合理上限。
   工具同时会打印**最长的 5 条**，便于人工判断趋势。
2. **键集合**：三份词典的 `Tip_*` 键必须完全一致（缺一个就是某语言没有提示）。
3. **空/占位**：文案不能为空、不能是纯空白、不能只由标点组成。
4. **换行符**：文案里不允许出现真实的 `\n` / `\r` —— 折行由 WPF 负责，
   写死换行符在别的语言/别的宽度下会错位。
5. **末尾标点**：同语言内应保持一致（中文用全角、英文用半角）。
6. **硬折行痕迹**：禁止用连续空格或超长 ASCII 无空格串制造视觉折行。

用法
====
    python tipcheck.py                          # 用默认路径与阈值
    python tipcheck.py --dir <语言词典目录>
    python tipcheck.py --max-width 120          # 收紧阈值
    python tipcheck.py --json                   # 输出机器可读结果

退出码：0 = 全部通过；1 = 有问题；2 = 用法/环境错误。
"""
import argparse
import io
import json
import os
import re
import sys

DEFAULT_LANG_DIR = os.path.join(
    os.path.dirname(os.path.dirname(os.path.abspath(__file__))),
    "Auto7z_WPF", "Auto7z_WPF", "Resources", "Languages")

TIP_RE = re.compile(r'<sys:String\s+x:Key="(Tip_[^"]+)">(.*?)</sys:String>', re.S)
CJK = re.compile(r'[\u3400-\u4dbf\u4e00-\u9fff\u3000-\u303f\uff00-\uffef]')


def display_width(text):
    """按显示列宽估算：CJK/全角记 2，其余记 1。"""
    return sum(2 if CJK.match(ch) else 1 for ch in text)


def read_lang_dir(path):
    """读三份语言词典，返回 {文件名: {键: 文案}}。"""
    result = {}
    for name in sorted(os.listdir(path)):
        if not name.lower().endswith(".xaml"):
            continue
        text = io.open(os.path.join(path, name), encoding="utf-8-sig").read()
        result[name] = {m.group(1): m.group(2) for m in TIP_RE.finditer(text)}
    return result


def check(langs, max_width):
    problems = []

    # ---- 1. 每个键的文案质量 ----
    for name, tips in sorted(langs.items()):
        for key, value in sorted(tips.items()):
            if not value.strip():
                problems.append({
                    "class": "空文案", "file": name, "key": key,
                    "detail": "Tip 文案为空或纯空白"})
                continue

            if "\n" in value or "\r" in value:
                problems.append({
                    "class": "真实换行符", "file": name, "key": key,
                    "detail": "文案里含真实换行符；折行应由 WPF 负责，写死会在别的宽度下错位"})

            if "  " in value:
                problems.append({
                    "class": "连续空格", "file": name, "key": key,
                    "detail": "含连续空格，疑似用空格手工折行"})

            # 只有标点、没有实质文字
            if re.fullmatch(r'[\s\W_]+', value):
                problems.append({
                    "class": "仅标点", "file": name, "key": key,
                    "detail": "文案只有标点或符号"})

            width = display_width(value)
            if width > max_width:
                problems.append({
                    "class": "过长", "file": name, "key": key,
                    "detail": "显示宽度 %d 超过阈值 %d" % (width, max_width),
                    "width": width})

            # 超长无空格串：折行器在**连续 ASCII** 上找不到断点，只能硬切或溢出。
            # 中文天然不靠空格断行（WPF 按字符断行），所以只对不含 CJK 的串做这项检查，
            # 否则每条中文提示都会被误报。
            for run in re.findall(r'\S{40,}', value):
                if CJK.search(run):
                    continue
                problems.append({
                    "class": "无空格长串", "file": name, "key": key,
                    "detail": "连续 %d 字符无空格：%s…" % (len(run), run[:30])})

    # ---- 2. 三语键集合一致 ----
    names = sorted(langs)
    for i in range(len(names)):
        for j in range(i + 1, len(names)):
            a, b = set(langs[names[i]]), set(langs[names[j]])
            if a - b:
                problems.append({
                    "class": "键缺失", "file": names[j],
                    "detail": "%s 有而 %s 没有：%s" % (
                        names[i], names[j], ", ".join(sorted(a - b)))})
            if b - a:
                problems.append({
                    "class": "键缺失", "file": names[i],
                    "detail": "%s 有而 %s 没有：%s" % (
                        names[j], names[i], ", ".join(sorted(b - a)))})

    # ---- 3. 末尾标点风格一致（同语言内） ----
    for name, tips in sorted(langs.items()):
        cjk_file = bool(re.search(r'(zh|cn|tw|jp|kr)', name, re.I))
        half_end = [k for k, v in tips.items()
                    if v.rstrip().endswith((".", ":", ";", "!", "?"))]
        full_end = [k for k, v in tips.items()
                    if v.rstrip().endswith(("。", "：", "；", "！", "？"))]
        # 中文词典里出现半角句末标点 → 与项目"中文注释 + 全角标点"的约定不符
        if cjk_file and half_end:
            problems.append({
                "class": "标点风格", "file": name,
                "detail": "中文词典里这些 Tip 用半角句末标点：%s" % ", ".join(sorted(half_end))})
        if (not cjk_file) and full_end:
            problems.append({
                "class": "标点风格", "file": name,
                "detail": "英文词典里这些 Tip 用全角句末标点：%s" % ", ".join(sorted(full_end))})

    return problems


def main():
    ap = argparse.ArgumentParser(description="Tip_* 提示文案检查")
    # 位置参数与 --dir 都收：其它检查工具习惯写成 `python xxx.py <源码根>`，
    # 这里同时支持那种写法，免得调用方记两套约定。
    ap.add_argument("path", nargs="?", default=None, help="语言词典目录或产品源码根")
    ap.add_argument("--dir", default=None, help="语言词典目录（也可用位置参数传源码根）")
    ap.add_argument("--max-width", type=int, default=200,
                    help="显示宽度阈值（CJK 记 2、ASCII 记 1），默认 200")
    ap.add_argument("--json", action="store_true", help="输出 JSON")
    args = ap.parse_args()

    target = args.dir or args.path or DEFAULT_LANG_DIR

    if not os.path.isdir(target):
        print("找不到路径: %s" % target)
        return 2

    # 兼容两种入参：直接给词典目录，或给产品源码根
    lang_dir = target
    if not any(n.lower().endswith(".xaml") for n in os.listdir(lang_dir)):
        candidate = os.path.join(lang_dir, "Resources", "Languages")
        if os.path.isdir(candidate):
            lang_dir = candidate
        else:
            print("目录里没有 .xaml 词典，也不是产品源码根: %s" % target)
            return 2

    langs = read_lang_dir(lang_dir)
    if not langs:
        print("目录里没有 .xaml 词典: %s" % lang_dir)
        return 2

    problems = check(langs, args.max_width)

    if args.json:
        print(json.dumps({"problems": problems,
                          "counts": {k: len(v) for k, v in sorted(langs.items())},
                          "max_width": args.max_width},
                         ensure_ascii=False, indent=1))
    else:
        print("=" * 74)
        print("提示文案检查: %s" % lang_dir)
        print("=" * 74)
        print("词典: " + ", ".join("%s=%d 条" % (k, len(v)) for k, v in sorted(langs.items())))
        print("显示宽度阈值: %d（CJK 记 2、ASCII 记 1）" % args.max_width)
        print("")
        print("最长的 5 条（供参考，不改判）：")
        flat = [(display_width(v), k, v) for tips in langs.values() for k, v in tips.items()]
        for w, k, v in sorted(flat, reverse=True)[:5]:
            print("   %4d  %-26s %s" % (w, k, v[:58]))
        print("")
        if problems:
            for cls in sorted(set(p["class"] for p in problems)):
                group = [p for p in problems if p["class"] == cls]
                print("--- %s (%d) ---" % (cls, len(group)))
                for p in group:
                    where = p.get("key") or p.get("file", "")
                    print("  !! %-22s %-28s %s" % (p.get("file", ""), where, p["detail"]))
                print("")
        else:
            print("(无问题)")
        print("TOTAL PROBLEMS: %d" % len(problems))

    return 0 if not problems else 1


if __name__ == "__main__":
    sys.exit(main())
