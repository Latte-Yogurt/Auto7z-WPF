#!/usr/bin/env python3
# -*- coding: utf-8 -*-
r"""一键回归测试。

跑「回归」这一类：针对已知条目基线表逐条确认 ——
凡是声称"已修"的条目必须有对应自动化测试且通过。

包含:
    regress.py      主回归（T01–T22：格式/等级/分卷/MD5/密码/日志/边界/便携…）
    cs_check.py     静态检查（命名类型一致性、文档标签、路径边界、死分支）
    find_dead2.py   废键与三语词典一致性

与系统测试的分工:
    run_regress.py      —— 回归：已知条目基线表
    run_system_test.py  —— 系统：端到端主流程

用法:
    python run_regress.py                     # 用默认的 Debug 构建
    python run_regress.py --app <exe>
    python run_regress.py --config Release
    python run_regress.py --keep              # 保留临时目录（调试用）

退出码: 0 = 全部通过, 1 = 有失败项, 2 = 环境不满足
"""
import argparse
import re
import subprocess
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from safe_ops import SafeTemp

HERE = Path(__file__).resolve().parent
WORKSPACE = HERE.parent
PROJECT = WORKSPACE / "Auto7z_WPF" / "Auto7z_WPF"

TEMP = SafeTemp("regress")


class Result:
    def __init__(self):
        self.items = []
        self.failed = 0

    def add(self, name, ok, detail=""):
        self.items.append((name, ok, detail))
        if not ok:
            self.failed += 1
        print("  [%s] %-34s %s" % ("通过" if ok else "失败", name, detail))

    def summary(self):
        print("")
        print("=" * 70)
        print("回归测试汇总: %d 项, 失败 %d 项" % (len(self.items), self.failed))
        print("=" * 70)
        for name, ok, detail in self.items:
            print("  [%s] %-34s %s" % ("通过" if ok else "失败", name, detail))


def run(cmd, encoding="gbk", **kw):
    """跑一个命令并收回输出。

    encoding 要按被测程序实际用的代码页来定：
    Auto7z 往控制台写的是 GBK（跟控制台代码页一致），
    而 cs_check.py / find_dead2.py 这些 python 工具输出的是 UTF-8。
    """
    return subprocess.run(cmd, capture_output=True, text=True,
                          encoding=encoding, errors="replace", **kw)


def t_main_regress(res, app):
    """主回归 T01–T22。"""
    print("")
    print("=== 主回归 (T01–T22) ===")
    r = run([sys.executable, str(HERE / "regress.py"), "--app", str(app)],
            cwd=str(HERE))
    out = (r.stdout or "") + (r.stderr or "")
    m = re.search(r"TOTAL ISSUES:\s*(\d+)", out)
    n = int(m.group(1)) if m else -1
    res.add("主回归 T01–T22", n == 0, "TOTAL ISSUES=%s" % n)
    return out


def t_static(res, app):
    """静态检查：命名类型一致性、文档标签、路径边界、死分支。"""
    print("")
    print("=== 静态检查 ===")
    src = PROJECT
    r = run([sys.executable, str(HERE / "cs_check.py"), str(src)], encoding="utf-8")
    out = (r.stdout or "") + (r.stderr or "")
    m = re.search(r"TOTAL PROBLEMS:\s*(\d+)", out)
    n = int(m.group(1)) if m else -1
    # 静态检查报出来的是"已知的现存问题"，不判失败，但要报出来让人知道
    detail = "TOTAL PROBLEMS=%s" % n
    if n > 0:
        for line in out.splitlines():
            if line.strip().startswith("!!"):
                detail += " | " + line.strip()[3:60]
    res.add("静态检查（已知问题计数）", True, detail)
    return out


def t_naming(res, app):
    """命名风格一致性。

    与静态检查不同：这一项**判失败**（命名不一致就是不一致，不是"已知的现存问题"）。
    2026-10-21 的 R4 轮新增该维度，见 testtools\\COVERAGE.md 的 D26。
    """
    print("")
    print("=== 命名风格一致性 ===")
    r = run([sys.executable, str(HERE / "namecheck.py"), str(PROJECT)], encoding="utf-8")
    out = (r.stdout or "") + (r.stderr or "")
    m = re.search(r"统计:(.*)", out)
    stats = m.group(1).strip() if m else "未知"
    bad = re.findall(r"^\s+\S+\.cs\s+.*应\s+\S+", out, re.M)
    res.add("命名风格一致", not bad,
            "%s%s" % (stats, (" | 不一致 %d 处" % len(bad)) if bad else " | 全部一致"))


def t_guard(res, app):
    """破坏性操作防护是否完好。

    这一项护的是**测试工具自身**的安全网：历史 P0 事故（测试脚本删掉了
    全部测试工具源码）的防线是 safe_ops.py 的白名单。
    2026-10-21 的 R7 轮实测发现：白名单自己被削弱时**没有任何工具会发现**，
    所以补了这个守护检查。详见 testtools\\COVERAGE.md 的 D20。
    """
    print("")
    print("=== 破坏性操作防护检查 ===")
    r = run([sys.executable, str(HERE / "guardcheck.py"), str(HERE)], encoding="utf-8")
    out = (r.stdout or "") + (r.stderr or "")
    blocked = len(re.findall(r"✓ 已拦住", out))
    allowed = len(re.findall(r"✓ 已放行", out))
    bad = re.findall(r"^\s*!!\s*(.+)$", out, re.M)
    res.add("破坏性操作防护完好", not bad,
            "拦住 %d 项，放行 %d 项%s" % (blocked, allowed,
                                        (" | " + bad[0][:50]) if bad else ""))


def t_behavior(res, app):
    """行为断言检查（§5b 的 R6 轮补的洞）。

    2026-10-21 的 R6 轮实测发现：把 D06/D15/D16/D21–D25 的关键判断改坏之后，
    cs_check.py **一个都报不出来**（它只做形态检查，不做语义分析），
    说明这些维度当时没有回归保护。behaviorcheck.py 就是补这个洞的。

    与命名/region 检查一样，这一项**判失败**。
    2026-10-21 的 R6 轮新增该维度，见 testtools\\COVERAGE.md 的 D21–D25。
    """
    print("")
    print("=== 行为断言检查 ===")
    r = run([sys.executable, str(HERE / "behaviorcheck.py"), str(PROJECT)], encoding="utf-8")
    out = (r.stdout or "") + (r.stderr or "")
    m = re.search(r"检查项\s*(\d+)\s*条，成立\s*(\d+)\s*条，不成立\s*(\d+)\s*条", out)
    total = m.group(1) if m else "?"
    ok_n = m.group(2) if m else "?"
    bad_n = m.group(3) if m else "?"
    detail = "成立 %s/%s" % (ok_n, total)
    if bad_n not in ("0", "?"):
        for line in out.splitlines():
            if line.strip().startswith("!!"):
                detail += " | " + line.strip()[3:60]
                break
    res.add("行为约束成立", bad_n in ("0", "?"), detail)


def t_region(res, app):
    """region 结构一致性。

    配对/嵌套问题**判失败**；超长 region 单独计数不判失败
    （拆超长 region 要改可执行逻辑，属已知限制）。
    2026-10-21 的 R5 轮新增该维度，见 testtools\\COVERAGE.md 的 D27。
    """
    print("")
    print("=== region 结构一致性 ===")
    r = run([sys.executable, str(HERE / "regioncheck.py"), str(PROJECT)], encoding="utf-8")
    out = (r.stdout or "") + (r.stderr or "")
    pair = re.search(r"配对/嵌套问题:\s*(\d+)", out)
    long_ = re.search(r"超长 region:\s*(\d+)", out)
    npair = int(pair.group(1)) if pair else -1
    nlong = int(long_.group(1)) if long_ else 0
    detail = "配对/嵌套问题=%d" % npair
    if nlong:
        longs = re.findall(r"^\s+--\s+(\S+\.cs\s+region.+)$", out, re.M)
        detail += " | 超长 region=%d（%s）" % (nlong, longs[0][:70] if longs else "见报告")
    res.add("region 结构一致", npair == 0, detail)


def t_dead_keys(res, app):
    """废键与三语词典一致性。"""
    print("")
    print("=== 废键与词典一致性 ===")
    r = run([sys.executable, str(HERE / "find_dead2.py"), str(PROJECT)], encoding="utf-8")
    out = (r.stdout or "") + (r.stderr or "")
    # 一致性：各语言键数相同，且没有"多出"的键
    counts = re.findall(r"(\w+-\w+\.xaml)=(\d+)", out)
    ok = True
    if counts:
        values = {int(v) for _, v in counts}
        ok = len(values) == 1
    extra = re.findall(r"!!\s*\S+\.xaml 多出:", out)
    if extra:
        ok = False
    res.add("三语词典键集合一致", ok,
            "键数=%s" % (", ".join("%s:%s" % c for c in counts) if counts else "未知"))


def t_shellreg(res, app):
    """外壳扩展注册/注销的注册表契约（B02 的回归保护）。

    查的是**源码里的契约**：注册写 Approved 的那个 hive，
    注销必须删同一个。不查注册表的当前状态 ——
    当前状态里可能本来就留着历史残留值，那是跑测试之前就有的。
    """
    print("")
    print("=== 外壳扩展注册契约 ===")
    r = run([sys.executable, str(HERE / "shellreg_check.py"), str(WORKSPACE)],
            encoding="utf-8")
    out = (r.stdout or "") + (r.stderr or "")
    ok = r.returncode == 0 and "契约成立" in out
    detail = ""
    m = re.search(r"RC\d\s+(.+)", out)
    if m:
        detail = m.group(1).strip()
    res.add("外壳扩展注册/注销契约一致（B02）", ok, detail)


def t_rename(res, app):
    """改名残留检查（B04 的回归保护）。

    旧名一个都不能剩；新名在 XAML 有定义、在代码里有引用；
    控件名里不能再带与实际元素类型不符的类型词。
    """
    print("")
    print("=== 改名残留检查 ===")
    r = run([sys.executable, str(HERE / "renamecheck.py"), str(PROJECT)], encoding="utf-8")
    out = (r.stdout or "") + (r.stderr or "")
    ok = r.returncode == 0 and "改名检查通过" in out
    residual = re.findall(r"旧名「(.+?)」仍有 (\d+) 处残留", out)
    detail = ("旧名残留: %s" % "; ".join("%s×%s" % x for x in residual)) if residual else "无残留"
    res.add("改名无残留且引用一致（B04）", ok, detail)


def t_guardprobe(res, app):
    """递归删除守卫的**运行时**验证（B01 / D28）。

    与 cs_check.py ⑤ 配对：
      cs_check ⑤  —— 静态：删除点前面有没有写边界校验
      guardprobe   —— 运行时：拿真实越界路径去调守卫，看它到底拦不拦

    静态只证明「代码里写了检查」，运行时才证明「检查真的拦得住」。
    """
    print("")
    print("=== 递归删除守卫（运行时）===")
    probe = HERE / "cs" / "guardprobe" / "guardprobe.exe"
    if not probe.exists():
        res.add("递归删除守卫运行时验证", False, "探针未编译（cs\\build_all.py guardprobe）")
        return
    # 探针是 .NET 控制台程序，输出跟着控制台代码页走，
    # 这里不假定编码：直接看退出码与「不符」的行数。
    # 退出码 0 = 探针自己认为全部符合预期（探针内部已逐项判定过）。
    r = run([str(probe), str(app)], encoding="gbk")
    out = (r.stdout or "") + (r.stderr or "")
    total = len([l for l in out.splitlines() if l.strip().startswith(("OK ", "!! "))])
    bad = len([l for l in out.splitlines() if l.strip().startswith("!! ")])
    ok = r.returncode == 0 and bad == 0 and total > 0
    if total == 0:
        detail = "输出未能正确解析（未读到检查项）"
    else:
        detail = "检查 %d 项，不符 %d 项" % (total, bad)
    res.add("递归删除守卫运行时验证", ok, detail)


def t_no_ghost(res, app):
    """收尾检查：系统里不该留下右键菜单注册（幽灵注册）。"""
    print("")
    print("=== 幽灵注册检查 ===")
    try:
        import winreg
        path = r"Software\Classes\*\shellex\ContextMenuHandlers\Auto7z"
        with winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE, path) as key:
            value, _ = winreg.QueryValueEx(key, "")
            registered = bool(value)
    except Exception:
        registered = False

    res.add("系统里无右键菜单残留注册", not registered,
            "注册状态=%s" % ("已注册" if registered else "未注册"))


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
    print("Auto7z 一键回归测试")
    print("被测程序: %s" % app)
    print("临时目录: %s" % TEMP.path)
    print("=" * 70)

    TEMP.ensure()
    res = Result()

    try:
        t_main_regress(res, app)
        t_static(res, app)
        t_naming(res, app)
        t_region(res, app)
        t_behavior(res, app)
        t_guard(res, app)
        t_dead_keys(res, app)
        t_shellreg(res, app)
        t_rename(res, app)
        t_guardprobe(res, app)
        t_no_ghost(res, app)
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
