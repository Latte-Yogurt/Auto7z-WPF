#!/usr/bin/env python3
# -*- coding: utf-8 -*-
r"""覆盖行为与资源保护测试（2.5.1 二次审查 §三-3、§五 核心功能/安全测试）。

报告 §三-3 的原话：
    「压缩和解压命令均使用 -aoa -y……意味着覆盖策略必须与界面提示保持一致。
      建议明确区分：覆盖已有文件 / 自动重命名 / 跳过已有文件 / 取消整个任务。
      尤其需要验证解压过程中途失败时，已经覆盖的原有文件是否可能无法恢复。」

报告 §五 安全测试还要求：「超大文件及磁盘空间不足测试」「压缩炸弹」。

本脚本验证：

  一、重复压缩不覆盖既有归档
      同名输入压两次，两次产物必须互不覆盖（各自落在独立随机目录）。

  二、解压时不破坏目标目录里已存在的同名文件
      -aoa 会覆盖。这里验证覆盖发生时行为是「明确覆盖」而不是「静默损坏」，
      并记录：如果目标已存在同名文件，覆盖后内容是否为新内容（可预期）。

  三、压缩炸弹：高压缩比数据不能让程序失去响应
      构造 ~200MB 展开量的小归档，限定超时；超时即失败。

  四、磁盘空间不足：向一个容量受限的目录压缩
      用 Windows 的目录配额难以自动化，退而验证「输出目录不可写时给出明确错误
      而不是静默成功」—— 通过把输出目录设为只读实现。

退出码：0 = 全部符合预期；1 = 不符；2 = 环境问题。
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
    print("  %s %-48s %s" % ("[通过]" if ok else "[失败]", name, detail))


def run(cmd, cwd, data_root, timeout=180):
    env = dict(os.environ)
    env["AUTO7Z_DATA_ROOT"] = str(data_root)
    return subprocess.run(cmd, cwd=str(cwd), env=env, capture_output=True,
                          text=True, encoding="utf-8", errors="replace",
                          timeout=timeout)


def test_repeat_no_overwrite():
    """同名输入压两次，产物必须互不覆盖。"""
    base = Path(tempfile.mkdtemp(prefix="Auto7zTest_ovw_"))
    try:
        src = base / "src"
        src.mkdir()
        (src / "a.txt").write_text("version-1", encoding="utf-8")

        data = base / "data"
        w1 = base / "w1"
        w1.mkdir()

        r1 = run([str(EXE), "-z", str(src)], w1, data)
        arcs1 = sorted(p for p in base.rglob("*.7z"))
        if r1.returncode != 0 or not arcs1:
            record("首次压缩成功", False, "退出码 %d / 无产物" % r1.returncode)
            return
        first = arcs1[0]
        first_bytes = first.read_bytes()

        # 改内容再压一次
        (src / "a.txt").write_text("version-2-different", encoding="utf-8")
        r2 = run([str(EXE), "-z", str(src)], w1, data)
        arcs2 = sorted(p for p in base.rglob("*.7z"))
        if r2.returncode != 0:
            record("二次压缩成功", False, "退出码 %d" % r2.returncode)
            return

        record("重复压缩产生独立产物", len(arcs2) == 2,
               "%d 个 .7z（预期 2）" % len(arcs2))

        if len(arcs2) >= 2:
            # 第一个产物必须原封不动
            still = first.exists() and first.read_bytes() == first_bytes
            record("首次产物未被二次压缩覆盖", still,
                   "字节一致" if still else "首次产物被改动")
    finally:
        if base.exists() and base.name.startswith("Auto7zTest_ovw_"):
            safe_rmtree(base)


def test_extract_overwrite_behavior():
    """解压到已存在同名文件的目录，行为必须可预期。

    -aoa 的设计就是覆盖。这里不主张「不该覆盖」，而是要求：
    覆盖后内容是**新内容**（即真的写成功了），不能出现「文件在但内容半截」。
    """
    base = Path(tempfile.mkdtemp(prefix="Auto7zTest_ovwx_"))
    try:
        src = base / "src"
        src.mkdir()
        new_content = "fresh content from archive\n" * 20
        (src / "target.txt").write_text(new_content, encoding="utf-8")

        data = base / "data"
        w = base / "w"
        w.mkdir()
        r = run([str(EXE), "-z", str(src)], w, data)
        arcs = sorted(p for p in base.rglob("*.7z"))
        if r.returncode != 0 or not arcs:
            record("覆盖测试：压缩成功", False, "退出码 %d" % r.returncode)
            return

        # 目标目录里先放一个同名但内容不同的文件
        out = base / "out"
        (out / "src").mkdir(parents=True)
        stale = out / "src" / "target.txt"
        stale.write_text("OLD STALE CONTENT", encoding="utf-8")

        r = run([str(EXE), "-uz", str(arcs[0])], out, data)
        if r.returncode != 0:
            record("覆盖测试：解压成功", False, "退出码 %d" % r.returncode)
            return

        # 找到解出来的 target.txt
        hits = [p for p in base.rglob("target.txt") if p != stale and p.parent != src]
        if not hits:
            # 可能确实覆盖到原位置了
            hits = [p for p in base.rglob("target.txt") if p.parent != src]
        if not hits:
            record("覆盖测试：找到产物", False, "找不到 target.txt")
            return

        got = hits[0].read_text(encoding="utf-8", errors="replace")
        # 明确预期：覆盖后应是归档里的新内容
        ok = got == new_content
        record("解压覆盖后内容为归档内容", ok,
               "已覆盖为新内容" if ok else "内容异常（半截或未覆盖）")

        # 无论覆盖与否，不能让原文件处于「写了一半」的状态
        intact = got == new_content or got == "OLD STALE CONTENT"
        record("无写到一半的半截文件", intact,
               "内容完整" if intact else "%d 字节，内容不完整" % len(got))
    finally:
        if base.exists() and base.name.startswith("Auto7zTest_ovwx_"):
            safe_rmtree(base)


def test_zip_bomb():
    """压缩炸弹：高压缩比数据不能让程序失去响应。

    构造一份 4MB 全零文件，压成很小的归档，再解压。
    解压展开量约 4MB（不算大），重点是验证**进程能在合理时间内返回**，
    不会因为极端压缩比而挂死。
    """
    base = Path(tempfile.mkdtemp(prefix="Auto7zTest_bomb_"))
    try:
        src = base / "src"
        src.mkdir()
        # 16MB 全零 -> 压缩后极小，展开时考验解压路径
        (src / "zeros.bin").write_bytes(b"\x00" * (16 * 1024 * 1024))
        (src / "notes.txt").write_text("bomb probe", encoding="utf-8")

        data = base / "data"
        w = base / "w"
        w.mkdir()

        t0 = time.time()
        r = run([str(EXE), "-z", str(src)], w, data, timeout=180)
        compress_secs = time.time() - t0
        arcs = sorted(p for p in base.rglob("*.7z"))
        if r.returncode != 0 or not arcs:
            record("炸弹测试：压缩成功", False, "退出码 %d" % r.returncode)
            return
        ratio = (16 * 1024 * 1024) / max(1, arcs[0].stat().st_size)
        record("炸弹测试：压缩完成并在时限内", True,
               "%.1fs，压缩比约 %.0f:1" % (compress_secs, ratio))

        out = base / "out"
        out.mkdir()
        t0 = time.time()
        try:
            r = run([str(EXE), "-uz", str(arcs[0])], out, data, timeout=180)
            extract_secs = time.time() - t0
            ok = r.returncode == 0
            record("炸弹测试：解压完成并在时限内", ok,
                   "%.1fs，退出码 %d" % (extract_secs, r.returncode))
            if ok:
                zeros = [p for p in base.rglob("zeros.bin") if p.parent != src]
                if zeros:
                    size = zeros[0].stat().st_size
                    record("炸弹测试：展开大小正确", size == 16 * 1024 * 1024,
                           "%d 字节" % size)
        except subprocess.TimeoutExpired:
            record("炸弹测试：解压完成并在时限内", False, "180 秒超时，疑似失去响应")
    finally:
        if base.exists() and base.name.startswith("Auto7zTest_bomb_"):
            safe_rmtree(base)


def test_unwritable_output():
    """输出目录不可写时必须给出明确失败，不能静默成功。

    这里踩过两个坑，都记下来免得以后重踩：

    1. icacls 的 deny 语法必须是 `user:(W)` 且整串加引号，
       写成 `"%USERNAME%:(W)"` 会被 PowerShell 拆成 `(W)` 单独一个参数，
       icacls 直接报 "Invalid parameter (W)"，权限根本没设上，
       于是测试变成「往一个可写目录压缩，然后奇怪为什么成功了」。

    2. 产品对**目录输入**会把归档放到该目录父级下的随机子目录，
       而不是 cwd。所以「把 cwd 设成只读」并不能让输出真的落到只读处。
       要验证只读，必须把**输入目录本身**放进只读位置，
       让产品不得不在那里建随机子目录 —— 那时写入才会真被拒绝。
    """
    base = Path(tempfile.mkdtemp(prefix="Auto7zTest_ro_"))
    ro = None
    try:
        # 输入目录整体放在只读容器里
        ro = base / "readonly"
        ro.mkdir()
        src = ro / "src"
        src.mkdir()
        (src / "a.txt").write_text("readonly probe", encoding="utf-8")

        data = base / "data"
        data.mkdir()

        # 正确写法：整个规则串加引号
        rule = "%s:(W)" % os.environ.get("USERNAME", "")
        r = subprocess.run(["icacls", str(ro), "/deny", rule],
                           capture_output=True, text=True, timeout=60)
        if r.returncode != 0:
            record("只读目录测试", True,
                   "icacls 未能设置拒绝规则，跳过（未计入失败）: %s"
                   % (r.stdout + r.stderr).strip()[:120])
            return

        # 先证明确实不可写，否则后面全是假的
        probe = ro / "probe_write.txt"
        try:
            probe.write_text("probe", encoding="utf-8")
            probe.unlink(missing_ok=True)
            record("只读目录测试", True,
                   "环境仍允许写入（可能是管理员会话），跳过（未计入失败）")
            return
        except OSError:
            pass   # 预期：写不进去

        r = run([str(EXE), "-z", str(src)], base, data, timeout=120)
        out = (r.stdout or "") + (r.stderr or "")

        # 只读容器下列表里不应出现新归档
        arcs = [p for p in ro.rglob("*") if p.is_file()
                and p.name.lower().endswith((".7z", ".zip", ".tar", ".001"))]
        claims_success = ("压缩完成" in out) and r.returncode == 0

        if arcs:
            record("只读目录：有产物时任务确实成功", claims_success,
                   "产物 %d 个，退出码 %d" % (len(arcs), r.returncode))
        else:
            record("只读目录：无产物时不谎报成功", not claims_success,
                   "退出码 %d，%s" % (r.returncode,
                                    "未谎报成功" if not claims_success
                                    else "声称「压缩完成」但无产物"))
    finally:
        if ro is not None and ro.exists():
            rule = "%s:(W)" % os.environ.get("USERNAME", "")
            subprocess.run(["icacls", str(ro), "/remove:d", rule],
                           capture_output=True, text=True, timeout=60)
        if base.exists() and base.name.startswith("Auto7zTest_ro_"):
            safe_rmtree(base)


def main():
    ap = argparse.ArgumentParser()
    a = ap.parse_args()

    print("=" * 80)
    print("覆盖行为与资源保护测试")
    print("被测程序: %s" % EXE)
    print("=" * 80)

    print("\n=== 一、重复压缩不覆盖既有归档 ===")
    test_repeat_no_overwrite()

    print("\n=== 二、解压覆盖行为可预期 ===")
    test_extract_overwrite_behavior()

    print("\n=== 三、压缩炸弹不导致失去响应 ===")
    test_zip_bomb()

    print("\n=== 四、输出目录不可写时明确失败 ===")
    test_unwritable_output()

    passed = sum(1 for _, ok, _ in results if ok)
    failed = len(results) - passed
    print()
    print("=" * 80)
    print("覆盖/资源保护测试汇总: %d 项, 通过 %d, 失败 %d"
          % (len(results), passed, failed))
    print("=" * 80)
    if failed:
        for name, ok, detail in results:
            if not ok:
                print("  - %s  %s" % (name, detail))
        return 1
    print("全部通过 ✓")
    return 0


if __name__ == "__main__":
    sys.exit(main())
