#!/usr/bin/env python3
# -*- coding: utf-8 -*-
r"""恶意归档与文件系统边界测试（审查报告 P1-2 重写版）。

## 为什么整篇重写

审查报告 P1-2 指出 2.5.1 版本有四个问题，逐条对应到这里的做法：

**问题 A：快照时机错误（假阳性）**
    旧写法是「先解压 → 拍快照 → 再拍一次快照 → 两者比较」。两次快照都在解压
    之后，解压期间真的越界写了也发现不了。
    → 现在每个用例**执行前**拍一次基线，执行后拍一次，逐文件比对
      （新增 / 删除 / 内容变化 / 大小变化）。

**问题 B：Junction 没对准实际解压目录**
    产品的 TryPrepareExtractOutput() 把解压目录建在**归档所在目录**下的随机
    子目录里，与进程 cwd 无关。旧测试把链接建在一处、归档放在另一处，
    根本没命中。
    → 现在把 Junction 直接放在**归档所在目录**里，并把归档条目写成沿该
      Junction 下去的路径；同时另一路把 Junction 放在输出父目录里。

**问题 C：没有构造 ZIP 内部的符号链接条目**
    Windows 目录 Junction ≠ ZIP 里的 Unix symlink 条目。
    → 现在用 ZipInfo.external_attr 设置 0xA1FF0000（S_IFLNK | 0777）并
      把链接目标写进条目内容，构造真正的 symlink 条目。
    → Windows 上 7-Zip 通常不会创建真链接；若确实没创建，判 SKIP 并明确披露，
      不记成 PASS。

**问题 D：跳过项被算成通过**
    旧写法 `record(..., True, "跳过")`。
    → 现在分 PASS / FAIL / SKIP 三类独立统计；有 SKIP 时汇总里单独列出。

## 断言原则

不看 7-Zip 退出码，只看**禁区目录的实际状态**。退出码可以说谎，
文件出现在禁区里不会。

退出码：0 = 无 FAIL（允许有 SKIP，但会披露）；1 = 有 FAIL；2 = 环境问题。
"""
import argparse
import hashlib
import os
import shutil
import subprocess
import sys
import tempfile
import zipfile
from pathlib import Path

from projectpaths import project_root
from safe_ops import safe_rmtree

ROOT = project_root()

PASS, FAIL, SKIP = "PASS", "FAIL", "SKIP"
results = []


def find_exe():
    for cfg in ("Release", "Debug"):
        p = ROOT / "Auto7z_WPF" / "Auto7z_WPF" / "bin" / cfg / "Auto7z.exe"
        if p.is_file():
            return p
    sys.stderr.write("错误: 找不到 Auto7z.exe，请先构建\n")
    raise SystemExit(2)


EXE = find_exe()


def record(name, status, detail=""):
    results.append((name, status, detail))
    tag = {PASS: "[通过]", FAIL: "[失败]", SKIP: "[跳过]"}[status]
    print("  %s %-48s %s" % (tag, name, detail))


# ---------------------------------------------------------------------------
# 目录快照：文件名 -> (大小, 内容sha, mtime)
# ---------------------------------------------------------------------------
def snapshot(root):
    """拍一份禁区目录的完整快照。

    含大小、内容摘要与修改时间：只比文件名会漏掉「原地覆盖」这种更隐蔽的破坏。
    """
    root = Path(root)
    out = {}
    if not root.is_dir():
        return out
    for p in root.rglob("*"):
        if not p.is_file():
            continue
        rel = p.relative_to(root).as_posix()
        try:
            st = p.stat()
            h = hashlib.sha256(p.read_bytes()).hexdigest()
            out[rel] = (st.st_size, h, int(st.st_mtime))
        except OSError:
            out[rel] = (-1, "<unreadable>", 0)
    return out


def diff_snapshots(before, after):
    """返回 (新增, 删除, 改动) 三个列表。"""
    b, a = set(before), set(after)
    added = sorted(a - b)
    removed = sorted(b - a)
    changed = sorted(k for k in (b & a) if before[k] != after[k])
    return added, removed, changed


def assert_untouched(name, forbidden, run_extract_fn, note=""):
    """**执行前**拍基线 → 执行 → 执行后再拍 → 逐项比对。

    这是对审查报告问题 A 的直接修正：基线必须在调用产品之前取得。
    """
    before = snapshot(forbidden)
    code, out = run_extract_fn()
    after = snapshot(forbidden)
    added, removed, changed = diff_snapshots(before, after)

    if added or removed or changed:
        detail = []
        if added:
            detail.append("新增%s" % added[:3])
        if removed:
            detail.append("删除%s" % removed[:3])
        if changed:
            detail.append("改动%s" % changed[:3])
        record(name, FAIL, "；".join(detail))
        return False

    record(name, PASS, "禁区零变化%s（退出码 %d）" % (note, code))
    return True


# ---------------------------------------------------------------------------
# 归档构造
# ---------------------------------------------------------------------------
def make_zip_plain(path, entries):
    with zipfile.ZipFile(path, "w", zipfile.ZIP_DEFLATED) as z:
        for arc, data in entries:
            z.writestr(arc, data)
    return path


def make_zip_symlink(path, link_name, target, extra=None):
    """构造一个真正的 Unix symlink 条目。

    zip 里用 external_attr 的高 16 位表示 Unix mode：0xA1FF = S_IFLNK | 0777。
    条目内容就是链接目标（7-Zip 与 unzip 都按这个约定处理）。
    """
    with zipfile.ZipFile(path, "w", zipfile.ZIP_DEFLATED) as z:
        zi = zipfile.ZipInfo(link_name)
        zi.create_system = 3                      # 3 = Unix
        zi.external_attr = 0xA1FF0000             # S_IFLNK | rwxrwxrwx
        z.writestr(zi, target)
        for arc, data in (extra or []):
            z.writestr(arc, data)
    return path


# ---------------------------------------------------------------------------
# 运行
# ---------------------------------------------------------------------------
def run_extract(archive, data_root, cwd, timeout=180):
    env = dict(os.environ)
    env["AUTO7Z_DATA_ROOT"] = str(data_root)
    r = subprocess.run([str(EXE), "-uz", str(archive)],
                       cwd=str(cwd), env=env, capture_output=True,
                       text=True, encoding="utf-8", errors="replace",
                       timeout=timeout)
    return r.returncode, (r.stdout or "") + (r.stderr or "")


def make_junction(link, target):
    """建目录联接。成功返回 True。"""
    try:
        r = subprocess.run(["cmd", "/c", "mklink", "/J", str(link), str(target)],
                           capture_output=True, text=True, timeout=30)
        return Path(link).exists()
    except (OSError, subprocess.SubprocessError):
        return False


def make_symlink_dir(link, target):
    """尝试建真正的目录符号链接（需要开发者模式或管理员）。"""
    try:
        r = subprocess.run(["cmd", "/c", "mklink", "/D", str(link), str(target)],
                           capture_output=True, text=True, timeout=30)
        return Path(link).exists()
    except (OSError, subprocess.SubprocessError):
        return False


def main():
    ap = argparse.ArgumentParser()
    a = ap.parse_args()

    print("=" * 82)
    print("恶意归档与文件系统边界测试")
    print("被测程序: %s" % EXE)
    print("每个用例：执行前拍基线 → 执行 → 执行后拍快照 → 逐项比对")
    print("=" * 82)

    base = Path(tempfile.mkdtemp(prefix="Auto7zTest_arcsec_"))
    try:
        forbidden = base / "forbidden"
        work = base / "work"
        data = base / "data"
        for d in (forbidden, work, data):
            d.mkdir(parents=True, exist_ok=True)

        # 禁区里放哨兵，能抓到「原地覆盖」这类隐蔽破坏
        (forbidden / "sentinel.txt").write_text("ORIGINAL-SENTINEL", encoding="utf-8")
        (forbidden / "sub").mkdir()
        (forbidden / "sub" / "deep.txt").write_text("deep original", encoding="utf-8")

        # ============ 一、路径穿越族 ============
        print("\n=== 一、路径穿越与绝对路径 ===")

        z = make_zip_plain(work / "dotdot.zip", [
            ("../forbidden/esc1.txt", b"escaped"),
            ("../../forbidden/esc2.txt", b"escaped2"),
            ("normal.txt", b"benign"),
        ])
        assert_untouched("zip 内含 ../ 路径穿越", forbidden,
                         lambda: run_extract(z, data, work))

        z = make_zip_plain(work / "abs.zip", [
            (str(forbidden / "abs1.txt").replace("\\", "/"), b"escaped"),
            ("normal2.txt", b"benign"),
        ])
        assert_untouched("zip 内含绝对路径条目", forbidden,
                         lambda: run_extract(z, data, work))

        z = make_zip_plain(work / "drive.zip", [
            ("C:/Windows/Temp/auto7z_never.txt", b"escaped"),
            ("normal3.txt", b"benign"),
        ])
        probe = Path("C:/Windows/Temp/auto7z_never.txt")
        existed = probe.exists()
        code, out = run_extract(z, data, work)
        created = probe.exists() and not existed
        if created:
            try:
                probe.unlink()
            except OSError:
                pass
        record("zip 内含盘符路径条目", FAIL if created else PASS,
               "C:/Windows/Temp %s" % ("被写入" if created else "无新文件"))

        # 反斜杠形式的穿越（审查报告 §3.1 新增要求）
        z = make_zip_plain(work / "bslash.zip", [
            ("..\\forbidden\\bs1.txt", b"escaped via backslash"),
            ("normal4.txt", b"benign"),
        ])
        assert_untouched("zip 内含反斜杠形式穿越", forbidden,
                         lambda: run_extract(z, data, work))

        # ============ 二、超深 / 超长 / 损坏 ============
        print("\n=== 二、极端结构 ===")

        deep = "/".join("d%d" % i for i in range(60)) + "/deep.txt"
        z = make_zip_plain(work / "deep.zip", [(deep, b"deep")])
        assert_untouched("超深目录（60 层）", forbidden,
                         lambda: run_extract(z, data, work))

        z = make_zip_plain(work / "long.zip", [("a" * 200 + ".txt", b"long")])
        assert_untouched("超长文件名（200 字符）", forbidden,
                         lambda: run_extract(z, data, work))

        z = work / "corrupt.zip"
        z.write_bytes(b"PK\x03\x04" + b"\x00" * 64 + b"garbage" * 200)
        assert_untouched("损坏压缩包", forbidden,
                         lambda: run_extract(z, data, work), note="，不挂死")

        # ============ 三、ZIP 内 symlink 条目（问题 C） ============
        print("\n=== 三、ZIP 内部符号链接条目 ===")

        # 3a: 链接指向禁区绝对路径
        link_zip = work / "symlink_abs.zip"
        make_zip_symlink(link_zip, "evil_link", str(forbidden).replace("\\", "/"),
                         extra=[("evil_link/through_link.txt", b"via symlink")])
        before = snapshot(forbidden)
        code, out = run_extract(link_zip, data, work)
        after = snapshot(forbidden)
        added, removed, changed = diff_snapshots(before, after)
        if added or removed or changed:
            record("zip symlink 条目（指向禁区）", FAIL,
                   "新增%s" % (added[:3],))
        else:
            # 禁区没被动到。再判断 7-Zip 是否真的创建了链接：
            # 找解压产物里是否存在名为 evil_link 的链接
            made_links = [p for p in base.rglob("evil_link") if p.is_symlink()]
            if made_links:
                record("zip symlink 条目（指向禁区）", PASS,
                       "创建了链接但未越界写入禁区")
            else:
                record("zip symlink 条目（指向禁区）", SKIP,
                       "7-Zip 未在 Windows 上创建 symlink（禁区仍未被写入）")

        # 3b: 链接指向禁区内的具体文件（更贴近「覆盖外部文件」攻击）
        link2 = work / "symlink_file.zip"
        make_zip_symlink(link2, "evil_file",
                         (forbidden / "sentinel.txt").as_posix(),
                         extra=[("evil_file.pwn", b"overwritten via link")])
        assert_untouched("zip symlink 指向禁区文件", forbidden,
                         lambda: run_extract(link2, data, work))

        # ============ 四、NTFS Junction（问题 B：对准实际解压目录） ============
        print("\n=== 四、NTFS 目录联接（Junction） ===")

        # 关键：产品的解压目录 = 归档所在目录下的随机子目录。
        # 所以把 Junction 建在**归档所在目录**里，归档条目沿它往下写。
        jdir = base / "jwork"
        jdir.mkdir()
        jlink = jdir / "jump"
        if make_junction(jlink, forbidden):
            z = make_zip_plain(jdir / "viajunction.zip", [
                ("jump/junc1.txt", b"written through junction"),
                ("jump/sub/junc2.txt", b"written through junction 2"),
            ])
            assert_untouched("Junction 在归档目录内被跟随写入", forbidden,
                             lambda: run_extract(z, data, jdir))
        else:
            record("Junction 在归档目录内被跟随写入", SKIP,
                   "环境不允许创建目录联接（未验证，不计为通过）")

        # 另一条路径：Junction 位于「解压输出的父目录」链上
        pdir = base / "pwork"
        pdir.mkdir()
        plink = pdir / "outlink"
        if make_junction(plink, forbidden):
            z = make_zip_plain(pdir / "parentjunc.zip", [
                ("outlink/pjunc.txt", b"written via parent junction"),
            ])
            assert_untouched("Junction 位于输出父目录链上", forbidden,
                             lambda: run_extract(z, data, pdir))
        else:
            record("Junction 位于输出父目录链上", SKIP,
                   "环境不允许创建目录联接（未验证，不计为通过）")

        # 4c: 真正的目录符号链接（与 Junction 不同，需要 SeCreateSymbolicLinkPrivilege）
        sdir = base / "swork"
        sdir.mkdir()
        slink = sdir / "symdir"
        if make_symlink_dir(slink, forbidden):
            z = make_zip_plain(sdir / "viasymdir.zip", [
                ("symdir/sym1.txt", b"written through dir symlink"),
            ])
            assert_untouched("目录符号链接（mklink /D）被跟随写入", forbidden,
                             lambda: run_extract(z, data, sdir))
        else:
            record("目录符号链接（mklink /D）被跟随写入", SKIP,
                   "无 SeCreateSymbolicLinkPrivilege 或未开开发者模式（不计为通过）")

        # ============ 五、正常路径与哨兵 ============
        print("\n=== 五、正向对照与哨兵完整性 ===")

        z = make_zip_plain(work / "benign.zip", [("inside.txt", b"legit")])
        code, out = run_extract(z, data, work)
        found = list(base.rglob("inside.txt"))
        record("正常归档可解压", PASS if found else FAIL,
               "%d 处产出" % len(found))
        if found:
            rel = found[0].relative_to(work).as_posix() if work in found[0].parents else found[0].name
            record("解压落在随机子目录内", PASS if "/" in rel else FAIL,
                   "路径: %s" % rel)

        sent = forbidden / "sentinel.txt"
        ok = sent.exists() and sent.read_text(encoding="utf-8") == "ORIGINAL-SENTINEL"
        record("禁区哨兵文件自始至终完好", PASS if ok else FAIL,
               "MC 一致" if ok else "被改动")

    finally:
        if base.exists() and base.name.startswith("Auto7zTest_arcsec_"):
            # 先解掉 junction，否则 rmtree 可能顺着链接删到别处
            for p in base.rglob("*"):
                try:
                    if p.is_dir() and p.is_symlink():
                        p.unlink()
                except OSError:
                    pass
            safe_rmtree(base)

    passed = sum(1 for _, s, _ in results if s == PASS)
    failed = sum(1 for _, s, _ in results if s == FAIL)
    skipped = sum(1 for _, s, _ in results if s == SKIP)
    print()
    print("=" * 82)
    print("归档安全测试汇总: %d 项, 通过 %d, 失败 %d, 跳过 %d"
          % (len(results), passed, failed, skipped))
    print("=" * 82)
    if skipped:
        print("\n以下用例因环境限制被跳过 —— **不能视为已通过**：")
        for name, s, detail in results:
            if s == SKIP:
                print("  [跳过] %s  %s" % (name, detail))
    if failed:
        print("\n以下用例失败：")
        for name, s, detail in results:
            if s == FAIL:
                print("  [失败] %s  %s" % (name, detail))
        return 1
    if skipped:
        print("\n无失败项，但存在 %d 个跳过项。" % skipped)
        return 0
    print("\n全部通过：未发现越界写入 ✓")
    return 0


if __name__ == "__main__":
    sys.exit(main())
