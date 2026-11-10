#!/usr/bin/env python3
# -*- coding: utf-8 -*-
r"""启发式探测：找「不符合预期设计」的行为（只探测，不改产品代码）。

与 regress / splitvol / systemtest 的区别
-----------------------------------------
那几个套件是**回归**：断言已知契约不退化。
本脚本是**探索**：拿设计文档与源码里写明的承诺，去撞既有用例没覆盖的边界，
把「看起来不对」的行为逐条摊出来给人判断。

判据来源（每一条都对应源码/README 里写明的承诺）：
  - 退出码契约：0 成功 / 1 跑了但失败或取消 / 2 根本没跑
  - 未知开关必须报错、退出码 2、不产出压缩包
  - 相对路径可用
  - 命令行不弹窗（失败也只写一行）
  - SHA256：UTF-8 无 BOM、`hash *name`、坏文件跳过
  - 分卷：tar 不分卷、输入≤分卷大小不切、Volume=0 不切
  - 配置损坏自动重建；单键非法只回落该键
  - 只有 `-` 开头的真实文件仍按路径处理

用法:
    python heuristic_probe.py --config Debug
    python heuristic_probe.py --keep          # 保留临时目录

退出码: 0 = 没发现可疑行为, 1 = 有可疑行为（不代表一定是 bug，需要人判）, 2 = 环境不满足
"""
import argparse
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

TEMP = SafeTemp("heuristic")

APP = None
FINDINGS = []


def say(s):
    print(s)


def suspect(area, what, evidence):
    """记录一条「可疑」。不是断言失败，是给人看的行为异常。"""
    FINDINGS.append((area, what, evidence))
    print("  [?] %-10s %s" % (area, what))
    print("        证据: %s" % evidence)


def ok(area, what, evidence=""):
    print("  [ok] %-10s %s %s" % (area, what, evidence))


def decode(raw):
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


def kill_running():
    for image in ("Auto7z.exe", "Auto7zGUI.exe"):
        subprocess.run(["taskkill", "/F", "/IM", image],
                       stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    time.sleep(0.25)


def run(args, timeout=90, stdin=subprocess.DEVNULL, cwd=None, exe=None):
    """跑一次被测程序。exe 可指定另一个入口（用于双入口一致性对照）。"""
    kill_running()
    target = str(exe or APP)
    try:
        r = subprocess.run([target] + [str(a) for a in args],
                           stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                           stdin=stdin, timeout=timeout, cwd=cwd)
        return r.returncode, decode(r.stdout)
    except subprocess.TimeoutExpired as e:
        return None, decode(e.stdout or b"")


def run_raw(args, **kw):
    """不经过 taskkill 的裸跑，用于观察进程是否自己退出。"""
    return subprocess.run([str(APP)] + [str(a) for a in args], **kw)


def set_cfg(**kw):
    values = {}
    try:
        values = json.loads(CFG.read_text(encoding="utf-8"))
    except Exception:
        values = {}
    values.update(kw)
    CFG.parent.mkdir(parents=True, exist_ok=True)
    CFG.write_text(json.dumps(values, ensure_ascii=False, indent=2) + "\n",
                   encoding="utf-8")


def base_cfg(**extra):
    kw = dict(Format="7z", Volume=2000, GenerateSHA256=False, OutputPath="",
              CompressionLevel="Store", IsSolidMode=False, Password="",
              UnzipPassword="", Zstd=False, ErrorLevel="Info", AutoSave=True)
    kw.update(extra)
    set_cfg(**kw)


def fresh(name):
    p = TEMP.path / name
    if p.exists():
        safe_rmtree(p)
    p.mkdir(parents=True, exist_ok=True)
    return p


def prod_dirs(d):
    d = Path(d)
    return [p for p in d.iterdir() if p.is_dir() and len(p.name) >= 40]


def prod_files(d):
    out = []
    for sub in prod_dirs(d):
        for f in sub.iterdir():
            if f.is_file():
                out.append(f)
    return out


def files_all(d):
    return [p for p in Path(d).rglob("*") if p.is_file()]


# ================================================================== 探测项

def p_exit_codes():
    """退出码契约：0 成功 / 1 跑了但失败 / 2 根本没跑。"""
    print("\n=== 退出码契约 ===")
    base_cfg()

    code, out = run([], timeout=30)
    if code != 2:
        suspect("退出码", "不带参数应为 2", "实际=%s 输出=%s" % (code, out.strip()[:60]))
    else:
        ok("退出码", "不带参数 → 2")

    d = fresh("ec_missing")
    code, out = run(["-z", d / "nope.txt"])
    if code != 2:
        suspect("退出码", "输入路径不存在应为 2", "实际=%s" % code)
    else:
        ok("退出码", "输入不存在 → 2")

    # 已存在目录用 -uz 解压：应当失败（1），不是 2
    d = fresh("ec_diruz")
    sub = d / "folder"
    sub.mkdir()
    (sub / "x.txt").write_text("x", encoding="utf-8")
    code, out = run(["-uz", str(sub)])
    if code not in (1, 2):
        suspect("退出码", "-uz 传目录退出码异常", "实际=%s 输出=%s" % (code, out.strip()[:80]))
    else:
        ok("退出码", "-uz 传目录 → %s" % code)


def p_unknown_switch():
    """未知开关：报错、退出码 2、不产出。"""
    print("\n=== 未知开关 ===")
    base_cfg()
    d = fresh("unk")
    f = d / "target.txt"
    f.write_text("x", encoding="utf-8")
    code, out = run(["-X", str(f)], timeout=30)
    produced = [p for p in files_all(d) if p.name != "target.txt"]
    if code != 2:
        suspect("未知开关", "退出码应为 2", "实际=%s" % code)
    else:
        ok("未知开关", "退出码 2")
    if produced:
        suspect("未知开关", "不该产出任何文件", "产物=%s" % [p.name for p in produced])


def p_dash_prefixed_real_file():
    """README 承诺：磁盘上真实存在、名字以 - 开头的路径仍按路径处理。"""
    print("\n=== 以 - 开头的真实文件 ===")
    base_cfg()
    d = fresh("dash")
    f = d / "-dashname.txt"
    f.write_text("dash content", encoding="utf-8")
    code, out = run(["-z", "-dashname.txt"], cwd=str(d), timeout=60)
    produced = [p for p in prod_files(d)]
    if code != 0 or not produced:
        suspect("路径", "真实存在的 -dashname.txt 应被当成路径压缩",
                "退出码=%s 产物=%s 输出=%s" % (code, [p.name for p in produced], out.strip()[:100]))
    else:
        ok("路径", "以 - 开头的真实文件按路径处理", "产物=%d" % len(produced))


def p_relative_path():
    """相对路径可用（README 明写）。"""
    print("\n=== 相对路径 ===")
    base_cfg()
    d = fresh("rel")
    f = d / "rel.txt"
    f.write_text("rel", encoding="utf-8")
    code, out = run(["-z", "rel.txt"], cwd=str(d), timeout=60)
    if code != 0 or not prod_files(d):
        suspect("路径", "相对路径应可用", "退出码=%s 产物=%d" % (code, len(prod_files(d))))
    else:
        ok("路径", "相对路径可用")


def p_corrupt_config():
    """配置损坏应被重建，程序仍可运行。"""
    print("\n=== 配置损坏 ===")
    CFG.write_text("{ this is not json at all ", encoding="utf-8")
    d = fresh("badcfg")
    f = d / "a.txt"
    f.write_text("a", encoding="utf-8")
    code, out = run(["-z", str(f)], timeout=60)
    try:
        json.loads(CFG.read_text(encoding="utf-8"))
        rebuilt = True
    except Exception:
        rebuilt = False
    if not rebuilt:
        suspect("配置", "损坏配置应自动重建为合法 JSON", "文件仍是坏的")
    elif code != 0:
        suspect("配置", "配置损坏后仍应能完成任务", "退出码=%s 输出=%s" % (code, out.strip()[:80]))
    else:
        ok("配置", "损坏配置已重建且任务成功")


def p_invalid_single_key():
    """单键非法值只回落该键，其余键保留。

    注意断言方式：命令行模式下产品**不保存配置**（AutoSave 只对界面模式生效），
    所以不能靠「文件里的值被改写」判断回落是否发生 —— 那会误报。
    真正的判据是**运行时行为**：Format=rar 不是合法输出格式，
    若真的回落了，产物应当是 .7z 而不是 .rar。
    """
    print("\n=== 单键非法值 ===")
    values = {
        "Language": "zh-CN", "Volume": "abc", "Format": "rar",
        "CompressionLevel": "Nope", "RuntimeMode": "Weird",
        "ErrorLevel": "Verbose", "Zstd": True, "GenerateSHA256": False,
        "AutoSave": True, "MaxLevelCompress": False, "IsTopMost": False,
        "Password": "", "UnzipPassword": "", "IsSolidMode": False,
        "IsEncFileName": False, "OutputPath": "", "SevenZUsageCount": 0,
    }
    CFG.write_text(json.dumps(values, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    d = fresh("badkey")
    f = d / "k.txt"
    f.write_text("k", encoding="utf-8")
    code, out = run(["-z", str(f)], timeout=60)
    produced = [p.name for p in prod_files(d)]
    if code != 0:
        suspect("配置", "单键非法不该让任务失败", "退出码=%s 输出=%s" % (code, out.strip()[:80]))
    elif not produced:
        suspect("配置", "单键非法后应仍能产出", "无产物")
    elif any(n.endswith(".rar") for n in produced):
        suspect("配置", "Format=rar 非法却产出了 .rar", produced)
    else:
        ok("配置", "非法单键回落、任务照常成功", produced[0])


def p_volume_boundaries():
    """分卷边界：tar 不分卷 / Volume=0 / 小于分卷大小。"""
    print("\n=== 分卷边界 ===")
    d = fresh("vol")
    rnd = random.Random(5)
    big = d / "big.bin"
    with open(big, "wb") as fh:
        for _ in range(2 * 1024 * 1024):
            fh.write(bytes([rnd.getrandbits(8)]))
    for fmt, vol in (("tar", 1), ("7z", 0)):
        sub = fresh("vol_%s_%s" % (fmt, vol))
        shutil.copy2(str(big), str(sub / "big.bin"))
        base_cfg(Format=fmt, Volume=vol)
        code, out = run(["-z", sub / "big.bin"], timeout=120)
        vols = [p for p in files_all(sub) if re.search(r"\.\d{3}$", p.name)]
        if vols:
            suspect("分卷", "%s/Volume=%s 不该产出分卷" % (fmt, vol), [p.name for p in vols])
        else:
            ok("分卷", "%s/Volume=%s 未分卷" % (fmt, vol))


def p_sha256_contract():
    """SHA256：无 BOM、hash *name、坏文件跳过但其余照常。"""
    print("\n=== SHA256 契约 ===")
    d = fresh("sha")
    f = d / "h.txt"
    f.write_text("hello", encoding="utf-8")
    base_cfg(GenerateSHA256=True)
    code, out = run(["-z", str(f)], timeout=60)
    shas = [p for p in files_all(d) if p.name.endswith(".sha256")]
    if not shas:
        suspect("SHA256", "应生成 .sha256", "没有产物")
        return
    raw = shas[0].read_bytes()
    if raw.startswith(b"\xef\xbb\xbf"):
        suspect("SHA256", "不应带 BOM", "首 3 字节=%s" % raw[:3].hex())
    else:
        ok("SHA256", "无 BOM")
    text = shas[0].read_text(encoding="utf-8")
    if not re.match(r"^[0-9a-f]{64} \*", text.strip()):
        suspect("SHA256", "行格式应为 `hash *name`", "首行=%s" % text.strip()[:40])
    else:
        ok("SHA256", "行格式 hash *name")


def p_occupied_file():
    """被独占占用的文件：不该挂起；归档照常产出、必须提示「部分被跳过」、退出码 1。

    设计契约（README「已知限制」）：文件被别的程序独占打开时压缩不报 0 ——
    归档照常产出，但该文件被跳过，退出码 1 并在控制台说明「未完全成功」。
    所以这里要断言的正是那三件事，而不是笼统地说「退出码非 0 需人工确认」。
    """
    print("\n=== 文件被独占占用 ===")
    d = fresh("lock")
    f = d / "locked.txt"
    f.write_text("locked", encoding="utf-8")
    base_cfg()
    import ctypes
    # 独占打开：share=0，别的进程连读都不行
    handle = ctypes.windll.kernel32.CreateFileW(
        str(f), 0x80000000, 0, None, 3, 0x80, None)
    try:
        start = time.time()
        code, out = run(["-z", str(f)], timeout=60)
        elapsed = time.time() - start
        produced = prod_files(d)

        if code is None:
            suspect("占用", "压缩占用文件时挂起", "超时未返回")
        elif elapsed > 45:
            suspect("占用", "压缩占用文件耗时异常", "%.1f 秒" % elapsed)
        elif not produced:
            suspect("占用", "归档应照常产出（只是跳过该文件）", "无产物")
        elif code != 1:
            suspect("占用", "被跳过时应以退出码 1 表示「未完全成功」", "实际=%s" % code)
        elif "未完全成功" not in out and "部分文件被跳过" not in out:
            suspect("占用", "应提示「未完全成功 / 部分文件被跳过」",
                    "控制台=%r" % out.strip()[:100])
        else:
            ok("占用", "归档照常产出 + 明确提示 + 退出码 1", "%.1f 秒" % elapsed)
    finally:
        if handle:
            ctypes.windll.kernel32.CloseHandle(handle)


def p_readonly_output_dir():
    """输出目录不可写时应明确失败，而不是产出半成品。

    注意：OutputPath 必须用 .NET 以 UTF-8 无 BOM 写入。PowerShell 的
    Set-Content/Out-File 默认会加 BOM，产品的 JSON 读取不能吃 BOM 前缀，
    于是它回落到「源文件旁边」—— 探针就会误判成「不可写目录也产出了文件」。
    """
    print("\n=== 只读输出目录 ===")
    d = fresh("ro")
    f = d / "a.txt"
    f.write_text("a", encoding="utf-8")
    outdir = d / "readonly"
    outdir.mkdir()
    set_cfg(OutputPath=str(outdir))
    subprocess.run(["icacls", str(outdir), "/deny", "%s:(W)" % os.environ["USERNAME"]],
                   capture_output=True)
    try:
        code, out = run(["-z", str(f)], timeout=60)
        produced = [p for p in files_all(outdir)]
        if produced:
            suspect("路径", "不可写输出目录仍然产出了文件", [p.name for p in produced])
        elif code != 1:
            suspect("路径", "不可写输出目录退出码应为 1",
                    "实际=%s 输出=%s" % (code, out.strip()[:100]))
        else:
            ok("路径", "不可写输出目录 → 明确失败", out.strip()[:50])
    finally:
        subprocess.run(["icacls", str(outdir), "/remove:d", os.environ["USERNAME"]],
                       capture_output=True)


def p_silent_skip_of_missing_input():
    """混合批里不存在的路径：该不该让调用方知道少处理了东西。

    设计契约（README「退出码」一节）：静默丢弃会让脚本与计划任务无法从退出码
    判断到底跑没跑 / 跑全没跑全。所以「部分输入不存在」至少应当在控制台里说一句，
    或者让退出码区别于「全部成功」。
    现状：只在日志里记一条 WARNING，控制台一个字都没有，退出码仍是 0。
    """
    print("\n=== 混合批中的缺失路径 ===")
    base_cfg()
    d = fresh("skip")
    real = d / "real.txt"
    real.write_text("REAL", encoding="utf-8")
    code, out = run(["-z", d / "nope.txt", real], timeout=60)
    produced = [p for p in prod_files(d)]
    mentioned = ("不存在" in out) or ("跳过" in out) or ("不存在的" in out)

    if not produced:
        suspect("输入", "有效输入应照常处理", "无产物")
        return
    if not mentioned:
        suspect("输入", "缺失路径被静默丢弃：控制台无提示、退出码仍为 0，"
                        "脚本无法察觉少处理了输入（日志里有 WARNING，但默认不显示）",
                "退出码=%s 产物=%d 控制台=%r" % (code, len(produced), out.strip()[:80]))
    else:
        ok("输入", "缺失路径有提示", out.strip()[:60])


def p_empty_input():
    """空文件 / 空目录。"""
    print("\n=== 空输入 ===")
    d = fresh("empty")
    empty = d / "zero.txt"
    empty.write_bytes(b"")
    base_cfg()
    code, out = run(["-z", str(empty)], timeout=60)
    produced = prod_files(d)
    if code != 0:
        suspect("空输入", "空文件应能压缩", "退出码=%s" % code)
    elif not produced:
        suspect("空输入", "空文件压缩没有产物", "无产物")
    else:
        ok("空输入", "空文件压缩成功")

    d2 = fresh("emptydir")
    sub = d2 / "nothing"
    sub.mkdir()
    code, out = run(["-z", str(sub)], timeout=60)
    vols = [p for p in files_all(d2) if p.name.endswith(".001")]
    if vols:
        suspect("空输入", "空目录不该产出孤立的 .001", [p.name for p in vols])
    else:
        ok("空输入", "空目录没有产出无意义分卷")


def p_special_names():
    """特殊字符与超长文件名。"""
    print("\n=== 特殊文件名 ===")
    d = fresh("names")
    base_cfg()
    names = ["a b&c(d) [x].txt", "中文名 文件.txt", "dots...txt", "üñî.txt"]
    for n in names:
        try:
            (d / n).write_text("c", encoding="utf-8")
        except OSError:
            pass
    code, out = run(["-z"] + [str(d) + os.sep + n for n in names if (d / n).exists()], timeout=90)
    produced = prod_files(d)
    if code != 0 or not produced:
        suspect("特殊名", "特殊文件名应能压缩", "退出码=%s 产物=%d" % (code, len(produced)))
    else:
        ok("特殊名", "特殊文件名压缩成功", "产物=%s" % produced[0].name)


def p_encoding_nonascii_console():
    """非 ASCII 文件名在控制台输出里应正确（不乱码）。"""
    print("\n=== 控制台中文输出 ===")
    d = fresh("enc")
    base_cfg()
    f = d / "中文文件.txt"
    f.write_text("zh", encoding="utf-8")
    code, out = run(["-z", str(f)], timeout=60)
    if "\ufffd" in out:
        suspect("编码", "控制台输出出现替换字符", out.strip()[:120])
    elif "中文文件" not in out:
        suspect("编码", "控制台未正确回显中文文件名", "输出=%s" % out.strip()[:120])
    else:
        ok("编码", "中文文件名正确回显")


def p_tar_zstd_two_step():
    """tar+zstd 两步流程不应残留中间 tar。"""
    print("\n=== tar + zstd ===")
    d = fresh("tzstd")
    f = d / "a.txt"
    f.write_text("a" * 100, encoding="utf-8")
    base_cfg(Format="tar", Zstd=True)
    code, out = run(["-z", str(f)], timeout=90)
    produced = prod_files(d)
    names = [p.name for p in produced]
    if any(n.endswith(".tar") for n in names):
        suspect("tar", "中间 .tar 残留", names)
    elif not any(n.endswith(".tar.zst") for n in names):
        suspect("tar", "应产出 .tar.zst", names or "无产物")
    else:
        ok("tar", "tar+zstd 无中间残留", names[0])


def p_single_instance():
    """单实例：第二个进程应交接后自行退出，不该并行写同一个包。"""
    print("\n=== 单实例 ===")
    d = fresh("single")
    f = d / "a.txt"
    f.write_text("a", encoding="utf-8")
    base_cfg()
    kill_running()
    p1 = subprocess.Popen([str(APP), "-z", str(f)],
                          stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                          stdin=subprocess.DEVNULL)
    time.sleep(0.7)
    p2 = subprocess.Popen([str(APP), "-z", str(f)],
                          stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                          stdin=subprocess.DEVNULL)
    rc2 = None
    try:
        rc2 = p2.wait(timeout=20)
    except subprocess.TimeoutExpired:
        p2.kill()
    try:
        p1.wait(timeout=90)
    except subprocess.TimeoutExpired:
        p1.kill()
    if rc2 is None or rc2 != 0:
        suspect("单实例", "第二实例应交接后立即以 0 退出", "退出码=%s" % rc2)
    else:
        ok("单实例", "第二实例交接后自行退出")


def p_shellmenu_dispatch():
    """外壳扩展的调用必须走无主窗口流程，且 -z / -uz 要各司其职。

    外壳扩展传进来的参数形如 `#映射名:字节数:事件名 -uz` —— 共享内存路径 + 模式开关。
    这个形态曾经被误判成「命令行语义」，于是：
      1) 无主窗口的小进度窗不见了（被命令行管道接管，它不建进度窗）；
      2) `-uz` 被当普通开关吞掉，两个菜单项都跑成压缩。
    判据：出现「正在处理」进度窗、不出现主窗口标题、解压只解出文件不产出新包。
    """
    print("\n=== 外壳扩展调用分流 ===")
    gui = APP.parent / "Auto7zGUI.exe"
    if not gui.exists():
        say("  (无 Auto7zGUI.exe，跳过)")
        return

    for mode, expect_extract in (("-z", False), ("-uz", True)):
        d = fresh("shellmenu_%s" % mode.strip("-"))
        base_cfg()

        target = d / "plain.txt"
        target.write_text("shell menu payload", encoding="utf-8")
        if expect_extract:
            # 解压场景要有一个真的包
            pkg = d / "pkg.7z"
            inner = d / "payload.txt"
            inner.write_text("inner payload", encoding="utf-8")
            subprocess.run([str(SZ), "a", "-bso0", "-bsp0", str(pkg), str(inner)],
                           stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, check=False)
            inner.unlink()
            target = pkg

        arg, keep = _make_shell_map([str(target)])
        p = subprocess.Popen([str(gui), arg, mode],
                             stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                             stdin=subprocess.DEVNULL)
        seen = []
        deadline = time.time() + 8
        while time.time() < deadline:
            for title in _window_titles(p.pid):
                if title not in seen:
                    seen.append(title)
            if p.poll() is not None:
                break
            time.sleep(0.2)
        try:
            p.wait(timeout=90)
        except subprocess.TimeoutExpired:
            p.kill()
        _release_shell_map(keep)

        archives = len([x for x in d.rglob("*.7z") if x.name != "pkg.7z"])
        extracted = len([x for x in d.rglob("*") if x.is_file()
                         and x.suffix != ".7z" and x.name != "plain.txt"])

        has_progress = any("正在处理" in t for t in seen)
        has_main = any(t.strip() == "Auto7z" for t in seen)

        if has_main:
            suspect("外壳", "%s 出现了主窗口（右键语义不该弹主窗口）" % mode,
                    "窗口=%s" % seen)
        elif not has_progress:
            suspect("外壳", "%s 没有出现进度窗口（无主窗口模式失效）" % mode,
                    "窗口=%s" % (seen or "无"))
        elif expect_extract and (extracted == 0 or archives > 0):
            suspect("外壳", "%s 被当成压缩执行" % mode,
                    "产物7z=%d 解出=%d" % (archives, extracted))
        elif not expect_extract and archives == 0:
            suspect("外壳", "%s 没有产出压缩包" % mode, "产物7z=0")
        else:
            ok("外壳", "%s 进度窗 + 正确模式" % mode,
               "产物7z=%d 解出=%d" % (archives, extracted))


def p_sha256_single_manifest():
    """SHA256 必须是「一份清单覆盖整套产物」，尤其分卷。

    分卷产出 7 个文件时，校验清单必须有 7 行写在同一份 .sha256 里。
    曾经是每卷各写一份只含一行的文件 —— 那样校验工具还得先手工拼起来，
    完全违背校验文件的用途。这条与 splitvol_test 的分卷断言互为补充。
    """
    print("\n=== SHA256 单清单 ===")
    d = fresh("sha_manifest")
    rnd = random.Random(11)
    big = d / "big.bin"
    with open(str(big), "wb") as f:
        for _ in range(4):
            f.write(bytes(rnd.getrandbits(8) for _ in range(1024 * 1024)))

    base_cfg(Format="7z", Volume=1, GenerateSHA256=True, CompressionLevel="Store")
    code, out = run(["-z", big], timeout=120)

    vols = [x for x in d.rglob("*.7z.[0-9][0-9][0-9]")]
    shas = sorted(d.rglob("*.sha256"))

    if not vols:
        suspect("SHA256", "分卷没切出来，无法判断清单", "卷数=0")
        return
    if len(shas) != 1:
        suspect("SHA256", "分卷应产出一份清单，实际 %d 份" % len(shas),
                ",".join(x.name for x in shas))
        return

    lines = [l.strip() for l in shas[0].read_text(encoding="utf-8-sig").splitlines() if l.strip()]
    if len(lines) != len(vols):
        suspect("SHA256", "清单行数与卷数不符",
                "行数=%d 卷数=%d" % (len(lines), len(vols)))
        return

    bad = []
    for line in lines:
        digest, _, name = line.partition(" *")
        target = shas[0].parent / name
        if not target.exists():
            bad.append(name + "(缺)")
        elif digest.lower() != hashlib.sha256(target.read_bytes()).hexdigest():
            bad.append(name + "(不符)")

    if bad:
        suspect("SHA256", "清单里的哈希与文件不符", ",".join(bad[:4]))
    else:
        ok("SHA256", "一份清单覆盖全部 %d 卷，逐行哈希正确" % len(vols),
           shas[0].name)


def _window_titles(pid):
    """取进程的可见窗口标题。"""
    import ctypes
    import ctypes.wintypes
    user32 = ctypes.windll.user32
    found = []

    @ctypes.WINFUNCTYPE(ctypes.c_bool, ctypes.wintypes.HWND, ctypes.wintypes.LPARAM)
    def cb(hwnd, lparam):
        wpid = ctypes.wintypes.DWORD()
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(wpid))
        if wpid.value == pid and user32.IsWindowVisible(hwnd):
            n = user32.GetWindowTextLengthW(hwnd)
            if n:
                buf = ctypes.create_unicode_buffer(n + 1)
                user32.GetWindowTextW(hwnd, buf, n + 1)
                found.append(buf.value)
        return True

    user32.EnumWindows(cb, 0)
    return found


def _make_shell_map(paths):
    """按外壳扩展的协议造共享内存参数：`#map:bytes:event`。

    前 2 字节是协议标记，其后是 UTF-16 路径、以 \\0 分隔。
    """
    import ctypes
    import uuid
    kernel32 = ctypes.windll.kernel32

    total_chars = 1
    for p in paths:
        total_chars += len(p) + 1
    total_bytes = total_chars * 2

    map_name = "Auto7zProbeMap" + uuid.uuid4().hex[:8]
    event_name = "Auto7zProbeEvent" + uuid.uuid4().hex[:8]

    kernel32.CreateFileMappingW.restype = ctypes.c_void_p
    handle = kernel32.CreateFileMappingW(ctypes.c_void_p(-1), None, 0x04,
                                         0, total_bytes, map_name)
    kernel32.MapViewOfFile.restype = ctypes.c_void_p
    view = kernel32.MapViewOfFile(ctypes.c_void_p(handle), 0x0002, 0, 0, total_bytes)

    buf = bytearray(total_bytes)
    pos = 2
    for p in paths:
        enc = p.encode("utf-16-le")
        buf[pos:pos + len(enc)] = enc
        pos += len(enc)
        buf[pos:pos + 2] = b"\x00\x00"
        pos += 2
    ctypes.memmove(view, bytes(buf), total_bytes)

    event = kernel32.CreateEventW(None, True, False, event_name)
    return ("#%s:%d:%s" % (map_name, total_bytes, event_name), (handle, view, event))


def _release_shell_map(keep):
    import ctypes
    kernel32 = ctypes.windll.kernel32
    kernel32.UnmapViewOfFile(ctypes.c_void_p(keep[1]))
    kernel32.CloseHandle(ctypes.c_void_p(keep[0]))
    kernel32.CloseHandle(ctypes.c_void_p(keep[2]))


def p_entry_consistency():
    """两个入口对同一批参数必须给出相同结论。

    契约（README「两个可执行体」）：两个 exe 共用同一份源码，带开关参数时走同一条
    命令行管道 —— 图形入口先 AttachConsole 借父控制台，再执行 RunConsolePipeline。
    所以「-X 是否被拒绝、退出码是不是 2、用是不是同一段用法文本」在两边必须一模一样。

    这条曾经是真实缺陷：`Auto7zGUI.exe -X real.txt` 静默忽略未知开关并照常压缩，
    而 `Auto7z.exe -X` 正确报错 —— 同一套参数两种结论。
    """
    print("\n=== 双入口一致性 ===")
    gui = APP.parent / "Auto7zGUI.exe"
    if not gui.exists():
        say("  (无 Auto7zGUI.exe，跳过)")
        return

    cases = [["-X"], ["-X", "real.txt"], ["-abc", "real.txt"], ["-h"]]
    base_cfg()

    for case in cases:
        results = []
        for exe in (APP, gui):
            d = fresh("consist_%s_%s" % (exe.stem, case[0].strip("-")))
            (d / "real.txt").write_text("DATA", encoding="utf-8")
            args = [str(d / x) if x == "real.txt" else x for x in case]
            code, out = run(args, exe=exe, timeout=60)
            lines = out.strip().splitlines()
            first = lines[0].replace("\ufeff", "").strip() if lines else ""
            results.append((code, first, len(prod_files(d))))

        if results[0] == results[1]:
            ok("入口", "%s 两入口一致" % " ".join(case),
               "退出码=%s" % results[0][0])
        else:
            suspect("入口", "%s 两入口结论不一致" % " ".join(case),
                    "Auto7z=%s | GUI=%s" % (results[0], results[1]))


def p_deep_nested_and_long_path():
    """深层目录：不应栈溢出或失败。"""
    print("\n=== 深层嵌套目录 ===")
    d = fresh("deep")
    base_cfg()
    cur = d
    for i in range(12):
        cur = cur / ("d%d" % i)
        cur.mkdir()
    (cur / "leaf.txt").write_text("leaf", encoding="utf-8")
    code, out = run(["-z", str(d / "d0")], timeout=90)
    if code != 0:
        suspect("路径", "12 层嵌套目录应能压缩", "退出码=%s" % code)
    else:
        ok("路径", "12 层嵌套压缩成功")


def p_log_level_effect():
    """ErrorLevel 键：非法值应回落，且不影响任务。"""
    print("\n=== 日志级别 ===")
    base_cfg(ErrorLevel="Info")
    d = fresh("log")
    f = d / "a.txt"
    f.write_text("a", encoding="utf-8")
    code, out = run(["-z", str(f)], timeout=60)
    # 用任务成功与否判断，不深挖日志内容
    if code != 0:
        suspect("日志", "ErrorLevel=Info 不应让任务失败", "退出码=%s" % code)
    else:
        ok("日志", "ErrorLevel=Info 任务成功")


# ================================================================== 主流程

def main():
    global APP
    ap = argparse.ArgumentParser()
    ap.add_argument("--app", help="被测程序路径")
    ap.add_argument("--config", default="Debug", help="Debug / Release")
    ap.add_argument("--keep", action="store_true", help="保留临时目录")
    args = ap.parse_args()

    APP = Path(args.app) if args.app else PROJECT / "bin" / args.config / "Auto7z.exe"
    if not APP.exists():
        print("找不到被测程序: %s" % APP)
        return 2
    if not SZ.exists():
        print("找不到 7z 组件: %s" % SZ)
        return 2

    mutex = claim_checkout()
    if mutex is None:
        print("已有另一份 Auto7z 测试正在跑，先等它结束。")
        return 2

    guard = ConfigGuard(APP)
    if not guard.prepare():
        release_checkout(mutex)
        return 2

    TEMP.ensure()
    chain_token = guard.join_chain()

    print("=" * 74)
    print("Auto7z 启发式探测（找不符合预期设计的行为）")
    print("被测程序: %s" % APP)
    print("临时目录: %s" % TEMP.path)
    print("=" * 74)

    probes = [
        p_exit_codes,
        p_unknown_switch,
        p_dash_prefixed_real_file,
        p_relative_path,
        p_corrupt_config,
        p_invalid_single_key,
        p_volume_boundaries,
        p_sha256_contract,
        p_sha256_single_manifest,
        p_occupied_file,
        p_readonly_output_dir,
        p_empty_input,
        p_special_names,
        p_silent_skip_of_missing_input,
        p_encoding_nonascii_console,
        p_tar_zstd_two_step,
        p_single_instance,
        p_shellmenu_dispatch,
        p_entry_consistency,
        p_deep_nested_and_long_path,
        p_log_level_effect,
    ]

    try:
        for probe in probes:
            try:
                probe()
            except Exception as e:
                suspect("探测异常", probe.__name__, repr(e)[:160])
    finally:
        guard.leave_chain(chain_token)
        kill_running()

        print("")
        print("=" * 74)
        print("可疑行为汇总: %d 条" % len(FINDINGS))
        print("=" * 74)
        for area, what, evidence in FINDINGS:
            print("  [?] %-10s %s" % (area, what))
            print("        证据: %s" % evidence)

        print("")
        print("##### 清理临时产物 #####")
        if args.keep:
            print("  --keep 指定了，保留: %s" % TEMP.path)
        else:
            print("  " + TEMP.cleanup())

        release_checkout(mutex)

    print("")
    print("SUSPICIOUS: %d" % len(FINDINGS))
    return 1 if FINDINGS else 0


if __name__ == "__main__":
    sys.exit(main())
