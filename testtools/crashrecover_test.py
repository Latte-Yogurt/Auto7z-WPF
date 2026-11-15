#!/usr/bin/env python3
# -*- coding: utf-8 -*-
r"""崩溃残留清理专项测试（只测试，不改产品代码）。

针对「强制结束进程留下分卷残片」的修复做回归保护。产品在任务开始前写
TaskState.json（JobId/JobState/JobOutputDir/JobStartedAt）+ 独占创建 job.lock，
正常结束写 state=done 并删锁；启动时若发现锁 + active 且锁里 PID 已死，
则精确清理残片目录。

覆盖的崩溃点（对应设计文档的恢复表）：

  A   锁已建、JSON 未写         → 锁在 + JSON 无 job → 保守清理，删锁，不删文件
  B   JSON active、分卷生成中   → 精确清理输出目录，写 done，删锁
  C'  done 已写、锁未删         → 仅删锁，不动完整产物
  D   JSON 损坏且 .bak 也坏     → 保守清理（不删任何文件）
  F   锁里 PID 还活着           → 跳过清理（第二实例不误删进行中任务）

用法:
    python crashrecover_test.py                    # 默认 Debug 构建
    python crashrecover_test.py --app <Auto7z.exe>
    python crashrecover_test.py --keep             # 保留临时目录（调试用）

退出码: 0 = 全部通过, 1 = 有失败项, 2 = 环境不满足
"""
import argparse
import json
import os
import shutil
import subprocess
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from safe_ops import SafeTemp, safe_rmtree
from config_guard import ConfigGuard, claim_checkout, release_checkout

HERE = Path(__file__).resolve().parent
WORKSPACE = HERE.parent
PROJECT = WORKSPACE / "Auto7z_WPF" / "Auto7z_WPF"

LOCAL = Path(os.environ["LOCALAPPDATA"]) / "Auto7z"
STATE_FILE = LOCAL / "TaskState.json"
STATE_BAK = Path(str(STATE_FILE) + ".bak")
LOCK_FILE = LOCAL / "job.lock"

TEMP = SafeTemp("crashrecover")


class Result(object):
    def __init__(self):
        self.items = []
        self.failed = 0

    def add(self, name, ok, detail=""):
        self.items.append((name, ok, detail))
        if not ok:
            self.failed += 1
        print("  [%s] %-34s %s" % ("通过" if ok else "失败", name, detail))

    def summary(self):
        print("")
        print("=" * 70)
        print("崩溃恢复测试汇总: %d 项, 失败 %d 项" % (len(self.items), self.failed))
        print("=" * 70)
        for name, ok, detail in self.items:
            print("  [%s] %-34s %s" % ("通过" if ok else "失败", name, detail))


def read_state():
    try:
        return json.loads(STATE_FILE.read_text(encoding="utf-8"))
    except Exception:
        return None


def write_state_raw(text):
    STATE_FILE.parent.mkdir(parents=True, exist_ok=True)
    STATE_FILE.write_text(text, encoding="utf-8")


def read_lock():
    try:
        return LOCK_FILE.read_text(encoding="utf-8").splitlines()
    except Exception:
        return []


def write_lock(lines):
    LOCK_FILE.parent.mkdir(parents=True, exist_ok=True)
    LOCK_FILE.write_text("\n".join(lines) + "\n", encoding="utf-8")


def cleanup_state_files():
    for f in (STATE_FILE, STATE_BAK, LOCK_FILE):
        try:
            if f.exists():
                f.unlink()
        except OSError:
            pass


def run_app(app, args, timeout=20):
    """启动被测程序并等它退出，返回退出码。"""
    try:
        r = subprocess.run([str(app)] + args, capture_output=True, timeout=timeout)
        return r.returncode
    except subprocess.TimeoutExpired:
        return -1


def find_generated_dirs(parent):
    """找出 43 字符、只含 [A-Za-z0-9_-] 的产物目录。"""
    out = []
    if not parent.exists():
        return out
    allowed = set("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_-")
    for d in parent.iterdir():
        if d.is_dir() and len(d.name) == 43 and all(c in allowed for c in d.name):
            out.append(d)
    return out


def make_big_file(path, mb):
    """造一个指定大小的随机文件（用于让任务持续足够久）。"""
    path.parent.mkdir(parents=True, exist_ok=True)
    chunk = os.urandom(1024 * 1024)
    with open(str(path), "wb") as f:
        for _ in range(mb):
            f.write(chunk)


def wait_for(predicate, timeout=15, interval=0.2):
    deadline = time.time() + timeout
    while time.time() < deadline:
        if predicate():
            return True
        time.sleep(interval)
    return predicate()


def scenario_a(res, app):
    """崩溃点 A：锁已建、JSON 未写。"""
    print("")
    print("=== A: 锁已建、JSON 未写 ===")
    work = TEMP.path / "a"
    work.mkdir(parents=True, exist_ok=True)

    # 手动造「锁在、JSON 无 job」：jobId 非空但 JobState 为空（JSON 未写完整）。
    cleanup_state_files()
    write_lock(["onlylockid", "99999", "2026-11-15T00:00:00Z"])
    # 状态文件「无 job」：四个键全空
    write_state_raw('{\n  "JobId": "",\n  "JobState": "",\n  "JobOutputDir": "",\n  "JobStartedAt": ""\n}\n')

    # 造一个用户目录，不应被删（无 job 时保守清理）
    user_dir = work / "userdata"
    user_dir.mkdir(parents=True, exist_ok=True)
    (user_dir / "keep.txt").write_text("keep", encoding="utf-8")

    run_app(app, ["-h"])

    ok_user = (user_dir / "keep.txt").exists()
    ok_lock = not LOCK_FILE.exists()
    res.add("A: 保守清理不删用户文件", ok_user, "用户文件%s" % ("保留" if ok_user else "被误删"))
    res.add("A: 锁被删除", ok_lock, "锁%s" % ("已删" if ok_lock else "仍在"))
    cleanup_state_files()


def scenario_b(res, app):
    """崩溃点 B：JSON active、分卷生成中被强杀 → 下次启动精确清理。"""
    print("")
    print("=== B: active 强杀后精确清理 ===")
    work = TEMP.path / "b"
    work.mkdir(parents=True, exist_ok=True)
    big = work / "big.bin"
    make_big_file(big, 60)

    cleanup_state_files()

    # 启动压缩，等锁 + active 都出现后强杀
    proc = subprocess.Popen([str(app), "-z", str(big)], stdout=subprocess.DEVNULL,
                            stderr=subprocess.DEVNULL)
    try:
        ready = wait_for(lambda: STATE_FILE.exists() and LOCK_FILE.exists(), 15)
        if not ready:
            res.add("B: 任务状态文件出现", False, "15 秒内未出现")
            proc.kill()
            proc.wait()
            cleanup_state_files()
            return
        state = read_state()
        ok_active = state and state.get("JobState") == "active"
        res.add("B: 任务状态为 active", ok_active,
                "state=%s" % (state.get("JobState") if state else "(空)"))

        # 记录残留目录，强杀
        output_dir = Path(state.get("JobOutputDir", ""))
        proc.kill()
        proc.wait()
        res.add("B: 强杀后残留目录存在", output_dir.exists(),
                "%s" % output_dir.name if output_dir.exists() else "无残留")

        # 再次启动触发恢复
        run_app(app, ["-h"])

        ok_cleaned = not output_dir.exists()
        state2 = read_state()
        ok_done = state2 is not None and state2.get("JobState") == "done"
        ok_lock = not LOCK_FILE.exists()
        res.add("B: 残留目录已清理", ok_cleaned, "目录%s" % ("已删" if ok_cleaned else "仍在"))
        res.add("B: 状态落为 done", ok_done, "state=%s" % (state2.get("JobState") if state2 else "(空)"))
        res.add("B: 锁已删除", ok_lock, "锁%s" % ("已删" if ok_lock else "仍在"))
    finally:
        try:
            proc.kill()
        except OSError:
            pass
        cleanup_state_files()


def scenario_c(res, app):
    """崩溃点 C'：done 已写、锁未删 → 仅删锁，不动完整产物。"""
    print("")
    print("=== C': done 已写、锁未删 ===")
    work = TEMP.path / "c"
    work.mkdir(parents=True, exist_ok=True)

    cleanup_state_files()
    # 造一个完整产物目录
    prod = work / ("a" * 43)
    prod.mkdir(parents=True, exist_ok=True)
    (prod / "done.7z").write_bytes(b"complete archive bytes")

    job_id = "donejobid1234567890"
    write_lock([job_id, "99999", "2026-11-15T00:00:00Z"])
    write_state_raw(json.dumps({
        "JobId": job_id,
        "JobState": "done",
        "JobOutputDir": str(prod),
        "JobStartedAt": "2026-11-15T00:00:00Z",
    }, ensure_ascii=False))

    run_app(app, ["-h"])

    ok_prod = (prod / "done.7z").exists()
    ok_lock = not LOCK_FILE.exists()
    res.add("C': 完整产物保留", ok_prod, "产物%s" % ("保留" if ok_prod else "被删"))
    res.add("C': 锁已删除", ok_lock, "锁%s" % ("已删" if ok_lock else "仍在"))
    cleanup_state_files()


def scenario_d(res, app):
    """崩溃点 D：JSON 损坏且 .bak 也坏 → 保守清理（不删任何文件）。"""
    print("")
    print("=== D: JSON 损坏且 .bak 也坏 ===")
    work = TEMP.path / "d"
    work.mkdir(parents=True, exist_ok=True)

    cleanup_state_files()
    user_dir = work / "userdata"
    user_dir.mkdir(parents=True, exist_ok=True)
    (user_dir / "keep.txt").write_text("keep", encoding="utf-8")

    write_lock(["somejobid", "99999", "2026-11-15T00:00:00Z"])
    write_state_raw("{ this is not valid json")
    STATE_BAK.write_text("{ also broken", encoding="utf-8")

    run_app(app, ["-h"])

    ok_user = (user_dir / "keep.txt").exists()
    ok_lock = not LOCK_FILE.exists()
    res.add("D: 保守清理不删用户文件", ok_user, "用户文件%s" % ("保留" if ok_user else "被误删"))
    res.add("D: 锁被删除", ok_lock, "锁%s" % ("已删" if ok_lock else "仍在"))
    cleanup_state_files()


def scenario_f(res, app):
    """崩溃点 F：锁里 PID 还活着 → 跳过清理。"""
    print("")
    print("=== F: 锁里 PID 还活着 ===")
    work = TEMP.path / "f"
    work.mkdir(parents=True, exist_ok=True)

    cleanup_state_files()
    live_dir = work / ("b" * 43)
    live_dir.mkdir(parents=True, exist_ok=True)
    (live_dir / "partial.7z").write_text("partial", encoding="utf-8")

    # PID 指向当前测试进程（活着），jobId 与锁一致
    job_id = "livejobid1234567890"
    write_lock([job_id, str(os.getpid()), "2026-11-15T00:00:00Z"])
    write_state_raw(json.dumps({
        "JobId": job_id,
        "JobState": "active",
        "JobOutputDir": str(live_dir),
        "JobStartedAt": "2026-11-15T00:00:00Z",
    }, ensure_ascii=False))

    run_app(app, ["-h"])

    ok_live = (live_dir / "partial.7z").exists()
    ok_lock = LOCK_FILE.exists()
    res.add("F: 进行中任务目录保留", ok_live, "目录%s" % ("保留" if ok_live else "被误删"))
    res.add("F: 锁保留", ok_lock, "锁%s" % ("保留" if ok_lock else "被删"))
    cleanup_state_files()


def main():
    ap = argparse.ArgumentParser(description="崩溃残留清理专项测试")
    ap.add_argument("--app", help="Auto7z.exe 路径，默认 bin/Debug")
    ap.add_argument("--config", default="Debug", help="Debug / Release")
    ap.add_argument("--keep", action="store_true", help="保留临时目录")
    args = ap.parse_args()

    app = Path(args.app) if args.app else PROJECT / "bin" / args.config / "Auto7z.exe"
    if not app.exists():
        print("找不到被测程序: %s" % app)
        return 2

    # 单实例闸门：与其它套件共用互斥体，避免并行抢配置。
    mutex = claim_checkout()
    if mutex is None:
        print("已有别的测试套件在跑，跳过（并行会抢配置、互相交接任务）")
        return 0

    guard = ConfigGuard(app)
    if not guard.prepare():
        release_checkout(mutex)
        return 2

    try:
        token = guard.join_chain()
        try:
            print("=" * 70)
            print("崩溃残留清理专项测试")
            print("被测程序: %s" % app)
            print("状态文件: %s" % STATE_FILE)
            print("临时目录: %s" % TEMP.path)
            print("=" * 70)

            res = Result()
            scenario_a(res, app)
            scenario_b(res, app)
            scenario_c(res, app)
            scenario_d(res, app)
            scenario_f(res, app)

            res.summary()
            return 1 if res.failed else 0
        finally:
            guard.leave_chain(token)
    finally:
        release_checkout(mutex)
        if not args.keep:
            safe_rmtree(TEMP.path)


if __name__ == "__main__":
    sys.exit(main())
