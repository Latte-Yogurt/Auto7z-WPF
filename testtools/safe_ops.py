#!/usr/bin/env python3
# -*- coding: utf-8 -*-
r"""测试用的安全删除与临时目录管理。

背景
----
这个项目出过一次 P0 事故：测试脚本传参错误 + 删除边界模糊，
把测试工具自己的源码全删了，测试全线崩溃。

所以这里的删除一律走白名单校验：

1. 目标必须在**受控根目录**之内（默认 %TEMP%，也可以显式指定别的根）；
2. 目标路径里必须含**标记串**（默认 "Auto7zTest"），避免误删同名目录；
3. 目标不能是受控根目录本身，更不能是根的上级；
4. 目录里如果有源码类文件（.cs/.py/.cpp/.h/.xaml/.csproj/.sln 等），
   直接拒绝删除 —— 测试产物里不该有这些东西，出现了说明路径搞错了；
5. 任何一条不满足就抛异常，绝不"尽力删一点"。

用法
----
    from safe_ops import SafeTemp, safe_rmtree

    tmp = SafeTemp("regress")            # %TEMP%\Auto7zTest_regress
    tmp.path                             # 用它
    tmp.cleanup()                        # 所有测试跑完后统一清理

    safe_rmtree(some_path)               # 带白名单校验的删除
"""
import os
import shutil
import sys
import time
from pathlib import Path

# 受控根目录：所有临时产物都必须在这些目录之下
DEFAULT_ROOTS = [Path(os.environ.get("TEMP", r"C:\Windows\Temp"))]

# 标记串：目标路径里必须含这个才允许删
MARKER = "Auto7zTest"

# 源码类扩展名：目录里有这些文件就说明路径搞错了，不能删
SOURCE_EXTS = {
    ".cs", ".py", ".cpp", ".h", ".xaml", ".csproj", ".sln", ".xaml.cs",
    ".def", ".rc", ".md", ".json", ".resx", ".settings", ".user", ".ico", ".png",
}


class UnsafeDeleteError(Exception):
    """目标路径不满足删除白名单要求。"""


def _norm(p):
    """把路径规整成绝对、解析过符号链接的形态。"""
    return Path(os.path.abspath(os.path.realpath(str(p))))


def is_under(child, parent):
    """child 是否在 parent 之内（不含 parent 本身）。"""
    try:
        child.relative_to(parent)
        return True
    except ValueError:
        return False


def check_deletable(target, roots=None, marker=MARKER):
    """校验目标是否允许删除。不允许就抛 UnsafeDeleteError。

    返回校验通过的原因说明，方便写进报告。
    """
    roots = [ _norm(r) for r in (roots or DEFAULT_ROOTS) ]
    t = _norm(target)

    # 1) 必须在受控根之内
    ok_root = False
    for r in roots:
        if is_under(t, r):
            ok_root = True
            break
    if not ok_root:
        raise UnsafeDeleteError(
            "目标不在受控根目录内: %s\n  受控根: %s" % (t, ", ".join(str(r) for r in roots)))

    # 2) 不能是根本身
    for r in roots:
        if t == r:
            raise UnsafeDeleteError("目标是受控根目录本身, 拒绝删除: %s" % t)

    # 3) 必须含标记串
    if marker and marker.lower() not in str(t).lower():
        raise UnsafeDeleteError(
            "目标路径里没有标记串 %r, 拒绝删除: %s" % (marker, t))

    # 4) 目录里有源码类文件就拒绝
    if t.is_dir():
        bad = []
        try:
            for f in t.rglob("*"):
                if f.is_file() and (f.suffix.lower() in SOURCE_EXTS
                                    or f.name.lower().endswith(".xaml.cs")):
                    bad.append(f)
                    if len(bad) >= 5:
                        break
        except OSError:
            pass
        if bad:
            raise UnsafeDeleteError(
                "目标目录里有源码类文件, 拒绝删除（路径可能搞错了）: %s\n  例如: %s"
                % (t, ", ".join(str(b) for b in bad[:5])))

    return "已通过白名单校验（根=%s, 标记=%s）" % (
        next(str(r) for r in roots if is_under(t, r)), marker)


def safe_rmtree(target, roots=None, marker=MARKER, ignore_errors=True, retries=3):
    """带白名单校验地删除目录。目标不存在时什么都不做。

    返回校验说明；不允许删除时抛 UnsafeDeleteError。
    """
    t = _norm(target)
    if not t.exists():
        return "目标不存在, 跳过: %s" % t
    reason = check_deletable(t, roots, marker)

    # 文件被占用（子进程刚退出、句柄还没放）时重试几次，
    # 而不是直接抛错让整个测试收尾崩掉。
    last = None
    for attempt in range(retries):
        try:
            shutil.rmtree(str(t), ignore_errors=ignore_errors)
            if not t.exists():
                return reason + " -> 已删除"
            last = "删除后目录仍在"
        except OSError as e:
            last = str(e)
        time.sleep(0.3)

    if t.exists():
        # 最后再试一次，这次让它把错误说清楚
        try:
            shutil.rmtree(str(t), ignore_errors=False)
            return reason + " -> 已删除"
        except OSError as e:
            return reason + " -> !! 没能删干净: %s" % e
    return reason + " -> 已删除"


def safe_unlink(target, roots=None, marker=MARKER):
    """带白名单校验地删除单个文件。"""
    t = _norm(target)
    if not t.exists():
        return "目标不存在, 跳过: %s" % t
    reason = check_deletable(t, roots, marker)
    t.unlink()
    return reason + " -> 已删除"


class SafeTemp:
    """受控的临时目录。

    用法:
        tmp = SafeTemp("regress")
        # ... 测试 ...
        tmp.cleanup()        # 所有测试跑完后再统一清理
    """

    def __init__(self, name, roots=None, marker=MARKER):
        self.roots = [ _norm(r) for r in (roots or DEFAULT_ROOTS) ]
        self.marker = marker
        self.name = name
        base = self.roots[0]
        self.path = base / ("%s_%s" % (marker, name))
        self._created = False

    def ensure(self):
        """建好目录并返回它。已存在就清空重来。"""
        if self.path.exists():
            safe_rmtree(self.path, self.roots, self.marker)
        self.path.mkdir(parents=True, exist_ok=True)
        self._created = True
        return self.path

    def sub(self, name):
        """在临时目录里开一个子目录。"""
        p = self.path / name
        p.mkdir(parents=True, exist_ok=True)
        return p

    def cleanup(self):
        """清理整个临时目录。校验不通过就报告，不强删。"""
        if not self.path.exists():
            return "临时目录不存在, 跳过"
        try:
            return safe_rmtree(self.path, self.roots, self.marker)
        except UnsafeDeleteError as e:
            return "!! 清理被拒绝: %s" % e


def report_and_exit(errors):
    """统一处理校验失败：打印清楚再退出，不要静默继续。"""
    for e in errors:
        print("!! %s" % e, file=sys.stderr)
    if errors:
        sys.exit(2)
