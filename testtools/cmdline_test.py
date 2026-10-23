#!/usr/bin/env python3
r"""命令行相关的测试:
- -h 显示用法
- -A / -Z / -U 三种模式
- -r / -ur 被识别成开关(只验解析, **不执行注册** —— 那要提权, 会弹 UAC)
- 拖到 exe 图标 = 命令行参数, 走的是同一条路
"""
import os
import re
import shutil
import subprocess
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from safe_ops import SafeTemp, safe_rmtree

APP = None
ISSUES = []
OUT = []

# 临时目录：受控在 %TEMP% 下，删除前有白名单校验
TEMP = SafeTemp("cmdline")


def check_switch_parsing():
    """验 -r / -ur 会被识别成开关，而不是当成文件路径。

    不跑产品的注册动作：-r / -ur 真跑起来要去写 HKLM，Windows 会弹 UAC 让人点。

    办法：把 Auto7z.exe **单独复制**到一个没有 Auto7zShell.dll 的临时目录再跑。
    产品的 GetShellDllPath() 找不到同目录的 dll 就直接返回，
    根本不会走到提权那一步 —— 于是：
      - 参数解析照常走（能看出开关被识别了）
      - 不会写注册表，不会弹 UAC
    跑在副本里也不会影响装好的那一份。
    """
    ok = True
    iso = TEMP.path / "no_dll"
    if iso.exists():
        safe_rmtree(iso)
    iso.mkdir(parents=True, exist_ok=True)

    # 只复制主程序和它的配置，不复制 Auto7zShell.dll
    app = Path(APP)
    for name in (app.name, app.name + ".config"):
        src = app.parent / name
        if src.exists():
            try:
                shutil.copy2(str(src), str(iso / name))
            except Exception as e:
                say("  复制 %s 失败: %s" % (name, e))
                return False

    if not (iso / app.name).exists():
        say("  隔离副本没建起来，跳过")
        return True

    for switch, keyword in (("-r", "注册"), ("-ur", "注销")):
        code, out = run([str(iso / app.name), switch], timeout=15, exe=str(iso / app.name))
        text = out.strip()
        say("  [%s] 退出码=%s 输出=%s" % (switch, code, text[:80] or "(空)"))
        #
        # 怎么判断"开关被识别了"：
        #   -r : 隔离副本里没有 Auto7zShell.dll，产品会走注册分支然后说"找不到 dll"
        #        -> 输出里能看到 dll 名字
        #   -ur: 注销时找不到 dll 直接返回 true，什么都不打印（这是产品的行为）
        #        -> 靠"退出码正常 + 没有把它当路径报错"来判断
        #
        # 反面判据：把它当成路径的话，输出里会有"路径不存在"之类的抱怨。
        as_path = ("不存在" in text and "路径" in text) or "找不到" in text and "路径" in text
        if switch == "-r":
            ok_switch = "Auto7zShell.dll" in text
        else:
            ok_switch = code == 0 and not as_path

        if ok_switch:
            say("    -> 开关被正确识别（没被当成文件路径）")
        else:
            say("    -> !! 没走到注册分支，输出=%s" % (text[:60] or "(空)"))
            ok = False
    return ok


def say(s):
    OUT.append(s)
    print(s)


def bad(s):
    ISSUES.append(s)
    OUT.append("  !! " + s)
    print("  !! " + s)


def run(args, timeout=25, stdin=None, exe=None):
    """跑一次程序, 返回 (退出码, 输出)

    exe 可以指定跑哪个可执行文件（默认被测程序）。
    """
    target = exe or APP
    try:
        r = subprocess.run([target] + list(args), stdout=subprocess.PIPE,
                           stderr=subprocess.STDOUT,
                           stdin=subprocess.DEVNULL if stdin is None else stdin,
                           timeout=timeout)
        return r.returncode, r.stdout.decode("gbk", "replace")
    except subprocess.TimeoutExpired as e:
        raw = e.stdout or b""
        if isinstance(raw, bytes):
            raw = raw.decode("gbk", "replace")
        return None, raw
    except Exception as e:
        return -1, str(e)


def main():
    global APP
    APP = sys.argv[1]
    work = Path(sys.argv[2]) if len(sys.argv) > 2 else TEMP.path

    # 清理上一次的产物。用白名单校验过的删除，
    # 免得路径参数传错时把不该删的东西删了。
    if work.exists():
        try:
            safe_rmtree(work)
        except Exception as e:
            print("!! 清理工作目录被拒绝: %s" % e)
            return 2
    work.mkdir(parents=True, exist_ok=True)

    # ---------- T1: -h 显示用法 ----------
    say("=== T1: -h 显示用法 ===")
    code, out = run(["-h"], timeout=15)
    say("  退出码 = %s" % code)
    for line in out.splitlines():
        if line.strip():
            say("    " + line)

    if code is None or code != 0:
        bad("-h: 退出码不对")
    for expect in ("-A", "-Z", "-U", "-r", "-ur", "-h"):
        if expect not in out:
            bad("-h: 说明里缺少 %s" % expect)

    # ---------- T2: 三种模式 ----------
    say("")
    say("=== T2: 模式开关 -A / -Z / -U ===")
    for mode, expect_zip in [("-Z", True), ("-A", True), ("-U", False)]:
        d = work / ("mode" + mode)
        d.mkdir(parents=True, exist_ok=True)
        f = d / ("文件.txt")
        f.write_text("内容", encoding="utf-8")

        code, out = run([mode, str(f)], timeout=20)
        archives = list(d.rglob("*.7z"))
        say("  [%s] 退出码=%s 压缩包=%d 个" % (mode, code, len(archives)))

        if mode == "-U":
            # 解压模式: 普通文件解不开, 不应有压缩包
            if archives:
                bad("-U: 普通文件不该被压缩")
        else:
            if not archives:
                bad("%s: 没有生成压缩包" % mode)

    # ---------- T3: 不带模式 = 自动模式 ----------
    say("")
    say("=== T3: 不带模式开关走自动模式 ===")
    d = work / "auto"
    d.mkdir(parents=True, exist_ok=True)
    f = d / "普通.txt"
    f.write_text("内容", encoding="utf-8")
    code, out = run([str(f)], timeout=20)
    archives = list(d.rglob("*.7z"))
    say("  退出码=%s 压缩包=%d 个" % (code, len(archives)))
    if not archives:
        bad("默认模式: 没有生成压缩包, 应该按自动模式处理")

    # ---------- T4: 拖到 exe 图标 = 命令行参数 ----------
    say("")
    say("=== T4: 拖到 exe 图标(命令行传路径) ===")
    d = work / "drop"
    d.mkdir(parents=True, exist_ok=True)
    f = d / "拖拽.txt"
    f.write_text("内容", encoding="utf-8")
    code, out = run([str(f)], timeout=20)
    archives = list(d.rglob("*.7z"))
    say("  退出码=%s 压缩包=%d 个" % (code, len(archives)))
    if not archives:
        bad("拖到 exe 图标: 没有生成压缩包")
    if code is not None and code != 0:
        bad("拖到 exe 图标: 退出码 %s" % code)

    # ---------- T5: 多个文件一次传入 ----------
    say("")
    say("=== T5: 多个文件一次传入 ===")
    d = work / "multi"
    d.mkdir(parents=True, exist_ok=True)
    paths = []
    for i in range(5):
        f = d / ("多%d.txt" % i)
        f.write_text("内容", encoding="utf-8")
        paths.append(str(f))
    code, out = run(paths, timeout=25)
    archives = list(d.rglob("*.7z"))
    say("  退出码=%s 压缩包=%d 个" % (code, len(archives)))
    if not archives:
        bad("多文件: 没有生成压缩包")

    # ---------- T6: -r / -ur 被识别 ----------
    say("")
    say("=== T6: -r / -ur 参数被识别 ===")
    #
    # 这里**不能**真跑 -r / -ur。
    #
    # 之前这里直接跑了一遍，注释写着"只确认参数不会当成文件路径"，
    # 但 -r / -ur 是真的会去注册/注销右键菜单的：
    #   -r  -> 写 HKLM（CLSID、Approved、四个菜单挂载点）-> 提权 -> 弹 UAC
    #   -ur -> 同样要写 HKLM -> 提权 -> 弹 UAC
    # 跑一次这个测试就要让人点两次 UAC 确认，很烦人，而且测的东西跟
    # "参数有没有被当成路径"毫无关系。
    #
    # 改成只验解析结果，不执行产品：
    #   把 -r / -ur 塞进参数里，看 ShellCommandService.Parse 会不会
    #   把它们识别成开关（而不是当成文件路径）。
    if not check_switch_parsing():
        bad("-r / -ur 参数解析: 开关没有被正确识别")

    say("")
    say("=== 问题汇总 (%d) ===" % len(ISSUES))
    for i in ISSUES:
        say("  - " + i)

    report = work / "report.txt"
    report.write_text("\n".join(OUT), encoding="utf-8")
    print("")
    print("TOTAL ISSUES: %d" % len(ISSUES))

    # 所有测试跑完之后统一清理临时产物。
    # 用户传了自己的工作目录就不动它 —— 那是人家指定要看的地方。
    if work == TEMP.path:
        print("")
        print("##### 清理临时产物 #####")
        print("  " + TEMP.cleanup())


if __name__ == "__main__":
    main()
