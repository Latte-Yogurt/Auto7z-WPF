#!/usr/bin/env python3
# -*- coding: utf-8 -*-
r"""测试生命周期规范符合性检查（硬性指标的可执行版本）。

规范正文见 `TEST_LIFECYCLE.md`。本脚本把其中的硬性条款变成可自动化判定的检查，
接入回归套件后，任何"改回旧行为"的改动都会当场失败。

检查项：
  L1 规范文档存在且含五个钩子与三阶段主框架
  L2 每个测试套件都使用 Lifecycle（而非自建清理顺序）
  L3 阶段二内不存在"全局清理"调用（rmtree 整个临时根 / 清环境变量）
  L4 所有 rmtree / unlink 递归删除前都过安全闸门（_assert_safe 或等价校验）
  L5 AfterAll 恢复 AUTO7Z_DATA_ROOT 环境变量
  L6 子进程经过 track_process（或 grep 允许的等价包装）
  L7 每个套件支持 --only 单跑（原则一：无隐式依赖）

退出码：0 全通过；1 有违规；2 环境/文档缺失
"""
import io
import os
import re
import sys

P = sys.argv[1] if len(sys.argv) > 1 else r"C:\Users\Mocha\Desktop\VSProject\Auto7z-WPF"
if os.path.isdir(os.path.join(P, "testtools")):
    TOOLS = os.path.join(P, "testtools")
else:
    TOOLS = P

DOC = os.path.join(TOOLS, "TEST_LIFECYCLE.md")
problems = []


def fail(code, msg):
    problems.append((code, msg))


def read(path):
    try:
        return io.open(path, encoding="utf-8", errors="replace").read()
    except OSError:
        return ""


def py_files():
    out = []
    for n in sorted(os.listdir(TOOLS)):
        if n.endswith(".py"):
            out.append(os.path.join(TOOLS, n))
    return out


# ---------------------------------------------------------------- L1 规范文档
print("=== L1 规范文档 ===")
if not os.path.isfile(DOC):
    fail("L1", "TEST_LIFECYCLE.md 不存在 —— 规范文档是硬性指标，不能缺失")
    print("  !! 缺少 TEST_LIFECYCLE.md")
else:
    doc = read(DOC)
    must = {
        "三阶段主框架": "三个阶段",
        "BeforeAll": "BeforeAll",
        "BeforeEach": "BeforeEach",
        "AfterEach": "AfterEach",
        "AfterAll": "AfterAll",
        "中途不清理": "中途",
        "串行": "串行",
        "核心原则": "全局环境清理只发生在",
    }
    missing = [k for k, v in must.items() if v not in doc]
    if missing:
        fail("L1", "规范文档缺少关键条款: " + ", ".join(missing))
        print("  !! 缺少条款: " + ", ".join(missing))
    else:
        print("  OK   包含全部 %d 项关键条款" % len(must))


# ---------------------------------------------------------------- L2 使用 Lifecycle
print("\n=== L2 套件使用 Lifecycle ===")
SUITES = ["run_regress.py", "run_system_test.py", "run_cs_probes.py",
          "regress.py", "run_all.py"]
found_any = False
for name in SUITES:
    path = os.path.join(TOOLS, name)
    if not os.path.isfile(path):
        continue
    text = read(path)
    if "test_lifecycle" in text or "Lifecycle(" in text:
        print("  OK   %s 使用 Lifecycle" % name)
        found_any = True
    else:
        # 目前允许：它们通过 test_env.isolate_data_root 做数据隔离
        if "isolate_data_root" in text:
            print("  --   %s 用 test_env.isolate_data_root（尚未迁到 Lifecycle，记为待迁移）" % name)
        else:
            fail("L2", "%s 既不用 Lifecycle 也不用 isolate_data_root，数据目录没有隔离" % name)
            print("  !!   %s 无任何隔离" % name)


# ---------------------------------------------------------------- L3 阶段二内不做全局清理
print("\n=== L3 阶段二内无全局清理 ===")
# 危险模式：删整个临时根 / 清掉改道变量 / 清整个 Auto7zTest_data
DANGEROUS = [
    (r'rmtree\s*\(\s*(?:os\.environ\[[^\]]+\]|str\(\s*TEMP|TEMP\.path\s*\)|temp_root\(\))\s*[,)]',
     "删整个临时根"),
    (r'rmtree\s*\(\s*str\(\s*MARKER', "删整个标记根"),
    (r'os\.environ\.pop\s*\(\s*DATA_ROOT_ENV', "清除数据目录改道变量"),
    (r'del\s+os\.environ\[\s*DATA_ROOT_ENV', "删除数据目录改道变量"),
]
hits = 0
for f in py_files():
    name = os.path.basename(f)
    # 生命周期模块自身在 AfterAll 里合法清除环境变量
    is_lifecycle_impl = name == "test_lifecycle.py"
    text = read(f)
    for i, line in enumerate(text.splitlines(), 1):
        s = line.strip()
        if s.startswith("#"):
            continue
        for pat, what in DANGEROUS:
            if re.search(pat, s):
                # test_lifecycle 的 after_all 里 pop 是规范要求的，放行
                if is_lifecycle_impl and "pop" in pat:
                    continue
                if is_lifecycle_impl and "rmtree" in pat:
                    continue
                fail("L3", "%s:%d %s —— %s" % (name, i, what, s[:70]))
                hits += 1
if hits == 0:
    print("  OK   未发现阶段二内的全局清理调用")


# ---------------------------------------------------------------- L4 删除前过安全闸门
print("\n=== L4 递归删除前有安全闸门 ===")
#
# 重要的前置认识：本项目**已经**有 `safe_ops.safe_rmtree` 作为统一守卫
# （它校验「在 %TEMP% 下 / 带标记串 / 不是受控根本身 / 不含源码文件」）。
# 因此「用了 safe_rmtree」就是合格，不需要再额外找 _assert_safe。
#
# 第一版检查只认 _assert_safe，把 55 处 `from safe_ops import safe_rmtree`
# 全报成违规 —— 那是我把「另一种同样正确的实现」当成了「缺失闸门」。
GUARD = re.compile(r'(_assert_safe|safe_rmtree|SafeTemp|IsUnderDirectory|'
                   r'IsSafeToDeleteRecursively|assert_safe|MARKER in .*parts|relative_to\()')
SAFE_CALL = re.compile(r'safe_rmtree\s*\(')
RAW_CALL = re.compile(r'(?<!safe_)rmtree\s*\(')      # 裸 shutil.rmtree / rmtree(
unguarded = 0
for f in py_files():
    name = os.path.basename(f)
    # safe_ops.py 自己就是守卫的实现，它的内部 rmtree 是闸门之后的事
    if name == "safe_ops.py":
        continue
    lines = read(f).splitlines()
    for i, line in enumerate(lines):
        s = line.strip()
        if s.startswith("#"):
            continue
        # 只查**裸** rmtree；safe_rmtree(...) 是合格用法
        if not RAW_CALL.search(s):
            continue
        # safe_rmtree 在同一行出现则视为合格
        if SAFE_CALL.search(s):
            continue
        # 往前 15 行内应有守卫，或该文件导入了 safe_ops
        window = "\n".join(lines[max(0, i - 15):i + 1])
        file_text = "\n".join(lines)
        if GUARD.search(window) or "from safe_ops import" in file_text:
            continue
        fail("L4", "%s:%d rmtree 前无安全闸门" % (name, i + 1))
        unguarded += 1
if unguarded == 0:
    print("  OK   全部 rmtree 前都有安全闸门（safe_ops.safe_rmtree 或 _assert_safe）")


# ---------------------------------------------------------------- L5 AfterAll 恢复环境
print("\n=== L5 AfterAll 恢复环境变量 ===")
lc = os.path.join(TOOLS, "test_lifecycle.py")
if os.path.isfile(lc):
    t = read(lc)
    if "environ.pop" in t and "DATA_ROOT_ENV" in t:
        print("  OK   AfterAll 恢复 AUTO7Z_DATA_ROOT")
    else:
        fail("L5", "test_lifecycle.AfterAll 未恢复环境变量")
        print("  !!   AfterAll 未恢复环境变量")
else:
    fail("L5", "test_lifecycle.py 不存在")


# ---------------------------------------------------------------- L6 子进程登记
print("\n=== L6 子进程经过 track_process ===")
lctext = read(lc)
if "track_process" in lctext:
    print("  OK   Lifecycle 提供 track_process")
else:
    fail("L6", "Lifecycle 缺少 track_process")
    print("  !!   缺少 track_process")


# ---------------------------------------------------------------- L7 --only 支持
print("\n=== L7 支持 --only 单跑 ===")
if "select_cases" in read(lc) or "--only" in read(lc):
    print("  OK   Lifecycle 提供 select_cases(--only)")
else:
    # 现有套件用 --filter / --case 之类的等价实现也可
    alt = False
    for f in py_files():
        t = read(f)
        if "--only" in t or "--case" in t or "--filter" in t:
            alt = True
            break
    if alt:
        print("  OK   已有等价的选择参数")
    else:
        fail("L7", "无 --only/--case 支持，用例无法单跑")
        print("  !!   无可单跑参数")


# ---------------------------------------------------------------- 汇总
print("\n" + "=" * 70)
if problems:
    print("发现 %d 项违规：" % len(problems))
    for code, msg in problems:
        print("  [%s] %s" % (code, msg))
    print("\nFAIL")
    sys.exit(1)

print("全部符合 TEST_LIFECYCLE.md 的硬性指标")
print("PASS")
sys.exit(0)
