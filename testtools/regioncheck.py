#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""region 结构一致性检查（§4.3 的三条规则）：

  1. 配对：每个 #region 必须有对应的 #endregion（数量相等且交替出现）
  2. 嵌套深度 ≤ 1：#region 里不得再套 #region
  3. 跨度 ≤ 150 行：单 region 超过 150 行视为超长（提示词 §4.3 建议）

用法:
    python regioncheck.py                 # 查被测程序的源码
    python regioncheck.py "<源码目录>"     # 查任意一份（变异测试用）

退出码：0 = 无配对/嵌套问题；1 = 有配对或嵌套问题（跨度超长单独计数，不改退出码，
         因为个别超长 region 的拆分要改可执行逻辑，属"已知限制"而不是错误）。
"""
import re
import sys
from pathlib import Path

# 源码目录：可由命令行第一个参数指定（变异测试要指向副本）
if len(sys.argv) > 1:
    SRC = Path(sys.argv[1])
else:
    SRC = Path(r"C:\Users\Mocha\Desktop\VSProject\Auto7z Project\Auto7z_WPF\Auto7z_WPF")

MAX_SPAN = 150


def check_file(path):
    """检查一个文件的 region 结构。

    返回 (问题列表, 超长列表)。问题是配对/嵌套错误；超长是 §4.3 的建议值超标。
    """
    problems = []
    longs = []
    try:
        lines = path.read_text(encoding="utf-8", errors="replace").splitlines()
    except OSError:
        return problems, longs

    stack = []          # 每个 entry: (起始行号, 名字)
    finished = []       # (名字, 起始行, 结束行)

    for i, raw in enumerate(lines, 1):
        line = raw.strip()
        m = re.match(r"^#region\s*(.*)$", line)
        e = re.match(r"^#endregion\b", line)
        if m:
            if stack:
                # 嵌套深度 > 1：栈里已经有一个没关
                problems.append("%s:%d  region「%s」嵌套在「%s」里（深度 %d，规则 ≤1）"
                                % (path.name, i, m.group(1) or "(无名)",
                                   stack[-1][1], len(stack) + 1))
            stack.append((i, m.group(1) or "(无名)"))
        elif e:
            if not stack:
                problems.append("%s:%d  #endregion 没有对应的 #region" % (path.name, i))
            else:
                start, name = stack.pop()
                finished.append((name, start, i))

    for start, name in stack:
        problems.append("%s:%d  #region「%s」没有对应的 #endregion" % (path.name, start, name))

    for name, s, t in finished:
        span = t - s + 1
        if span > MAX_SPAN:
            longs.append("%s  region「%s」%d 行（L%d–L%d，超 %d 行）"
                         % (path.name, name, span, s, t, span - MAX_SPAN))
    return problems, longs


def main():
    if not SRC.is_dir():
        print("源码目录不存在: %s" % SRC)
        return 2

    all_problems = []
    all_longs = []
    files = 0
    regions = 0
    for f in sorted(SRC.rglob("*.cs")):
        if f.name.endswith(".Designer.cs"):
            continue
        files += 1
        p, l = check_file(f)
        all_problems += p
        all_longs += l
        t = f.read_text(encoding="utf-8", errors="replace")
        regions += len(re.findall(r"(?m)^\s*#region\b", t))

    print("=" * 70)
    print("region 结构检查: %s" % SRC)
    print("扫描文件 %d 个，region %d 个" % (files, regions))
    print("=" * 70)

    if all_problems:
        print()
        print("--- 配对/嵌套问题 (%d) ---" % len(all_problems))
        for x in all_problems:
            print("  !! " + x)
    if all_longs:
        print()
        print("--- 超长 region（> %d 行，已知限制计数）(%d) ---" % (MAX_SPAN, len(all_longs)))
        for x in all_longs:
            print("  -- " + x)

    print()
    print("配对/嵌套问题: %d" % len(all_problems))
    print("超长 region: %d" % len(all_longs))
    if len(all_problems) == 0 and len(all_longs) == 0:
        print("结构全部符合 ✓")
    return 0 if not all_problems else 1


if __name__ == "__main__":
    sys.exit(main())
