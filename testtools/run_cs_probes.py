#!/usr/bin/env python3
# -*- coding: utf-8 -*-
r"""一键跑完 testtools\cs 下全部 C# 探针，并把结果摊开给人看。

为什么要有它：`cs\` 里那 15 个 exe 各自要一堆参数（dll 路径、CLSID、样本文件、
工作目录……）。直接双击只会看到一句用法就退出，用户既不知道该填什么、更看不到
任何一条检测结论。这个脚本负责：

    ap.add_argument("--app", help="Auto7z.exe 路径，默认自动找 bin/<config>/Auto7z.exe")
  2. 按每个工具真正需要的参数把它调起来；
  3. 把每条结论翻译成「通过 / 失败 / 跳过 + 一句话说明」；
  4. 汇总成一张表，退出码 0 = 全通过，1 = 有失败。

用法：
    python run_cs_probes.py                  # 默认用 Debug 构建
    python run_cs_probes.py --config Release
    python run_cs_probes.py --app <Auto7z.exe 路径>
    python run_cs_probes.py --verbose        # 连工具原始输出一起打印

只读产品、只写 %TEMP%，不碰工程里的任何东西。
"""
import argparse
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
WORKSPACE = HERE.parent
CS_DIR = HERE / "cs"

CLSID = "{6C3F8A21-4B7D-4E5A-9A2C-3D710E5B8841}"

# 各工具自报「全部通过」的固定说法。它们不打印 TOTAL PROBLEMS，
# 而是用一句总结表意 —— 判定得跟着实际输出走，不能假设所有工具格式一致。
PASS_MARKERS = (
    "全部符合预期",
    "全部成立",
    "结构全部符合",
    "全部一致",
    "检查通过",
    "(无问题)",
)


def decode(raw):
    """解探针的控制台输出。

    这些工具按控制台代码页写中文，但代码页随调用方而变：cmd 默认 936(GBK)，
    PowerShell 7 / Windows Terminal 常是 65001(UTF-8)。顺序很关键 ——
    UTF-8 字节用 GBK 去解不会报错，只会变成一串合法但错误的汉字（「鏀寔」这种），
    所以必须先试 UTF-8：它能严格校验，不合法就抛异常，正好当探针用。
    """
    if raw is None:
        return ""
    try:
        return raw.decode("utf-8-sig")
    except UnicodeDecodeError:
        pass
    for enc in ("gbk", "oem"):
        try:
            return raw.decode(enc)
        except (LookupError, UnicodeDecodeError):
            continue
    return raw.decode("utf-8", errors="replace")
def tool(name):
    return CS_DIR / name / (name + ".exe")


class Result:
    def __init__(self, name, purpose):
        self.name = name
        self.purpose = purpose
        self.status = "跳过"
        self.detail = ""
        self.raw = ""
        self.code = None


def run(exe, args, timeout=180, cwd=None):
    """跑一个探针，返回 (退出码, 合并后的文本输出)。"""
    cmd = [str(exe)] + [str(a) for a in args]
    try:
        proc = subprocess.run(
            cmd, cwd=str(cwd) if cwd else None,
            stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
            timeout=timeout)
        return proc.returncode, decode(proc.stdout)
    except subprocess.TimeoutExpired:
        return -1, "<超时 %d 秒>" % timeout
    except OSError as exc:
        return -1, "<无法启动: %s>" % exc


def parse_problems(text):
    """多数工具会打印 TOTAL PROBLEMS: N，直接读它。"""
    for line in text.splitlines():
        s = line.strip()
        if s.startswith("TOTAL PROBLEMS:"):
            try:
                return int(s.split(":", 1)[1].strip())
            except (IndexError, ValueError):
                pass
    return None


def first_fail_line(text):
    for line in text.splitlines():
        s = line.strip()
        if s.startswith("!!"):
            return s
    return ""


def judge(text, code):
    """通用判定：TOTAL PROBLEMS → 工具自己的总结句 → !! 行 → 退出码。"""
    problems = parse_problems(text)
    if problems is not None:
        if problems == 0:
            return "通过", ""
        return "失败", first_fail_line(text) or ("%d 个问题" % problems)

    for marker in PASS_MARKERS:
        if marker in text:
            return "通过", ""

    bad = [l.strip() for l in text.splitlines() if l.strip().startswith("!!")]
    if bad:
        return "失败", bad[0]

    if code == 0:
        return "通过", ""
    return "失败", "退出码=%s" % code


def judge_report_file(path, code):
    """有些工具把结论写进 work 目录下的 <name>.txt，读那个文件判定。"""
    if not path.exists():
        return ("失败", "没有生成报告文件 %s" % path.name)
    body = path.read_text(encoding="utf-8", errors="replace")
    bad = [l.strip() for l in body.splitlines() if "!!" in l]
    if bad:
        return ("失败", bad[0])
    return ("通过", "")


def ensure_tool(res):
    """工具没编译就直接说明怎么办，而不是报一个看不懂的错。"""
    exe = tool(res.name)
    if exe.exists():
        return exe
    res.status = "跳过"
    res.detail = "未编译：先运行 python cs\\build_all.py"
    return None


def is_release_skip(code, text):
    """Release 构建没有测试钩子，探针会用退出码 3 明确跳过。"""
    return code == 3 or "RELEASE_NO_TEST_HOOK" in text


def main():
    ap = argparse.ArgumentParser(description="一键跑完 cs 下的全部 C# 探针")
    ap.add_argument("--app", help="Auto7z.exe 路径，默认自动找 bin\\<config>\\Auto7z.exe")
    ap.add_argument("--config", default="Debug", choices=["Debug", "Release"])
    ap.add_argument("--verbose", action="store_true", help="连工具原始输出一起打印")
    args = ap.parse_args()

    app = (Path(args.app).resolve() if args.app
           else WORKSPACE / "Auto7z_WPF" / "Auto7z_WPF" / "bin" / args.config / "Auto7z.exe")

    print("=" * 74)
    print(" C# 探针一键运行")
    print("=" * 74)
    print("  被测程序 :", app)
    if not app.exists():
        print("")
        print("  !! 找不到被测程序。先构建一次：")
        print("       msbuild \"Auto7z_WPF\\Auto7z_WPF.sln\" /p:Configuration=%s" % args.config)
        print("     外壳扩展另需：Auto7zShell\\buildtool\\buildtool.exe")
        print("     或用 --app 指定 Auto7z.exe 的完整路径。")
        return 2

    bin_dir = app.parent
    dll = bin_dir / "Auto7zShell.dll"
    gui = bin_dir / "Auto7zGUI.exe"
    print("  外壳组件 :", dll, "" if dll.exists() else "（缺失，外壳相关项会跳过）")
    print("  图形入口 :", gui, "" if gui.exists() else "（缺失）")

    work = Path(tempfile.mkdtemp(prefix="Auto7zTest_csprobes_"))
    # 样本单独放一个子目录：portable 探针会整目录搬迁，共用 work 会把它挪走。
    # 样本单独建一个临时目录，和探针的输出目录分开：
    # verify/taborder/uiwidth 会按需清理自己那个输出目录，放在里面会被连带删掉。
    sample_dir = Path(tempfile.mkdtemp(prefix="Auto7zTest_cssample_"))
    sample_dir.mkdir(exist_ok=True)
    sample = sample_dir / "sample.txt"
    sample.write_text("cs probe sample\n", encoding="utf-8")
    results = []

    def add(name, purpose):
        r = Result(name, purpose)
        results.append(r)
        return r

    # ---------- 1. 只依赖产品程序集的探针 ----------
    r = add("langcheck", "系统语言 → 界面语言的映射")
    if ensure_tool(r):
        code, text = run(tool("langcheck"), [app])
        r.raw, r.code = text, code
        if "支持的语言" not in text:
            r.status, r.detail = "失败", "没有输出支持的语言列表"
        else:
            r.status, r.detail = judge(text, code)

    r = add("resultprobe", "结果颜色契约（失败必须是红字）")
    if ensure_tool(r):
        code, text = run(tool("resultprobe"), [app])
        r.raw, r.code = text, code
        if is_release_skip(code, text):
            r.status, r.detail = "跳过", "Release 构建无测试钩子"
        else:
            r.status, r.detail = judge(text, code)

    r = add("guardprobe", "破坏性操作防护（拒绝删源码 / 拒绝越界）")
    if ensure_tool(r):
        code, text = run(tool("guardprobe"), [app])
        r.raw, r.code = text, code
        if is_release_skip(code, text):
            r.status, r.detail = "跳过", "Release 构建无测试钩子"
        else:
            r.status, r.detail = judge(text, code)

    r = add("sha256probe", "SHA256 服务（格式 / 无 BOM / 递归 / 进度）")
    if ensure_tool(r):
        code, text = run(tool("sha256probe"), [app, work])
        r.raw, r.code = text, code
        if is_release_skip(code, text):
            r.status, r.detail = "跳过", "Release 构建无测试钩子"
        else:
            r.status, r.detail = judge(text, code)

    r = add("verify", "对话框按钮文字 / 居中 / 提示切换")
    if ensure_tool(r):
        code, text = run(tool("verify"), [work, app])
        r.raw, r.code = text, code
        if is_release_skip(code, text):
            r.status, r.detail = "跳过", "Release 构建无测试钩子"
        else:
            r.status, r.detail = judge_report_file(work / "verify.txt", code)

    r = add("taborder", "Tab 顺序（静态 TabIndex + 运行时可达）")
    if ensure_tool(r):
        xaml_dir = WORKSPACE / "Auto7z_WPF" / "Auto7z_WPF"
        code, text = run(tool("taborder"), [work, app, xaml_dir])
        r.raw, r.code = text, code
        if is_release_skip(code, text):
            r.status, r.detail = "跳过", "Release 构建无测试钩子"
        else:
            r.status, r.detail = judge_report_file(work / "taborder.txt", code)

    r = add("uiwidth", "三语文案在界面上的溢出与遮挡")
    if ensure_tool(r):
        lang_dir = WORKSPACE / "Auto7z_WPF" / "Auto7z_WPF" / "Resources" / "Languages"
        code, text = run(tool("uiwidth"), [work, app, lang_dir])
        r.raw, r.code = text, code
        if is_release_skip(code, text):
            r.status, r.detail = "跳过", "Release 构建无测试钩子"
        else:
            r.status, r.detail = judge_report_file(work / "uiwidth.txt", code)

    r = add("portable", "便携模式切换与取消（.config 是否被删）")
    if ensure_tool(r):
        code, text = run(tool("portable"), [work, app])
        r.raw, r.code = text, code
        if is_release_skip(code, text):
            r.status, r.detail = "跳过", "Release 构建无测试钩子"
        else:
            r.status, r.detail = judge(text, code)

    # ---------- 2. 依赖外壳扩展的探针 ----------
    shell_tools = [
        ("shellcheck", "组件自检：菜单项数与谓词名", [dll, CLSID, sample]),
        ("scopeprobe", "作用范围：哪些目标该出菜单项", [dll, CLSID, work]),
        ("iconprobe", "菜单图标能否取到且有内容", [dll, CLSID, sample]),
        ("dataprobe", "完整链路：Initialize → InvokeCommand", [dll, CLSID, sample]),
    ]
    for name, purpose, argv in shell_tools:
        r = add(name, purpose)
        if not ensure_tool(r):
            continue
        if not dll.exists():
            r.status = "跳过"
            r.detail = "缺少 Auto7zShell.dll（先跑 buildtool）"
            continue
        r.raw, r.code = text, code
        r.status, r.detail = judge(text, code)

    r = add("menuprobe", "点击槽位分派：压缩/解压 × 两种调用形态")
    if ensure_tool(r):
        if not dll.exists():
            r.status, r.detail = "跳过", "缺少 Auto7zShell.dll（先跑 buildtool）"
        else:
            # 两种调用形态、两个菜单项，四个组合都要对。
            want = [("ansi", "compress", "-z"), ("ansi", "extract", "-uz"),
                    ("unicode-null", "compress", "-z"), ("unicode-null", "extract", "-uz")]
            bad = []
            texts = []
            for style, which, expect in want:
                code, text = run(tool("menuprobe"),
                                 ["--style", style, dll, CLSID, which, sample])
                texts.append(text)
                got = ""
                for line in text.splitlines():
                    if line.startswith("MODE="):
                        got = line[5:].strip()
                if got != expect:
                    bad.append("%s/%s 得到 %r 期望 %r" % (style, which, got, expect))
            r.raw, r.code = "\n".join(texts), 0
            if bad:
                r.status, r.detail = "失败", bad[0]
            else:
                r.status, r.detail = "通过", "4 种组合全部正确"

    r = add("shellproto", "共享内存传参 / 模式开关 / 单实例交接")
    if ensure_tool(r):
        if not gui.exists():
            r.status, r.detail = "跳过", "缺少 Auto7zGUI.exe"
        else:
            # shellproto 的第一个参数是被测 exe；它自己会推出同目录的 Auto7zGUI.exe。
            code, text = run(tool("shellproto"), [app, work])
            r.raw, r.code = text, code
            r.status, r.detail = judge(text, code)

    r = add("bench", "性能基准（不参与通过判定，只给参考值）")
    if ensure_tool(r):
        benchmark_dir = work / "bench"
        benchmark_dir.mkdir(exist_ok=True)
        code, text = run(tool("bench"), [app, benchmark_dir], timeout=600)
        r.raw, r.code = text, code
        # 基准是参考值，不因为慢或波动判失败；只有跑不起来才算失败。
        if code == 0:
            r.status, r.detail = "完成", "参考值见 --verbose"
        else:
            r.status, r.detail = "失败", "退出码=%s" % code

    # ---------- 汇总 ----------
    print("")
    print("-" * 74)
    print(" %-13s %-8s %s" % ("工具", "结果", "说明"))
    print("-" * 74)
    fails = passes = skips = 0
    for r in results:
        if r.status == "失败":
            fails += 1
            mark = "[失败]"
        elif r.status == "跳过":
            skips += 1
            mark = "[跳过]"
        else:
            passes += 1
            mark = "[通过]" if r.status == "通过" else "[完成]"
        line = " %-13s %-8s %s" % (r.name, mark, r.purpose)
        if r.detail:
            line += "  ← " + r.detail
        print(line)
    print("-" * 74)
    print(" 合计 %d 项：通过/完成 %d，失败 %d，跳过 %d"
          % (len(results), passes, fails, skips))
    print("")

    if args.verbose:
        print("=" * 74)
        print(" 各工具原始输出")
        print("=" * 74)
        for r in results:
            print("")
            print("##### %s #####" % r.name)
            print(r.raw.rstrip() if r.raw.strip() else "(无输出)")

    if skips:
        print(" 跳过的项通常是：被测程序是 Release 构建（没有测试钩子）、")
        print(" 或 Auto7zShell.dll 没构建。补齐后重跑即可。")
        print("")

    shutil.rmtree(work, ignore_errors=True)
    shutil.rmtree(sample_dir, ignore_errors=True)
    return 0 if fails == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
