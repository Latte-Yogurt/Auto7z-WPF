#!/usr/bin/env python3
"""Auto7z 回归测试（只测试，不改产品代码）

用法:
    python regress.py                 # 用默认的 Debug 构建
    python regress.py --app <exe>     # 指定被测程序
    python regress.py --config Debug  # 测 Debug / Release 构建

测试会在 %LOCALAPPDATA%\\Auto7z 下改配置（测完会恢复），
产物写到工作区的 regress/ 目录。

收尾会：
  1) 注销右键菜单（测试产物不是生产版本，不该在系统里留注册）
  2) 清理临时目录（所有测试跑完之后统一清，带白名单校验）
"""
import ctypes
import locale
import os
import shutil
import subprocess
import sys
import time
import xml.etree.ElementTree as ET
from ctypes import wintypes
from pathlib import Path


def decode_console_output(data):
    """把子进程的控制台输出解成文本。

    Windows 上这些子进程（tasklist、portable.exe）按 **OEM 代码页** 输出，
    中文系统上是 GBK/CP936，不是 UTF-8。
    而 Python 3.15 起默认是 UTF-8 模式（PEP 686），
    locale.getpreferredencoding() 在那里返回的就是 utf-8，
    所以不能直接拿它来解码 —— 会把中文解成乱码，
    后面的字符串断言就全部落空（表现为「配置里没有记下便携标记」这种假失败）。

    做法：先按 OEM 代码页试，解不出来再退到 mbcs，再退到 utf-8。
    """
    if not data:
        return ""
    for enc in ("gbk", "mbcs", "utf-8"):
        try:
            return data.decode(enc)
        except (UnicodeDecodeError, LookupError):
            continue
    return data.decode("utf-8", errors="replace")

sys.path.insert(0, str(Path(__file__).resolve().parent))
from safe_ops import SafeTemp, safe_rmtree, UnsafeDeleteError

PROJECT = Path(r"C:\Users\Mocha\Desktop\VSProject\Auto7z_WPF\Auto7z_WPF")
WORKSPACE = Path(__file__).resolve().parent.parent

# 临时目录：受控在 %TEMP% 下，路径含标记串，删除前有白名单校验
TEMP = SafeTemp("regress")


def parse_args():
    app = None
    config = "Debug"
    argv = sys.argv[1:]
    i = 0
    while i < len(argv):
        if argv[i] == "--app" and i + 1 < len(argv):
            app = Path(argv[i + 1])
            i += 2
        elif argv[i] == "--config" and i + 1 < len(argv):
            config = argv[i + 1]
            i += 2
        else:
            i += 1
    if app is None:
        app = PROJECT / "bin" / config / "Auto7z.exe"
    return app, config


APP, CONFIG = parse_args()

# 图形入口与命令行入口是两个可执行体：
#   Auto7z.exe      Console 子系统，只做命令行（不带参数时打印用法就退出）；
#   Auto7zGUI.exe   GUI 子系统，只做图形的事（双击 / 右键 / 拖拽 / 主窗口）。
# 需要「启动一次程序让它重建配置、写日志」的用例必须走图形入口，
# 否则拿到的是命令行版本的用法输出，什么都不会发生。
GUI_APP = APP.parent / "Auto7zGUI.exe"
if not GUI_APP.exists():
    GUI_APP = APP


def exe_for(args):
    """没有参数 = 界面模式，走图形入口；带参数 = 命令行模式，走命令行入口。"""
    return GUI_APP if not list(args) else APP
LOCAL = Path(os.environ["LOCALAPPDATA"]) / "Auto7z"
CFG = LOCAL / "Auto7z.xml"
LOGS = LOCAL / "Logs"
SZ = LOCAL / "7-Zip" / "7z.exe"
ROOT = WORKSPACE / "regress"

ISSUES = []
OUT = []
CONFIG_SNAPSHOT = None


def say(s):
    OUT.append(s)
    print(s)


def bad(s):
    ISSUES.append(s)
    OUT.append("  !! " + s)
    print("  !! " + s)


def find_lang_dir():
    """找语言词典目录。

    从被测程序的位置往上推: bin\\Debug\\Auto7z.exe -> 源码目录 -> Resources\\Languages。
    推不出来就在工程树里找 Resources\\Languages。
    写死绝对路径的做法已经过时, 换个目录就找不到, 而找不到时下面的比对
    会静默地什么都不检查还报"0 问题"。
    """
    candidates = []
    try:
        app_dir = APP.resolve().parent
        # bin\\Debug -> 上一级是 bin, 再上一级是工程目录
        candidates.append(app_dir.parent.parent / "Resources" / "Languages")
        candidates.append(app_dir.parent / "Resources" / "Languages")
    except Exception:
        pass

    candidates.append(WORKSPACE / "Auto7z_WPF" / "Auto7z_WPF" / "Resources" / "Languages")

    for c in candidates:
        try:
            if c.is_dir() and len(list(c.glob("*.xaml"))) >= 2:
                return c
        except OSError:
            continue

    # 最后在工程树里搜一遍
    try:
        for c in WORKSPACE.rglob("Resources/Languages"):
            if c.is_dir() and len(list(c.glob("*.xaml"))) >= 2:
                return c
    except OSError:
        pass

    return None


def set_cfg(**kw):
    tree = ET.parse(CFG)
    root = tree.getroot()
    for k, v in kw.items():
        el = root.find(k)
        if el is None:
            el = ET.SubElement(root, k)
        el.text = str(v)
    tree.write(CFG, encoding="utf-8", xml_declaration=True)


def files_in(d):
    d = Path(d)
    if not d.exists():
        return []
    return sorted(str(p.relative_to(d)).replace("\\", "/")
                  for p in d.rglob("*") if p.is_file())


def products_in(d, skip=()):
    return [f for f in files_in(d)
            if not ("/" not in f and f in skip)]


def fresh(name):
    """在受控临时目录里开一个干净的工作目录。

    原来这个目录开在工作区（工程根）下面，用 shutil.rmtree 直接删。
    现在挪到 %TEMP% 下的受控目录，删除前有白名单校验：
    必须在受控根内、路径含标记串、目录里没有源码类文件。
    这条边界是历史 P0 事故的直接教训 ——
    当时路径参数传错 + 删除边界模糊，把测试工具源码删光了。
    """
    d = TEMP.path / name
    if d.exists():
        safe_rmtree(d)
    d.mkdir(parents=True, exist_ok=True)
    return d


def close_window(p, timeout=15.0):
    """给进程的主窗口发 WM_CLOSE, 等它自己退出。

    界面模式要靠这个走 OnClosing, 配置才会被保存下来。
    """
    user32 = ctypes.windll.user32
    hwnds = []

    @ctypes.WINFUNCTYPE(ctypes.c_bool, ctypes.wintypes.HWND, ctypes.wintypes.LPARAM)
    def cb(hwnd, lparam):
        wpid = ctypes.wintypes.DWORD()
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(wpid))
        if wpid.value == p.pid and user32.IsWindowVisible(hwnd):
            hwnds.append(hwnd)
        return True

    user32.EnumWindows(cb, 0)
    for h in hwnds:
        user32.PostMessageW(h, 0x0010, 0, 0)

    try:
        p.wait(timeout=timeout)
    except Exception:
        p.kill()
        p.wait()


def kill_running():
    """结束还在跑的 Auto7z 进程。

    程序是单实例的: 上一个还在跑的话, 新起的会认为已经有实例,
    把文件交过去就自己退了。测试要一个一个单独跑, 所以先清干净。
    """
    for image in ("Auto7z.exe", "Auto7zGUI.exe"):
        subprocess.run(["taskkill", "/F", "/IM", image],
                       stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    time.sleep(0.3)


def close_gracefully(args, wait=5.0):
    """启动程序, 等它把事做完。

    命令行模式下程序跑完自己就退, 没有窗口可关;
    界面模式下才会发 WM_CLOSE 让它保存配置再退。
    """
    kill_running()
    p = subprocess.Popen([exe_for(args)] + list(args),
                         stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                         stdin=subprocess.DEVNULL)

    # 先等它自己结束
    deadline = time.time() + wait
    while time.time() < deadline:
        if p.poll() is not None:
            return
        time.sleep(0.1)

    # 还在跑, 说明是界面模式, 发 WM_CLOSE 让它正常保存配置退出
    close_window(p)


def windows_of(pid):
    user32 = ctypes.windll.user32
    res = []

    @ctypes.WINFUNCTYPE(ctypes.c_bool, wintypes.HWND, wintypes.LPARAM)
    def cb(hwnd, lparam):
        wpid = wintypes.DWORD()
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(wpid))
        if wpid.value == pid and user32.IsWindowVisible(hwnd):
            n = user32.GetWindowTextLengthW(hwnd)
            if n:
                buf = ctypes.create_unicode_buffer(n + 1)
                user32.GetWindowTextW(hwnd, buf, n + 1)
                res.append(buf.value)
        return True

    user32.EnumWindows(cb, 0)
    return res


def probe(args, wait=4.0):
    p = subprocess.Popen([APP] + list(args),
                         stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    time.sleep(wait)
    titles = windows_of(p.pid)
    if p.poll() is None:
        p.kill()
        p.wait()
    return titles


def run_capture(args, wait=6.0):
    """跑一次程序, 把控制台输出收回来

    stdin 要断开: 程序等"是否合并"的回答时读的是控制台,
    不给输入它就一直等着, 整个测试就卡住了。

    控制台输出按 GBK 编码, 解码时要对上, 不然中文全是乱码。
    """
    kill_running()
    try:
        out = subprocess.run([APP] + list(args), stdout=subprocess.PIPE,
                             stderr=subprocess.STDOUT, stdin=subprocess.DEVNULL,
                             timeout=wait)
        return out.stdout.decode("gbk", "replace")
    except subprocess.TimeoutExpired as e:
        raw = e.stdout or b""
        if isinstance(raw, bytes):
            raw = raw.decode("gbk", "replace")
        return raw
    except Exception:
        return ""


def md5_of(p):
    import hashlib
    h = hashlib.md5()
    with open(p, "rb") as f:
        for c in iter(lambda: f.read(1 << 20), b""):
            h.update(c)
    return h.hexdigest()


def shell_menu_registered():
    """检查右键菜单的挂载点在不在"""
    try:
        import winreg
        path = r"Software\Classes\*\shellex\ContextMenuHandlers\Auto7z"
        with winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE, path) as key:
            value, _ = winreg.QueryValueEx(key, "")
            return bool(value)
    except Exception:
        return False


def main():
    # 报告写到工程根的 regress\ 目录（给人看，跑完还留在这儿方便查）。
    # 测试产物写到受控临时目录（给测试用，跑完统一清）。
    # 这样工程里只多一个 report.txt，不会攒下一堆压缩包、临时文件。
    if ROOT.exists():
        shutil.rmtree(ROOT, ignore_errors=True)
    ROOT.mkdir(parents=True, exist_ok=True)
    TEMP.ensure()

    # 备份真实配置, 测完原样还原(由 run() 负责)
    say("##### 被测程序 #####")
    say("  " + str(APP))
    say("  配置: " + str(CFG))
    say("  临时目录: " + str(TEMP.path))
    say("")

    data = TEMP.path / "data"
    data.mkdir(parents=True, exist_ok=True)
    (data / "a.txt").write_text("alpha\n", encoding="utf-8")
    (data / "b.txt").write_text("beta\n", encoding="utf-8")
    import random
    rnd = random.Random(11)
    with open(data / "rand.bin", "wb") as f:
        for _ in range(3):
            f.write(bytes(rnd.getrandbits(8) for _ in range(1024 * 1024)))
    # 可压缩文本: 用于对比压缩等级
    with open(data / "text.txt", "w", encoding="utf-8") as f:
        f.write("The quick brown fox jumps over the lazy dog.\n" * 20000)

    set_cfg(Format="7z", Volume=2000, GenerateMD5="False", OutputPath="",
            AutoSave="True", ErrorLevel="Info", CompressionLevel="Normal",
            Password="", UnzipPassword="", Zstd="False", IsSolidMode="False")

    # T01 格式
    say("##### T01: 压缩格式 #####")
    for fmt in ("7z", "zip", "tar"):
        d = fresh("t01_" + fmt)
        shutil.copy(data / "a.txt", d)
        set_cfg(Format=fmt, Volume=2000, GenerateMD5="False", OutputPath="")
        close_gracefully([str(d / "a.txt")], 4)
        p = products_in(d, {"a.txt"})
        if p:
            say("  %-6s -> %s" % (fmt, ", ".join(p)))
        else:
            bad("格式 %s: 没有产物" % fmt)

    # T02 等级
    # 用可压缩文本: Store 是原样复制, 必然最大; 压缩等级越高体积越小。
    # (不可压缩数据下 Normal/Ultra 会得到同样结果, 那是 7-Zip 自身的行为, 不算缺陷)
    say("")
    say("##### T02: 压缩等级 #####")
    sizes = {}
    for lv in ("Store", "Normal", "Ultra"):
        d = fresh("t02_" + lv)
        shutil.copy(data / "text.txt", d)
        set_cfg(Format="7z", Volume=2000, GenerateMD5="False",
                CompressionLevel=lv, OutputPath="")
        close_gracefully([str(d / "text.txt")], 6)
        fs = [f for f in files_in(d) if f.endswith(".7z")]
        if fs:
            sizes[lv] = (d / fs[0]).stat().st_size
            say("  %-8s -> %d bytes" % (lv, sizes[lv]))
        else:
            bad("压缩等级 %s: 没有产物" % lv)
    if len(sizes) == 3:
        if sizes["Store"] > sizes["Normal"] and sizes["Normal"] >= sizes["Ultra"]:
            say("  Store > Normal >= Ultra: OK")
        else:
            bad("压缩等级体积异常: %s" % sizes)

    # T03 分卷
    say("")
    say("##### T03: 分卷 #####")
    for v in ("0", "1", "2"):
        d = fresh("t03_v" + v)
        shutil.copy(data / "rand.bin", d)
        set_cfg(Format="7z", Volume=v, GenerateMD5="False", OutputPath="")
        close_gracefully([str(d / "rand.bin")], 6)
        p = products_in(d, {"rand.bin"})
        say("  分卷 %sMB -> %s" % (v, ", ".join(p) if p else "无产物"))
        if not p:
            bad("分卷 %sMB: 没有产物" % v)
        if v == "0" and any(x.endswith(".001") for x in p):
            bad("分卷 0MB: 仍然生成了分卷文件")

    # T04 MD5
    say("")
    say("##### T04: MD5 #####")
    d = fresh("t04_md5")
    shutil.copy(data / "a.txt", d)
    set_cfg(Format="7z", Volume=2000, GenerateMD5="True", OutputPath="")
    close_gracefully([str(d / "a.txt")], 4)
    mds = [f for f in files_in(d) if f.endswith(".md5")]
    arcs = [f for f in files_in(d) if f.endswith(".7z")]
    if not mds:
        bad("MD5: 没有生成 .md5")
    elif arcs:
        content = (d / mds[0]).read_text(encoding="utf-8").strip().lstrip("\ufeff")
        real = md5_of(d / arcs[0])
        if real == content.split()[0].lower():
            say("  MD5 与实际一致: OK")
        else:
            bad("MD5 不一致: 实际=%s 文件=%s" % (real, content.split()[0]))

    # T05 密码
    say("")
    say("##### T05: 密码 #####")
    d = fresh("t05_pwd")
    shutil.copy(data / "a.txt", d)
    set_cfg(Format="7z", Volume=2000, GenerateMD5="False",
            Password="test123", OutputPath="")
    close_gracefully([str(d / "a.txt")], 4)
    arcs = [f for f in files_in(d) if f.endswith(".7z")]
    if not arcs:
        bad("密码: 没有生成加密包")
    else:
        o1 = d / "ok"; o1.mkdir()
        subprocess.run([str(SZ), "x", str(d / arcs[0]), "-ptest123", "-o" + str(o1), "-y"],
                       stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        if (o1 / "a.txt").exists():
            say("  正确密码解压: OK")
        else:
            bad("正确密码解压失败")
        o2 = d / "bad"; o2.mkdir()
        subprocess.run([str(SZ), "x", str(d / arcs[0]), "-pwrong", "-o" + str(o2), "-y"],
                       stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        if (o2 / "a.txt").exists():
            bad("错误密码竟然成功")
        else:
            say("  错误密码正确失败: OK")

    # T06 tar+zstd
    say("")
    say("##### T06: tar + zstd #####")
    d = fresh("t06_zstd")
    shutil.copy(data / "a.txt", d)
    set_cfg(Format="tar", Volume=2000, GenerateMD5="False", Zstd="True", OutputPath="")
    close_gracefully([str(d / "a.txt")], 4)
    p = products_in(d, {"a.txt"})
    say("  产物: %s" % (", ".join(p) if p else "无产物"))
    if not p:
        bad("tar+zstd: 没有产物")
    if any(f.endswith(".tar") for f in p):
        bad("tar+zstd: 中间 .tar 残留")

    # T07 解压往返
    say("")
    say("##### T07: 解压往返 #####")
    d = fresh("t07_extract")
    shutil.copy(data / "a.txt", d)
    set_cfg(Format="7z", Volume=2000, GenerateMD5="False", OutputPath="",
            Password="", UnzipPassword="")
    # 命令行模式跑完自己就退了, 用 run_capture 等它结束
    run_capture([str(d / "a.txt")], 10)
    arcs = [f for f in files_in(d) if f.endswith(".7z")]
    if not arcs:
        bad("解压: 没有生成压缩包")
    else:
        run_capture([str(d / arcs[0])], 10)
        p = products_in(d, {"a.txt"})
        say("  解压后: %s" % (", ".join(p) if p else "无产物"))
        if not any(f.endswith("a.txt") for f in p):
            bad("解压没有还原 a.txt")
        else:
            say("  a.txt 还原: OK")

    # T08 空文件夹
    say("")
    say("##### T08: 空文件夹 #####")
    d = fresh("t08_empty")
    (d / "emptydir").mkdir()
    set_cfg(Format="7z", Volume=2000, GenerateMD5="False", OutputPath="")
    close_gracefully([str(d / "emptydir")], 4)
    p = products_in(d)
    say("  产物: %s" % (", ".join(p) if p else "无产物"))
    if not p:
        bad("空文件夹: 没有产物")
    elif any(f.endswith(".001") for f in p):
        bad("空文件夹: 生成了分卷文件 .001")
    else:
        say("  产物名正常: OK")

    # T09 特殊字符
    say("")
    say("##### T09: 特殊字符文件名 #####")
    d = fresh("t09_special")
    weird = d / "a b&c(d) [x].txt"
    weird.write_text("special", encoding="utf-8")
    set_cfg(Format="7z", Volume=2000, GenerateMD5="False", OutputPath="")
    close_gracefully([str(weird)], 4)
    p = products_in(d)
    say("  产物: %s" % (", ".join(p) if p else "无产物"))
    if not p:
        bad("特殊字符文件名: 没有产物")

    # T10 嵌套目录
    say("")
    say("##### T10: 嵌套目录 #####")
    d = fresh("t10_nested")
    deep = d / "d1" / "d2" / "d3"
    deep.mkdir(parents=True)
    (deep / "n.txt").write_text("nested", encoding="utf-8")
    set_cfg(Format="7z", Volume=2000, GenerateMD5="False", OutputPath="")
    close_gracefully([str(deep)], 4)
    p = products_in(d)
    say("  产物: %s" % (", ".join(p) if p else "无产物"))
    if not p:
        bad("嵌套目录: 没有产物")

    # T11 合并询问框
    say("")
    say("##### T11: 合并询问框 #####")
    d = fresh("t11_ask")
    (d / "x.txt").write_text("one", encoding="utf-8")
    (d / "y.dat").write_text("two", encoding="utf-8")
    set_cfg(Format="7z", Volume=2000, GenerateMD5="False", OutputPath="")
    # 命令行模式下不弹窗口, 改看控制台输出
    out = run_capture([str(d / "x.txt"), str(d / "y.dat")], 8)
    flat = out.replace("\r", " ").replace("\n", " | ")
    say("  控制台输出: %s" % (flat[:120] or "(空)"))
    # 提示语跟着界面语言走。
    # 命令行模式用的是 Console_MergeQuestion 这个键（带 Y/N 提示），
    # 界面模式用的是 MultiFilePackQuery（不带 Y/N）。
    # 两个键措辞不同是故意的：控制台要告诉用户按什么键。
    if ("是否合并" in out or "打包" in out or "单个压缩文件" in out or
            "Y/N" in out or
            "package them into a single" in out or "Multiple files" in out):
        say("  合并询问已在控制台提示: OK")
    else:
        bad("合并询问没有在控制台提示")

    # T12 输出路径
    say("")
    say("##### T12: 输出路径 #####")
    d = fresh("t12_out")
    shutil.copy(data / "a.txt", d)
    set_cfg(Format="7z", Volume=2000, GenerateMD5="False",
            OutputPath=r"C:\__no_such_dir__\xxx")
    close_gracefully([str(d / "a.txt")], 4)
    p = products_in(d, {"a.txt"})
    say("  无效路径 -> %s" % (", ".join(p) if p else "无产物"))
    if not p:
        bad("无效输出路径: 没有产物")
    if os.path.exists(r"C:\__no_such_dir__"):
        bad("无效输出路径: 创建了该目录")

    d2 = fresh("t12_out2")
    shutil.copy(data / "a.txt", d2)
    outdir = d2 / "out"
    outdir.mkdir()
    set_cfg(Format="7z", Volume=2000, GenerateMD5="False", OutputPath=str(outdir))
    close_gracefully([str(d2 / "a.txt")], 4)
    p2 = products_in(outdir)
    say("  有效路径 -> %s" % (", ".join(p2) if p2 else "无产物"))
    if not p2:
        bad("有效输出路径: 没有写入产物")

    # T13 自动模式混合
    say("")
    say("##### T13: 自动模式混合输入 #####")
    d = fresh("t13_auto")
    shutil.copy(data / "a.txt", d)
    shutil.copy(data / "b.txt", d)
    set_cfg(Format="7z", Volume=2000, GenerateMD5="False", OutputPath="",
            Password="", UnzipPassword="")
    # 命令行模式不显示窗口, 用 run_capture 等它自己跑完
    run_capture([str(d / "a.txt")], 10)
    arcs = [f for f in files_in(d) if f.endswith(".7z")]
    if not arcs:
        bad("自动模式: 没有造出源压缩包")
    else:
        run_capture([str(d / arcs[0]), str(d / "b.txt")], 12)
        p = products_in(d, {"a.txt", "b.txt"})
        say("  产物: %s" % (", ".join(p) if p else "无产物"))
        if not p:
            bad("自动模式混合: 没有产物")
        else:
            if not any(".7z" in x for x in p):
                bad("自动模式混合: 普通文件被丢弃")
            if not any(x.endswith("a.txt") for x in p):
                bad("自动模式混合: 压缩包没有被解压")

    # T14 废键
    say("")
    say("##### T14: 废键清理 #####")
    old = ["ScreenWidth", "ScreenHeight", "SystemScale", "LocationX",
           "LocationY", "DisableVolume", "Portable", "ZipLevel", "MainText"]
    tree = ET.parse(CFG)
    root = tree.getroot()
    for k in old:
        if root.find(k) is None:
            el = ET.SubElement(root, k)
            el.text = "0"
    tree.write(CFG, encoding="utf-8", xml_declaration=True)
    before = len(list(ET.parse(CFG).getroot()))
    set_cfg(AutoSave="True")
    close_gracefully([], 2)
    ar = ET.parse(CFG).getroot()
    left = [k for k in old if ar.find(k) is not None]
    say("  键数 %d -> %d" % (before, len(list(ar))))
    if left:
        bad("废键未清理: %s" % ", ".join(left))
    else:
        say("  废键清理: OK")

    # T15 非法格式
    say("")
    say("##### T15: 非法格式值 #####")
    set_cfg(Format="gzip", ErrorLevel="Info")
    close_gracefully([], 2)
    found = False
    for f in sorted(LOGS.glob("*.log")):
        if "无法识别" in f.read_text(encoding="utf-8", errors="replace"):
            found = True
    if found:
        say("  已给出警告: OK")
    else:
        bad("非法格式值: 没有警告")

    # T16 语言资源
    say("")
    say("##### T16: 语言资源 #####")
    import re
    # 词典目录从被测程序的位置推出来, 不要再写死绝对路径:
    # 写死的旧路径早已不存在, glob 匹配到 0 个文件时下面所有检查都成了空转,
    # 键缺失永远检不出来, 还会报成"0 问题"。
    lang_dir = find_lang_dir()
    if lang_dir is None:
        bad("找不到语言词典目录, T16 无法执行")
    else:
        sets = {}
        for f in sorted(lang_dir.glob("*.xaml")):
            keys = re.findall(r'x:Key="([^"]+)"', f.read_text(encoding="utf-8"))
            sets[f.name] = keys
            dups = [k for k in set(keys) if keys.count(k) > 1]
            say("  %-12s %d keys%s" % (f.name, len(keys),
                                       "  重复: " + ",".join(dups) if dups else ""))
            if dups:
                bad("%s 重复键: %s" % (f.name, ",".join(dups)))
        if len(sets) < 2:
            bad("语言词典只找到 %d 个, 少于 2 个, 无法比对" % len(sets))
        names = list(sets)
        for i in range(len(names)):
            for j in range(i + 1, len(names)):
                a, b = sets[names[i]], sets[names[j]]
                oa = [k for k in a if k not in b]
                ob = [k for k in b if k not in a]
                if oa:
                    bad("%s 有 %s 没有的键: %s" % (names[i], names[j], ",".join(oa)))
                if ob:
                    bad("%s 有 %s 没有的键: %s" % (names[j], names[i], ",".join(ob)))

    # T17 日志
    say("")
    say("##### T17: 日志系统 #####")
    normal = sorted(LOGS.glob("Auto7z_2*.log"))
    issue = sorted(LOGS.glob("Auto7z_Issue_*.log"))
    say("  普通日志: %s" % (", ".join(p.name for p in normal) or "无"))
    say("  问题日志: %s" % (", ".join(p.name for p in issue) or "无"))
    if not normal:
        bad("没有普通日志文件")
    if not issue:
        bad("没有问题日志文件")
    old_log = LOGS / "Auto7z_Issue_2016-01-01.log"
    old_log.write_text("old", encoding="utf-8")
    close_gracefully([], 2)
    if old_log.exists():
        bad("过期问题日志没有被清理")
    else:
        say("  过期问题日志已清理: OK")

    # T18 边界
    say("")
    say("##### T18: 边界输入 #####")
    d = fresh("t18_edge")
    (d / "s.txt").write_text("x", encoding="utf-8")
    subprocess.run([str(SZ), "a", "-aoa", "-y", "-t7z", "good.7x", "s.txt"], cwd=str(d),
                   stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    raw = bytearray((d / "good.7x").read_bytes())
    raw[40] = 0xFF
    raw[41] = 0xFF
    (d / "broken.7z").write_bytes(bytes(raw))
    set_cfg(Format="7z", Volume=2000, GenerateMD5="False", OutputPath="")
    close_gracefully([str(d / "broken.7z")], 5)
    say("  损坏压缩包: 已处理")

    d2 = fresh("t18_zero")
    (d2 / "zero.bin").write_bytes(b"")
    close_gracefully([str(d2 / "zero.bin")], 4)
    p = products_in(d2, {"zero.bin"})
    say("  空文件 -> %s" % (", ".join(p) if p else "无产物"))
    if not p:
        bad("空文件: 没有产物")

    # T19 非法分卷
    say("")
    say("##### T19: 非法分卷大小 #####")
    for v in ("-5", "abc", "99999999999999999999"):
        d = fresh("t19_v")
        shutil.copy(data / "a.txt", d)
        set_cfg(Format="7z", Volume=v, GenerateMD5="False", OutputPath="")
        close_gracefully([str(d / "a.txt")], 4)
        p = products_in(d, {"a.txt"})
        say("  Volume=%-22s -> %s" % (v, ", ".join(p) if p else "无产物"))
        if not p:
            bad("非法分卷 %s: 没有产物" % v)

    # T20 配置损坏
    say("")
    say("##### T20: 配置损坏 #####")
    backup = CFG.read_text(encoding="utf-8")
    try:
        CFG.write_text("not xml <<<", encoding="utf-8")
        p = subprocess.Popen([exe_for([])], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        time.sleep(3)
        alive = p.poll() is None
        if p.poll() is None:
            p.kill(); p.wait()
        say("  应用存活: %s" % alive)
        if not alive:
            bad("配置损坏导致应用无法启动")
        close_gracefully([], 2)
        try:
            ET.parse(CFG)
            say("  配置已重建: OK")
        except Exception:
            bad("配置损坏后未被重建")
    finally:
        CFG.write_text(backup, encoding="utf-8")

    # T21 残留
    say("")
    say("##### T21: 进程残留 #####")
    # tasklist 的输出是本地代码页（中文系统上是 GBK），不是 UTF-8。
    # 用 text=True 会按 locale 解码，中文系统上遇到非 ASCII 字节就抛
    # UnicodeDecodeError，整个测试脚本在最后一步崩掉。
    # 这里显式按本地代码页解码并忽略无法识别的字节。
    out = subprocess.run(["tasklist", "/FI", "IMAGENAME eq 7z.exe", "/NH"],
                         stdout=subprocess.PIPE)
    listing = decode_console_output(out.stdout)
    n = listing.lower().count("7z.exe")
    say("  残留 7z 进程: %d" % n)
    if n:
        bad("残留 7z 进程: %d" % n)

    # T22 便携模式(调用应用里的真实逻辑)
    say("")
    say("##### T22: 便携模式 #####")
    try:
        csc = r"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
        src = WORKSPACE / "testtools" / "cs" / "portable" / "portable.cs"
        # 编译产物放受控临时目录，不要往产品的 bin 目录里扔 exe
        exe = TEMP.path / "portable.exe"
        refs = [
            r"C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.7.2\PresentationFramework.dll",
            r"C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.7.2\PresentationCore.dll",
            r"C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.7.2\WindowsBase.dll",
            r"C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.7.2\System.Xaml.dll",
        ]
        if os.path.exists(csc) and src.exists():
            args = [csc, "/nologo", "/out:" + str(exe)]
            for r in refs:
                args.append("/r:" + r)
            args.append(str(src))
            build = subprocess.run(args, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
            if build.returncode == 0:
                # 第一个参数是"报告写到哪"。
                # 原来传的是工程根(WORKSPACE)，portable.exe 会把 portable.txt
                # 直接丢在工程根目录里 —— 那是产品代码的地盘，不该留测试产物。
                # 改成传到受控临时目录，跟着其它临时产物一起被统一清理。
                out = subprocess.run([str(exe), str(TEMP.path), str(APP)],
                                     stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
                # 同 T21：辅助程序按控制台代码页输出中文，不能用默认的 UTF-8 解码
                text = decode_console_output(out.stdout)
                for line in text.splitlines():
                    if line.strip():
                        say("  " + line.strip())

                # 断言: 切过去、切回来、目录都清干净
                if "IsPortable = True" not in text:
                    bad("便携模式: 没有切到便携模式")
                if "IsPortable = False" not in text:
                    bad("便携模式: 没有切回普通模式")
                if "普通目录还在 = True" in text:
                    bad("便携模式: 旧目录没有移除")
                if "便携目录还在 = True" in text:
                    bad("便携模式: 切回后便携目录仍在")
                if "配置里的 IsPortable = True" not in text:
                    bad("便携模式: 配置里没有记下便携标记")
                if "配置里的 IsPortable = False" not in text:
                    bad("便携模式: 配置里没有记下普通模式标记")
            else:
                say("  (编译辅助程序失败, 已跳过)")
        else:
            say("  (没有 csc, 已跳过)")
    except Exception as e:
        bad("便携模式测试异常: %s" % e)

    # 测试收尾：把右键菜单注销掉。
    #
    # 之前这里是"重新注册回去"，理由是怕影响用户日常使用。
    # 但测试完成后的程序并不是直接投入生产的那一份，
    # 留着注册等于在系统里挂了一个指向测试产物的右键菜单 ——
    # 那才是真正的"幽灵注册"：菜单在，但指向的 dll 可能已被覆盖或删掉。
    #
    # 收尾策略：**先查再动**。
    #   - 系统里本来就没有注册（大多数情况）-> 什么都不用做
    #   - 有残留 -> 才注销
    #
    # 为什么要"先查"：注销要走 regsvr32 提权，Windows 会弹 UAC 让用户点确认。
    # 每次跑测试都弹一次 UAC 让人点，是很烦人的；
    # 而"没有残留就不需要注销"，本来就不该有东西要清。
    # 有残留才值得打扰用户一次。
    say("")
    say("##### 收尾：检查右键菜单注册 #####")
    if not shell_menu_registered():
        say("  系统里没有右键菜单注册，无需注销（不会弹 UAC）")
    else:
        say("  检测到注册残留，执行注销")
        for attempt in range(3):
            try:
                subprocess.run([APP, "-ur"], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                               stdin=subprocess.DEVNULL, timeout=60)
            except Exception as e:
                say("  注销失败: %s" % e)
                continue

            if not shell_menu_registered():
                say("  已注销")
                break
            time.sleep(1.5)
        else:
            say("  !! 右键菜单仍处于注册状态")
            bad("收尾没能注销右键菜单, 系统里留了幽灵注册")

    say("")
    say("##### 问题汇总 (%d) #####" % len(ISSUES))
    for i in ISSUES:
        say("  - " + i)

    (ROOT / "report.txt").write_text("\n".join(OUT), encoding="utf-8")
    print("")
    print("TOTAL ISSUES: %d" % len(ISSUES))


def cleanup():
    """所有测试跑完之后统一清理临时产物。

    放在最后统一清，而不是边测边清：
    中途清理一旦边界判断出错，删掉的是还在用的目录，
    后面几个用例跟着崩；统一清理则只剩一次风险点。
    """
    print("")
    print("##### 清理临时产物 #####")
    try:
        print("  " + TEMP.cleanup())
    except UnsafeDeleteError as e:
        print("  !! 清理被拒绝: %s" % e)


def run():
    """跑完全部测试, 无论成败都把配置还原、清理临时目录"""
    global CONFIG_SNAPSHOT
    if CFG.exists():
        CONFIG_SNAPSHOT = CFG.read_text(encoding="utf-8")
    try:
        main()
    finally:
        if CONFIG_SNAPSHOT is not None:
            try:
                CFG.write_text(CONFIG_SNAPSHOT, encoding="utf-8")
                print("配置已还原")
            except Exception as e:
                print("配置还原失败: %s" % e)
        cleanup()


if __name__ == "__main__":
    run()
