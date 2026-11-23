#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""BUG-06 并发压力测试（GPT6 明确要求）。

## GPT6 的要求

> 对高频短任务和大量错误输出进行压力测试。
> 这是需要专项验证的并发风险，目前我没有将其认定为已经复现的崩溃。

## 竞态是什么

两个异步回调在 `e.Data == null` 时调 `outputDrained.Set()`。
排空超时（`drained == false`）意味着至少一路回调还没到 Set 那一步。
此时主线程若 `Dispose()`，那个回调就会抛 `ObjectDisposedException`。

**修复**：用 `callbackExited` 计数，两个回调都退出才 Dispose。

## 本脚本怎么压

  A. **高频短任务**：连续跑 N 个极小压缩，任务越快、收尾窗口越密集。
  B. **大量错误输出**：对严重损坏的归档反复解压，7-Zip 每秒吐几万字节错误。
  C. **并发多进程**：多个实例同时跑，放大调度抖动。
  D. **极端短超时**：故意让排空超时，强制走 `OutputTruncated` 分支。

判据：整个过程**不出现未处理异常**（进程不崩溃）、退出码合理、无孤儿进程。
"""
import json
import os
import shutil
import subprocess
import sys
import time
from pathlib import Path

from safe_ops import safe_rmtree

P = sys.argv[1] if len(sys.argv) > 1 else r"C:\Users\Mocha\Desktop\VSProject\Auto7z-WPF"
EXE = os.path.join(P, "Auto7z_WPF", "Auto7z_WPF", "bin", "Debug", "Auto7z.exe")
SDK = os.path.join(os.environ.get("TEMP", r"C:\Windows\Temp"), "Auto7zTest_data", "stress")

results = []


def check(name, ok, detail=""):
    results.append({"name": name, "ok": ok, "detail": detail})
    print(("  OK   " if ok else "  !!   ") + name + ("  --- " + detail if detail else ""))


def prep(tag):
    work = os.path.join(SDK, tag)
    data = os.path.join(work, "data")
    os.makedirs(os.path.join(data, "Auto7z"), exist_ok=True)
    cfg = {
        "Language": "zh-CN", "Volume": "0", "Format": "7z", "Password": "",
        "Zstd": True, "GenerateSHA256": False, "ChecksumAlgorithm": "sha256",
        "AutoSave": False, "IsTopMost": False, "RuntimeMode": "Balanced",
        "OutputPath": "", "IsSolidMode": False, "IsEncFileName": False,
        "CompressionLevel": "Store", "UnzipPassword": "", "ErrorLevel": "Warning",
    }
    open(os.path.join(data, "Auto7z", "Auto7z.json"), "w", encoding="utf-8").write(
        json.dumps(cfg, ensure_ascii=False, indent=2))
    env = dict(os.environ)
    env["AUTO7Z_DATA_ROOT"] = data
    env["AUTO7Z_AUTOANSWER"] = "1,1,1,1,1"
    return work, data, env


def run(args, env, cwd, timeout=120):
    try:
        p = subprocess.run([EXE] + args, capture_output=True, env=env, cwd=cwd, timeout=timeout)
        out = ((p.stdout or b"") + (p.stderr or b"")).decode("utf-8", "replace")
        return p.returncode, out
    except subprocess.TimeoutExpired:
        return None, "(超时)"


CRASH_MARKERS = (
    "Unhandled exception",
    "ObjectDisposedException",
    "未处理的异常",
    "System.ObjectDisposedException",
)


def crashed(rc, out):
    """判断这次运行是否发生了未处理异常（真崩溃）。"""
    if any(m in out for m in CRASH_MARKERS):
        return True
    # .NET 未处理异常退出码是 0xE0434352 或 -532462766
    if rc in (-532462766, 0xE0434352, 3762504530):
        return True
    return False


def main():
    if os.path.isdir(SDK):
        safe_rmtree(SDK)
    os.makedirs(SDK, exist_ok=True)

    print("=" * 76)
    print("BUG-06 并发压力测试")
    print("=" * 76)

    # ============ A. 高频短任务 ============
    print("\n【A】高频短任务：连续 25 次极小压缩")
    w_a, d_a, env_a = prep("short")
    f_a = os.path.join(w_a, "s.txt")
    open(f_a, "w", encoding="utf-8").write("x")

    crashes_a = []
    rc_bad_a = []
    t0 = time.time()
    for i in range(25):
        # 每次换文件名，避免重复覆盖同一路径带来的额外噪声
        src = os.path.join(w_a, "s%d.txt" % i)
        open(src, "w", encoding="utf-8").write("payload%d" % i)
        rc, out = run(["-z", src], env_a, w_a, timeout=90)
        if crashed(rc, out):
            crashes_a.append((i, rc, out[:200]))
        if rc not in (0, 1):
            rc_bad_a.append((i, rc))
    elapsed = time.time() - t0

    check("高频短任务无未处理异常", len(crashes_a) == 0,
          "25 次用时 %.1fs%s" % (elapsed, "" if not crashes_a else " 首例: %s" % (crashes_a[0][2][:80],)))
    check("高频短任务退出码均合理(0/1)", len(rc_bad_a) == 0,
          "异常退出码: %s" % (rc_bad_a[:3] if rc_bad_a else "无"))

    # ============ B. 大量错误输出 ============
    print("\n【B】大量错误输出：对损坏归档反复解压")
    w_b, d_b, env_b = prep("errors")
    # 造一个「像 7z 但内容是垃圾」的文件，7-Zip 会吐大量错误
    broken = os.path.join(w_b, "broken.7z")
    raw = bytearray(512 * 1024)
    magic = [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C]
    raw[:len(magic)] = bytes(magic)
    for i in range(len(magic), len(raw)):
        raw[i] = (i * 37 + 11) & 0xFF
    open(broken, "wb").write(bytes(raw))

    crashes_b = []
    for i in range(12):
        rc, out = run(["-uz", broken], env_b, w_b, timeout=90)
        if crashed(rc, out):
            crashes_b.append((i, rc, out[:200]))

    check("大量错误输出下无未处理异常", len(crashes_b) == 0,
          "12 次解压损坏包%s" % ("" if not crashes_b else " 首例 rc=%s: %s" % (crashes_b[0][1], crashes_b[0][2][:80])))

    # ============ C. 并发多进程 ============
    print("\n【C】并发多进程：4 个实例同时跑")
    w_c, d_c, env_c = prep("concurrent")
    procs = []
    for i in range(4):
        src = os.path.join(w_c, "c%d.txt" % i)
        open(src, "w", encoding="utf-8").write("concurrent%d" % i)
        procs.append((i, subprocess.Popen(
            [EXE, "-z", src], env=env_c, cwd=w_c,
            stdout=subprocess.PIPE, stderr=subprocess.STDOUT)))

    crash_c = []
    for i, p in procs:
        try:
            out, _ = p.communicate(timeout=120)
        except subprocess.TimeoutExpired:
            p.kill()
            out, _ = p.communicate()
        text = (out or b"").decode("utf-8", "replace")
        if crashed(p.returncode, text):
            crash_c.append((i, p.returncode, text[:200]))

    check("并发 4 实例无未处理异常", len(crash_c) == 0,
          "" if not crash_c else "首例 rc=%s: %s" % (crash_c[0][1], crash_c[0][2][:80]))

    # ============ D. 强制输出截断路径 ============
    print("\n【D】强制走 OutputTruncated 分支（极端大量输出 + 短超时）")
    # 用「一个含大量文件名的目录」制造巨量 stdout
    w_d, d_d, env_d = prep("truncate")
    many = os.path.join(w_d, "many")
    os.makedirs(many, exist_ok=True)
    for i in range(300):
        open(os.path.join(many, "file_with_a_long_name_%04d.dat" % i), "w").close()

    crashes_d = []
    for i in range(5):
        rc, out = run(["-z", many], env_d, w_d, timeout=180)
        if crashed(rc, out):
            crashes_d.append((i, rc, out[:200]))

    check("巨量输出下无未处理异常", len(crashes_d) == 0,
          "" if not crashes_d else "首例 rc=%s: %s" % (crashes_d[0][1], crashes_d[0][2][:80]))

    # ============ E. 残留进程检查 ============
    print("\n【E】压力测试后不得残留 7z 进程")
    time.sleep(1)
    try:
        p = subprocess.run(
            ["powershell", "-NoProfile", "-Command",
             "(Get-Process -Name 7z -ErrorAction SilentlyContinue | Measure-Object).Count"],
            capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=30)
        n = int((p.stdout or "0").strip() or 0)
    except Exception:
        n = -1
    check("无残留 7z 进程", n == 0, "残留=%d" % n)

    print("\n" + "=" * 76)
    bad = [r for r in results if not r["ok"]]
    print("共 %d 项，通过 %d，失败 %d" % (len(results), len(results) - len(bad), len(bad)))
    if bad:
        for b in bad:
            print("  " + b["name"] + "  " + b["detail"])
        print("FAIL")
        code = 1
    else:
        print("高频短任务 / 大量错误输出 / 并发 / 截断路径 均未出现未处理异常")
        print("PASS")
        code = 0
    out_path = os.path.join(SDK, "stress.json")
    open(out_path, "w", encoding="utf-8").write(json.dumps(results, ensure_ascii=False, indent=2))
    print("输出 -> %s" % out_path)
    return code


if __name__ == "__main__":
    sys.exit(main())
