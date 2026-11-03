#!/usr/bin/env python3
# -*- coding: utf-8 -*-
r"""一键性能基准测试。

跑 testtools\\cs\\bench\\bench.exe（用反射直调被测程序里的热点方法），
把结果解析成一张表，并跟上一次的结果对比。

覆盖的热点（B1–B9）:
    B1/B2/B3  SHA256 吞吐：共用 SHA256Service / 裸 SHA256.Create() / 可压缩文本
    B4/B5     7-Zip 压缩 / 解压吞吐
    B6        配置读 / 写
    B7/B8     目录扫描（GetSizeInBytes）/ 安全文件夹名生成
    B9        拖入接收路径（SetPendingFiles）—— 界面线程上那一步

用法:
    python run_bench.py                       # 跑一轮，结果存 bench/last.json
    python run_bench.py --app <Auto7z.exe>
    python run_bench.py --baseline bench/baseline.json   # 跟基线对比
    python run_bench.py --keep                # 保留临时目录（调试用）

退出码: 0 = 跑完（不判失败，性能基准只做对比，不设硬门槛）

注意：基准跑的是 Debug 构建。要测 Release，先按 README 重新构建再传 --app。
"""
import argparse
import json
import os
import re
import subprocess
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from safe_ops import SafeTemp

HERE = Path(__file__).resolve().parent
WORKSPACE = HERE.parent
PROJECT = WORKSPACE / "Auto7z_WPF" / "Auto7z_WPF"
BENCH_DIR = HERE / "bench"

TEMP = SafeTemp("bench")

# BENCH <name> units=<x> iters=<n> seconds=<s> per_op_ms=<ms> throughput=<t>
BENCH_RE = re.compile(
    r"^BENCH\s+(\S+)\s+units=([\d.]+)\s+iters=(\d+)\s+seconds=([\d.]+)\s+"
    r"per_op_ms=([\d.]+)\s+throughput=([\d.]+)\s*$"
)


def run(cmd, **kw):
    """跑一个命令并收回输出。

    控制台输出的编码跟着代码页走（父 cmd 默认 GBK，chcp 65001 就是 UTF-8），
    不能写死一种 —— 写死一种在另一种环境下全是乱码。两种都试。
    """
    proc = subprocess.run(cmd, capture_output=True, **kw)
    out = (proc.stdout or b"") + (proc.stderr or b"")
    for enc in ("gbk", "utf-8", "mbcs"):
        try:
            return proc.returncode, out.decode(enc)
        except (UnicodeDecodeError, LookupError):
            continue
    return proc.returncode, out.decode("utf-8", errors="replace")


def parse_bench(out):
    """从 bench.exe 的输出里抽出所有 BENCH 行。"""
    rows = {}
    for line in out.splitlines():
        m = BENCH_RE.match(line.strip())
        if not m:
            continue
        name = m.group(1)
        rows[name] = {
            "units": float(m.group(2)),
            "iters": int(m.group(3)),
            "seconds": float(m.group(4)),
            "per_op_ms": float(m.group(5)),
            "throughput": float(m.group(6)),
        }
    return rows


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--app", help="被测程序路径")
    ap.add_argument("--baseline", help="跟这个 json 对比")
    ap.add_argument("--keep", action="store_true", help="保留临时目录")
    args = ap.parse_args()

    app = Path(args.app) if args.app else PROJECT / "bin" / "Debug" / "Auto7z.exe"
    if not app.exists():
        print("找不到被测程序: %s" % app)
        print("先构建: msbuild Auto7z_WPF\\Auto7z_WPF.sln /t:Rebuild /p:Configuration=Debug")
        return 2

    # 基准工具没编译就先编译
    bench_exe = HERE / "cs" / "bench" / "bench.exe"
    if not bench_exe.exists():
        print("基准工具未编译，先编译 ...")
        env = dict(os.environ)
        env["PYTHONIOENCODING"] = "utf-8"
        _, build_out = run([sys.executable, str(HERE / "cs" / "build_all.py"), "bench"],
                           cwd=str(HERE), env=env)
        print(build_out)
        if not bench_exe.exists():
            print("!! 编译失败")
            return 2

    print("=" * 74)
    print("Auto7z 性能基准")
    print("被测程序: %s" % app)
    print("临时目录: %s" % TEMP.path)
    print("=" * 74)

    TEMP.ensure()
    _, out = run([str(bench_exe), str(app), str(TEMP.path)], cwd=str(HERE))
    print(out)

    rows = parse_bench(out)
    if not rows:
        print("!! 没有读到任何 BENCH 行，基准没跑起来")
        return 2

    # ---- 打印结果表 ----
    print("")
    print("=" * 74)
    print("基准结果（per_op_ms 越小越好，throughput 越大越好）")
    print("=" * 74)
    for name in sorted(rows):
        row = rows[name]
        print("  %-22s per_op=%9.3f ms  throughput=%10.2f  units=%10.2f"
              % (name, row["per_op_ms"], row["throughput"], row["units"]))

    # ---- 保存 / 对比 ----
    BENCH_DIR.mkdir(parents=True, exist_ok=True)
    last_path = BENCH_DIR / "last.json"
    last_path.write_text(json.dumps(rows, ensure_ascii=False, indent=2), encoding="utf-8")
    print("")
    print("已保存: %s" % last_path)

    if args.baseline:
        base_path = Path(args.baseline)
        if not base_path.exists():
            print("!! 基线文件不存在: %s" % base_path)
            return 2
        base = json.loads(base_path.read_text(encoding="utf-8"))

        print("")
        print("=" * 74)
        print("跟基线对比（baseline=%s）" % base_path)
        print("=" * 74)
        print("  %-22s %12s %12s %10s" % ("基准", "基线 per_op", "本次 per_op", "变化"))
        worse = []
        for name in sorted(rows):
            if name not in base:
                print("  %-22s %12s %12.3f %10s" % (name, "(无)", rows[name]["per_op_ms"], "新增"))
                continue
            b = base[name]["per_op_ms"]
            a = rows[name]["per_op_ms"]
            if b <= 0:
                delta = "n/a"
            else:
                pct = (a - b) / b * 100.0
                delta = "%+.1f%%" % pct
                if a > b:
                    worse.append((name, pct))
            print("  %-22s %12.3f %12.3f %10s" % (name, b, a, delta))
        if worse:
            print("")
            print("  !! 有 %d 项比基线慢（可能是机器负载或磁盘缓存差异）：" % len(worse))
            for name, pct in worse:
                print("      %s 慢 %.1f%%" % (name, pct))
        else:
            print("")
            print("  全部不低于基线 ✓")

    print("")
    print("##### 清理临时产物 #####")
    if args.keep:
        print("  --keep 指定了，保留: %s" % TEMP.path)
    else:
        print("  " + TEMP.cleanup())

    return 0


if __name__ == "__main__":
    sys.exit(main())
