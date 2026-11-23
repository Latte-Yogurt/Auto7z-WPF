#!/usr/bin/env python3
# -*- coding: utf-8 -*-
r"""把此前「写好但从未接入」的 9 个测试脚本纳入回归。

## 为什么要专门做这件事

2026-11-23 做了一次「检查器是否真的在跑」的审计，发现 19 个测试工具
从未被任何套件引用。逐个核实后，其中 **9 个是真测试脚本**（有 main + 退出码），
它们写好了却孤立地放在目录里，等于不存在。

审计暴露了三个真实问题，全都因为「没人跑」而长期潜伏：

1. **4 个脚本直接崩溃** —— `NameError: safe_rmtree is not defined`。
   某次把 `shutil.rmtree` 批量换成 `safe_rmtree` 时漏了 import。
   如果它们被接入过套件，第一次跑就会暴露。

2. **`password_adopt_test` 挖出一个 P1 产品缺陷** ——
   `AskPasswordAndRetry` 里提前执行了 `UnzipPassword = password;`，
   而该属性是纯内存的（setter 只发通知、不写盘）。于是
   `AdoptVerifiedPassword` 里「值已一致就跳过写盘」的优化误判为已保存，
   配置文件**永远不会被更新**：内存是新密码、磁盘是旧密码，
   下次启动又拿旧密码去试。实测复现（解压成功 rc=0 但配置仍是旧值），
   修复后密码正确回写。

3. **`cmd_real_test` 的 4 项断言是「假失败」** ——
   Runner 没设 `AUTO7Z_DATA_ROOT`、也没写配置文件，于是产品用了默认格式
   **tar.zst**，而断言在找 `*.7z`。表现是「退出码 0、末行『完成：压缩完成』、
   产物=0」，看起来像压缩失败，其实是后缀和断言对不上。

结论：**「写好了」不等于「在跑」。** 本模块把这 9 个脚本逐个接入。
"""
import io
import os
import re
import subprocess
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
WORKSPACE = HERE.parent
PROJECT_APP = WORKSPACE / "Auto7z_WPF" / "Auto7z_WPF" / "bin" / "Debug" / "Auto7z.exe"

# 逐个脚本的执行方式。有些需要额外参数，有些输出格式不同。
CASES = [
    # (文件名, 支持哪个参数, 判定正则, 说明)
    #
    # 「支持哪个参数」这一列是必要的：九个脚本是不同时期写的，
    # 指定被测构建的方式不统一 —— 有的认 --config，有的认 --app，
    # 有的两者都不认（硬编码 Debug）。早先本模块一律用默认值，
    # 结果**回归里只测了各脚本自己的默认构建**：
    # cmd_escape / cancel_residue 默认 Release，cmd_real / heuristic 默认 Debug，
    # 其余干脆写死 Debug。于是 Release 构建实际上只被两个脚本测到。
    ("roundtrip_test.py", "none", r"全部一致", "压缩→解压→SHA256 内容一致性"),
    ("password_adopt_test.py", "none", r"全部通过", "解压成功后正确密码必须回写配置"),
    ("extract_partial_test.py", "app", r"全部通过", "批量解压失败结论不被后续成功覆盖"),
    ("cmd_escape_test.py", "config", r"全部通过", "命令行参数转义与格式矩阵"),
    ("cmd_real_test.py", "app", r"全部通过", "真实 cmd 语义（含退出码与相对路径）"),
    ("archive_security_test.py", "none", r"失败 0", "恶意归档与文件系统边界"),
    ("overwrite_resource_test.py", "none", r"全部通过", "覆盖行为与资源保护"),
    ("cancel_residue_test.py", "config", r"全部通过", "取消/强杀后的残留检查"),
    ("heuristic_probe.py", "app", r"0 条", "启发式探测：找不符合预期设计的行为"),
]


def ensure_components(data_root, app):
    """先跑一次产品，让它把 7z.exe / 7z.dll 释放到 data_root 下。

    为什么需要：部分脚本（如 cmd_real_test 的加密用例）要直接用 7z.exe 构造测试包，
    而组件是产品首次运行时才释放的。数据根若是全新的空目录，
    那些脚本会报「找不到 7z.exe，无法构造加密包」——
    看起来像脚本坏了，其实只是没预热。
    """
    marker = Path(data_root) / "Auto7z" / "7-Zip" / "7z.exe"
    if marker.exists():
        return True

    warm = Path(data_root) / "_warm"
    warm.mkdir(parents=True, exist_ok=True)
    seed = warm / "seed.txt"
    seed.write_text("warmup", encoding="utf-8")
    env = dict(os.environ)
    env["AUTO7Z_DATA_ROOT"] = str(data_root)
    try:
        subprocess.run([str(app), "-z", str(seed)], cwd=str(warm), env=env,
                       stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                       timeout=300)
    except Exception:
        return False
    return marker.exists()


def run_one(name, extra, pattern, build="Debug", support="none"):
    script = HERE / name
    if not script.is_file():
        return False, "脚本缺失"

    # 必须给每个子脚本**自己的**隔离数据根。
    #
    # 早先这里是 env.pop("AUTO7Z_DATA_ROOT")，结果在回归里跑时子脚本
    # 继承了回归自己的 AUTO7Z_DATA_ROOT（regress/ 那个共享目录），
    # 而那个目录正被回归主流程占用 —— 产品的单实例互斥会把子脚本挡掉，
    # 表现为「独立跑 9/9 通过、在回归里却退出码 1」。
    # 必须声明「互斥闸门已由父进程持有」。
    #
    # heuristic_probe.py 等脚本会在开头 claim_checkout() 拿一个全局命名互斥体，
    # 防止两份测试并行互相抢配置与单实例。本模块是被回归**嵌套调用**的，
    # 回归主进程已持有那个互斥体，子脚本一拿就失败，然后打印
    # 「已有另一份 Auto7z 测试正在跑，先等它结束」并返回 2 ——
    # 表现是「独立跑 3 次全通过、在回归里必失败」。
    #
    # config_guard 本来就提供了嵌套调用的正确姿势：父进程通过这个环境变量
    # 声明锁已持有，子进程的 claim_checkout() 直接返回占位句柄 0。
    # 这是官方支持的路径，不是绕过保护。
    env = dict(os.environ)
    env["AUTO7Z_TEST_CHECKOUT_INHERITED"] = "1"
    env["AUTO7Z_PROJECT_ROOT"] = str(WORKSPACE)
    env["AUTO7Z_DATA_ROOT"] = str(HERE.parent / "tmp" / "legacy_data" / Path(name).stem)
    Path(env["AUTO7Z_DATA_ROOT"]).mkdir(parents=True, exist_ok=True)

    # 预热：确保该数据根里已有 7z 组件，否则依赖它的脚本会误报失败
    app = PROJECT_APP if PROJECT_APP.is_file() else None
    if app is not None:
        ensure_components(env["AUTO7Z_DATA_ROOT"], app)

    # 按脚本支持的参数形式指定被测构建，统一 driver 的覆盖范围
    app_path = WORKSPACE / "Auto7z_WPF" / "Auto7z_WPF" / "bin" / build / "Auto7z.exe"
    cmd = [sys.executable, str(script)] + extra
    if support == "config":
        cmd += ["--config", build]
    elif support == "app":
        cmd += ["--app", str(app_path)]

    try:
        proc = subprocess.run(
            cmd,
            cwd=str(WORKSPACE), env=env,
            stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
            timeout=900,
        )
    except subprocess.TimeoutExpired:
        return False, "超时（900 秒）"

    out = (proc.stdout or b"").decode("utf-8", "replace")
    tail = out.strip().splitlines()[-1] if out.strip() else "(无输出)"

    # 崩溃检测：Python 异常栈比「退出码非 0」更明确
    if "Traceback (most recent call last)" in out:
        m = re.search(r"^(\w*Error): (.+)$", out, re.M)
        return False, "崩溃: %s" % (m.group(0) if m else "未知异常")

    if re.search(pattern, out) and proc.returncode == 0:
        return True, tail[:70]

    # 失败时把完整输出落盘：只看一行摘要无法定位是哪一环出问题
    try:
        dump = HERE / ("legacy_fail_" + Path(name).stem + ".log")
        dump.write_text(out, encoding="utf-8")
    except Exception:
        pass

    fails = re.findall(r"^\s*-\s+(.+)$", out, re.M)
    if fails:
        return False, "失败 %d 项: %s" % (len(fails), fails[0][:50])
    return False, "退出码=%d 末行=%s" % (proc.returncode, tail[:60])


def run_one_with_retry(name, extra, pattern, build="Debug", support="none"):
    """失败时重试一次。

    为什么需要重试：本组脚本里有依赖**单实例互斥**与**独占文件占用**的用例
    （heuristic_probe 的单实例交接、cmd_real_test 的占用快速返回）。
    在回归里跑时前面刚有别的测试起进程，互斥锁或句柄可能还没完全释放 ——
    表现为「独立跑 3 次全通过、在回归里偶发 1 项失败」。

    这类失败不是产品问题，也不是脚本问题，是测试间的最小间隔不足。
    重试一次能吃掉这个抖动；若重试仍失败，那就是真问题，照常报出来。
    """
    ok, detail = run_one(name, extra, pattern, build, support)
    if ok:
        return True, detail

    # 清掉可能残留的进程再试
    try:
        if os.name == "nt":
            for name_ in ("Auto7z.exe", "Auto7zGUI.exe", "7z.exe"):
                subprocess.run(["taskkill", "/F", "/IM", name_],
                               stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                               timeout=15)
    except Exception:
        pass

    import time
    time.sleep(2)
    ok2, detail2 = run_one(name, extra, pattern, build, support)
    if ok2:
        return True, "重试后通过（首次: %s）" % detail[:40]
    return False, detail2


def main():
    print("=" * 74)
    print("历史测试脚本接入检查（9 个此前从未被套件调用的脚本）")
    print("=" * 74)
    print("覆盖 Debug 与 Release 两个构建")

    total = passed = failed = 0
    problems = []

    for build in ("Debug", "Release"):
        app = WORKSPACE / "Auto7z_WPF" / "Auto7z_WPF" / "bin" / build / "Auto7z.exe"
        if not app.is_file():
            print("\n--- %s：跳过（未构建）---" % build)
            continue

        print("\n--- %s ---" % build)
        for name, support, pattern, desc in CASES:
            ok, detail = run_one_with_retry(name, [], pattern, build, support)
            total += 1
            mark = "  OK   " if ok else "  !!   "
            print("%s%-28s %s" % (mark, name, detail))
            if ok:
                passed += 1
            else:
                failed += 1
                problems.append((build, name, desc, detail))

    print("")
    print("=" * 74)
    print("共 %d 项，通过 %d，失败 %d" % (total, passed, failed))

    if failed:
        print("")
        print("失败明细：")
        for build, name, desc, detail in problems:
            print("  [%s] %s (%s)" % (build, name, desc))
            print("      %s" % detail)
        print("FAIL")
        return 1

    print("PASS")
    return 0


if __name__ == "__main__":
    sys.exit(main())
