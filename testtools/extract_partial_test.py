#!/usr/bin/env python3
# -*- coding: utf-8 -*-
r"""批量解压「失败结论不被后续成功覆盖」专项验证。

覆盖 2.5.0 修掉的一类真实缺陷：批量解压里前面某个归档失败，
后面成功的那一个若按「干净成功」收尾，就会把红色失败结论覆盖成
「解压完成」，而退出码仍然是 1 —— 同一次运行给出两个互相矛盾的结论。

用法:
    python extract_partial_test.py
    python extract_partial_test.py --keep     # 保留临时目录（调试用）

退出码: 0 = 全部通过, 1 = 有失败项, 2 = 环境不满足
"""
import argparse
import json
import os
import shutil
import subprocess
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from safe_ops import SafeTemp, safe_rmtree
from test_env import isolate_data_root

DATA_ROOT = isolate_data_root("extractpartial")

HERE = Path(__file__).resolve().parent
WORKSPACE = HERE.parent
PROJECT = WORKSPACE / "Auto7z_WPF" / "Auto7z_WPF"

LOCAL = Path(DATA_ROOT) / "Auto7z"
CFG = LOCAL / "Auto7z.json"
SZ = LOCAL / "7-Zip" / "7z.exe"

issues = []


def check(name, ok, detail=""):
    print("  [%s] %s%s" % ("通过" if ok else "失败", name, ("  " + detail) if detail else ""))
    if not ok:
        issues.append(name)


def write_config():
    CFG.parent.mkdir(parents=True, exist_ok=True)
    CFG.write_text(json.dumps({
        "Language": "zh-CN", "Volume": "2000", "Format": "7z", "Password": "",
        "Zstd": True, "GenerateSHA256": False, "ChecksumAlgorithm": "sha256",
        "AutoSave": False, "IsTopMost": False, "RuntimeMode": "Balanced",
        "OutputPath": "", "IsSolidMode": False, "IsEncFileName": False,
        "CompressionLevel": "Normal", "UnzipPassword": "", "ErrorLevel": "Warning",
    }, ensure_ascii=False, indent=2), encoding="utf-8")


def run(exe, args, cwd):
    env = dict(os.environ)
    env["AUTO7Z_DATA_ROOT"] = str(DATA_ROOT)
    env["PYTHONIOENCODING"] = "utf-8"
    p = subprocess.run([str(exe)] + args, cwd=str(cwd), env=env,
                       stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                       stdin=subprocess.DEVNULL, timeout=180)
    out = p.stdout.decode("utf-8", errors="replace")
    return p.returncode, out


def make_archive(work, name, payload):
    """用产品释放出来的 7z 造一个真实归档。"""
    src = work / ("src_" + name)
    src.mkdir(parents=True, exist_ok=True)
    (src / "a.txt").write_text(payload, encoding="utf-8")
    out = work / name
    r = subprocess.run([str(SZ), "a", "-y", "-aoa", str(out), str(src / "a.txt")],
                       stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=120)
    return out if out.exists() else None


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--app", default=str(PROJECT / "bin" / "Debug" / "Auto7z.exe"))
    ap.add_argument("--keep", action="store_true")
    a = ap.parse_args()

    exe = Path(a.app)
    if not exe.exists():
        print("被测程序不存在: %s" % exe)
        return 2

    temp = SafeTemp("extractpartial")
    work = Path(temp.ensure())
    try:
        write_config()

        # 先跑一次真实压缩，让产品把 7z.exe 释放出来（-h 不会释放组件）。
        seedDir = work / "seed"
        seedDir.mkdir(parents=True, exist_ok=True)
        (seedDir / "seed.txt").write_text("seed", encoding="utf-8")
        run(exe, ["-z", str(seedDir / "seed.txt")], work)

        if not SZ.exists():
            print("7-Zip 组件没有释放出来，无法造测试归档")
            return 2

        good1 = make_archive(work, "good1.7z", "one")
        good2 = make_archive(work, "good2.7z", "two")
        if not good1 or not good2:
            print("造测试归档失败")
            return 2

        print("=== 场景 A：损坏归档 + 正常归档（-uz）===")

        # 这里的顺序与选型都是关键的：
        # 缺失的路径在**收集输入**阶段就被拒了，根本走不到解压循环；
        # 而损坏的归档是「看起来是归档、真的去解才失败」，正好命中
        # 「前面失败、后面成功」这条覆盖路径。
        corrupt = work / "corrupt.7z"
        corrupt.write_bytes(b"NOTANARCHIVE" * 50)

        code, out = run(exe, ["-uz", str(corrupt), str(good1), str(good2)], work)

        print("  退出码=%d" % code)
        has_done = "解压完成" in out
        has_partial = "未完全成功" in out
        check("退出码为 1（有项失败）", code == 1, "实际=%d" % code)
        check("整批没有被后续成功覆盖成「解压完成」", not has_done,
              "含'解压完成'=%s" % has_done)
        check("给出了「未完全成功」的部分结论", has_partial,
              "含'未完全成功'=%s" % has_partial)
        check("确实解出了后续归档的内容",
              sum(1 for _ in work.rglob("a.txt")) >= 2,
              "找到 a.txt %d 个" % sum(1 for _ in work.rglob("a.txt")))

        print("")
        print("=== 场景 B：全部缺失（-uz）===")
        code, out = run(exe, ["-uz", str(work / "nope1.7z"), str(work / "nope2.7z")], work)
        print("  退出码=%d" % code)
        check("全部缺失时退出码非 0", code != 0, "实际=%d" % code)
        check("全部缺失时不给「解压完成」", "解压完成" not in out)

        print("")
        print("=== 场景 B2：全部损坏（-uz）===")
        c1 = work / "corrupt1.7z"
        c2 = work / "corrupt2.7z"
        c1.write_bytes(b"BAD" * 100)
        c2.write_bytes(b"BAD" * 100)
        code, out = run(exe, ["-uz", str(c1), str(c2)], work)
        print("  退出码=%d" % code)
        check("全部损坏时退出码为 1", code == 1, "实际=%d" % code)
        check("全部损坏时不给「解压完成」", "解压完成" not in out)

        print("")
        print("=== 场景 C：单个正常归档（-uz）对照组 ===")
        clean = work / "clean"
        clean.mkdir(exist_ok=True)
        code, out = run(exe, ["-uz", str(good2)], clean)
        print("  退出码=%d" % code)
        check("干净的成功仍然报「解压完成」", "解压完成" in out)
        check("干净的成功退出码为 0", code == 0, "实际=%d" % code)

    finally:
        if a.keep:
            print("\n保留临时目录: %s" % work)
        else:
            temp.cleanup()

    print("")
    if issues:
        print("!! 有 %d 项失败: %s" % (len(issues), "; ".join(issues)))
        return 1
    print("全部通过 ✓")
    return 0


if __name__ == "__main__":
    sys.exit(main())
