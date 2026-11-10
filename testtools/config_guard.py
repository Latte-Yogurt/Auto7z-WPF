#!/usr/bin/env python3
# -*- coding: utf-8 -*-
r"""测试用的配置隔离与运行闸门。

为什么需要它
------------
被测程序把状态存在这几个地方，测试跑完必须原样还回去，否则下一轮测试
（甚至用户自己跑程序）会读到一个被测试改过的配置：

  1. %LOCALAPPDATA%\Auto7z\Auto7z.json        普通模式配置
  2. <exe 目录>\.config\Auto7z\              便携模式的数据目录
  3. HKCU\Software\Auto7z                     界面语言（只影响右键菜单文字）

其中 2 是最阴的一个：**只要 exe 旁边存在 .config\Auto7z\Auto7z.json，产品就
判定自己是便携版，完全无视 %LOCALAPPDATA% 那份配置**，连 7-Zip 组件和日志
都搬到 exe 旁边。实测确认过：留一份这样的文件在那儿，之后每一条测试读到的
都是它，而测试改的却是 %LOCALAPPDATA% 那份 —— 表现成一大片查不出原因的失败。

另外，产品是单实例的，而且所有测试共用同一份配置。两个测试同时跑会互相
改对方的配置、还会把任务交接给对方的实例（实测并行跑出的 23 项失败全是假失败）。
`claim_checkout()` 用一个命名互斥体把这个坑堵死 —— 四个套件共用**同一个名字**
（`Auto7zTest_shared`），因为它们驱动的是同一个单实例产品，名字不同等于没有保护。

嵌套调用要能继承，否则会把父进程锁在外面
------------------------------------------
`run_system_test.py` 会以子进程方式调用 `splitvol_test.py`。父进程持有互斥体时，
子进程去抢同名互斥体必然失败、直接退出 —— 分卷那项就永远跑不成。
所以父进程启动子进程时传 `AUTO7Z_TEST_CHECKOUT_INHERITED=1`，
子进程看到它就跳过抢锁（见 `child_env_with_inherited_checkout()`）。
这个标记只用于嵌套，不要拿它绕过并行保护。

用法
----
    from config_guard import (ConfigGuard, claim_checkout, release_checkout,
                              child_env_with_inherited_checkout)

    mutex = claim_checkout()
    if mutex is None: 退出（已有别的套件在跑）

    guard = ConfigGuard(app_path)          # app_path 是被测 exe
    if not guard.prepare(clear=False):     # 检测便携配置等
        退出

    token = guard.join_chain()             # 加入配置链
    try:
        ... 跑测试 ...
    finally:
        guard.leave_chain(token)           # 只有最后一个离开时才真正还原
        release_checkout(mutex)

启动子测试脚本（嵌套）时：

    subprocess.run([sys.executable, "splitvol_test.py", "--app", app],
                   env=child_env_with_inherited_checkout())

单独使用（手工清空配置，跑完还原）：

    python config_guard.py --app "<exe>" --clear
    python config_guard.py --app "<exe>" --purge-portable   # 删掉 exe 旁的便携数据

安全边界
--------
这里动的是**真实配置**，不是临时产物，所以不能套 safe_ops 的 %TEMP% 白名单。
改用「路径必须与计算结果逐字相等」的窄校验：只认那两个算出来的绝对路径，
不接受通配、不接受拼出来的近似路径，删除前还要确认目录里没有源码类文件。
"""

import argparse
import ctypes
import json
import os
import shutil
import sys
from pathlib import Path

# 数据目录名与配置文件名：跟产品里的 AppPaths.DataFolderName / ConfigFileName 对齐。
DATA_FOLDER = "Auto7z"
CONFIG_FILE = "Auto7z.json"
PORTABLE_ROOT = ".config"

# 源码类扩展名：目录里有这些就说明路径是源码树而不是产品数据目录，拒绝删除。
SOURCE_EXTS = {
    ".cs", ".py", ".cpp", ".h", ".xaml", ".csproj", ".sln", ".def", ".rc",
}


class ConfigGuardError(Exception):
    """配置隔离的自检没通过。"""


def _norm(p):
    return Path(os.path.abspath(os.path.realpath(str(p))))


def local_config_path():
    return _norm(Path(os.environ["LOCALAPPDATA"]) / DATA_FOLDER / CONFIG_FILE)


def portable_data_dir(exe_path):
    r"""exe 旁边的便携数据目录：<exe 目录>\.config\Auto7z"""
    return _norm(Path(exe_path).parent / PORTABLE_ROOT / DATA_FOLDER)


def portable_config_path(exe_path):
    return portable_data_dir(exe_path) / CONFIG_FILE


def _looks_like_source_tree(directory):
    try:
        for f in Path(directory).rglob("*"):
            if f.is_file() and f.suffix.lower() in SOURCE_EXTS:
                return True
    except OSError:
        pass
    return False


# 四个套件共用同一个互斥体：产品是单实例的，两套测试同时跑会互相抢实例、
# 把任务交接给对方，症状是一大片查不出原因的假失败（实测并行跑出过 23 项假失败）。
# 只隔离「配置」是不够的 —— 互斥体也必须统一，否则两个套件仍会同时驱动产品。
CHECKOUT_NAME = "Auto7zTest_shared"

# 嵌套调用的继承标记。run_system_test.py 会以子进程方式调用 splitvol_test.py，
# 子进程若去抢同一个互斥体必然失败（父进程正持有），于是会直接退出 ——
# 分卷那项就永远跑不成。父进程在启动子进程时把这个变量传下去，
# 子进程看到它就跳过抢锁，直接认为"已持有"。
CHECKOUT_INHERITED_ENV = "AUTO7Z_TEST_CHECKOUT_INHERITED"


def claim_checkout(name=CHECKOUT_NAME):
    """拿一个命名互斥体当「同时只跑一个测试」的闸门。

    返回句柄（调用方负责 CloseHandle），已被别的测试占用时返回 None。
    嵌套调用（父进程已持有、并通过环境变量声明）时返回整数 0 作为占位句柄，
    release_checkout 对 0 不做任何事。
    """
    if os.environ.get(CHECKOUT_INHERITED_ENV) == "1":
        return 0

    kernel32 = ctypes.windll.kernel32
    handle = kernel32.CreateMutexW(None, True, "Global\\%s" % name)
    if not handle:
        return None
    # 已存在时 GetLastError 是 ERROR_ALREADY_EXISTS = 183
    if kernel32.GetLastError() == 183:
        kernel32.CloseHandle(handle)
        return None
    return handle


def release_checkout(handle):
    if handle:
        ctypes.windll.kernel32.CloseHandle(handle)


def child_env_with_inherited_checkout():
    """给子进程用的环境变量副本：声明"互斥体已由父进程持有"。

    只给嵌套调用用（父进程调子测试脚本），不要用它来绕过并行保护。
    """
    env = os.environ.copy()
    env[CHECKOUT_INHERITED_ENV] = "1"
    return env


class ConfigGuard(object):
    """被测程序配置的隔离与还原。

    snapshot()  原样保留配置字节（含换行符），restore() 一对一还回去
    clear()     删掉配置，让产品下次启动重建一份默认值（这才是「清空配置」）
    purge_portable()  删掉 exe 旁的便携数据目录

    链式用法（多个测试接着跑时用这套，不要各自还原）:
        guard = ConfigGuard(app)
        token = guard.join_chain()      # 第一个进来的负责记下原始配置
        try:
            ... 跑测试 ...
        finally:
            guard.leave_chain(token)    # 只有最后一个离开时才真正还原
    """

    # 链式会话的共享状态文件。放在 %TEMP% 下，带固定标记，
    # 让"谁先来、谁最后走"这种跨进程协调有个落地的地方。
    CHAIN_FILE_NAME = "Auto7zTest_config_chain.json"

    def __init__(self, exe_path, verbose=True):
        self.exe_path = Path(exe_path)
        self.exe_dir = self.exe_path.parent
        self.local_cfg = local_config_path()
        self.portable_dir = portable_data_dir(exe_path)
        self.portable_cfg = self.portable_dir / CONFIG_FILE
        self.verbose = verbose

    # ------------------------------------------------------------ 链式会话
    @staticmethod
    def _chain_path():
        return Path(os.environ.get("TEMP", r"C:\Windows\Temp")) / ConfigGuard.CHAIN_FILE_NAME

    def join_chain(self):
        """加入配置链，返回本次的令牌（进程 pid）。

        第一个进来的工具把**当时的原始配置**落到链文件里；后续工具只登记自己的 pid，
        绝不覆盖那份原始快照 —— 这正是旧做法出问题的根源：每个工具都"还原到自己开始那刻"，
        而那一刻往往已经是被上一个工具改过的状态，于是越还原越偏。

        链文件读到损坏时按"没有链"处理并重建，不抛异常 —— 宁可多留一份快照也不要让测试跑不起来。
        """
        path = self._chain_path()
        try:
            data = json.loads(path.read_text(encoding="utf-8"))
        except Exception:
            data = None

        if not isinstance(data, dict) or "original" not in data:
            data = {
                # 原样保留字节（含换行符），base64 存，避免任何编码/换行转换
                "original": self._encode_snapshot(self.snapshot()),
                "members": [],
            }

        pid = os.getpid()
        members = data.get("members") or []
        if pid not in members:
            members.append(pid)
        data["members"] = members

        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(json.dumps(data, ensure_ascii=False), encoding="utf-8")
        self._chain_path_cache = path
        return pid

    def leave_chain(self, token):
        """离开配置链。只有最后一个成员离开时才把原始配置还回去。"""
        path = self._chain_path()
        try:
            data = json.loads(path.read_text(encoding="utf-8"))
        except Exception:
            return False

        members = [m for m in (data.get("members") or []) if m != token]
        data["members"] = members

        if members:
            # 还有人要跑：只把自己摘掉，原样保留快照给下一位
            path.write_text(json.dumps(data, ensure_ascii=False), encoding="utf-8")
            return False

        # 最后一个：还原，然后删掉链文件
        self.restore(self._decode_snapshot(data.get("original")))
        try:
            path.unlink()
        except OSError:
            pass
        return True

    @staticmethod
    def _encode_snapshot(state):
        """把 snapshot() 的字节转成可 JSON 存的形式。None 表示"原本不存在"。"""
        import base64
        out = {}
        for key, payload in (state or {}).items():
            out[key] = None if payload is None else base64.b64encode(payload).decode("ascii")
        return out

    @staticmethod
    def _decode_snapshot(encoded):
        import base64
        if not isinstance(encoded, dict):
            return None
        out = {}
        for key, value in encoded.items():
            out[key] = None if value is None else base64.b64decode(value.encode("ascii"))
        return out

    # ------------------------------------------------------------ 现状描述
    def describe(self):
        lines = ["  普通配置: %s%s" % (self.local_cfg, "" if self.local_cfg.exists() else "（不存在）")]
        if self.portable_cfg.exists():
            lines.append("  !! 便携配置: %s" % self.portable_cfg)
            lines.append("     它存在时产品判定为便携版，会完全无视普通配置 —— 必须先清掉")
        return "\n".join(lines)

    # ------------------------------------------------------------ 自检
    def prepare(self, purge_portable=False):
        """跑测试之前的自检。

        便携配置在时会顶掉普通配置，测试改了半天其实改的是另一份。
        默认只报告并拒绝运行（静静删掉用户真实的便携数据太危险），
        显式给 purge_portable=True 才清掉，且清前要过窄校验。
        """
        if not self.portable_cfg.exists():
            return True

        if self.verbose:
            print(self.describe())

        if not purge_portable:
            print("")
            print("!! 检测到便携配置，测试会读到它而不是普通配置，结果不可信。")
            print("   处理办法二选一：")
            print("     python config_guard.py --app \"%s\" --purge-portable" % self.exe_path)
            print("     或给测试脚本加 --purge-portable")
            return False

        self.purge_portable()
        return True

    def purge_portable(self):
        """删掉 exe 旁的便携数据目录，删前做窄校验。"""
        target = self.portable_dir
        if not target.exists():
            return "便携数据目录不存在，跳过"

        # 窄校验：必须是算出来的那个路径、必须真有 Auto7z.json、不能是源码树。
        if _norm(target) != portable_data_dir(self.exe_path):
            raise ConfigGuardError("便携目录路径与计算值不符，拒绝删除: %s" % target)
        if not self.portable_cfg.exists():
            raise ConfigGuardError(
                "目录里没有 %s，不像是产品数据目录，拒绝删除: %s" % (CONFIG_FILE, target))
        if _looks_like_source_tree(target):
            raise ConfigGuardError("目录里有源码类文件，拒绝删除: %s" % target)

        shutil.rmtree(str(target), ignore_errors=True)
        if target.exists():
            raise ConfigGuardError("没能删干净便携数据目录: %s" % target)

        # 数据目录清掉后，.config 往往只剩一个空壳；留着它会让产品仍按便携版启动。
        portable_root = target.parent
        try:
            if portable_root.exists() and not any(portable_root.iterdir()):
                portable_root.rmdir()
        except OSError:
            pass

        return "已删除便携数据目录: %s" % target

    # ------------------------------------------------------------ 快照 / 清空
    def snapshot(self):
        """原样保留配置字节。

        用 text 模式读会把 CRLF 折成 LF，写回去就成了另一种换行 ——
        内容虽等价，但用户的配置文件不该被测试改动任何一个字节。
        """
        state = {
            "local": self.local_cfg.read_bytes() if self.local_cfg.exists() else None,
            "portable": self.portable_cfg.read_bytes() if self.portable_cfg.exists() else None,
        }
        return state

    def clear(self):
        """删掉配置，让产品重建默认值。

        只删这两个确定的路径：普通配置、便携配置。不碰日志、组件与其它文件 ——
        那些不是「配置」，删了只会让排查更困难。
        """
        removed = []
        if self.local_cfg.exists():
            self.local_cfg.unlink()
            removed.append(str(self.local_cfg))
        if self.portable_cfg.exists():
            self.portable_cfg.unlink()
            removed.append(str(self.portable_cfg))
        return removed

    # ------------------------------------------------------------ 还原
    def restore(self, state):
        """按快照原样还原。快照里没有（原本就不存在）的就删掉，避免留下测试产物。"""
        if state is None:
            return

        self._restore_one(self.local_cfg, state.get("local"))
        self._restore_one(self.portable_cfg, state.get("portable"))

    @staticmethod
    def _restore_one(path, payload):
        try:
            if payload is None:
                if path.exists():
                    path.unlink()
            else:
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_bytes(payload)
        except OSError as e:
            print("  !! 还原配置失败 (%s): %s" % (path, e))


# ---------------------------------------------------------------- 命令行用法

def main():
    ap = argparse.ArgumentParser(description="配置隔离：清空 / 还原被测程序的配置")
    ap.add_argument("--app", required=True, help="被测程序路径（Auto7z.exe）")
    ap.add_argument("--clear", action="store_true", help="清空配置（产品会重建默认值）")
    ap.add_argument("--purge-portable", action="store_true", help="删掉 exe 旁的便携数据目录")
    ap.add_argument("--show", action="store_true", help="只看现状，不动任何东西")
    args = ap.parse_args()

    app = Path(args.app)
    if not app.exists():
        print("找不到被测程序: %s" % app)
        return 2

    guard = ConfigGuard(app)

    if args.show or not (args.clear or args.purge_portable):
        print("=" * 70)
        print("配置现状")
        print("=" * 70)
        print(guard.describe())
        if not (args.clear or args.purge_portable):
            print("")
            print("加 --clear 清空配置、--purge-portable 删便携数据；两者都没有就只是查看。")
        return 0

    print("=" * 70)
    print("配置清理: %s" % app)
    print("=" * 70)
    print(guard.describe())

    if args.purge_portable:
        print("")
        print("  " + guard.purge_portable())

    if args.clear:
        removed = guard.clear()
        print("")
        if removed:
            for r in removed:
                print("  已删除: %s" % r)
            print("  下次启动由产品重建一份默认配置。")
        else:
            print("  没有可删的配置（本来就不存在）。")

    return 0


if __name__ == "__main__":
    sys.exit(main())
