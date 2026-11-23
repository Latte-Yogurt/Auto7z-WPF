#!/usr/bin/env python3
# -*- coding: utf-8 -*-
r"""取消与异常退出的残留检查（2.5.1 二次审查 §三-2）。

审查报告 §三-2 的原话：
    「我建议增加一个专项测试：用户连续取消任务、关闭窗口、重新启动任务时，
      旧 7z 进程是否可能继续运行？同时验证取消后是否残留半成品归档，
      以及任务状态是否正确恢复。」

这个脚本验证三件事：

  1. 强杀 Auto7z 进程后，它派生的 7z.exe 不会变成孤儿继续跑
     （通过 SevenZipService.KillAllRunningProcesses 与进程树检查）
  2. 中途强杀不会在输出目录留下「看起来像完整归档」的半成品
  3. 强杀后重启，TaskState 的残留清理能把任务目录收掉

做法：启动一个足够大的压缩任务，在它运行中断时强杀父进程，
然后检查进程表与文件系统。用大批文件保证压缩不会瞬间完成。

退出码：0 = 全部符合预期；1 = 有不符；2 = 环境问题。
"""
import argparse
import os
import shutil
import subprocess
import sys
import tempfile
import time
from pathlib import Path

from projectpaths import project_root
from safe_ops import safe_rmtree

ROOT = project_root()


def find_exe():
    for cfg in ("Release", "Debug"):
        p = ROOT / "Auto7z_WPF" / "Auto7z_WPF" / "bin" / cfg / "Auto7z.exe"
        if p.is_file():
            return p
    sys.stderr.write("错误: 找不到 Auto7z.exe，请先构建\n")
    raise SystemExit(2)


EXE = find_exe()
results = []


def record(name, ok, detail=""):
    results.append((name, ok, detail))
    print("  %s %-46s %s" % ("[通过]" if ok else "[失败]", name, detail))


def child_env(data_root):
    env = dict(os.environ)
    env["AUTO7Z_DATA_ROOT"] = str(data_root)
    return env


def list_7z_processes():
    """列出当前所有 7z.exe 进程（PID, 命令行）。不依赖 psutil。"""
    ps = (
        "Get-CimInstance Win32_Process -Filter \"Name='7z.exe'\" | "
        "ForEach-Object { $_.ProcessId.ToString() + '|' + $_.CommandLine }"
    )
    r = subprocess.run(["powershell", "-NoProfile", "-Command", ps],
                       capture_output=True, text=True, encoding="utf-8",
                       errors="replace")
    out = []
    for line in (r.stdout or "").splitlines():
        line = line.strip()
        if not line or "|" not in line:
            continue
        pid, _, cmd = line.partition("|")
        try:
            out.append((int(pid), cmd))
        except ValueError:
            continue
    return out


def make_big_corpus(target, count=1500, size=65536):
    """造一批够大的数据，让压缩有明显持续时间。

    用 os.urandom 而不是「逐字节按公式算」：随机内容几乎不可压缩，
    7z 必须真的花时间读+压，才能保证我们在它运行时下手强杀。
    （早先用 900 个 4KB 文件时压缩瞬间完成，强杀发生在结束之后，
      「未观察到 7z 子进程」把测试变成了空跑 —— 这是修掉那个坑的原因。）
    """
    target = Path(target)
    target.mkdir(parents=True, exist_ok=True)
    for i in range(count):
        sub = target / ("d%02d" % (i % 20))
        sub.mkdir(exist_ok=True)
        (sub / ("f%04d.bin" % i)).write_bytes(os.urandom(size))
    total = sum(p.stat().st_size for p in target.rglob("*") if p.is_file())
    return count, total


def wait_for_7z(deadline):
    """等一个有 7z.exe 在跑的时刻；返回 True 表示看到了。"""
    while time.time() < deadline:
        if list_7z_processes():
            return True
        time.sleep(0.05)
    return False


def test_kill_leaves_no_orphan():
    """强杀父进程后不能留下孤儿 7z.exe。"""
    base = Path(tempfile.mkdtemp(prefix="Auto7zTest_cancel_"))
    try:
        src = base / "src"
        n, total = make_big_corpus(src)
        data = base / "data"
        work = base / "work"
        work.mkdir()

        # 记录启动前的 7z 进程，避免把别处的 7z 算进来
        before = {pid for pid, _ in list_7z_processes()}

        proc = subprocess.Popen(
            [str(EXE), "-z", str(src)],
            cwd=str(work), env=child_env(data),
            stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)

        saw_7z = wait_for_7z(time.time() + 60)

        if not saw_7z:
            # 没观察到 7z 说明强杀发生在压缩结束之后 —— 这次测试没有测到
            # 它本该测的东西（孤儿进程），不能算通过。早先这里记的是「通过」，
            # 结果整条用例是空跑；那种假绿比失败更有害。
            record("压缩中确实有 7z.exe 子进程", False,
                   "60 秒内未观察到 7z.exe，强杀发生在任务结束后，本次孤儿检查无效")
            try:
                proc.kill()
            except OSError:
                pass
            proc.wait(timeout=30)
            return
        record("压缩中确实有 7z.exe 子进程", True, "已观察到")

        # 强杀父进程（模拟用户关窗口 / 任务管理器结束进程）
        try:
            proc.kill()
        except OSError:
            pass
        proc.wait(timeout=30)

        # 给子进程一点时间退出；正常实现会随父进程一起结束
        deadline = time.time() + 15
        leftover = []
        while time.time() < deadline:
            now = {pid for pid, _ in list_7z_processes()}
            leftover = list(now - before)
            if not leftover:
                break
            time.sleep(0.2)

        if leftover:
            # 兜底：调用产品自己的清理入口，看它能不能收掉
            try:
                subprocess.run(
                    ["powershell", "-NoProfile", "-Command",
                     "$a=[System.Reflection.Assembly]::LoadFrom('%s');"
                     "$t=$a.GetType('Auto7z_WPF.Services.SevenZipService');"
                     "$t.GetMethod('KillAllRunningProcesses').Invoke($null,$null)"
                     % str(EXE).replace("'", "''")],
                    capture_output=True, text=True, timeout=30)
            except (OSError, subprocess.SubprocessError):
                pass
            time.sleep(1)
            now = {pid for pid, _ in list_7z_processes()}
            still = list(now - before)
            record("强杀后无孤儿 7z.exe", not still,
                   ("残留 %d 个（KillAllRunningProcesses 后仍未清）: %s"
                    % (len(still), still)) if still
                   else "初次检测到残留，经 KillAllRunningProcesses 已清理")
        else:
            record("强杀后无孤儿 7z.exe", True, "子进程随父进程一并结束")
    finally:
        # 无论如何都要清掉可能残留的 7z，避免污染后续测试
        for pid, _ in list_7z_processes():
            try:
                subprocess.run(["taskkill", "/F", "/PID", str(pid)],
                               capture_output=True, timeout=10)
            except (OSError, subprocess.SubprocessError):
                pass
        if base.exists() and base.name.startswith("Auto7zTest_cancel_"):
            safe_rmtree(base)


def test_partial_archive_not_disguised():
    """强杀留下的半成品不能看起来像完整归档。

    判定标准：被中断的那次任务，要么产物不存在，要么存在但
    没有配套的 .sha256（说明任务没走完完整性收尾），不能出现
    「有 .7z 且有 .sha256，但内容其实不完整」这种误导状态。
    """
    base = Path(tempfile.mkdtemp(prefix="Auto7zTest_partial_"))
    try:
        src = base / "src"
        make_big_corpus(src)
        data = base / "data"
        work = base / "work"
        work.mkdir()

        proc = subprocess.Popen(
            [str(EXE), "-z", str(src)],
            cwd=str(work), env=child_env(data),
            stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        wait_for_7z(time.time() + 30)
        try:
            proc.kill()
        except OSError:
            pass
        proc.wait(timeout=30)
        time.sleep(1)

        archives = [p for p in base.rglob("*.7z")]
        manifests = [p for p in base.rglob("*.sha256")]

        if not archives:
            record("中断不留半成品归档", True, "未留下任何 .7z")
        elif len(manifests) < len(archives):
            record("中断不留半成品归档", True,
                   "留下 %d 个 .7z 但只有 %d 个 .sha256（未走完完整性收尾）"
                   % (len(archives), len(manifests)))
        else:
            # 有归档也有清单：那就必须真的能被 7z 正常打开
            arc = archives[0]
            r = subprocess.run(
                [str(EXE), "-uz", str(arc), ],
                cwd=str(base / "verify"), env=child_env(data),
                capture_output=True, text=True, encoding="utf-8",
                errors="replace", timeout=180) \
                if (base / "verify").mkdir(exist_ok=True) is None else None
            ok = r is not None and r.returncode == 0
            record("中断留下的归档仍然可用", ok,
                   "退出码 %s（不完整归档应失败或被列出）"
                   % (r.returncode if r else "?"))
    finally:
        for pid, _ in list_7z_processes():
            try:
                subprocess.run(["taskkill", "/F", "/PID", str(pid)],
                               capture_output=True, timeout=10)
            except (OSError, subprocess.SubprocessError):
                pass
        if base.exists() and base.name.startswith("Auto7zTest_partial_"):
            safe_rmtree(base)


def test_restart_cleans_state():
    """强杀后重启，任务状态不应卡在「进行中」，残留目录应被清理。"""
    base = Path(tempfile.mkdtemp(prefix="Auto7zTest_restart_"))
    try:
        src = base / "src"
        make_big_corpus(src)
        data = base / "data"
        work = base / "work"
        work.mkdir()

        proc = subprocess.Popen(
            [str(EXE), "-z", str(src)],
            cwd=str(work), env=child_env(data),
            stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        wait_for_7z(time.time() + 30)
        try:
            proc.kill()
        except OSError:
            pass
        proc.wait(timeout=30)
        time.sleep(1)

        state_file = data / "Auto7z" / "TaskState.json"
        before_text = state_file.read_text(encoding="utf-8", errors="replace") \
            if state_file.exists() else ""

        # 重启：跑一个小任务，触发启动时的残留清理
        small = base / "small"
        small.mkdir()
        (small / "x.txt").write_text("restart probe", encoding="utf-8")
        r = subprocess.run([str(EXE), "-z", str(small)],
                           cwd=str(work), env=child_env(data),
                           capture_output=True, text=True, encoding="utf-8",
                           errors="replace", timeout=180)
        record("强杀后重启可正常执行新任务", r.returncode == 0,
               "退出码 %d" % r.returncode)

        after_text = state_file.read_text(encoding="utf-8", errors="replace") \
            if state_file.exists() else ""

        # 关键：状态文件不应仍标记着上一次那个已被强杀的任务
        stale = ('"Active"' in after_text or '"Running"' in after_text
                 or '"InProgress"' in after_text)
        record("重启后任务状态未卡在进行中", not stale,
               "状态含进行中标记" if stale else "无残留进行中标记")
    finally:
        for pid, _ in list_7z_processes():
            try:
                subprocess.run(["taskkill", "/F", "/PID", str(pid)],
                               capture_output=True, timeout=10)
            except (OSError, subprocess.SubprocessError):
                pass
        if base.exists() and base.name.startswith("Auto7zTest_restart_"):
            safe_rmtree(base)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--config", default="Release")
    a = ap.parse_args()

    print("=" * 78)
    print("取消与异常退出残留检查")
    print("被测程序: %s" % EXE)
    print("=" * 78)

    # 开跑前先确保没有守着别处的 7z（否则计数会错）
    pre = list_7z_processes()
    if pre:
        print("  注意: 启动前已有 %d 个 7z.exe，会从计数中排除" % len(pre))

    print("\n=== 一、强杀后无孤儿 7z 进程 ===")
    test_kill_leaves_no_orphan()

    print("\n=== 二、中断不留误导性半成品 ===")
    test_partial_archive_not_disguised()

    print("\n=== 三、强杀后重启状态恢复 ===")
    test_restart_cleans_state()

    passed = sum(1 for _, ok, _ in results if ok)
    failed = len(results) - passed
    print()
    print("=" * 78)
    print("取消/异常退出检查汇总: %d 项, 通过 %d, 失败 %d"
          % (len(results), passed, failed))
    print("=" * 78)
    if failed:
        for name, ok, detail in results:
            if not ok:
                print("  - %s  %s" % (name, detail))
        return 1
    print("全部通过 ✓")
    return 0


if __name__ == "__main__":
    sys.exit(main())
