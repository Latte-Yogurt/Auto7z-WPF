#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""行为断言检查：验证「本轮改动引入的关键行为约束」在源码里成立。

与 cs_check.py 的区别：
  cs_check.py      —— 形态检查（命名/类型/文档标签/死分支/路径边界）
  behaviorcheck.py —— 行为约束检查（某段逻辑必须存在某个关键判断）

为什么要单独一个工具：§5b 的变异测试显示，
把 D21/D22/D24/D25/D06/D16 的关键判断改坏之后，cs_check.py **一个都报不出来**
（它不做语义分析）。这说明这些维度当时**没有回归保护** ——
本工具就是补这个洞的。

检查项（每条对应台账里的一个维度）：
  BC1 D21 拖拽判定必须要求「有路径 且 父进程是 explorer」
  BC2 D22 控制台密码必须真的写提示并读输入
  BC3 D23 命令行模式的失败必须**不**走弹窗分支
  BC4 D24 进度正则必须要求 `+` 标记（排除 7-Zip 的 `  0%` 与 `100% T`）
  BC5 D25 右键菜单注册同步必须能被 SkipShellMenuSync 关掉
  BC6 D06 必须调用 AttachConsole(ATTACH_PARENT_PROCESS)
  BC7 D16 MessageWindow 的 Owner 必须经过 ResolveOwner 保护
  BC8 D22 控制台密码提示末尾必须有冒号（文案键带 `：`）
  BC9 D24 等密码期间必须抑制进度输出

用法:
    python behaviorcheck.py                 # 查被测程序源码
    python behaviorcheck.py "<源码目录>"     # 查任意一份（变异测试用）

退出码：0 = 全部成立；1 = 有不成立。
"""
import re
import sys
from pathlib import Path

# 源码目录：可由命令行第一个参数指定（变异测试要指向副本）
if len(sys.argv) > 1:
    SRC = Path(sys.argv[1])
else:
    SRC = Path(r"C:\Users\Mocha\Desktop\VSProject\Auto7z Project\Auto7z_WPF\Auto7z_WPF")

# 每条：编号、维度、描述、文件名、判定
# 判定用「字面子串包含」，不走正则 —— 源码里混着 C# 的转义（\s \+ \\），
# 用正则再转义一层极易出错（BC4 就踩过）。字面匹配没有这层歧义。
CHECKS = [
    ("BC1", "D21", "拖拽判定要求「有路径 且 父进程是 explorer」",
     "Services/LaunchContext.cs",
     "hasPaths && ParentIsExplorer"),

    ("BC2", "D22", "控制台密码询问必须写提示并读一行输入",
     "ViewModel/Auto7zViewModel.cs",
     "RequestPasswordOnConsole"),

    ("BC3", "D23", "命令行模式的失败必须走「只写控制台」分支",
     "ViewModel/Auto7zViewModel.cs",
     "CurrentRunMode == RunMode.Console"),

    ("BC4", "D24", "进度正则必须要求 `+` 标记",
     "Services/SevenZipService.cs",
     r"%\s*\+"),

    ("BC5", "D25", "右键菜单注册同步必须能被 SkipShellMenuSync 关掉",
     "ViewModel/Auto7zViewModel.cs",
     "if (!SkipShellMenuSync)"),

    ("BC6", "D06", "必须调用 AttachConsole(ATTACH_PARENT_PROCESS)",
     "Services/ConsoleAttach.cs",
     "AttachConsole(ATTACH_PARENT_PROCESS)"),

    ("BC7", "D16", "MessageWindow 的 Owner 必须经过 ResolveOwner 保护",
     "Services/MessageService.cs",
     "Owner = ResolveOwner(app)"),

    ("BC8", "D22", "控制台密码提示文案末尾带冒号",
     "Resources/Languages/zh-CN.xaml",
     'x:Key="Console_PasswordPrompt">该压缩包已加密，请输入密码：'),

    ("BC9", "D24", "等密码期间必须抑制进度输出",
     "ViewModel/Auto7zViewModel.cs",
     "if (_awaitingPassword) return;"),

    ("BC10", "D22", "控制台密码必须真的读一行输入",
     "ViewModel/Auto7zViewModel.cs",
     "ReadConsoleLine()"),
]


def main():
    if not SRC.is_dir():
        print("源码目录不存在: %s" % SRC)
        return 2

    print("=" * 74)
    print("行为断言检查: %s" % SRC)
    print("=" * 74)

    failed = []
    for code, dim, desc, rel, pattern in CHECKS:
        f = SRC / rel
        if not f.exists():
            print("  !! %-4s %-5s 文件不存在: %s" % (code, dim, rel))
            failed.append((code, dim, desc, "文件不存在"))
            continue
        text = f.read_text(encoding="utf-8", errors="replace")
        if pattern in text:
            print("  ✓  %-4s %-5s %s" % (code, dim, desc))
        else:
            print("  !! %-4s %-5s %s" % (code, dim, desc))
            print("        %s 里找不到字面：%s" % (rel, pattern))
            failed.append((code, dim, desc, "关键判断缺失"))

    print()
    print("检查项 %d 条，成立 %d 条，不成立 %d 条"
          % (len(CHECKS), len(CHECKS) - len(failed), len(failed)))
    if failed:
        print("行为约束被破坏 -> 相关维度失去回归保护")
        return 1
    print("全部成立 ✓")
    return 0


if __name__ == "__main__":
    sys.exit(main())
