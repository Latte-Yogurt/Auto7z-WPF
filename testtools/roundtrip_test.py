#!/usr/bin/env python3
# -*- coding: utf-8 -*-
r"""压缩 → 解压 → SHA256 校验 的内容一致性测试（2.5.1 二次审查 §四 建议）。

审查报告的原话：
    「特别建议加入一个测试：随机文件压缩 → 解压 → SHA256 对比 →
      内容一致性验证。……这比单纯验证命令返回 0 更有价值。」

这个脚本就做这件事。和现有 run_system_test 的区别：
现有系统测试主要断言「命令退出码对不对」，这里断言「字节是否一样」。

覆盖的数据形态（都是真实会遇到的坑）：
    - 空文件
    - 极小文件（1 字节）
    - 中等文本
    - 结构化随机二进制（不可压缩，考验存储路径）
    - 高压缩比内容（大量重复）
    - 中文 / 空格 / 括号 / 加号 / 百分号 文件名
    - UTF-8 内容与 CRLF 内容
    - 多层目录嵌套
    - 大量小文件（触发 7z 的多文件路径）

断言方式：解压出来的每个文件，逐字节与原文比对；
另外对整棵树算一个汇总摘要，防止「解出了同名但内容错的文件」。

用法:
    python roundtrip_test.py            # 默认 3 轮随机
    python roundtrip_test.py --rounds 5

退出码：0 = 全部一致；1 = 有不一致；2 = 环境/构建问题。
"""
import argparse
import hashlib
import os
import random
import shutil
import subprocess
import sys
import tempfile
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


def sha256_file(p):
    h = hashlib.sha256()
    with open(p, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def tree_digest(root):
    """整棵树的汇总摘要：按相对路径排序，逐个并入 路径+内容摘要。

    这样既能发现「某个文件内容变了」，也能发现「少了一个文件」或「多了 一个」。
    """
    root = Path(root)
    entries = []
    for p in sorted(root.rglob("*")):
        if p.is_file():
            entries.append((p.relative_to(root).as_posix(), sha256_file(p)))
    h = hashlib.sha256()
    for rel, digest in entries:
        h.update(rel.encode("utf-8"))
        h.update(b"\0")
        h.update(digest.encode("ascii"))
        h.update(b"\0")
    return h.hexdigest(), entries


def build_corpus(target, rng):
    """造一批覆盖典型边界的数据。返回写出的文件列表。"""
    target = Path(target)
    target.mkdir(parents=True, exist_ok=True)
    made = []

    def write(rel, data):
        p = target / rel
        p.parent.mkdir(parents=True, exist_ok=True)
        p.write_bytes(data)
        made.append(rel)

    # 空文件：7z 能处理，但不能丢
    write("empty.bin", b"")

    # 1 字节
    write("one_byte.bin", b"\x00")

    # 中等文本，带 UTF-8 中文
    write("text/notes.txt",
          ("行一：Auto7z 往返测试\n行二：中文内容\n行三：ASCII mixed\n" * 40).encode("utf-8"))

    # CRLF 内容：换行不能被「规范化」掉
    write("text/crlf.txt", b"line1\r\nline2\r\nline3\r\n" * 30)

    # 不可压缩的随机二进制
    write("random/blob.bin", bytes(rng.getrandbits(8) for _ in range(64 * 1024)))

    # 高压缩比重复内容
    write("repetitive/zeros.bin", b"\x00" * (256 * 1024))

    # 故意刁难的文件名：空格 / 括号 / 加号 / 百分号
    write("odd names/sp ace (paren) +plus%pct.txt", b"odd name content")

    # 中文文件名
    write("odd names/中文文件名.txt", "中文内容测试".encode("utf-8"))

    # 多层嵌套
    write("deep/" + "/".join("lvl%d" % i for i in range(8)) + "/leaf.txt",
          b"deep leaf")

    # 大量小文件
    for i in range(40):
        write("many/f%03d.txt" % i, ("small file %d\n" % i).encode("utf-8"))

    return made


def run(cmd, cwd, data_root, timeout=300):
    env = dict(os.environ)
    env["AUTO7Z_DATA_ROOT"] = str(data_root)
    return subprocess.run(cmd, cwd=str(cwd), env=env, capture_output=True,
                          text=True, encoding="utf-8", errors="replace",
                          timeout=timeout)


def one_round(idx, rng):
    """跑一轮完整往返。返回 (是否通过, 说明)。"""
    base = Path(tempfile.mkdtemp(prefix="Auto7zTest_roundtrip_"))
    try:
        src = base / "src"
        out = base / "out"
        data = base / "data"
        out.mkdir(parents=True, exist_ok=True)
        made = build_corpus(src, rng)

        src_digest, src_entries = tree_digest(src)
        before = len(src_entries)

        # ---- 压缩：把整个 src 目录打成 7z ----
        # 输出位置说明：产品对「目录输入」会在**该目录的父目录**下建一个随机
        # 子目录来放归档（每任务一目录，避免并发任务互相覆盖同名归档）。
        # 实测：输入 base/src，归档出现在 base/<随机名>/src.7z，而不是 cwd。
        # 所以搜索范围必须是 base 整体，不能只搜 cwd 或某个指定输出目录。
        arc_dir = base / "arc"
        arc_dir.mkdir(exist_ok=True)
        r = run([str(EXE), "-z", str(src)], arc_dir, data)
        if r.returncode != 0:
            return False, "压缩失败(退出码 %d): %s" % (
                r.returncode, (r.stdout + r.stderr).strip()[:300])

        # 排除解压产物目录，只认压缩这一步新产生的 .7z
        archives = sorted(p for p in base.rglob("*.7z")
                          if out not in p.parents)
        if not archives:
            listing = [p.relative_to(base).as_posix()
                       for p in base.rglob("*") if p.is_file()][:10]
            return False, "压缩后没有产出 .7z（base 内文件: %s）" % (listing or "空")
        archive = archives[0]

        # ---- 解压 ----
        # 解压同样会新建随机子目录：实测结构变成
        #   <归档所在目录>/<随机名A>/src/...
        # 而不是直接解到 cwd。所以这里在整个 base 里找「名字等于 src 且
        # 位于归档目录之下」的那一个 —— 用 base/src 会误抓到原始输入。
        r = run([str(EXE), "-uz", str(archive)], out, data)
        if r.returncode != 0:
            return False, "解压失败(退出码 %d): %s" % (
                r.returncode, (r.stdout + r.stderr).strip()[:300])

        src_abs = src.resolve()
        candidates = [
            p for p in base.rglob("src")
            if p.is_dir() and p.resolve() != src_abs
        ]
        if not candidates:
            listing = [p.relative_to(base).as_posix()
                       for p in base.rglob("*") if p.is_dir()][:12]
            return False, "解压后找不到 corpus 根目录（目录: %s）" % (listing or "空")

        # 多个候选时优先选文件数最多的（就是真正解出来的那棵）
        extracted = max(candidates, key=lambda d: len([x for x in d.rglob("*") if x.is_file()]))

        got_digest, got_entries = tree_digest(extracted)

        if got_digest == src_digest:
            return True, "%d 个文件，摘要一致 %s" % (before, src_digest[:16])

        # 不一致时给出可行动的差异，而不是只说「失败」
        src_map = dict(src_entries)
        got_map = dict(got_entries)
        missing = sorted(set(src_map) - set(got_map))
        extra = sorted(set(got_map) - set(src_map))
        changed = sorted(k for k in set(src_map) & set(got_map)
                         if src_map[k] != got_map[k])
        parts = []
        if missing:
            parts.append("缺失 %d 个: %s" % (len(missing), missing[:3]))
        if extra:
            parts.append("多出 %d 个: %s" % (len(extra), extra[:3]))
        if changed:
            parts.append("内容不符 %d 个: %s" % (len(changed), changed[:3]))
        return False, "; ".join(parts) or "摘要不一致但逐项相同（可疑）"
    finally:
        # 守卫：路径必须带本次测试的固定前缀（只可能由 mkdtemp 生成），
        # 且必须确实存在。两者都满足才删。
        if base.exists() and base.name.startswith("Auto7zTest_roundtrip_"):
            safe_rmtree(base)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--rounds", type=int, default=3, help="随机轮数（默认 3）")
    ap.add_argument("--seed", type=int, default=None, help="固定随机种子，便于复现")
    a = ap.parse_args()

    seed = a.seed if a.seed is not None else random.randrange(1 << 30)
    rng = random.Random(seed)

    print("=" * 74)
    print("压缩 → 解压 → SHA256 内容一致性测试")
    print("被测程序: %s" % EXE)
    print("随机种子: %d（复现用 --seed %d）" % (seed, seed))
    print("=" * 74)

    ok_count = 0
    for i in range(1, a.rounds + 1):
        ok, detail = one_round(i, rng)
        print("  第 %d/%d 轮  %s  %s" % (i, a.rounds, "[通过]" if ok else "[失败]", detail))
        if ok:
            ok_count += 1

    print()
    print("=" * 74)
    print("往返测试汇总: %d 轮, 通过 %d, 失败 %d" % (a.rounds, ok_count, a.rounds - ok_count))
    print("=" * 74)
    if ok_count != a.rounds:
        return 1
    print("全部一致：压缩/解压未改变任何字节 ✓")
    return 0


if __name__ == "__main__":
    sys.exit(main())
