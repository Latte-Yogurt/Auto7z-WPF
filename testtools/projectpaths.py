#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""项目路径解析：让所有测试工具在任何开发环境/CI 上都能找到被测源码。

背景（2.5.1 发布审查 §问题 6）：
    早期版本的检查脚本把
        C:\\Users\\Mocha\\Desktop\\VSProject\\Auto7z Project\\...
    这样的绝对路径写死在默认值里。后果有两类：

      1. 换一台机器就跑不起来 —— 但至少会明显报错；
      2. 更危险的是「路径不存在 → 扫到 0 个文件 → 什么都不报 → 退出码 0」，
         把「没检查到」伪装成「检查通过」。

    第 2 类是这个模块存在的主要理由。任何脚本都不应该在没有实际扫描到文件时
    返回成功。

用法：
    from projectpaths import project_root, source_dir, require_dir, guard_scan

    SRC = source_dir()                      # 自动定位；尊重命令行第一个参数
    SRC = source_dir(sys.argv[1] if len(sys.argv) > 1 else None)

定位规则（按顺序，第一个命中的为准）：
    1. 显式传入的路径（命令行参数最高优先）
    2. 环境变量 AUTO7Z_PROJECT_ROOT
    3. 从本文件所在目录向上找，直到看见 Auto7z_WPF 与 testtools 并列
    4. 从当前工作目录向上找，同样的判据
"""
import os
import sys
from pathlib import Path

# 判定「这是项目根」的标志：这两个目录同时存在
_ROOT_MARKERS = ("Auto7z_WPF", "testtools")

# 源码目录相对项目根的路径
_SRC_REL = Path("Auto7z_WPF") / "Auto7z_WPF"

__all__ = [
    "project_root",
    "source_dir",
    "shell_src_dir",
    "require_dir",
    "require_file",
    "guard_scan",
    "TT",
]


def _looks_like_root(p):
    """p 是否是项目根：两个标志目录都在。"""
    try:
        return all((p / m).is_dir() for m in _ROOT_MARKERS)
    except OSError:
        return False


def _walk_up(start):
    """从 start 开始向上找项目根，找不到返回 None。"""
    try:
        cur = Path(start).resolve()
    except OSError:
        return None
    for candidate in [cur] + list(cur.parents):
        if _looks_like_root(candidate):
            return candidate
    return None


def project_root(override=None):
    """返回项目根目录（含 Auto7z_WPF 与 testtools 的那一层）。

    找不到时直接退出，退出码 2 —— 绝不返回一个不存在的路径让调用方
    继续扫出一个空结果集。
    """
    if override:
        p = Path(override)
        if _looks_like_root(p):
            return p
        # 调用方可能传的是源码目录本身，往上退一层也认
        if _looks_like_root(p.parent):
            return p.parent
        _die("指定的项目根不存在或缺少 Auto7z_WPF / testtools: %s" % p)

    env = os.environ.get("AUTO7Z_PROJECT_ROOT")
    if env:
        p = Path(env)
        if _looks_like_root(p):
            return p
        _die("环境变量 AUTO7Z_PROJECT_ROOT 指向的不是项目根: %s" % p)

    found = _walk_up(Path(__file__).resolve().parent) or _walk_up(Path.cwd())
    if found:
        return found
    _die("无法自动定位项目根（找 Auto7z_WPF 与 testtools 并列的目录）。"
         "请用 AUTO7Z_PROJECT_ROOT 环境变量或命令行参数显式指定。")


def source_dir(override=None, root=None):
    """返回被测 C# 源码目录 Auto7z_WPF/Auto7z_WPF。"""
    if override:
        p = Path(override)
        if not p.is_dir():
            _die("源码目录不存在: %s" % p)
        return p
    base = Path(root) if root else project_root()
    p = base / _SRC_REL
    if not p.is_dir():
        _die("源码目录不存在: %s" % p)
    return p


def shell_src_dir(override=None, root=None):
    """返回外壳扩展源码目录 Auto7zShell/src。"""
    base = Path(root) if root else project_root()
    p = Path(override) if override else base / "Auto7zShell" / "src"
    if not p.is_dir():
        _die("外壳扩展源码目录不存在: %s" % p)
    return p


def require_dir(path, label="目录"):
    """确认目录存在，否则以退出码 2 结束。"""
    p = Path(path)
    if not p.is_dir():
        _die("%s不存在: %s" % (label, p))
    return p


def require_file(path, label="文件"):
    """确认文件存在，否则以退出码 2 结束。"""
    p = Path(path)
    if not p.is_file():
        _die("%s不存在: %s" % (label, p))
    return p


def guard_scan(items, label="扫描", minimum=1):
    """扫描结果为空时以退出码 2 结束。

    这是本模块的核心约束：**扫到 0 个文件不等于检查通过。**
    调用方拿到列表后必须过一遍这个函数，再决定退出码。
    """
    items = list(items)
    if len(items) < minimum:
        _die("%s 没有扫描到任何文件（期望至少 %d 个）。"
             "这通常意味着路径配置错误，不能当作检查通过。" % (label, minimum))
    return items


def _die(msg):
    """统一的失败出口：写 stderr，退出码 2。"""
    sys.stderr.write("路径错误: %s\n" % msg)
    raise SystemExit(2)


# 本文件所在目录 = testtools，个别断言查的就是测试工具自己的源码
TT = Path(__file__).resolve().parent
