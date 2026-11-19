#!/usr/bin/env python3
# -*- coding: utf-8 -*-
r"""测试套件的入口引导：在**导入配置工具之前**把产品数据目录改道到临时目录。

为什么单独一个模块、而且必须在 import config_guard **之前** 用
---------------------------------------------------------------
`config_guard.local_config_path()` 是在**导入时**按环境变量算出来并缓存进
`ConfigGuard` 实例字段的（每个套件第一件事就是 `ConfigGuard(APP)`）。所以改道
变量必须在那一刻之前就已经在 `os.environ` 里，否则快照 / 清空 / 还原会指向
用户真实的那份配置 —— 而产品实际写的是临时目录里那份，两边永远对不上。

用法（放在套件脚本的最上面，紧跟 `sys.path.insert` 之后）：

    from test_env import isolate_data_root
    DATA_ROOT = isolate_data_root("regress")

    from config_guard import ConfigGuard, ...     # 之后的导入都看到改道后的根

返回的就是这个套件独占的数据根目录（%TEMP%\Auto7zTest_data\<套件名>）。
`config_guard.local_config_path()` 随后算出的路径就落在它下面。

为什么每个套件一个子目录而不是共用一个
--------------------------------------
同一套件内的用例要能看到上一轮写出来的配置（T20「配置损坏后能否重建」这类
用例就是这么设计的），所以套件内部的根必须稳定；但两个不同套件同时跑时，
如果共用同一个根，一个套件的配置会被另一个套件覆盖 —— 那正是历史上"并行跑
出 23 项假失败"的同一个坑。互斥体已经禁止并行，这里再按套件名分一层，
让"即使互斥体失效也不会串"这件事成立。
"""
import os
import shutil
from pathlib import Path

# 与 config_guard.DATA_ROOT_ENV 一致。这里各自写一份而不是 import：
# 本模块必须能在 config_guard 之前被导入，不能反过来依赖它。
DATA_ROOT_ENV = "AUTO7Z_DATA_ROOT"

# 重置时的删除边界：只认 %TEMP% 下带标记的那一棵树。
MARKER = "Auto7zTest_data"


def isolated_root(suite):
    """算出（不创建）某个套件的隔离数据根目录。"""
    return Path(os.environ.get("TEMP", r"C:\Windows\Temp")) / MARKER / str(suite)


def _assert_safe(path):
    """删除前确认目标确实是受控临时目录：在 %TEMP% 之下、且路径带标记串。"""
    temp = Path(os.environ.get("TEMP", r"C:\Windows\Temp")).resolve()
    target = path.resolve()
    if not str(target).lower().startswith(str(temp).lower()):
        raise RuntimeError("拒绝删除 %%TEMP%% 之外的目录: %s" % target)
    if MARKER not in target.parts:
        raise RuntimeError("拒绝删除不带标记串的目录: %s" % target)


def isolate_data_root(suite, reset=True):
    """把产品数据目录改道到 %TEMP%\\Auto7zTest_data\\<suite>，并设置环境变量。

    reset=True 时先清掉上一轮遗留的内容（默认）：套件之间不共享状态，
    但**同一个套件内部**的用例仍然能读到彼此写出的配置。

    返回本套件的数据根目录（已存在）。

    这里连数据目录本身（`<根>\\Auto7z`）一起建出来：不少用例在启动产品**之前**
    就要 `set_cfg(...)` 直接写配置，父目录不存在会抛 FileNotFoundError。
    预先建一个空目录不会改变产品行为 —— 它本来就会在启动时创建这个目录。
    """
    root = isolated_root(suite)

    if reset and root.exists():
        _assert_safe(root)
        shutil.rmtree(str(root), ignore_errors=True)

    (root / "Auto7z").mkdir(parents=True, exist_ok=True)
    os.environ[DATA_ROOT_ENV] = str(root)
    return root


def child_env(suite=None, base=None):
    """给子进程用的 env 副本：带上改道变量，可选地带上「互斥体已被父进程持有」。

    base 传 config_guard.child_env_with_inherited_checkout() 就能同时满足
    「嵌套调用的互斥体继承」和「数据目录隔离」两件事。
    """
    env = dict(base) if base is not None else os.environ.copy()
    if env.get(DATA_ROOT_ENV):
        return env

    root = isolated_root(suite) if suite else Path(env.get(DATA_ROOT_ENV, ""))
    if suite:
        root.mkdir(parents=True, exist_ok=True)
        env[DATA_ROOT_ENV] = str(root)
    return env
