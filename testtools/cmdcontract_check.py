#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""命令行参数解析的行为契约（自动化）。

覆盖 2.5.1 修复的一项真实缺陷：
    早先 `-z -uz` 同时给出时，**后者静默覆盖前者** —— 程序按 -uz 执行，
    而用户以为自己给了两个互斥开关、程序应当报错。
    命令行的契约是「参数写错必须让调用方知道」，静默按最后一个执行是错的。

同时守住几条既有承诺（README 里写着）：
    - 开关大小写不敏感（-A 与 -a 等价）
    - 磁盘上真有以 - 开头的文件时按路径处理，不报成未知开关
    - 未知开关必须报错并以退出码 2 结束

安全约束：**跳过 -r / -ur** —— 它们会走 RunElevated 弹 UAC 并写 HKLM，
自动化里一旦弹出 UAC，进程会一直等到用户点确认（实测 120s 超时）。
"""
import os
import shutil
import subprocess
import sys

from safe_ops import safe_rmtree

P = sys.argv[1] if len(sys.argv) > 1 else r"C:\Users\Mocha\Desktop\VSProject\Auto7z-WPF"
if os.path.isdir(os.path.join(P, "Auto7z_WPF", "Auto7z_WPF")):
    ROOT = P
else:
    ROOT = os.path.dirname(os.path.dirname(P))

EXE = os.path.join(ROOT, "Auto7z_WPF", "Auto7z_WPF", "bin", "Debug", "Auto7z.exe")
WORK = os.path.join(os.environ.get("TEMP", r"C:\Windows\Temp"), "Auto7zTest_data", "cmdcontract")

problems = 0


def check(name, ok, detail=""):
    global problems
    print(("  OK   " if ok else "  !!   ") + name + ("  --- " + detail if detail else ""))
    if not ok:
        problems += 1


def run(args, cwd):
    env = dict(os.environ)
    env["AUTO7Z_DATA_ROOT"] = os.path.join(WORK, "data")
    env["AUTO7Z_AUTOANSWER"] = "1"
    try:
        p = subprocess.run([EXE] + args, capture_output=True, env=env, cwd=cwd, timeout=60)
    except subprocess.TimeoutExpired:
        return None, "(超时)", None
    out = (p.stdout or b"").decode("utf-8", "replace") + (p.stderr or b"").decode("utf-8", "replace")
    return p.returncode, out, p


def main():
    if not os.path.isfile(EXE):
        print("!! 找不到 %s（先构建 Debug）" % EXE)
        return 2

    work = os.path.join(WORK, "work")
    # 走 safe_ops 的统一守卫（TEST_LIFECYCLE.md L4）：校验目标在 %TEMP% 下、
    # 带标记串、不是受控根本身、不含源码文件。裸 rmtree 会被 lifecyclecheck 判违规。
    if os.path.isdir(work):
        safe_rmtree(work)
    os.makedirs(work, exist_ok=True)
    os.makedirs(os.path.join(WORK, "data"), exist_ok=True)

    sample = os.path.join(work, "sample.txt")
    open(sample, "w", encoding="utf-8").write("hello\n")

    print("=" * 72)
    print("命令行参数解析契约")
    print("=" * 72)

    # ---- 1) 互斥开关冲突必须报错，不能静默取后者 ----
    print("\n【1】互斥开关冲突")
    for label, args in [
        ("-z -uz", ["-z", "-uz", sample]),
        ("-uz -z", ["-uz", "-z", sample]),
        ("-a -z", ["-a", "-z", sample]),
        ("-a -uz", ["-a", "-uz", sample]),
    ]:
        rc, out, _ = run(args, work)
        conflict_reported = "不能同时使用" in out
        check("%s 报冲突" % label, conflict_reported and rc == 2,
              "" if conflict_reported else "输出: " + out.strip().replace("\n", " | ")[:80])

    # 重复同一个开关不算冲突
    rc, out, _ = run(["-z", "-z", sample], work)
    no_conflict = "不能同时使用" not in out
    check("-z -z 不报冲突（同一开关重复）", no_conflict,
          "" if no_conflict else "输出: " + out.strip().replace("\n", " | ")[:80])

    # ---- 2) 未知开关必须报错 ----
    print("\n【2】未知开关")
    for label, args in [
        ("-xyz", ["-xyz", sample]),
        ("--help", ["--help", sample]),
        ("/A", ["/A", sample]),
    ]:
        rc, out, _ = run(args, work)
        reported = "无法识别" in out or "未知" in out
        check("%s 被拒绝" % label, reported and rc == 2,
              "" if reported else "输出: " + out.strip().replace("\n", " | ")[:80])

    # ---- 3) 大小写不敏感 ----
    print("\n【3】大小写不敏感（README 承诺）")
    rc_l, out_l, _ = run(["-h"], work)
    rc_u, out_u, _ = run(["-H"], work)
    check("-h 与 -H 行为一致", rc_l == rc_u and rc_l == 0,
          "-h exit=%s -H exit=%s" % (rc_l, rc_u))

    rc_l2, out_l2, _ = run(["-z", sample], work)
    rc_u2, out_u2, _ = run(["-Z", sample], work)
    check("-z 与 -Z 行为一致", rc_l2 == rc_u2,
          "-z exit=%s -Z exit=%s" % (rc_l2, rc_u2))

    # ---- 4) 以 - 开头但磁盘存在的文件按路径处理 ----
    print("\n【4】以 - 开头但磁盘真有同名文件")
    dash = os.path.join(work, "-dashfile.txt")
    open(dash, "w", encoding="utf-8").write("dash\n")
    rc, out, _ = run(["-dashfile.txt"], work)
    treated_as_path = "无法识别" not in out and "未知" not in out
    check("按路径处理而非报未知开关", treated_as_path,
          "" if treated_as_path else "输出: " + out.strip().replace("\n", " | ")[:80])

    # ---- 5) 安全开关都能正常分发 ----
    print("\n【5】白名单开关分发（跳过 -r/-ur：会弹 UAC）")
    for sw in ("-a", "-z", "-uz", "-h"):
        rc, out, _ = run([sw], work)
        not_unknown = "无法识别" not in out and "不能同时使用" not in out
        check("%s 正常分发" % sw, not_unknown,
              "" if not_unknown else "输出: " + out.strip().replace("\n", " | ")[:80])

    print("\n" + "=" * 72)
    if problems:
        print("发现 %d 项不符合契约" % problems)
        print("FAIL")
        return 1
    print("全部符合命令行契约")
    print("PASS")
    return 0


if __name__ == "__main__":
    sys.exit(main())
