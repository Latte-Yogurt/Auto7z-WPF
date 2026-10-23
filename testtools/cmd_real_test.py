"""真实 cmd 语义测试（#13 的回归保护）。

为什么必须单独有这一份
======================
`cmdline_test.py` 用 `subprocess.run([exe, ...])` 直接起进程，天然会等它结束，
**测不出** cmd 自己的等待语义。而 #13 的原症状恰恰是「cmd 立刻还回提示符，
之后用户敲的 Y/N 与密码被 cmd 当成自己的命令」。

注意：**不能**用 .bat 包一层来跑。批处理里 cmd 会等 GUI 程序，
那样即使子系统改错也照样"通过"——本文件里的用例全部走
`cmd /c "<exe> ... & echo MARK"` 这种真正的命令行形式。

用法：
    python cmd_real_test.py [--app <Auto7z.exe>] [--keep]

退出码：0 全部通过；1 有失败项；2 环境不满足。
"""
import argparse
import os
import re
import shutil
import subprocess
import sys
import tempfile
import time
from pathlib import Path

DEFAULT_APP = Path(__file__).resolve().parent.parent / "Auto7z_WPF" / "Auto7z_WPF" / "bin" / "Debug" / "Auto7z.exe"


def decode_console(raw):
    # 先按 UTF-8 严格解：解不开再退回本地代码页。
    # 反过来先试 GBK 的话，UTF-8 字节会被「成功」解成乱码，看起来像产品输出坏了。
    for enc in ("utf-8", "gbk"):
        try:
            return raw.decode(enc)
        except UnicodeDecodeError:
            continue
    return raw.decode("latin-1")


class Runner:
    def __init__(self, app, work):
        self.app = Path(app)
        self.work = Path(work)
        self.failures = []

    def ok(self, msg):
        print("  [通过] " + msg)

    def bad(self, msg):
        print("  [失败] " + msg)
        self.failures.append(msg)

    def cmd(self, line, stdin_text=None, timeout=300):
        """用真实 cmd.exe 跑一行命令（不是批处理）。

        整行作为一个字符串交给 CreateProcess，cmd 自己解析；
        用列表传参会先被 Python 加一层引号，cmd 解析出来的东西就变了。
        """
        argv = 'cmd /s /c "%s"' % line
        started = time.time()
        proc = subprocess.run(
            argv,
            input=(stdin_text or "").encode("ascii", "ignore"),
            stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
            cwd=str(self.work), timeout=timeout)
        return proc.returncode, decode_console(proc.stdout), time.time() - started

    def exe_line(self, args):
        return '"%s" %s' % (self.app, args)


def case_help(r):
    print("\n=== C1  -h：输出可读、cmd 等它跑完 ===")
    code, out, elapsed = r.cmd(r.exe_line("-h") + " & echo MARK %TIME%")
    print("  退出码=%d 用时=%.2fs" % (code, elapsed))
    has_usage = ("用法" in out) or ("Usage" in out)
    has_mark = "MARK" in out
    bad_cmd = re.search(r"不是内部或外部命令|is not recognized", out)
    r.ok("有用法输出") if has_usage else r.bad("没有用法输出")
    r.ok("cmd 继续执行了后续命令（MARK 出现）") if has_mark else r.bad("没看到 MARK 行")
    if has_usage and has_mark:
        # 用法必须排在 MARK 之前，说明 cmd 等到了输出结束才继续
        if out.rindex("MARK") > out.index("用法" if "用法" in out else "Usage"):
            r.ok("输出顺序正确：用法在前，MARK 在后")
        else:
            r.bad("顺序异常：MARK 出现在用法之前，cmd 没有等待")
    r.ok("没有「不是内部或外部命令」") if not bad_cmd else r.bad("出现 cmd 抢输入的症状")


def case_merge(r):
    print("\n=== C2  多文件 Y/N：cmd 等到压缩结束 ===")
    for name, size in (("ca.bin", 6 * 1024 * 1024), ("cb.bin", 6 * 1024 * 1024)):
        data = bytes((i * 37 + 11) % 251 for i in range(1024)) * (size // 1024)
        (r.work / name).write_bytes(data)
    code, out, elapsed = r.cmd(
        r.exe_line('-Z "%s" "%s"' % (r.work / "ca.bin", r.work / "cb.bin")) + " & echo MARK %TIME%",
        stdin_text="y\n")
    archives = list(r.work.rglob("*.7z"))
    print("  退出码=%d 用时=%.2fs 产物=%d" % (code, elapsed, len(archives)))
    r.ok("控制台问了 Y/N") if ("(Y/N)" in out or "Y/N" in out) else r.bad("没看到 Y/N 问句")
    r.ok("产物已生成") if archives else r.bad("没有生成压缩包")
    if "MARK" in out:
        before = out[:out.rindex("MARK")]
        r.ok("MARK 之前有进度文本") if ("已处理" in before or "完成" in before) else r.bad("MARK 之前没有进度文本")
    else:
        r.bad("没看到 MARK 行")


def case_failure_exit_code(r):
    print("\n=== C3  失败路径：退出码必须非 0（#3 的回归保护）===")
    code, out, elapsed = r.cmd(
        r.exe_line('-Z "%s"' % (r.work / "根本不存在.bin")) + " & echo MARK %TIME%")
    match = re.search(r"MARK (\S+)", out)
    print("  退出码(of cmd)=%d 用时=%.2fs" % (code, elapsed))
    # 退出码要从被测进程直接取，cmd 里的 %ERRORLEVEL% 在复合命令行里会被提前展开
    direct = subprocess.run([str(r.app), "-Z", str(r.work / "根本不存在.bin")],
                            stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=300)
    print("  被测进程退出码=%d 输出行数=%d" % (direct.returncode, len(decode_console(direct.stdout).strip().splitlines())))
    if direct.returncode == 0:
        r.bad("失败任务退出码仍为 0（对应 releasefix #3，本轮复现）")
    else:
        r.ok("失败退出码非 0")


def case_password(r):
    print("\n=== C4  加密包：密码在控制台问、错密码重试提示独占一行且带冒号 ===")
    src = r.work / "secret.txt"
    src.write_text("top secret", encoding="utf-8")
    sz = Path(os.environ["LOCALAPPDATA"]) / "Auto7z" / "7-Zip" / "7z.exe"
    if not sz.exists():
        r.bad("找不到 7z.exe，无法构造加密包")
        return
    enc = r.work / "enc.7z"
    subprocess.run([str(sz), "a", "-t7z", "-pCorrect1", "-mhe=on", str(enc), str(src)],
                   stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=300)
    if not enc.exists():
        r.bad("加密包没有生成")
        return
    code, out, elapsed = r.cmd(
        r.exe_line('-U "%s"' % enc) + " & echo MARK %TIME%",
        stdin_text="WrongPass\nCorrect1\n")
    print("  退出码=%d 用时=%.2fs" % (code, elapsed))
    asked = "请输入密码" in out or "password" in out.lower()
    r.ok("控制台问了密码") if asked else r.bad("没有问密码")
    # 注意：stdin 是管道时没有终端回显，提示语后面的换行不会出现在输出里，
    # 所以这里只能判「末尾有没有冒号」，判不了「是不是独占一行」——
    # 独占一行需要真实键盘输入，列入人工测试项。
    retry = re.search(r"密码错误[^\r\n]*?重新询问密码([:：])", out)
    if retry:
        r.ok("重试提示带末尾冒号")
        segment = out[retry.start():retry.end()]
        print("        实际文本：%s" % segment)
    else:
        r.bad("没看到带冒号的「密码错误」重试提示")
    bad_cmd = re.search(r"不是内部或外部命令|is not recognized", out)
    r.ok("没有「不是内部或外部命令」") if not bad_cmd else r.bad("出现 cmd 抢输入的症状")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--app", default=str(DEFAULT_APP))
    ap.add_argument("--keep", action="store_true")
    a = ap.parse_args()

    app = Path(a.app)
    if not app.exists():
        print("被测程序不存在: %s" % app)
        return 2

    work = Path(tempfile.gettempdir()) / "Auto7zTest_cmdreal"
    if work.exists():
        shutil.rmtree(work, ignore_errors=True)
    work.mkdir(parents=True)

    print("=" * 70)
    print("真实 cmd 语义测试")
    print("被测程序: %s" % app)
    print("=" * 70)
    r = Runner(app, work)
    try:
        case_help(r)
        case_merge(r)
        case_failure_exit_code(r)
        case_password(r)
    finally:
        if a.keep:
            print("\n保留临时目录: %s" % work)
        else:
            resolved = str(work.resolve())
            temp = str(Path(tempfile.gettempdir()).resolve())
            if resolved.lower().startswith(temp.lower()) and "Auto7zTest" in resolved:
                shutil.rmtree(work, ignore_errors=True)

    print("\n" + "=" * 70)
    if r.failures:
        print("失败 %d 项：" % len(r.failures))
        for f in r.failures:
            print("  - " + f)
        return 1
    print("全部通过")
    return 0


if __name__ == "__main__":
    sys.exit(main())
