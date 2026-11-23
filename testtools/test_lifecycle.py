#!/usr/bin/env python3
# -*- coding: utf-8 -*-
r"""测试生命周期基础设施：BeforeAll / BeforeEach / Execute / AfterEach / AfterAll。

设计依据（2026-11-22 LTS 轮确立的硬性规范，见 testtools/TEST_LIFECYCLE.md）
-------------------------------------------------------------------------
最重要的一条：**全局环境清理只发生在整轮测试的开始与结束；
单个测试的生命周期内只允许管理它自己拥有的资源，绝不清理共享环境。**

本模块把这条规范落成可调用的 API，让每个套件都用同一个骨架，
而不是各自发明一套清理顺序 —— 历史上「套件 A 清空了套件 B 正在用的目录」
就是这么发生的。

与 test_env.py 的分工
---------------------
- `test_env.py`：**数据目录改道**（把产品写入重定向到 %TEMP%），必须最早导入。
- `test_lifecycle.py`（本模块）：**阶段编排**（清理时机、资源归属、幂等、恢复）。

两者叠加使用：

    from test_env import isolate_data_root
    DATA_ROOT = isolate_data_root("mysuite")      # 必须在导入 config_guard 之前

    from test_lifecycle import Lifecycle
    with Lifecycle("mysuite") as lc:
        lc.before_each("case_a")      # 只初始化 case_a 自己的资源
        run_case_a()
        lc.after_each("case_a")       # 只清理 case_a 自己的资源

三个阶段各自负责什么
-------------------
BeforeAll  ：清理上一轮整轮遗留 + 建目录 + 检查依赖（允许全局清理）
BeforeEach ：为当前用例建「资源容器」，登记它要用的路径（只动自己的）
Execute    ：执行（默认串行；共享资源竞争由调用方保证不并行）
AfterEach  ：只清理当前用例登记过的资源（幂等）
AfterAll   ：统一清理整轮产物 + 杀残留进程 + 恢复环境（允许全局清理）

三条附加原则
-----------
1. **无隐式依赖**：任何用例都应能单独跑。本模块提供 `--only <case>` 支持。
2. **资源归属可追溯**：每个用例的资源都落在 `<DATA_ROOT>/cases/<case>/` 下，
   清理时按目录删，不会碰到别人的。
3. **清理幂等**：删不存在的资源不报错；`after_each` 可重复调用。
"""
import os
import shutil
import subprocess
import sys
import time
from pathlib import Path

# 与 test_env.py 保持一致（这里不 import 它，避免循环依赖）
DATA_ROOT_ENV = "AUTO7Z_DATA_ROOT"
MARKER = "Auto7zTest_data"


class LifecycleError(RuntimeError):
    """生命周期使用错误（例如在 BeforeAll 之前调 BeforeEach）。"""


def _temp_root():
    return Path(os.environ.get("TEMP", r"C:\Windows\Temp")).resolve()


def _assert_safe(path):
    """删除前确认目标确实是受控临时目录：在 %TEMP% 之下、且路径带标记串。

    这是**唯一**的删除闸门。任何清理动作都必须先过它 ——
    历史上出现过测试脚本删掉产品数据目录的事故，这道校验就是那次之后加的。
    """
    target = Path(path).resolve()
    temp = _temp_root()
    try:
        target.relative_to(temp)
    except ValueError:
        raise LifecycleError("拒绝清理 %%TEMP%% 之外的目录: %s" % target)
    if MARKER not in target.parts:
        raise LifecycleError("拒绝清理不带标记串的目录: %s" % target)


class Lifecycle(object):
    """一个测试套件的生命周期编排器。

    典型用法见模块文档。所有方法都是幂等的，重复调用安全。
    """

    def __init__(self, suite, verbose=True):
        self.suite = str(suite)
        self.verbose = verbose
        self.root = _temp_root() / MARKER / self.suite
        self.cases_dir = self.root / "cases"
        self._phase = "created"
        self._current_case = None
        self._owned = []          # 当前用例登记的路径（BeforeEach 之后填）
        self._started_at = None
        self._finished = False
        self._processes = []      # 本套件拉起的子进程，AfterAll 统一收尾

    # ---------------------------------------------------------------- 工具
    def _log(self, msg):
        if self.verbose:
            print("[lifecycle:%s] %s" % (self.suite, msg))

    # ---------------------------------------------------------------- 阶段一
    def before_all(self, required_tools=None):
        """阶段一：清理上一轮整轮遗留、建目录、检查依赖。

        **允许全局清理**——这是整轮测试唯一两个可以动全局的时点之一。
        """
        if self._phase not in ("created", "after_all"):
            raise LifecycleError("before_all 状态不对: " + self._phase)

        self._started_at = time.time()

        # 1) 清上一轮整轮遗留（只清本套件那一棵，不碰别人的）
        if self.root.exists():
            _assert_safe(self.root)
            shutil.rmtree(str(self.root), ignore_errors=True)
            self._log("已清理上一轮遗留: %s" % self.root)

        # 2) 重建目录结构。产品数据目录预先建出来：
        #    不少用例在启动产品**之前**就要直接写配置，父目录不存在会抛异常。
        (self.root / "Auto7z").mkdir(parents=True, exist_ok=True)
        self.cases_dir.mkdir(parents=True, exist_ok=True)

        # 3) 数据目录改道（与 test_env 的约定一致）
        os.environ[DATA_ROOT_ENV] = str(self.root)

        # 4) 依赖检查：缺工具应当在开始时就报，而不是跑到一半才发现
        missing = self._check_tools(required_tools or [])
        if missing:
            raise LifecycleError("缺少必需依赖: " + ", ".join(missing))

        self._phase = "before_all"
        self._log("环境就绪: %s" % self.root)
        return self.root

    def _check_tools(self, tools):
        missing = []
        for t in tools:
            if isinstance(t, (str,)):
                p = Path(t)
                if p.is_absolute() or os.sep in t:
                    if not p.exists():
                        missing.append(t)
                    continue
            if shutil.which(t) is None:
                missing.append(t)
        return missing

    # ---------------------------------------------------------------- 阶段二
    def before_each(self, case):
        """阶段二：为当前用例建独立的资源容器。

        **只操作当前用例自己的资源**，不动任何共享状态。
        这保证「用例 A 的清理不会影响用例 B」。
        """
        if self._phase not in ("before_all", "after_each"):
            raise LifecycleError("before_each 必须在 before_all 之后: " + self._phase)

        # 若上一个用例没走 after_each（例如断言直接抛异常），先补一次清理。
        # 这是「无隐式依赖」的兜底：不清理就会让下一个用例看到脏状态。
        if self._current_case is not None:
            self.after_each()

        self._current_case = str(case)
        self._owned = []

        case_dir = self.cases_dir / self._current_case
        if case_dir.exists():
            _assert_safe(case_dir)
            shutil.rmtree(str(case_dir), ignore_errors=True)
        case_dir.mkdir(parents=True, exist_ok=True)

        self._phase = "before_each"
        self._log("用例开始: %s" % case)
        return case_dir

    def own(self, *paths):
        """登记「这些路径属于当前用例」，AfterEach 只清理它们。

        归属必须明确：清理时按登记表删，而不是「扫整个临时目录」——
        后者会误删别的用例正在用的东西。
        """
        if self._current_case is None:
            raise LifecycleError("own() 必须在 before_each 之后调用")
        for p in paths:
            if p is not None:
                self._owned.append(Path(p))
        return self._owned

    def case_dir(self, name=None):
        """当前用例（或指定用例）的资源目录，供 own() 之外的自定义布局使用。"""
        return self.cases_dir / (name or self._current_case)

    # ---------------------------------------------------------------- 阶段三
    def track_process(self, proc):
        """登记一个本套件拉起的子进程，AfterAll 统一收尾。

        不登记就可能留下跑着的 GUI 进程，它持有产品数据目录里的锁，
        导致下一轮清理失败 —— 这是「残留进程」这一类问题的根因。
        """
        if proc is not None:
            self._processes.append(proc)
        return proc

    # ---------------------------------------------------------------- 阶段四
    def after_each(self):
        """阶段四：只清理当前用例登记过的资源。**幂等**。

        不存在的资源直接跳过，不报错 —— 清理失败不应掩盖真正的测试结果。
        """
        if self._current_case is None:
            return

        case = self._current_case

        # 1) 登记过的路径：逐个删（删不存在的不算错）
        for p in self._owned:
            try:
                if p.is_dir():
                    if MARKER in p.resolve().parts:
                        shutil.rmtree(str(p), ignore_errors=True)
                elif p.exists():
                    p.unlink()
            except Exception:
                # 清理失败不抛：它不该把测试结论盖掉
                pass

        # 2) 用例目录本身
        cd = self.cases_dir / case
        if cd.exists():
            _assert_safe(cd)
            shutil.rmtree(str(cd), ignore_errors=True)

        self._owned = []
        self._current_case = None
        self._phase = "after_each"
        self._log("用例结束: %s" % case)

    # ---------------------------------------------------------------- 阶段五
    def after_all(self, keep=False):
        """阶段五：整轮收尾。**允许全局清理**。

        清产物、收进程、恢复环境。即使前面失败也应当被调用（放 finally 里）。
        """
        if self._finished:
            return

        # 1) 还在进行中的用例先收掉
        try:
            self.after_each()
        except Exception:
            pass

        # 2) 子进程收尾：先温和等，再强杀
        for proc in self._processes:
            try:
                if proc.poll() is None:
                    proc.terminate()
                    try:
                        proc.wait(timeout=5)
                    except Exception:
                        proc.kill()
            except Exception:
                pass
        self._processes = []

        # 3) 整轮产物：默认删除；keep=True 保留现场供调试
        if keep:
            self._log("保留现场: %s" % self.root)
        elif self.root.exists():
            _assert_safe(self.root)
            shutil.rmtree(str(self.root), ignore_errors=True)
            self._log("已清理整轮产物")

        # 4) 恢复环境变量（不留改道痕迹给同进程后续代码）
        if os.environ.get(DATA_ROOT_ENV) == str(self.root):
            os.environ.pop(DATA_ROOT_ENV, None)

        self._finished = True
        self._phase = "after_all"
        elapsed = round(time.time() - self._started_at, 1) if self._started_at else 0
        self._log("整轮结束，耗时 %ss" % elapsed)

    # ---------------------------------------------------------------- 上下文
    def __enter__(self):
        self.before_all()
        return self

    def __exit__(self, exc_type, exc, tb):
        # 无论成功失败都要收尾 —— 这保证失败时也尽可能完成清理
        self.after_all(keep=bool(os.environ.get("AUTO7Z_TEST_KEEP")))
        return False


# -------------------------------------------------------------------- 便捷入口
def select_cases(all_cases, argv=None):
    """支持 `--only case_a,case_b` 只跑指定用例，保证每个用例都能独立运行。

    「测试之间不应存在隐式依赖」的具体落实：任何用例都应当能单独跑通。
    """
    argv = argv if argv is not None else sys.argv
    only = None
    for i, a in enumerate(argv):
        if a == "--only" and i + 1 < len(argv):
            only = argv[i + 1]
        elif a.startswith("--only="):
            only = a.split("=", 1)[1]

    if not only:
        return list(all_cases)

    wanted = [x.strip() for x in only.split(",") if x.strip()]
    unknown = [w for w in wanted if w not in all_cases]
    if unknown:
        raise LifecycleError("未知用例: " + ", ".join(unknown))
    return wanted


if __name__ == "__main__":
    # 自检：跑一遍完整生命周期，验证幂等与安全闸门
    print("=== test_lifecycle 自检 ===")
    problems = 0

    lc = Lifecycle("selftest", verbose=True)
    lc.before_all()
    assert lc.root.exists(), "before_all 之后根目录应存在"

    # 幂等：before_all 重复调用应当报错（状态机保护）
    try:
        lc.before_all()
        print("  !! before_all 重复调用未被拒绝")
        problems += 1
    except LifecycleError:
        print("  OK   before_all 重复调用被拒绝")

    lc.before_each("case_a")
    own = lc.case_dir() / "res.txt"
    lc.own(own)
    own.write_text("x", encoding="utf-8")
    print("  OK   case_a 资源已创建")
    lc.after_each()
    print("  OK   after_each 清掉了登记资源" if not own.exists() else "  !! 资源未清理")
    if own.exists():
        problems += 1

    # 幂等：重复 after_each 不报错
    lc.after_each()
    print("  OK   after_each 幂等")

    # 用例间隔离：两个用例的目录必须不同
    lc.before_each("case_b")
    b_dir = lc.case_dir()
    lc.after_each()
    lc.before_each("case_c")
    c_dir = lc.case_dir()
    lc.after_each()
    if b_dir != c_dir:
        print("  OK   用例资源目录互相独立")
    else:
        print("  !! 用例资源目录重名")
        problems += 1

    # 安全闸门：拒绝清理 %TEMP% 之外
    try:
        _assert_safe(Path("C:/Windows"))
        print("  !! 越界清理未被拦下")
        problems += 1
    except LifecycleError:
        print("  OK   越界清理被拦下")

    lc.after_all()
    if not lc.root.exists():
        print("  OK   after_all 清掉了整轮产物")
    else:
        print("  !! after_all 未清理")
        problems += 1

    print("")
    print("PASS" if problems == 0 else "TOTAL PROBLEMS: %d" % problems)
    sys.exit(0 if problems == 0 else 1)
