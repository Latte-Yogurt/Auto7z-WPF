#!/usr/bin/env python3
# -*- coding: utf-8 -*-
r"""一键系统测试。

跑「系统测试」这一类：验证产品在真实场景下的行为 ——
命令行模式、命令行多文件交互、命令行加密包密码输入、
外壳扩展（右键菜单）的各项能力、界面相关的运行时检查。

与回归测试的分工:
    run_regress.py  —— 回归：针对已知条目基线表逐条确认
    run_system_test.py —— 系统：端到端覆盖主流程

用法:
    python run_system_test.py                     # 用默认的 Debug 构建
    python run_system_test.py --app <exe>
    python run_system_test.py --config Release
    python run_system_test.py --keep              # 保留临时目录（调试用）

退出码: 0 = 全部通过, 1 = 有失败项, 2 = 环境不满足
"""
import argparse
import os
import subprocess
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from safe_ops import SafeTemp

HERE = Path(__file__).resolve().parent
WORKSPACE = HERE.parent
PROJECT = WORKSPACE / "Auto7z_WPF" / "Auto7z_WPF"
CS_DIR = HERE / "cs"

CSC = r"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
REF_ROOT = Path(r"C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.7.2")
REFS = [
    REF_ROOT / "PresentationFramework.dll",
    REF_ROOT / "PresentationCore.dll",
    REF_ROOT / "WindowsBase.dll",
    REF_ROOT / "System.Xaml.dll",
]

CLSID = "{6C3F8A21-4B7D-4E5A-9A2C-3D710E5B8841}"

TEMP = SafeTemp("systemtest")


class Result:
    def __init__(self):
        self.items = []      # (名称, 通过?, 说明)
        self.failed = 0

    def add(self, name, ok, detail=""):
        self.items.append((name, ok, detail))
        if not ok:
            self.failed += 1
        print("  [%s] %-34s %s" % ("通过" if ok else "失败", name, detail))

    def summary(self):
        print("")
        print("=" * 70)
        print("系统测试汇总: %d 项, 失败 %d 项" % (len(self.items), self.failed))
        print("=" * 70)
        for name, ok, detail in self.items:
            print("  [%s] %-34s %s" % ("通过" if ok else "失败", name, detail))


def need_tools():
    """检查编译工具在不在。缺了就现编。"""
    missing = []
    for d in sorted(CS_DIR.iterdir()):
        if not d.is_dir():
            continue
        cs = d / (d.name + ".cs")
        if not cs.exists():
            continue
        if not (d / (d.name + ".exe")).exists():
            missing.append(d.name)

    if missing:
        print("=== 编译缺失的检查工具: %s ===" % ", ".join(missing))
        r = subprocess.run([sys.executable, str(CS_DIR / "build_all.py")] + missing,
                           cwd=str(CS_DIR))
        if r.returncode != 0:
            print("!! 编译失败")
            return False
    return True


def tool(name):
    """工具的完整路径。"""
    return CS_DIR / name / (name + ".exe")


def decode_console(raw):
    """解控制台输出的字节。

    不能写死编码：程序写出来的编码**跟着控制台代码页走**，
    而控制台代码页取决于启动它的那个 cmd：
      - cmd 默认代码页是 936(GBK)     -> 输出是 GBK
      - 用了 chcp 65001 的 cmd        -> 输出是 UTF-8
    （程序启动时会借父 cmd 的控制台，编码也跟着父 cmd 的代码页走。）

    办法：两种都试一遍，取"解码后中文正常"的那���。
    UTF-8 优先试，因为 UTF-8 解 GBK 几乎不会乱成合法中文，
    反过来则不然。
    """
    if not raw:
        return ""
    for enc in ("utf-8", "gbk"):
        try:
            text = raw.decode(enc)
        except UnicodeDecodeError:
            continue
        # 出现替换字符说明这个编码不对
        if "\ufffd" not in text:
            return text
    return raw.decode("gbk", "replace")


class RunResult(object):
    """一次子进程运行的结果。

    做成对象而不是元组：调用方既能 r.stdout 读输出，
    也能 r.returncode 拿退出码，两种写法都不别扭。
    """

    def __init__(self, returncode, stdout, stderr):
        self.returncode = returncode
        self.stdout = stdout
        self.stderr = stderr

    @property
    def output(self):
        return (self.stdout or "") + (self.stderr or "")


def run(cmd, **kw):
    """跑一个命令并收回输出。

    返回 RunResult，输出已按控制台实际编码解好。
    """
    r = subprocess.run(cmd, capture_output=True, **kw)
    return RunResult(r.returncode,
                    decode_console(r.stdout or b""),
                    decode_console(r.stderr or b""))


def run_to_file(cmd, stdin_file, out_file):
    """跑一个命令，输入从文件读，输出写文件（原始字节）。

    落盘再自己解码最稳，也避免管道把子进程卡住。
    """
    with open(str(stdin_file), "rb") as fin, open(str(out_file), "wb") as fout:
        return subprocess.run(cmd, stdin=fin, stdout=fout,
                              stderr=subprocess.STDOUT)


# ---------------------------------------------------------------- 各测试项

def t_cmdline_basic(res, app):
    """命令行基本用法：-h / 三种模式 / 拖放 / 多文件。"""
    print("")
    print("=== 命令行模式 ===")
    r = run([sys.executable, str(HERE / "cmdline_test.py"), str(app),
             str(TEMP.path / "cmdline")], cwd=str(HERE))
    out = (r.stdout or "") + (r.stderr or "")
    m = __import__("re").search(r"TOTAL ISSUES:\s*(\d+)", out)
    n = int(m.group(1)) if m else -1
    res.add("命令行基本用法", n == 0, "TOTAL ISSUES=%s" % n)
    return out


def t_cmdline_password(res, app):
    """命令行解压加密包：提示要密码、读密码、密码错会重试。"""
    print("")
    print("=== 命令行加密包密码交互 ===")
    work = TEMP.path / "cnpw"
    work.mkdir(parents=True, exist_ok=True)

    src = work / "src.txt"
    src.write_text("secret\n", encoding="utf-8")
    enc = work / "enc.7z"

    sz = Path(os.environ["LOCALAPPDATA"]) / "Auto7z" / "7-Zip" / "7z.exe"
    if not sz.exists():
        res.add("命令行加密包密码交互", False, "找不到 7z 组件, 跳过")
        return

    r = run([str(sz), "a", "-aoa", "-y", "-t7z", "-p测试密码123", str(enc), str(src)])
    if r.returncode != 0 or not enc.exists():
        res.add("命令行加密包密码交互", False, "造测试压缩包失败")
        return

    # 用 GBK 编码喂密码（跟控制台代码页一致）
    pwfile = work / "pw.txt"
    pwfile.write_bytes("测试密码123".encode("gbk"))
    outfile = work / "out.txt"

    run_to_file([str(app), "-U", str(enc)], pwfile, outfile)
    text = decode_console(outfile.read_bytes())

    ok_prompt = "请输入密码" in text
    ok_done = "解压完成" in text
    # 解出来的内容落在安全命名文件夹里，要递归找
    extracted = [f for f in work.rglob("src.txt") if f.parent != work]
    ok_file = len(extracted) > 0

    res.add("加密包: 控制台提示输入密码", ok_prompt,
            "提示文本=%s" % ("有" if ok_prompt else "无"))
    res.add("加密包: 密码正确时解压完成", ok_done,
            "输出含'解压完成'=%s" % ok_done)
    res.add("加密包: 内容真的解出来了", ok_file,
            "解出 %d 个文件" % len(extracted))


def t_cmdline_merge(res, app):
    """命令行多文件：问 Y/N，答 N 就各压各的。"""
    print("")
    print("=== 命令行多文件 Y/N 交互 ===")
    work = TEMP.path / "merge"
    work.mkdir(parents=True, exist_ok=True)
    a = work / "a.txt"
    b = work / "b.txt"
    a.write_text("a\n", encoding="utf-8")
    b.write_text("b\n", encoding="utf-8")

    ynfile = work / "yn.txt"
    ynfile.write_bytes("N".encode("gbk"))
    outfile = work / "out.txt"

    run_to_file([str(app), "-Z", str(a), str(b)], ynfile, outfile)
    text = decode_console(outfile.read_bytes())

    ok_q = "Y/N" in text or "是否合并" in text
    res.add("多文件: 控制台问 Y/N", ok_q,
            "问句=%s" % ("有" if ok_q else "无"))

    # 答 N 之后应该出两个包。
    # 压缩包落在安全命名文件夹里（不是直接放在 work 下面），所以要递归找。
    packs = list(work.rglob("*.7z"))
    res.add("多文件: 答 N 后各自压缩", len(packs) >= 2,
            "生成 %d 个压缩包" % len(packs))


def t_shell_extension(res, app):
    """外壳扩展：菜单项、作用范围、图标、完整链路。"""
    print("")
    print("=== 外壳扩展（右键菜单）===")
    dll = app.parent / "Auto7zShell.dll"
    if not dll.exists():
        res.add("外壳扩展", False, "找不到 Auto7zShell.dll, 跳过")
        return

    tfile = TEMP.path / "t.txt"
    tfile.write_text("x\n", encoding="utf-8")

    checks = [
        ("shellcheck", [str(tool("shellcheck")), str(dll), CLSID, str(tfile)]),
        ("scopeprobe", [str(tool("scopeprobe")), str(dll), CLSID, str(TEMP.path)]),
        ("iconprobe", [str(tool("iconprobe")), str(dll), CLSID, str(tfile)]),
        ("dataprobe", [str(tool("dataprobe")), str(dll), CLSID, str(tfile)]),
    ]
    for name, cmd in checks:
        if not Path(cmd[0]).exists():
            res.add(name, False, "工具没编译, 跳过")
            continue
        r = run(cmd)
        out = (r.stdout or "") + (r.stderr or "")
        if "TOTAL PROBLEMS" in out:
            ok = "TOTAL PROBLEMS: 0" in out
            res.add(name, ok, "退出码=%d" % r.returncode)
        else:
            # 这几个工具不报 PROBLEMS，只看关键步骤有没有走通
            ok = ("成功" in out) or ("S_OK" in out) or ("菜单项数 = 2" in out)
            res.add(name, ok and r.returncode == 0, "退出码=%d" % r.returncode)


def t_shellproto(res, app):
    """共享内存传参协议 + 单实例。"""
    print("")
    print("=== 共享内存传参与单实例 ===")
    exe = tool("shellproto")
    if not exe.exists():
        res.add("shellproto", False, "工具没编译, 跳过")
        return
    work = TEMP.path / "shellproto"
    if work.exists():
        import shutil
        shutil.rmtree(str(work), ignore_errors=True)
    r = run([str(exe), str(app), str(work)])
    out = (r.stdout or "") + (r.stderr or "")
    ok = "TOTAL PROBLEMS: 0" in out
    res.add("shellproto", ok, "退出码=%d" % r.returncode)


def t_ui_checks(res, app):
    """界面相关：按钮文字、文本宽度、Tab 顺序、系统语言映射。

    这几个工具的报告方式不一样：
      verify / uiwidth  —— 打印 "TOTAL PROBLEMS: N"
      taborder          —— 写 taborder.txt，stdout 不说话
      langcheck        —— 打印支持的��言与映射表，不报问题数
    所以分开判断，不能一把尺子量到底。
    """
    print("")
    print("=== 界面检查 ===")
    lang = WORKSPACE / "Auto7z_WPF" / "Auto7z_WPF" / "Resources" / "Languages"

    # --- verify: 对话框按钮文字与提示切换 ---
    # verify.exe 把报告写到第一个参数指定的目录，给它受控临时目录，
    # 不要写进产品的 bin 目录。
    r = run([str(tool("verify")), str(TEMP.path), str(app)])
    out = (r.stdout or "") + (r.stderr or "")
    ok = "DONE" in out and "  !! " not in out
    res.add("verify 对话框按钮与提示", ok, "退出码=%d" % r.returncode)

    # --- uiwidth: 三语文案宽度 ---
    r = run([str(tool("uiwidth")), str(TEMP.path), str(app), str(lang)])
    out = (r.stdout or "") + (r.stderr or "")
    ok = "TOTAL PROBLEMS: 0" in out
    res.add("uiwidth 三语文案宽度", ok, "退出码=%d" % r.returncode)

    # --- taborder: 结果在 taborder.txt 里 ---
    tb = TEMP.path / "taborder.txt"
    if tb.exists():
        try:
            tb.unlink()
        except OSError:
            pass
    r = run([str(tool("taborder")), str(TEMP.path), str(app),
             str(WORKSPACE / "Auto7z_WPF" / "Auto7z_WPF" / "MainWindow.xaml")])
    text = ""
    if tb.exists():
        try:
            text = tb.read_text(encoding="utf-8", errors="replace")
        except OSError:
            pass
    ok = r.returncode == 0 and "  !! " not in text
    res.add("taborder Tab 顺序", ok,
            "退出码=%d%s" % (r.returncode, "" if ok else " (报告有异常)"))

    # --- langcheck: 系统语言 -> 界面语言映射 ---
    # langcheck 自己写文件用 StreamWriter(UTF8)，所以 stdout 是 UTF-8 不是 GBK
    r = run([str(tool("langcheck")), str(app)])
    out = (r.stdout or "") + (r.stderr or "")
    ok = r.returncode == 0 and "支持的语言" in out
    res.add("langcheck 语言映射", ok, "退出码=%d" % r.returncode)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--app", help="被测程序路径")
    ap.add_argument("--config", default="Debug", help="Debug / Release")
    ap.add_argument("--keep", action="store_true", help="保留临时目录")
    args = ap.parse_args()

    app = Path(args.app) if args.app else PROJECT / "bin" / args.config / "Auto7z.exe"
    if not app.exists():
        print("找不到被测程序: %s" % app)
        print("先构建: msbuild Auto7z_WPF\\Auto7z_WPF.sln /t:Rebuild /p:Configuration=%s" % args.config)
        return 2

    print("=" * 70)
    print("Auto7z 一键系统测试")
    print("被测程序: %s" % app)
    print("临时目录: %s" % TEMP.path)
    print("=" * 70)

    TEMP.ensure()
    res = Result()

    try:
        t_cmdline_basic(res, app)
        t_cmdline_password(res, app)
        t_cmdline_merge(res, app)
        t_shell_extension(res, app)
        t_shellproto(res, app)
        t_ui_checks(res, app)
    finally:
        res.summary()
        print("")
        print("##### 清理临时产物 #####")
        if args.keep:
            print("  --keep 指定了，保留: %s" % TEMP.path)
        else:
            print("  " + TEMP.cleanup())

    return 1 if res.failed else 0


if __name__ == "__main__":
    sys.exit(main())
