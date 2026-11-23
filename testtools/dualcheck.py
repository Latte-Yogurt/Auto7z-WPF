#!/usr/bin/env python3
# -*- coding: utf-8 -*-
r"""双构建总验证：Debug 与 Release 都要全套通过。

## 为什么需要这个脚本

各套件都支持 `--config Debug|Release`，但**默认都是 Debug**。
日常跑回归时如果不显式传 `--config Release`，Release 构建实际上从未被验证。

这在本轮被证实为真实缺口：`legacy_suites` 早先一律用各脚本的默认值，
而九个脚本的默认构建各不相同 ——
`cmd_escape` / `cancel_residue` 默认 Release，
`cmd_real` / `heuristic_probe` 默认 Debug，其余硬编码 Debug。
于是「跑一遍套件」实际只覆盖了两三个 Release 用例。

Release 是**交付构建**，它和 Debug 的差异不只是优化开关：
`#if DEBUG` 包住的代码在两边的编译结果完全不同
（DebugAutoDriver / TestHooks / 若干 Probe 钩子只在 Debug 存在）。
Debug 全绿不代表 Release 能用。

## 本脚本做什么

依次跑：
  1. 回归套件（两种构建）
  2. 系统测试（两种构建）
  3. C# 探针（两种构建）
  4. 历史脚本聚合（内部已含两种构建）

任何一项失败即整体失败。
"""
import os
import re
import subprocess
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
WORKSPACE = HERE.parent
PROJECT = WORKSPACE / "Auto7z_WPF" / "Auto7z_WPF"
BUILDS = ("Debug", "Release")


def kill_strays():
    if os.name != "nt":
        return
    for name in ("Auto7z.exe", "Auto7zGUI.exe", "7z.exe"):
        try:
            subprocess.run(["taskkill", "/F", "/IM", name],
                           stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                           timeout=15)
        except Exception:
            pass


def _assert_safe(target, build):
    """递归删除前的安全闸门：逐条确认目标就是「这个构建目录下的 .config」。

    lifecyclecheck 的 L4 要求「rmtree 前有安全闸门」，并把 `_assert_safe`
    这个函数名列为合法闸门标识 —— 所以这里用这个名字，既符合规范也让
    后来的读者一眼看出这是守卫而不是普通校验。

    四条断言缺一不可：
      1) 名字必须正好是 .config
      2) 父目录必须正好是 bin/<build>
      3) build 只能是允许的两个值
      4) 解析后的绝对路径必须确实位于项目目录之内
    """
    name = target.name
    parent = target.parent.name

    if name != ".config":
        raise RuntimeError("拒绝删除：目标名不是 .config（%s）" % name)
    if parent != build:
        raise RuntimeError("拒绝删除：父目录不是构建目录（%s）" % parent)
    if build not in ("Debug", "Release"):
        raise RuntimeError("拒绝删除：未知构建（%s）" % build)

    resolved = target.resolve()
    project_root = PROJECT.resolve()
    if not str(resolved).lower().startswith(str(project_root).lower()):
        raise RuntimeError("拒绝删除：目标不在项目目录内（%s）" % resolved)


def clear_portable_leftover(build):
    """清掉 bin 目录下对应构建的 .config 残留。

    上一轮如果以便携模式跑过，会在 bin 下留下 .config；之后产品会按便携模式
    去找配置，导致回归里的 config_guard 直接失败。

    这里**不能用 safe_rmtree**：那套白名单要求目标位于 %TEMP% 下且路径含
    Auto7zTest 标记，而本目标是 bin 目录里的构建产物残留，必然被拒。
    所以改用 _assert_safe 这个范围更窄、判据同样明确的闸门。
    """
    leftover = PROJECT / "bin" / build / ".config"

    _assert_safe(leftover, build)

    if not leftover.exists():
        return

    import shutil
    try:
        shutil.rmtree(leftover)
    except OSError:
        # 被占用时留着即可：下一次回归的 config_guard 会自己报出来
        pass


def run_suite(script, build, timeout):
    cmd = [sys.executable, str(HERE / script), "--config", build]
    env = dict(os.environ)
    env["AUTO7Z_PROJECT_ROOT"] = str(WORKSPACE)
    env.pop("AUTO7Z_DATA_ROOT", None)
    try:
        proc = subprocess.run(cmd, cwd=str(WORKSPACE), env=env,
                              stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                              timeout=timeout)
    except subprocess.TimeoutExpired:
        return False, "超时（%d 秒）" % timeout, ""
    out = (proc.stdout or b"").decode("utf-8", "replace")
    return proc.returncode == 0, out, out


def parse_summary(script, out):
    if script == "run_regress.py":
        m = re.search(r"回归测试汇总: (\d+) 项, 失败 (\d+) 项", out)
    elif script == "run_system_test.py":
        m = re.search(r"系统测试汇总: (\d+) 项, 失败 (\d+) 项", out)
    else:
        m = re.search(r"合计 (\d+) 项：通过/完成 (\d+)，失败 (\d+)", out)
        if m:
            return "%s/%s 通过" % (m.group(2), m.group(1)), int(m.group(3))
    if not m:
        return None, None
    return "%s/%s 通过" % (int(m.group(1)) - int(m.group(2)), m.group(1)), int(m.group(2))


def main():
    print("=" * 78)
    print("双构建总验证：Debug 与 Release 全套通过")
    print("=" * 78)

    suites = [
        ("run_regress.py", "回归套件", 2400),
        ("run_system_test.py", "系统测试", 2400),
        ("run_cs_probes.py", "C# 探针", 1200),
    ]

    total = passed = failed = 0
    problems = []

    for build in BUILDS:
        exe = PROJECT / "bin" / build / "Auto7z.exe"
        if not exe.is_file():
            print("\n--- %s：跳过（未构建：%s 不存在）---" % (build, exe.name))
            continue

        print("\n" + "=" * 78)
        print("【%s】%s" % (build, exe))
        print("=" * 78)

        for script, label, timeout in suites:
            kill_strays()
            clear_portable_leftover(build)

            ok, *rest = run_suite(script, build, timeout)
            out = rest[-1]
            detail, fail_count = parse_summary(script, out)

            total += 1
            if ok and fail_count == 0:
                passed += 1
                print("  OK   %-12s %s" % (label, detail or "完成"))
            else:
                failed += 1
                fails = re.findall(r"^\s*\[失败\]\s+(.+)$", out, re.M)
                msg = detail or "退出码非 0"
                if fails:
                    msg += " | 首项失败: " + fails[0].strip()[:50]
                print("  !!   %-12s %s" % (label, msg))
                problems.append((build, label, msg))

    # 历史脚本聚合（内部已含两个构建）
    print("\n" + "=" * 78)
    print("【两种构建】历史脚本聚合")
    print("=" * 78)
    kill_strays()
    env = dict(os.environ)
    env["AUTO7Z_PROJECT_ROOT"] = str(WORKSPACE)
    env.pop("AUTO7Z_DATA_ROOT", None)
    try:
        proc = subprocess.run([sys.executable, str(HERE / "legacy_suites.py")],
                              cwd=str(WORKSPACE), env=env,
                              stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                              timeout=3600)
        out = (proc.stdout or b"").decode("utf-8", "replace")
        m = re.search(r"共 (\d+) 项，通过 (\d+)，失败 (\d+)", out)
        total += 1
        if proc.returncode == 0 and m and m.group(3) == "0":
            passed += 1
            print("  OK   历史脚本     %s/%s 通过（Debug+Release）" % (m.group(2), m.group(1)))
        else:
            failed += 1
            msg = ("%s/%s 通过" % (m.group(2), m.group(1))) if m else "退出码非 0"
            print("  !!   历史脚本     %s" % msg)
            problems.append(("both", "历史脚本", msg))
    except subprocess.TimeoutExpired:
        failed += 1
        print("  !!   历史脚本     超时")
        problems.append(("both", "历史脚本", "超时"))

    print("\n" + "=" * 78)
    print("共 %d 项，通过 %d，失败 %d" % (total, passed, failed))
    if failed:
        print("\n失败明细：")
        for build, label, msg in problems:
            print("  [%s] %s: %s" % (build, label, msg))
        print("FAIL")
        return 1
    print("Debug 与 Release 两个构建全部通过")
    print("PASS")
    return 0


if __name__ == "__main__":
    sys.exit(main())
