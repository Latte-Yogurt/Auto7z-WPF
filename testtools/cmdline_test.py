#!/usr/bin/env python3
r"""命令行相关的测试:
- -h 显示用法
- -A / -Z / -U 三种模式
- -r / -ur 注册与注销(需要 UAC, 自动化点不了, 只验证参数被识别)
- 拖到 exe 图标 = 命令行参数, 走的是同一条路
"""
import os
import re
import subprocess
import sys
import time
from pathlib import Path

APP = None
ISSUES = []
OUT = []


def say(s):
    OUT.append(s)
    print(s)


def bad(s):
    ISSUES.append(s)
    OUT.append("  !! " + s)
    print("  !! " + s)


def run(args, timeout=25, stdin=None):
    """跑一次程序, 返回 (退出码, 输出)"""
    try:
        r = subprocess.run([APP] + list(args), stdout=subprocess.PIPE,
                           stderr=subprocess.STDOUT,
                           stdin=subprocess.DEVNULL if stdin is None else stdin,
                           timeout=timeout)
        return r.returncode, r.stdout.decode("gbk", "replace")
    except subprocess.TimeoutExpired as e:
        raw = e.stdout or b""
        if isinstance(raw, bytes):
            raw = raw.decode("gbk", "replace")
        return None, raw
    except Exception as e:
        return -1, str(e)


def main():
    global APP
    APP = sys.argv[1]
    work = Path(sys.argv[2]) if len(sys.argv) > 2 else Path("cmdline_test")

    if work.exists():
        import shutil
        shutil.rmtree(work, ignore_errors=True)
    work.mkdir(parents=True, exist_ok=True)

    # ---------- T1: -h 显示用法 ----------
    say("=== T1: -h 显示用法 ===")
    code, out = run(["-h"], timeout=15)
    say("  退出码 = %s" % code)
    for line in out.splitlines():
        if line.strip():
            say("    " + line)

    if code is None or code != 0:
        bad("-h: 退出码不对")
    for expect in ("-A", "-Z", "-U", "-r", "-ur", "-h"):
        if expect not in out:
            bad("-h: 说明里缺少 %s" % expect)

    # ---------- T2: 三种模式 ----------
    say("")
    say("=== T2: 模式开关 -A / -Z / -U ===")
    for mode, expect_zip in [("-Z", True), ("-A", True), ("-U", False)]:
        d = work / ("mode" + mode)
        d.mkdir(parents=True, exist_ok=True)
        f = d / ("文件.txt")
        f.write_text("内容", encoding="utf-8")

        code, out = run([mode, str(f)], timeout=20)
        archives = list(d.rglob("*.7z"))
        say("  [%s] 退出码=%s 压缩包=%d 个" % (mode, code, len(archives)))

        if mode == "-U":
            # 解压模式: 普通文件解不开, 不应有压缩包
            if archives:
                bad("-U: 普通文件不该被压缩")
        else:
            if not archives:
                bad("%s: 没有生成压缩包" % mode)

    # ---------- T3: 不带模式 = 自动模式 ----------
    say("")
    say("=== T3: 不带模式开关走自动模式 ===")
    d = work / "auto"
    d.mkdir(parents=True, exist_ok=True)
    f = d / "普通.txt"
    f.write_text("内容", encoding="utf-8")
    code, out = run([str(f)], timeout=20)
    archives = list(d.rglob("*.7z"))
    say("  退出码=%s 压缩包=%d 个" % (code, len(archives)))
    if not archives:
        bad("默认模式: 没有生成压缩包, 应该按自动模式处理")

    # ---------- T4: 拖到 exe 图标 = 命令行参数 ----------
    say("")
    say("=== T4: 拖到 exe 图标(命令行传路径) ===")
    d = work / "drop"
    d.mkdir(parents=True, exist_ok=True)
    f = d / "拖拽.txt"
    f.write_text("内容", encoding="utf-8")
    code, out = run([str(f)], timeout=20)
    archives = list(d.rglob("*.7z"))
    say("  退出码=%s 压缩包=%d 个" % (code, len(archives)))
    if not archives:
        bad("拖到 exe 图标: 没有生成压缩包")
    if code is not None and code != 0:
        bad("拖到 exe 图标: 退出码 %s" % code)

    # ---------- T5: 多个文件一次传入 ----------
    say("")
    say("=== T5: 多个文件一次传入 ===")
    d = work / "multi"
    d.mkdir(parents=True, exist_ok=True)
    paths = []
    for i in range(5):
        f = d / ("多%d.txt" % i)
        f.write_text("内容", encoding="utf-8")
        paths.append(str(f))
    code, out = run(paths, timeout=25)
    archives = list(d.rglob("*.7z"))
    say("  退出码=%s 压缩包=%d 个" % (code, len(archives)))
    if not archives:
        bad("多文件: 没有生成压缩包")

    # ---------- T6: -r / -ur 被识别 ----------
    say("")
    say("=== T6: -r / -ur 参数被识别 ===")
    # 注册要 UAC, 自动化点不了, 这里只确认参数不会当成文件路径
    for switch in ("-r", "-ur"):
        code, out = run([switch], timeout=8)
        say("  [%s] 退出码=%s 输出=%s" % (switch, code, out.strip()[:80] or "(空)"))
        # 不该因为当成路径而报"路径不存在"
        if "不存在" in out and "路径" in out:
            bad("%s: 被当成了文件路径" % switch)

    say("")
    say("=== 问题汇总 (%d) ===" % len(ISSUES))
    for i in ISSUES:
        say("  - " + i)

    report = work / "report.txt"
    report.write_text("\n".join(OUT), encoding="utf-8")
    print("")
    print("TOTAL ISSUES: %d" % len(ISSUES))


if __name__ == "__main__":
    main()
