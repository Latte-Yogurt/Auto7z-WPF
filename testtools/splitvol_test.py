#!/usr/bin/env python3
# -*- coding: utf-8 -*-
r"""分卷压缩 / 分卷解压专项测试（只测试，不改产品代码）。

覆盖三种模式对「分卷」的处理：

  压缩模式（-z）   —— 分卷真的切开了吗：卷数、每卷大小、首卷魔数、SHA256 分卷产物
  解压模式（-uz）  —— 传入 .001 能解开吗；传入中间卷/末卷/缺卷时怎么失败
  自动模式（-a）   —— 传入 .001 能不能识别成压缩包并解压；传入 .002 会怎样

用法:
    python splitvol_test.py                      # 默认 Debug 构建
    python splitvol_test.py --app <Auto7z.exe>
    python splitvol_test.py --keep               # 保留临时目录（调试用）

退出码: 0 = 全部通过, 1 = 有失败项, 2 = 环境不满足

约定：
  - 临时目录走 safe_ops 的受控根（%TEMP%\Auto7zTest_splitvol），清理前有白名单校验；
  - 被测程序会往 %LOCALAPPDATA%\Auto7z\Auto7z.json 写配置，跑前备份、跑完还原；
  - 控制台输出编码跟着代码页走（GBK / UTF-8 都试），不写死一种。

本轮实测确认的产品行为（2026-11-09）：

  | 输入                          | 自动模式                          | 解压模式               |
  |-------------------------------|-----------------------------------|------------------------|
  | x.7z.001                      | 解压（首卷魔数就是 7z 头，认得出） | 解压                   |
  | x.7z.002 / .007（中间/末卷）   | 当成普通文件又压一遍 → x.7z.7z     | 失败，退出码 1         |
  | 全部卷一起选                  | .001 解压 + .002… 被压成新包       | 只解压第一个输入       |
  | 逻辑名 x.7z（只有 .001 落地）  | 找不到文件，退出码 2               | 找不到文件，退出码 2   |

  后三行是产品的真实缺陷（见汇总里标 BUG 的项）。测试按「应有行为」断言，
  不为了让测试变绿把断言放宽成「随便怎样都算过」。
"""
import argparse
import ctypes
import ctypes.wintypes
import hashlib
import json
import os
import random
import re
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
CFG = LOCAL / "Auto7z.json"
SZ = LOCAL / "7-Zip" / "7z.exe"

# 1 MB 是产品允许的最小分卷粒度，测试数据要明显大于它才能切出多卷
VOLUME_MB = 1
VOLUME_BYTES = VOLUME_MB * 1024 * 1024

# 产品每任务开一个随机命名的输出目录，名字是 SHA256 截出来的（长度 ≥40）
PROD_DIR_MINLEN = 40

# 归档产物有两种形态：单卷包（.7z/.zip…）与分卷包（.001/.002…）。
# 分卷后缀也要算：全选分卷时误压出来的产物是「x.7z 等 6 个.7z.001」这种。
ARCHIVE_EXTS = (".7z", ".zip", ".tar", ".gz", ".bz2", ".xz", ".zst")
VOLUME_SUFFIX = re.compile(r"\.\d{3,}$")

TEMP = SafeTemp("splitvol")

APP = None
GUI_APP = None
REF = None


class Result(object):
    def __init__(self):
        self.items = []
        self.failed = 0

    def add(self, name, ok, detail=""):
        self.items.append((name, ok, detail))
        if not ok:
            self.failed += 1
            print("  [失败] %-44s %s" % (name, detail))
        else:
            print("  [通过] %-44s %s" % (name, detail))


def decode_console(raw):
    """控制台输出按代码页走：先 UTF-8 再 GBK，出现替换字符就换下一种。"""
    if not raw:
        return ""
    for enc in ("utf-8", "gbk"):
        try:
            text = raw.decode(enc)
        except UnicodeDecodeError:
            continue
        if "\ufffd" not in text:
            return text
    return raw.decode("gbk", "replace")


# ------------------------------------------------------------------ 配置

def read_cfg():
    try:
        return json.loads(CFG.read_text(encoding="utf-8"))
    except Exception:
        return {}


def set_cfg(**kw):
    """改配置的若干键（标准 JSON，用 json 读写保证永远合法）。"""
    values = read_cfg()
    values.update(kw)
    CFG.write_text(json.dumps(values, ensure_ascii=False, indent=2) + "\n",
                   encoding="utf-8")


def base_cfg(**extra):
    kw = dict(Format="7z", Volume=VOLUME_MB, GenerateSHA256=False,
              OutputPath="", CompressionLevel="Store", IsSolidMode=False,
              Password="", UnzipPassword="", Zstd=False, ErrorLevel="Info")
    kw.update(extra)
    set_cfg(**kw)


# ------------------------------------------------------------------ 进程

def kill_running():
    """程序是单实例的：上一个还在跑，新起的会把任务交接过去然后自己退出。"""
    for image in ("Auto7z.exe", "Auto7zGUI.exe"):
        subprocess.run(["taskkill", "/F", "/IM", image],
                       stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    time.sleep(0.3)


def run(args, exe=None, timeout=120, stdin=subprocess.DEVNULL):
    """跑一次产品，返回 (退出码, 解好码的控制台输出)。"""
    kill_running()
    target = str(exe or APP)
    try:
        r = subprocess.run([target] + [str(a) for a in args],
                           stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                           stdin=stdin, timeout=timeout)
        return r.returncode, decode_console(r.stdout)
    except subprocess.TimeoutExpired as e:
        return None, decode_console(e.stdout or b"")


def spawn(args, exe=None):
    """起一个产品进程但不等待 —— 用来观察它有没有弹窗挂住。"""
    kill_running()
    target = str(exe or APP)
    return subprocess.Popen([target] + [str(a) for a in args],
                            stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                            stdin=subprocess.DEVNULL)


def stop(p, timeout=10.0):
    """结束进程：先等它自己退，超时再强杀。"""
    try:
        p.wait(timeout=timeout)
        return
    except Exception:
        pass
    try:
        p.kill()
        p.wait(timeout=5.0)
    except Exception:
        pass


def wait_windows(pid, timeout=20.0, settle=1.5):
    """等进程把窗口建出来，返回可见窗口的标题列表。

    图形入口遇到「是否合并」这类对话框时会一直挂住，
    只能靠标题判断它停在哪一步 —— 命令行入口没有窗口，看退出码就够了。
    """
    user32 = ctypes.windll.user32
    deadline = time.time() + timeout
    titles = []
    while time.time() < deadline:
        found = []

        def cb(hwnd, lparam):
            wpid = ctypes.wintypes.DWORD()
            user32.GetWindowThreadProcessId(hwnd, ctypes.byref(wpid))
            if wpid.value != pid or not user32.IsWindowVisible(hwnd):
                return True
            n = user32.GetWindowTextLengthW(hwnd)
            if n:
                buf = ctypes.create_unicode_buffer(n + 1)
                user32.GetWindowTextW(hwnd, buf, n + 1)
                found.append(buf.value)
            return True

        user32.EnumWindows(
            ctypes.WINFUNCTYPE(ctypes.c_bool, ctypes.wintypes.HWND, ctypes.wintypes.LPARAM)(cb),
            0)
        titles = found
        if found:
            break
        time.sleep(0.3)

    # 窗口出来后再等一下：进度窗口会自己消失，对话框不会
    deadline = time.time() + settle
    while time.time() < deadline:
        time.sleep(0.2)
    return titles


# ------------------------------------------------------------------ 文件工具

def fresh(name):
    p = TEMP.path / name
    if p.exists():
        safe_rmtree(p)
    p.mkdir(parents=True, exist_ok=True)
    return p


def prod_dirs(d):
    """产品每任务开一个随机命名的输出子目录。"""
    d = Path(d)
    return [p for p in d.iterdir() if p.is_dir() and len(p.name) >= PROD_DIR_MINLEN]


def prod_contents(d):
    """产品输出目录里的全部文件名。"""
    out = []
    for sub in prod_dirs(d):
        out.extend(sorted(f.name for f in sub.iterdir() if f.is_file()))
    return out


def junk_archives(d, expected=None):
    """产品输出目录里**不该出现**的归档产物。

    这个判据原来写错过，值得留个说明：
    早先的实现是「出现任何 .7z 或分卷后缀就算误压」，于是把两种完全正常的情况
    都判成了缺陷 ——
      · 压缩分卷文件本身时，产物本来就叫 `x.7z.021.7z`（或修复前的 `x.7z.7z`）；
      · 自动模式对「压缩包」执行解压是正确行为。
    错误的判据比没有判据更糟：它会把正确行为判红，让人以为产品坏了，
    而真正的问题被这层噪声盖住。

    现在改成：只有在**明确预期不该有归档**时才用这个函数（expected 为空表示
    「这一场景不该产出任何归档」），并且排除调用方点名允许的产物。
    """
    out = []
    for name in prod_contents(d):
        low = name.lower()
        if expected and any(low == e.lower() for e in expected):
            continue
        if any(low.endswith(ext) for ext in ARCHIVE_EXTS) or VOLUME_SUFFIX.search(low):
            out.append(name)
    return out


def volumes_in(d, prefix, fmt):
    """收集 <prefix>.<fmt>.001 / .002 … 这套分卷，按卷号排序。"""
    d = Path(d)
    pat = re.compile(r"^" + re.escape(prefix + "." + fmt) + r"\.(\d{3,})$")
    found = []
    for p in d.rglob("*"):
        if p.is_file():
            m = pat.match(p.name)
            if m:
                found.append((int(m.group(1)), p))
    return [p for _, p in sorted(found, key=lambda x: x[0])]


def sha256_of(p):
    h = hashlib.sha256()
    with open(str(p), "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def extracted(d, name):
    """解压出来的文件落在一个随机子目录里；输入侧的同名文件要排除掉。"""
    d = Path(d)
    hits = [p for p in d.rglob(name) if p.is_file()]
    return [p for p in hits if p.parent != d]


# ------------------------------------------------------------------ 造数据 / 造分卷

def make_data():
    """造一份「不可压缩」的 6 MB 随机数据 + 一个小文本。

    必须不可压缩：可压缩数据会被 7-Zip 压到很小，根本切不出多卷
    （实测 6MB 文本压到 920KB，1MB 分卷下只产出一个 .7z）。
    """
    data = TEMP.path / "data"
    data.mkdir(parents=True, exist_ok=True)
    rnd = random.Random(20261109)
    big = data / "big.bin"
    if not big.exists() or big.stat().st_size != 6 * 1024 * 1024:
        with open(str(big), "wb") as f:
            for _ in range(6):
                f.write(bytes(rnd.getrandbits(8) for _ in range(1024 * 1024)))
    (data / "a.txt").write_text("alpha\n" * 50, encoding="utf-8")
    return data


def make_reference_volumes(data):
    """用 7z.exe 直接造两套标准分卷（7z / zip），供解压与自动模式共用。

    产品自己的分卷压缩另有覆盖（t_compress_split）。这里用 7z.exe 造，
    是为了让「传入别人给的分卷包」也测到，且基线卷不被产品行为污染。
    """
    ref = TEMP.path / "ref"
    if ref.exists():
        safe_rmtree(ref)
    ref.mkdir(parents=True, exist_ok=True)

    for fmt in ("7z", "zip"):
        arch = ref / ("manual." + fmt)
        subprocess.run([str(SZ), "a", "-bso0", "-bsp0", "-t" + fmt,
                        str(arch), str(data / "big.bin"),
                        "-v%dm" % VOLUME_MB],
                       stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                       check=False)
    return ref


def copy_ref(dest, fmt="7z", only=None):
    """把参考分卷复制进用例目录，返回 (首卷, 全部卷)。only 给定时只复制这些卷号。"""
    vols = sorted(REF.glob("manual." + fmt + ".[0-9][0-9][0-9]"))
    out = []
    for v in vols:
        if only is not None and int(v.name[-3:]) not in only:
            continue
        target = Path(dest) / v.name
        shutil.copy2(str(v), str(target))
        out.append(target)
    return (out[0] if out else None), out


# ------------------------------------------------------------------ 压缩模式

def t_compress_split(res, data):
    """分卷压缩：7z 与 zip 都要真的切出多卷，每卷不超过分卷大小。"""
    print("")
    print("=== 压缩模式：分卷是否真的切开 ===")
    src = data / "big.bin"
    src_size = src.stat().st_size

    for fmt in ("7z", "zip"):
        d = fresh("c_" + fmt)
        shutil.copy2(str(src), str(d / "big.bin"))
        base_cfg(Format=fmt)
        code, out = run(["-z", d / "big.bin"])
        vols = volumes_in(d, "big", fmt)
        sizes = [p.stat().st_size for p in vols]
        total = sum(sizes)

        ok = code == 0 and len(vols) >= 2
        detail = "退出码=%s 卷数=%d" % (code, len(vols))
        if ok:
            over = [p.name for p, s in zip(vols, sizes) if s > VOLUME_BYTES]
            if over:
                ok = False
                detail += " 超限卷=%s" % ",".join(over)
            # Store 等级下产物总大小应约等于输入（只多出一点容器头部）
            if abs(total - src_size) > src_size * 0.02:
                ok = False
                detail += " 总大小=%d 输入=%d" % (total, src_size)
            detail += " 单卷=%d" % sizes[0]
        res.add("分卷压缩 %s：切出多卷、每卷≤%dMB、总量与输入相符" % (fmt, VOLUME_MB),
                ok, detail)

        if vols:
            head = open(str(vols[0]), "rb").read(8)
            magic = b"\x37\x7a\xbc\xaf\x27\x1c" if fmt == "7z" else b"PK"
            res.add("分卷压缩 %s：首卷带格式魔数（自动模式靠它识别）" % fmt,
                    head[:len(magic)] == magic, "前 8 字节=%s" % head.hex())


def t_compress_no_split(res, data):
    """不该分卷的边界：输入小于分卷 / Volume=0 / tar 不支持分卷。"""
    print("")
    print("=== 压缩模式：不该分卷的边界 ===")

    d = fresh("c_small")
    shutil.copy2(str(data / "a.txt"), str(d / "a.txt"))
    base_cfg(Format="7z", Volume=VOLUME_MB)
    code, out = run(["-z", d / "a.txt"])
    extra = volumes_in(d, "a", "7z")
    res.add("输入小于分卷大小：不分卷", code == 0 and not extra,
            "退出码=%s 分卷产物=%s" % (code, ",".join(p.name for p in extra) or "无"))

    # 正好等于分卷大小：TotalSize <= volumeBytes，也不该切
    d = fresh("c_exact")
    with open(str(d / "x.bin"), "wb") as f:
        rnd = random.Random(9)
        for _ in range(1024 * 1024):
            f.write(bytes([rnd.getrandbits(8)]))
    base_cfg(Format="7z", Volume=VOLUME_MB)
    code, out = run(["-z", d / "x.bin"])
    extra = volumes_in(d, "x", "7z")
    res.add("输入恰好等于分卷大小：不分卷（边界含等号）",
            code == 0 and not extra,
            "退出码=%s 分卷产物=%s" % (code, ",".join(p.name for p in extra) or "无"))

    # 只多 1 字节就必须切
    d = fresh("c_plus1")
    with open(str(d / "y.bin"), "wb") as f:
        rnd = random.Random(9)
        for _ in range(1024 * 1024 + 1):
            f.write(bytes([rnd.getrandbits(8)]))
    base_cfg(Format="7z", Volume=VOLUME_MB)
    code, out = run(["-z", d / "y.bin"])
    extra = volumes_in(d, "y", "7z")
    res.add("输入比一分卷多 1 字节：必须切成两卷",
            code == 0 and len(extra) >= 2,
            "退出码=%s 卷数=%d" % (code, len(extra)))

    d = fresh("c_vol0")
    shutil.copy2(str(data / "big.bin"), str(d / "big.bin"))
    base_cfg(Format="7z", Volume=0)
    code, out = run(["-z", d / "big.bin"])
    extra = volumes_in(d, "big", "7z")
    res.add("Volume=0：不分卷", code == 0 and not extra,
            "退出码=%s 分卷产物=%s" % (code, ",".join(p.name for p in extra) or "无"))

    # tar 是连续字节流，分卷会产出解不开的 .tar.001
    d = fresh("c_tar")
    shutil.copy2(str(data / "big.bin"), str(d / "big.bin"))
    base_cfg(Format="tar", Volume=VOLUME_MB, Zstd=False)
    code, out = run(["-z", d / "big.bin"])
    extra = volumes_in(d, "big", "tar")
    res.add("tar + 分卷：不分卷（产品对 tar 直接拒绝分卷）",
            code == 0 and not extra,
            "退出码=%s 分卷产物=%s" % (code, ",".join(p.name for p in extra) or "无"))


def t_compress_sha256(res, data):
    """分卷场景下 SHA256 必须是**一份**清单，列出全部卷的哈希。

    这条断言曾经写反过：早先的实现对每个分卷各写一份只含一行的 .sha256，
    当时的测试把它当成了正确行为（「每卷各一份」），于是把一个真缺陷固化成了期望值。
    校验文件的用途是「用一份清单校验整套产物」，一个归档拆成 7 卷就必须有 7 行，
    而不是 7 个各含一行的文件 —— 那样校验工具还得先手工拼起来。
    """
    print("")
    print("=== 压缩模式：分卷 + SHA256 ===")
    d = fresh("c_sha")
    shutil.copy2(str(data / "big.bin"), str(d / "big.bin"))
    base_cfg(Format="7z", GenerateSHA256=True)
    code, out = run(["-z", d / "big.bin"])
    vols = volumes_in(d, "big", "7z")
    shas = sorted(Path(d).rglob("*.sha256"))

    # 一份，且只有一份
    res.add("分卷 + SHA256：整套只产出一份 .sha256",
            code == 0 and len(vols) >= 2 and len(shas) == 1,
            "卷数=%d .sha256=%d %s"
            % (len(vols), len(shas), ",".join(p.name for p in shas)))

    if len(shas) != 1:
        return

    sha = shas[0]
    lines = [l.strip() for l in sha.read_text(encoding="utf-8-sig").splitlines() if l.strip()]

    # 行数必须等于卷数：每一卷都要被记录
    res.add("分卷 + SHA256：清单行数 = 卷数（每卷一行）",
            len(lines) == len(vols),
            "行数=%d 卷数=%d" % (len(lines), len(vols)))

    # 逐行核对哈希与文件名
    bad = []
    listed = set()
    for line in lines:
        digest, _, name = line.partition(" *")
        if not name:
            bad.append("格式异常:" + line[:20])
            continue
        listed.add(name)
        target = sha.parent / name
        if not target.exists():
            bad.append(name + "(文件不存在)")
            continue
        if digest.lower() != sha256_of(target):
            bad.append(name + "(哈希不符)")

    res.add("分卷 + SHA256：每行哈希与对应卷一致", not bad,
            ",".join(bad[:4]) or "全部一致")

    missing = [v.name for v in vols if v.name not in listed]
    res.add("分卷 + SHA256：没有漏记任何一卷", not missing,
            "漏记=%s" % (",".join(missing) or "无"))

    # 清单名取逻辑名（big.7z.sha256），不挂到某个分卷上
    res.add("分卷 + SHA256：清单名为逻辑名 + .sha256",
            sha.name == "big.7z.sha256",
            "实际=%s" % sha.name)

    # 无 BOM 仍是硬性要求
    res.add("分卷 + SHA256：清单不带 BOM",
            not sha.read_bytes().startswith(b"\xef\xbb\xbf"),
            "首 3 字节=%s" % sha.read_bytes()[:3].hex())


def t_compress_multi_input(res, data):
    """多个输入合并成一个分卷包：卷数正常、名字统一。"""
    print("")
    print("=== 压缩模式：多输入合并后分卷 ===")
    d = fresh("c_multi")
    rnd = random.Random(31)
    for name in ("f1.bin", "f2.bin"):
        with open(str(d / name), "wb") as f:
            for _ in range(1024 * 1024):
                f.write(bytes([rnd.getrandbits(8)]))
    base_cfg(Format="7z")
    code, out = run(["-z", d / "f1.bin", d / "f2.bin"])
    sub = prod_dirs(d)
    vols = sorted(p.name for p in sub[0].iterdir()) if sub else []
    res.add("多输入合并 -> 分卷包（卷数≥2、名字统一）",
            code == 0 and len(sub) == 1 and len(vols) >= 2,
            "退出码=%s 卷数=%d 首卷=%s" % (code, len(vols), vols[0] if vols else "无"))


# ------------------------------------------------------------------ 解压模式

def t_extract_first_volume(res, data):
    """解压模式的核心用例：传入 .001 必须解开，内容必须逐字节一致。"""
    print("")
    print("=== 解压模式：传入首卷 .001 ===")
    src_hash = sha256_of(data / "big.bin")

    for fmt in ("7z", "zip"):
        d = fresh("x_first_" + fmt)
        base_cfg(Format="7z")
        first, vols = copy_ref(d, fmt)
        code, out = run(["-uz", first])
        got = extracted(d, "big.bin")
        ok = code == 0 and len(got) == 1 and sha256_of(got[0]) == src_hash
        detail = "退出码=%s 卷数=%d 解出=%d" % (code, len(vols), len(got))
        if not ok and out.strip():
            detail += " 末行=%s" % out.strip().splitlines()[-1][:50]
        res.add("解压模式 %s：.001 → 成功且内容一致" % fmt, ok, detail)


def t_extract_middle_volume(res, data):
    """传入中间卷 / 末卷：必须自动指回首卷并解开，内容逐字节一致。

    这条曾经是「必须失败」：旧实现把 .002 原样交给 7-Zip，7-Zip 只认 .001，
    于是必然报错。用户从资源管理器多选时送进来的可能就是续卷，
    这不是「传错了」，而是分卷必须按「套」解析 —— 修好之后断言改为应当成功。
    """
    print("")
    print("=== 解压模式：传入中间卷 / 末卷 ===")
    src_hash = sha256_of(data / "big.bin")
    for idx in ("002", "007"):
        d = fresh("x_mid_" + idx)
        base_cfg(Format="7z")
        copy_ref(d, "7z")
        code, out = run(["-uz", d / ("manual.7z." + idx)])
        got = extracted(d, "big.bin")
        ok = code == 0 and bool(got) and sha256_of(got[0]) == src_hash
        last = out.strip().splitlines()[-1][:60] if out.strip() else "(无输出)"
        res.add("解压模式：传入 .%s → 自动指回首卷并解开" % idx, ok,
                "退出码=%s 解出=%d 末行=%s" % (code, len(got), last))

    # 首卷不在（被删掉）时仍然必须失败，不能凭续卷硬解出一个空目录。
    d = fresh("x_mid_no001")
    base_cfg(Format="7z")
    copy_ref(d, "7z", only=[2, 3, 4, 5, 6, 7])
    code, out = run(["-uz", d / "manual.7z.002"])
    res.add("解压模式：传入 .002 且首卷缺失 → 退出码 1、不留产物",
            code == 1 and not extracted(d, "big.bin") and not prod_dirs(d),
            "退出码=%s 解出=%d"
            % (code, len(extracted(d, "big.bin"))))


def t_extract_broken_set(res, data):
    """缺中间卷 / 只有首卷 / 卷被改坏：失败、退出码 1、清掉半成品目录。"""
    print("")
    print("=== 解压模式：不完整 / 损坏的分卷集 ===")

    d = fresh("x_missing_mid")
    base_cfg(Format="7z")
    first, _ = copy_ref(d, "7z", only=[1, 2, 4, 5, 6, 7])
    code, out = run(["-uz", first])
    res.add("解压模式：缺中间卷 → 退出码 1、清理半成品目录",
            code == 1 and not prod_dirs(d),
            "退出码=%s 残留目录=%s"
            % (code, ",".join(p.name for p in prod_dirs(d)) or "无"))

    d = fresh("x_only_001")
    base_cfg(Format="7z")
    first, _ = copy_ref(d, "7z", only=[1])
    code, out = run(["-uz", first])
    res.add("解压模式：只有 .001 → 退出码 1、清理半成品目录",
            code == 1 and not prod_dirs(d),
            "退出码=%s 残留目录=%s"
            % (code, ",".join(p.name for p in prod_dirs(d)) or "无"))

    d = fresh("x_corrupt")
    base_cfg(Format="7z")
    first, _ = copy_ref(d, "7z")
    (d / "manual.7z.003").write_bytes(b"garbage" * 10000)
    code, out = run(["-uz", first])
    res.add("解压模式：中间卷内容被改坏 → 退出码 1、清理半成品目录",
            code == 1 and not prod_dirs(d),
            "退出码=%s 残留目录=%s"
            % (code, ",".join(p.name for p in prod_dirs(d)) or "无"))


def t_extract_logical_name(res, data):
    """逻辑名 x.7z、磁盘上只有 x.7z.001。

    产品三处代码都写了「只有 .001 落地」的回退
    （IsSplitArchive / ResolveFirstVolumePath / TryPrepareExtractOutput），
    但 SetPendingFiles 先要求 File.Exists(逻辑名) —— 那一关就把它挡掉了：
    退出码 2，压根没开始解压。这条断言按「应当回退」写，所以当前是失败项。
    """
    print("")
    print("=== 解压模式：逻辑名（磁盘上只有 .001）===")
    src_hash = sha256_of(data / "big.bin")
    d = fresh("x_logical")
    base_cfg(Format="7z")
    copy_ref(d, "7z")
    code, out = run(["-uz", d / "manual.7z"])
    got = extracted(d, "big.bin")
    ok = code == 0 and bool(got) and sha256_of(got[0]) == src_hash
    last = out.strip().splitlines()[-1][:60] if out.strip() else "(无输出)"
    if not ok:
        last = "BUG(回退没走到) " + last
    res.add("解压模式：逻辑名 x.7z → 应回退到 .001", ok,
            "退出码=%s 解出=%d 末行=%s" % (code, len(got), last))


def t_extract_all_selected(res, data):
    """多选全部分卷：整套归并成首卷，只解一次，与枚举顺序无关。"""
    print("")
    print("=== 解压模式：一次传入全部分卷 ===")
    src_hash = sha256_of(data / "big.bin")

    d = fresh("x_all_ok")
    base_cfg(Format="7z")
    _, vols = copy_ref(d, "7z")
    code, out = run(["-uz"] + vols)
    got = extracted(d, "big.bin")
    started = out.count("开始解压")
    res.add("解压模式：全选分卷（.001 是第一个）→ 只解一次、内容一致",
            code == 0 and len(got) == 1 and sha256_of(got[0]) == src_hash
            and started == 1,
            "退出码=%s 解出=%d 开始解压=%d" % (code, len(got), started))

    # 顺序颠倒：旧实现只认 batch.Files[0]，第一个是 .003 就必然失败。
    # 这是 UI 多选的真实场景 —— 资源管理器框选不保证顺序。
    d = fresh("x_all_rev")
    base_cfg(Format="7z")
    _, vols = copy_ref(d, "7z")
    bynum = dict((int(p.name[-3:]), p) for p in vols)
    order = [bynum[3], bynum[1], bynum[2], bynum[4], bynum[5], bynum[6], bynum[7]]
    code, out = run(["-uz"] + order)
    got = extracted(d, "big.bin")
    started = out.count("开始解压")
    res.add("解压模式：全选分卷（.003 排第一个）→ 仍只解一次、内容一致",
            code == 0 and len(got) == 1 and sha256_of(got[0]) == src_hash
            and started == 1,
            "退出码=%s 解出=%d 开始解压=%d" % (code, len(got), started))

    # 多个互不相干的归档：每个都要解开，不能只解第一个。
    d = fresh("x_multi_arch")
    base_cfg(Format="7z", Volume=0)
    for name in ("one", "two", "three"):
        (d / (name + ".txt")).write_text(name * 100, encoding="utf-8")
        subprocess.run([str(SZ), "a", "-bso0", "-bsp0",
                        str(d / (name + ".7z")), str(d / (name + ".txt"))],
                       stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, check=False)
    code, out = run(["-uz", d / "one.7z", d / "two.7z", d / "three.7z"])
    names = [p.name for p in d.rglob("*.txt") if p.parent != d]
    res.add("解压模式：三个独立归档 → 三个都要解开",
            code == 0 and sorted(set(names)) == ["one.txt", "three.txt", "two.txt"],
            "退出码=%s 解出=%s" % (code, ",".join(sorted(names)) or "无"))


# ------------------------------------------------------------------ 自动模式

def t_auto_first_volume(res, data):
    """自动模式的核心用例：传入 .001 要识别成压缩包并解压，不能再压一遍。"""
    print("")
    print("=== 自动模式：传入 .001 ===")
    src_hash = sha256_of(data / "big.bin")

    for label, pre in (("-a 显式", ["-a"]), ("不带开关", [])):
        d = fresh("a_first_" + ("explicit" if pre else "implicit"))
        base_cfg(Format="7z")
        first, vols = copy_ref(d, "7z")
        code, out = run(pre + [first])
        got = extracted(d, "big.bin")
        junk = junk_archives(d)
        ok = (code == 0 and len(got) == 1
              and sha256_of(got[0]) == src_hash and not junk)
        last = out.strip().splitlines()[-1][:50] if out.strip() else "(无输出)"
        res.add("自动模式（%s）：.001 → 识别为压缩包并解压" % label, ok,
                "退出码=%s 解出=%d 误压产物=%s 末行=%s"
                % (code, len(got), ",".join(junk) or "无", last))

    # zip 分卷的首卷同样是 PK 头，自动模式也该认
    d = fresh("a_first_zip")
    base_cfg(Format="7z")
    first, _ = copy_ref(d, "zip")
    code, out = run(["-a", first])
    got = extracted(d, "big.bin")
    res.add("自动模式：zip 分卷 .001 → 识别为压缩包并解压",
            code == 0 and bool(got) and sha256_of(got[0]) == src_hash
            and not junk_archives(d),
            "退出码=%s 解出=%d 误压产物=%s"
            % (code, len(got), ",".join(junk_archives(d)) or "无"))

    # 大写的 .7Z.001 也要认（后缀比较是 OrdinalIgnoreCase）
    d = fresh("a_first_upper")
    base_cfg(Format="7z")
    for v in sorted(REF.glob("manual.7z.[0-9][0-9][0-9]")):
        shutil.copy2(str(v), str(d / ("MANUAL.7Z." + v.name[-3:])))
    code, out = run(["-a", d / "MANUAL.7Z.001"])
    got = extracted(d, "big.bin")
    res.add("自动模式：大写 .7Z.001 → 仍然识别并解压",
            code == 0 and bool(got) and sha256_of(got[0]) == src_hash,
            "退出码=%s 解出=%d" % (code, len(got)))


def t_auto_middle_volume(res, data):
    """自动模式传入中间卷 .002 / 末卷 .007。

    中间卷的魔数是压缩数据流，不是 7z 头，IsArchiveFile 认不出来 ——
    于是走进压缩分支，产出 x.7z.7z。这是「分卷被当成普通文件」的核心缺陷。
    """
    print("")
    print("=== 自动模式：传入中间卷 / 末卷 ===")
    for idx in ("002", "007"):
        d = fresh("a_mid_" + idx)
        base_cfg(Format="7z")
        copy_ref(d, "7z")
        code, out = run(["-a", d / ("manual.7z." + idx)])
        junk = junk_archives(d)
        res.add("自动模式：.%s → 应识别为分卷续卷、不该再压一遍" % idx,
                code == 0 and not junk,
                "退出码=%s 误压产物=%s（BUG：续卷魔数不是 7z 头，被当成普通文件）"
                % (code, ",".join(junk) or "无"))


def t_auto_all_volumes(res, data):
    """一次把整套分卷交给自动模式：应当只解压一次。"""
    print("")
    print("=== 自动模式：一次传入全部分卷 ===")
    src_hash = sha256_of(data / "big.bin")

    d = fresh("a_all")
    base_cfg(Format="7z")
    _, vols = copy_ref(d, "7z")
    code, out = run(["-a"] + vols)
    got = extracted(d, "big.bin")
    junk = junk_archives(d)
    # 「只解压一次」不能只看有没有产物：把同一套卷解 7 遍也会有产物。
    # 数两个信号：解出来的 big.bin 份数、控制台里「开始解压」的条数，都必须是 1。
    started = out.count("开始解压")
    res.add("自动模式：全选分卷 → 只解压一次，不把 .002… 压成新包",
            code == 0 and len(got) == 1 and sha256_of(got[0]) == src_hash
            and not junk and started == 1,
            "退出码=%s 解出=%d 开始解压=%d 误压产物=%s"
            % (code, len(got), started, ",".join(junk) or "无"))


def t_auto_mixed(res, data):
    """自动模式混合输入：分卷该解、普通文件该压，两条分支各走各的。"""
    print("")
    print("=== 自动模式：分卷 + 普通文件 ===")
    src_hash = sha256_of(data / "big.bin")
    d = fresh("a_mixed")
    base_cfg(Format="7z")
    first, vols = copy_ref(d, "7z")
    shutil.copy2(str(data / "a.txt"), str(d / "a.txt"))
    code, out = run(["-a", first, d / "a.txt"])
    got = extracted(d, "big.bin")
    packs = [p for p in Path(d).rglob("a.7z") if p.is_file()]
    res.add("自动模式：分卷解压 + 普通文件压缩",
            code == 0 and bool(got) and sha256_of(got[0]) == src_hash and packs,
            "退出码=%s 解出=%d 普通文件压缩包=%d" % (code, len(got), len(packs)))


def t_auto_logical_name(res, data):
    """自动模式给逻辑名 x.7z：应当回退到 .001。"""
    print("")
    print("=== 自动模式：逻辑名（磁盘上只有 .001）===")
    src_hash = sha256_of(data / "big.bin")
    d = fresh("a_logical")
    base_cfg(Format="7z")
    copy_ref(d, "7z")
    code, out = run(["-a", d / "manual.7z"])
    got = extracted(d, "big.bin")
    ok = code == 0 and bool(got) and sha256_of(got[0]) == src_hash
    last = out.strip().splitlines()[-1][:60] if out.strip() else "(无输出)"
    if not ok:
        last = "BUG(回退没走到) " + last
    res.add("自动模式：逻辑名 x.7z → 应回退到 .001", ok,
            "退出码=%s 解出=%d 末行=%s" % (code, len(got), last))


# ------------------------------------------------------------------ 端到端 / 加密 / 入口

def t_round_trip(res, data):
    """产品自己切出来的分卷，再用产品解回去 —— 最接近真实使用的一条链路。"""
    print("")
    print("=== 端到端：产品压缩 → 产品解压 ===")
    src_hash = sha256_of(data / "big.bin")

    for fmt in ("7z", "zip"):
        d = fresh("rt_" + fmt)
        shutil.copy2(str(data / "big.bin"), str(d / "big.bin"))
        base_cfg(Format=fmt)
        code, out = run(["-z", d / "big.bin"])
        subs = prod_dirs(d)
        if not subs:
            res.add("端到端 %s：压缩产出分卷" % fmt, False,
                    "退出码=%s 无产物目录" % code)
            continue
        vols = volumes_in(subs[0], "big", fmt)
        # 挪到用例目录根部，模拟用户把分卷拷出来转交给解压
        for v in vols:
            shutil.copy2(str(v), str(d / v.name))
        code, out = run(["-uz", d / ("big." + fmt + ".001")])
        got = extracted(d, "big.bin")
        res.add("端到端 %s：分卷压缩 → .001 解压 → 内容一致" % fmt,
                code == 0 and bool(got) and sha256_of(got[0]) == src_hash,
                "退出码=%s 卷数=%d" % (code, len(vols)))


def t_password_split(res, data):
    """加密分卷：带密码能解；-mhe=on 的首卷仍有 7z 魔数。"""
    print("")
    print("=== 加密分卷 ===")
    src_hash = sha256_of(data / "big.bin")

    for label, enc_name in (("不加密文件名", False), ("加密文件名", True)):
        d = fresh("p_" + ("name" if enc_name else "data"))
        shutil.copy2(str(data / "big.bin"), str(d / "big.bin"))
        base_cfg(Format="7z", Password="test123", IsEncFileName=enc_name)
        code1, _ = run(["-z", d / "big.bin"])
        subs = prod_dirs(d)
        vols = volumes_in(subs[0], "big", "7z") if subs else []
        for v in vols:
            shutil.copy2(str(v), str(d / v.name))
        if vols:
            head = open(str(vols[0]), "rb").read(6)
            # -mhe=on 加密的是头部内容，7z 签名块仍留在文件开头
            res.add("加密分卷（%s）：首卷仍有 7z 魔数（自动模式识别的前提）" % label,
                    head == b"\x37\x7a\xbc\xaf\x27\x1c", "前 6 字节=%s" % head.hex())
        else:
            res.add("加密分卷（%s）：首卷仍有 7z 魔数" % label, False, "没有分卷产物")

        base_cfg(Format="7z", UnzipPassword="test123")
        code2, out = run(["-uz", d / "big.7z.001"])
        got = extracted(d, "big.bin")
        res.add("加密分卷（%s）：密码解压成功且内容一致" % label,
                code1 == 0 and code2 == 0 and bool(got)
                and sha256_of(got[0]) == src_hash,
                "压缩=%s 解压=%s 解出=%d" % (code1, code2, len(got)))

    # 加密文件名 + 自动模式：魔数在，应当能识别并解压
    d = fresh("p_auto_name")
    shutil.copy2(str(data / "big.bin"), str(d / "big.bin"))
    base_cfg(Format="7z", Password="test123", IsEncFileName=True)
    run(["-z", d / "big.bin"])
    subs = prod_dirs(d)
    vols = volumes_in(subs[0], "big", "7z") if subs else []
    for v in vols:
        shutil.copy2(str(v), str(d / v.name))
    base_cfg(Format="7z", UnzipPassword="test123")
    code, out = run(["-a", d / "big.7z.001"])
    got = extracted(d, "big.bin")
    res.add("加密分卷 + 自动模式：.001 → 识别并解压",
            code == 0 and bool(got) and sha256_of(got[0]) == src_hash,
            "退出码=%s 解出=%d" % (code, len(got)))


def t_single_instance_mode_handoff(res, data):
    """单实例交接必须带着模式开关。

    这条曾经是真实缺陷：外壳扩展以 -uz 启动第二个进程，第二个进程发现已有实例在跑，
    就把路径交给它然后退出。若交接只传路径，-uz 在这一步丢了 ——
    接收方按自己的界面模式处理，右键「解压」就变成了压缩。
    症状极误导：菜单发的是 -uz、日志显示进程正常、产物却是压缩包。

    验证方式：先起一个实例（主窗口 + 监听），再用第二个进程带 -uz 传入分卷，
    期望第一个实例解出原始文件，而不是产出压缩包。
    """
    print("")
    print("=== 单实例交接：模式开关不能丢 ===")
    if not GUI_APP or not Path(GUI_APP).exists():
        res.add("单实例交接", False, "找不到 Auto7zGUI.exe, 跳过")
        return

    src_hash = sha256_of(data / "big.bin")
    d = fresh("handoff")
    base_cfg(Format="7z")

    # 造一套分卷（用产品自己的压缩，顺带确认压缩路径正常）
    shutil.copy2(str(data / "big.bin"), str(d / "big.bin"))
    base_cfg(Format="7z", Volume=VOLUME_MB, GenerateSHA256=False)
    code, _ = run(["-z", d / "big.bin"])
    subs = prod_dirs(d)
    if not subs:
        res.add("单实例交接", False, "压缩没有产出分卷目录")
        return
    vols = volumes_in(subs[0], "big", "7z")
    for v in vols:
        shutil.copy2(str(v), str(d / v.name))

    # 起第一个实例（无参数 = 主窗口 + 监听）
    kill_running()
    first = subprocess.Popen([str(GUI_APP)],
                             stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                             stdin=subprocess.DEVNULL)
    try:
        time.sleep(5.0)
        if first.poll() is not None:
            res.add("单实例交接", False, "第一个实例没起来")
            return

        # 第二个进程：-uz 传入 .001（走交接路径）
        r2 = subprocess.run([str(APP), "-uz", str(d / "big.7z.001")],
                            stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                            stdin=subprocess.DEVNULL, timeout=30)

        # 等第一个实例处理完（最多 60 秒）
        got = []
        for _ in range(60):
            got = extracted(d, "big.bin")
            if got:
                break
            time.sleep(1)

        junk = junk_archives(d)

        # 上面的 junk_archives 会把「产品输出目录里的归档」都算成误压产物，
        # 但本用例的输入是一整套分卷，产品会把它们读进去；
        # 判定只关心「有没有把分卷压成新包」，所以这里直接看有没有 .7z 结尾的新归档，
        # 而不是分卷后缀。这样判据与本用例真正要防的缺陷对齐。
        packed_new = [n for n in junk if n.lower().endswith(".7z")]

        ok = bool(got) and sha256_of(got[0]) == src_hash and not packed_new
        res.add("单实例交接：-uz 解压请求不被降级成压缩", ok,
                "第二进程退出码=%s 解出=%d 被压成新包=%s"
                % (r2.returncode, len(got), ",".join(packed_new) or "无"))
    finally:
        try:
            first.kill()
            first.wait(timeout=5)
        except Exception:
            pass


def t_shellmenu_entry(res, data):
    """图形入口（右键菜单 / 拖拽，Auto7zGUI.exe）传 .001 也要能解、能识别。"""
    print("")
    print("=== 图形入口（右键菜单 / 拖拽）===")
    if not GUI_APP or not Path(GUI_APP).exists():
        res.add("图形入口：分卷解压", False, "找不到 Auto7zGUI.exe, 跳过")
        return

    src_hash = sha256_of(data / "big.bin")

    d = fresh("sm_uz")
    base_cfg(Format="7z")
    first, _ = copy_ref(d, "7z")
    code, out = run(["-uz", first], exe=GUI_APP)
    got = extracted(d, "big.bin")
    res.add("图形入口：-uz .001 → 解压成功且内容一致",
            code == 0 and bool(got) and sha256_of(got[0]) == src_hash,
            "退出码=%s 解出=%d" % (code, len(got)))

    d = fresh("sm_auto")
    base_cfg(Format="7z")
    first, _ = copy_ref(d, "7z")
    code, out = run(["-a", first], exe=GUI_APP)
    got = extracted(d, "big.bin")
    junk = junk_archives(d)
    res.add("图形入口：-a .001 → 识别为压缩包并解压",
            code == 0 and bool(got) and sha256_of(got[0]) == src_hash and not junk,
            "退出码=%s 解出=%d 误压产物=%s"
            % (code, len(got), ",".join(junk) or "无"))

    # 资源管理器多选「整套分卷」再右键（真实用法）：整套应当只解压一次。
    # 旧实现把 .002… 当成普通文件，于是走到「是否合并」对话框 —— 弹窗等确认，
    # 没人点就一直挂着。命令行下同一批输入不弹窗（读不到输入按合并处理），
    # 所以只在图形入口复现「挂住」这个症状。
    d = fresh("sm_all")
    base_cfg(Format="7z")
    _, vols = copy_ref(d, "7z")
    p = spawn(["-a"] + vols, exe=GUI_APP)
    try:
        titles = wait_windows(p.pid, timeout=20.0)
        hung = p.poll() is None
    finally:
        stop(p)
    res.add("图形入口：-a 全选分卷 → 不该弹阻塞对话框",
            (not hung) and not any("提示" in t for t in titles),
            "窗口=%s 仍挂住=%s"
            % (",".join(titles) or "无", "是" if hung else "否"))


# ------------------------------------------------------------------ 收尾

def main():
    global APP, GUI_APP, REF
    ap = argparse.ArgumentParser()
    ap.add_argument("--app", help="被测程序路径（Auto7z.exe）")
    ap.add_argument("--config", default="Debug", help="Debug / Release")
    ap.add_argument("--keep", action="store_true", help="保留临时目录")
    ap.add_argument("--clear-config", action="store_true",
                    help="开跑前清空配置（产品会重建默认值），跑完仍还原成原样")
    ap.add_argument("--purge-portable", action="store_true",
                    help="删掉 exe 旁的便携数据目录（它存在时会顶掉普通配置）")
    args = ap.parse_args()

    APP = Path(args.app) if args.app else PROJECT / "bin" / args.config / "Auto7z.exe"
    if not APP.exists():
        print("找不到被测程序: %s" % APP)
        print("先构建: msbuild Auto7z_WPF.sln /t:Rebuild /p:Configuration=%s" % args.config)
        return 2
    GUI_APP = APP.parent / "Auto7zGUI.exe"

    mutex = claim_checkout()
    if mutex is None:
        print("已有另一份 Auto7z 测试正在跑；并行跑会互相抢配置与单实例，先等它结束。")
        return 2

    guard = ConfigGuard(APP)
    if not guard.prepare(purge_portable=args.purge_portable):
        release_checkout(mutex)
        return 2

    # 7-Zip 组件是「首次执行任务时」才释放到数据目录的，不是启动时。
    # 而别的测试（尤其便携模式那项）会清空数据目录，组件跟着一起没掉 ——
    # 此时直接跑分卷测试会全部失败，且失败原因看起来像产品缺陷。
    # 先跑一次无足轻重的压缩让它自行重新释放，再开始真正的测试。
    # 注意要先把临时目录建好：run() 要用它做工作目录。
    TEMP.ensure()
    if not SZ.exists():
        print("7-Zip 组件缺失，先跑一次任务让产品自行释放 ...")
        warm = TEMP.path / "warmup"
        warm.mkdir(parents=True, exist_ok=True)
        (warm / "w.txt").write_text("warmup", encoding="utf-8")
        set_cfg(Format="7z", Volume=2000, GenerateSHA256=False, OutputPath="",
                CompressionLevel="Store", ErrorLevel="Info")
        run(["-z", warm / "w.txt"], timeout=90)
        safe_rmtree(warm)
        if not SZ.exists():
            print("仍然找不到 7z 组件: %s" % SZ)
            release_checkout(mutex)
            return 2

    data = make_data()
    REF = make_reference_volumes(data)

    # 加入配置链：第一个进来的工具记下原始配置，后续工具复用它，
    # 只有最后一个离开时才真正还原 —— 避免「每个工具都还原到自己开始那刻」的漂移。
    # --clear-config 要求「从默认配置起跑」，而用户原本那份配置仍会一字不差地还回去。
    chain_token = guard.join_chain()
    cleared = []
    if args.clear_config:
        cleared = guard.clear()

    print("=" * 74)
    print("Auto7z 分卷压缩 / 分卷解压专项测试")
    print("被测程序: %s" % APP)
    print("临时目录: %s" % TEMP.path)
    print("配置: %s" % guard.local_cfg)
    if cleared:
        print("已清空配置 %d 份，产品将从默认值起跑" % len(cleared))
    n7 = len(list(REF.glob("manual.7z.[0-9]*")))
    nz = len(list(REF.glob("manual.zip.[0-9]*")))
    print("参考分卷: %d 个 7z 卷 / %d 个 zip 卷" % (n7, nz))
    print("=" * 74)

    res = Result()
    try:
        t_compress_split(res, data)
        t_compress_no_split(res, data)
        t_compress_sha256(res, data)
        t_compress_multi_input(res, data)
        t_extract_first_volume(res, data)
        t_extract_middle_volume(res, data)
        t_extract_broken_set(res, data)
        t_extract_logical_name(res, data)
        t_extract_all_selected(res, data)
        t_auto_first_volume(res, data)
        t_auto_middle_volume(res, data)
        t_auto_all_volumes(res, data)
        t_auto_mixed(res, data)
        t_auto_logical_name(res, data)
        t_round_trip(res, data)
        t_password_split(res, data)
        t_single_instance_mode_handoff(res, data)
        t_shellmenu_entry(res, data)
    finally:
        guard.leave_chain(chain_token)
        kill_running()

        print("")
        print("=" * 74)
        print("分卷测试汇总: %d 项, 失败 %d 项" % (len(res.items), res.failed))
        print("=" * 74)
        for name, ok, detail in res.items:
            print("  [%s] %-44s %s" % ("通过" if ok else "失败", name, detail))

        print("")
        print("##### 清理临时产物 #####")
        if args.keep:
            print("  --keep 指定了，保留: %s" % TEMP.path)
        else:
            print("  " + TEMP.cleanup())

        release_checkout(mutex)

    print("")
    print("TOTAL ISSUES: %d" % res.failed)
    return 1 if res.failed else 0


if __name__ == "__main__":
    sys.exit(main())
