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
import os
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

from safe_ops import safe_rmtree

HERE = Path(__file__).resolve().parent
WORKSPACE = HERE.parent
CS_DIR = HERE / "cs"

# 探针会在进程内驱动产品，产品的数据目录默认是 %LOCALAPPDATA%\Auto7z（用户真实那份）。
# 带上改道变量，让它们统一写进 %TEMP% 下的受控目录。
from test_env import isolate_data_root
DATA_ROOT = isolate_data_root("csprobes")

import test_env as _test_env
PRODUCT_ENV = _test_env.child_env("csprobes")

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
    """单个探针的结果。

    **status 的默认值是 "未执行"，不是 "跳过"。** 这一点是本轮最重要的修复。

    上一轮发现四个外壳探针（shellcheck / scopeprobe / iconprobe / dataprobe）
    从来没被真正执行过：那个循环里漏了 code, text = run(...)，
    却把**上一个探针遗留的局部变量**拿去判定。而 Result 的默认状态是 "跳过"，
    汇总时 "跳过" 不算失败 —— 于是「根本没跑」被静默计入，只在总数上少了几项，
    而汇总行看起来一切正常。

    现在默认值改成 "未执行"，汇总时**一律算失败**。
    这样任何「加了 add() 却忘了给它赋值」的路径都会立刻暴露，
    而不是伪装成一次正常的跳过。

    三种状态的区别：
      - 通过 / 失败：真的跑了，judge 给出结论
      - 跳过：**主动**跳过，脚本显式写了理由（如 Release 无测试钩子、缺组件）
      - 未执行：代码路径没走到 —— 这是缺陷，不是跳过
    """
    def __init__(self, name, purpose):
        self.name = name
        self.purpose = purpose
        self.status = "未执行"
        self.detail = ""
        self.raw = ""
        self.code = None
        # 创建时该探针已执行过多少次；汇总时若次数没增加，说明这次根本没跑。
        self.runs_before = _EXECUTIONS.count(name)


# 执行台账：每次 run() 都登记一笔。Result 用它验证「我到底跑没跑」。
#
# 为什么需要这个：上一轮发现四个外壳探针从未被执行 —— 循环里漏了 run()，
# 而 	ext / code 恰好是上一个探针的遗留值（rc=0、输出正常），
# 于是**没跑的探针被判定为通过**。这是最危险的假绿形态：
# 「未执行」防护抓不到它，因为状态被赋成了「通过」而不是「未执行」。
#
# 光看 status 无法区分「真跑通了」和「拿了别人的结果」，
# 所以改成对账：Result 记下它创建时的台账序号，汇总时核对
# 「之后确实有过一次 run()」。
_EXECUTIONS = []


def run(exe, args, timeout=180, cwd=None):
    """跑一个探针，返回 (退出码, 合并后的文本输出)。"""
    cmd = [str(exe)] + [str(a) for a in args]
    try:
        proc = subprocess.run(
            cmd, cwd=str(cwd) if cwd else None,
            stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
            timeout=timeout, env=PRODUCT_ENV)
        _EXECUTIONS.append(os.path.splitext(os.path.basename(str(exe)))[0])
        return proc.returncode, decode(proc.stdout)
    except subprocess.TimeoutExpired:
        _EXECUTIONS.append(os.path.splitext(os.path.basename(str(exe)))[0])
        return -1, "<超时 %d 秒>" % timeout
    except OSError as exc:
        _EXECUTIONS.append(os.path.splitext(os.path.basename(str(exe)))[0])
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
    """探针是否因「Release 构建没有测试钩子」而主动跳过。

    **必须只认显式标记，不能只看退出码 3。**

    早先的实现是 code == 3 or "RELEASE_NO_TEST_HOOK" in text，这是个真实的假绿：
    查过全部探针源码后确认，退出码 3 有两个完全不同的含义 ——

      1. 真跳过：colorprobe / portable 打印 RELEASE_NO_TEST_HOOK 后返回 3；
      2. **真错误**：dataprobe / menuprobe / scopeprobe / shellcheck / iconprobe
         在 LoadLibrary 失败 或 DllGetClassObject 返回非 0 HRESULT 时返回 3。

    用 code == 3 一律当跳过，等于把「外壳组件加载失败」这种致命问题
    伪装成「Release 没有测试钩子，正常跳过」。实测就是这样：Release 下
    四个外壳探针全部「跳过」，而直接手工跑同一个探针是 rc=0 正常的。

    现在只认标记：没有标记的退出码 3 一律算失败，由 judge 报出来。
    """
    return "RELEASE_NO_TEST_HOOK" in text


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

    r = add("hashprobe", "计算服务（HashingService：18 种算法 / 格式 / 递归 / 进度）")
    if ensure_tool(r):
        code, text = run(tool("hashprobe"), [app, work])
        r.raw, r.code = text, code
        if is_release_skip(code, text):
            r.status, r.detail = "跳过", "Release 构建无测试钩子"
        else:
            r.status, r.detail = judge(text, code)

    r = add("jsonrt", "配置 JSON 往返（写入/解析/类型保持/边界校验）")
    if ensure_tool(r):
        code, text = run(tool("jsonrt"), [app, work])
        r.raw, r.code = text, code
        if is_release_skip(code, text):
            r.status, r.detail = "跳过", "Release 构建无测试钩子"
        else:
            r.status, r.detail = judge(text, code)

    r = add("crosspollute", "跨任务状态不污染（压缩↔解压的 LastTaskFailed 隔离）")
    if ensure_tool(r):
        code, text = run(tool("crosspollute"), [app, work])
        r.raw, r.code = text, code
        if is_release_skip(code, text):
            r.status, r.detail = "跳过", "Release 构建无测试钩子"
        else:
            r.status, r.detail = judge(text, code)

    r = add("nullhard", "HashAlgorithmInfo 的 Aliases/Extensions 设 null 后仍可安全遍历")
    if ensure_tool(r):
        code, text = run(tool("nullhard"), [app])
        r.raw, r.code = text, code
        if is_release_skip(code, text):
            r.status, r.detail = "跳过", "Release 构建无测试钩子"
        else:
            r.status, r.detail = judge(text, code)

    r = add("batchprog", "批量进度不跨任务残留（GUI/命令行两条入口一致）")
    if ensure_tool(r):
        code, text = run(tool("batchprog"), [app, work])
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

    r = add("chromecheck", "无边框窗口标题栏命令绑定（关闭/最小化）")
    if ensure_tool(r):
        code, text = run(tool("chromecheck"), [work, app])
        r.raw, r.code = text, code
        if is_release_skip(code, text):
            r.status, r.detail = "跳过", "Release 构建无测试钩子"
        else:
            r.status, r.detail = judge(text, code)

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
        # 这里原本直接用了 text / code 两个变量，但**它们从未在本循环里赋值** ——
        # 那是上一个探针（taborder）遗留的局部变量。于是四个外壳探针：
        #   · 实际从未被执行；
        #   · 拿到的是上一个探针的退出码与输出，判成 rc=3 失败；
        #   · 而 rc=3 又曾被 is_release_skip 当成「Release 跳过」，
        #     所以这个缺陷长期不可见 —— 两道错误互相掩盖。
        code, text = run(tool(name), argv)
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
    fails = passes = skips = unrun = 0
    for r in results:
        if r.status == "失败":
            fails += 1
            mark = "[失败]"
        elif _EXECUTIONS.count(r.name) <= r.runs_before:
            # 状态有值，但从创建到现在**一次 run() 都没发生** ——
            # 说明这个结果是拿别的探针 leftover 变量填的，实际根本没执行。
            unrun += 1
            fails += 1
            mark = "[未执行]"
        elif r.status == "未执行":
            # 「加了 add() 却没给它赋值」= 代码路径没走到，这是缺陷，不是跳过。
            # 单独计数并计入失败，否则它会伪装成一次正常跳过（曾经发生过）。
            unrun += 1
            fails += 1
            mark = "[未执行]"
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
    if unrun:
        print(" 合计 %d 项：通过/完成 %d，失败 %d（其中未执行 %d），跳过 %d"
              % (len(results), passes, fails, unrun, skips))
    else:
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

    safe_rmtree(work)
    safe_rmtree(sample_dir)
    return 0 if fails == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
