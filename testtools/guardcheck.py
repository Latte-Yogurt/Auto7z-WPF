#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""守护检查：验证测试工具的「破坏性操作防护」本身没被削弱。

背景：这个工程出过一次 P0 事故 —— 测试脚本传参错误 + 删除边界模糊，
把测试工具源码全删了，测试全线崩溃。防复发靠 `safe_ops.py` 的白名单，
但**白名单自己坏掉时没有东西会发现** —— §6 迭代 R7 的变异测试证实了这一点：
把「目录里有源码就不许删」这条规则去掉后，危险的源码目录会被放行删除，
而当时没有任何工具报出来。

本工具做两件事：
  1. **实测** safe_ops 的四条规则：拿真实的危险输入去试，看它拦不拦
  2. **静态**检查 safe_ops.py 里那几条规则的关键字面还在不在

用法:
    python guardcheck.py
    python guardcheck.py "<testtools 目录>"

退出码：0 = 防护完好；1 = 有防护被削弱。
"""
import importlib
import os
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

if len(sys.argv) > 1:
    TT = Path(sys.argv[1])
else:
    TT = Path(r"C:\Users\Mocha\Desktop\VSProject\Auto7z Project\testtools")

# safe_ops 里必须存在的关键代码形态（缺了就说明规则被削弱）
REQUIRED_LITERALS = [
    ("拒绝删除源码目录", "源码类文件"),
    ("必须含标记串", "marker"),
    ("必须受控根内", "roots"),
    ("不是受控根本身", "本身"),
]


def main():
    if not (TT / "safe_ops.py").exists():
        print("找不到 safe_ops.py: %s" % (TT / "safe_ops.py"))
        return 2

    sys.path.insert(0, str(TT))
    # 重新导入，确保拿到的是目标目录里的那份
    for m in list(sys.modules):
        if m == "safe_ops":
            del sys.modules[m]
    import safe_ops
    from safe_ops import check_deletable, UnsafeDeleteError

    print("=" * 74)
    print("守护检查：破坏性操作防护是否完好")
    print("testtools: %s" % TT)
    print("=" * 74)

    failures = []

    # ---------- 1. 静态：关键规则字面还在 ----------
    print()
    print("--- 1. 关键规则字面（改坏防护后这些会消失）---")
    text = (TT / "safe_ops.py").read_text(encoding="utf-8", errors="replace")
    for desc, lit in REQUIRED_LITERALS:
        ok = lit in text
        print("  %s  %-22s （关键字面 %r）" % ("✓" if ok else "!!", desc, lit))
        if not ok:
            failures.append("safe_ops.py 里缺少：%s（%r）" % (desc, lit))

    # ---------- 2. 实测：拿真实危险输入去试 ----------
    print()
    print("--- 2. 实测拦截（拿真实危险输入试）---")
    tmp = Path(tempfile.gettempdir())
    cases = []

    # (a) 含源码文件的、路径合规的目录 —— 必须拦住
    src_dir = tmp / "Auto7zTest_guard_src"
    shutil.rmtree(str(src_dir), ignore_errors=True)
    src_dir.mkdir(parents=True, exist_ok=True)
    (src_dir / "x.cs").write_text("class A{}", encoding="utf-8")
    cases.append(("含源码文件的目录", src_dir, True))

    # (b) 路径不含标记串的目录 —— 必须拦住
    nomark = tmp / "guard_nomarker_dir"
    shutil.rmtree(str(nomark), ignore_errors=True)
    nomark.mkdir(parents=True, exist_ok=True)
    cases.append(("路径不含标记串", nomark, True))

    # (c) 受控根本身 —— 必须拦住
    cases.append(("受控根本身", tmp, True))

    # (d) 工程目录 —— 必须拦住（不在受控根内）
    project = Path(r"C:\Users\Mocha\Desktop\VSProject\Auto7z Project")
    if project.exists():
        cases.append(("工程目录", project, True))

    # (e) 桌面 —— 必须拦住
    desktop = Path(os.path.expanduser("~")) / "Desktop"
    if desktop.exists():
        cases.append(("桌面", desktop, True))

    # (f) 合法的临时目录 —— 必须放行
    good = tmp / "Auto7zTest_guard_ok"
    shutil.rmtree(str(good), ignore_errors=True)
    good.mkdir(parents=True, exist_ok=True)
    (good / "data.txt").write_text("x", encoding="utf-8")
    cases.append(("合法临时目录（应放行）", good, False))

    for desc, target, should_block in cases:
        try:
            r = check_deletable(str(target))
            blocked = False
            note = str(r)[:50]
        except UnsafeDeleteError as e:
            blocked = True
            note = str(e).splitlines()[0][:60]
        except Exception as e:
            blocked = None
            note = "%s: %s" % (type(e).__name__, e)

        if blocked is None:
            verdict = "!! 异常"
            failures.append("%s：抛了非预期异常 %s" % (desc, note))
        elif should_block:
            verdict = "✓ 已拦住" if blocked else "!! 未拦住"
            if not blocked:
                failures.append("%s：应该拦住但放行了" % desc)
        else:
            verdict = "✓ 已放行" if not blocked else "!! 误拦"
            if blocked:
                failures.append("%s：应该放行但被拦了" % desc)
        print("  %-22s %-10s %s" % (desc, verdict, note))

    # 清理自己建的探测目录
    for d in (src_dir, nomark, good):
        shutil.rmtree(str(d), ignore_errors=True)

    print()
    print("=" * 74)
    if failures:
        print("防护被削弱 —— %d 处：" % len(failures))
        for x in failures:
            print("  !! " + x)
        print()
        print("D20 失去回归保护 —— 必须在下一轮补回")
        return 1
    print("破坏性操作防护完好 ✓")
    print("D20 保持回归保护")
    return 0


if __name__ == "__main__":
    sys.exit(main())
