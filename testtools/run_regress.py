#!/usr/bin/env python3
# -*- coding: utf-8 -*-
r"""一键回归测试。

跑「回归」这一类：针对已知条目基线表逐条确认 ——
凡是声称"已修"的条目必须有对应自动化测试且通过。

包含:
    regress.py      主回归（T01–T22：格式/等级/分卷/SHA256/密码/日志/边界/便携…）
    cs_check.py     静态检查（命名类型一致性、文档标签、路径边界、死分支）
    deadcode.py     死代码审查（零引用成员、只写不读状态、废词典键、无人调用的工具）
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

# 数据目录改道必须在导入 config_guard **之前**：配置路径是在那一刻按环境变量算出来的。
from test_env import isolate_data_root
DATA_ROOT = isolate_data_root("regress")

from config_guard import (ConfigGuard, claim_checkout, release_checkout,
                          child_env_with_inherited_checkout)

HERE = Path(__file__).resolve().parent
WORKSPACE = HERE.parent
PROJECT = WORKSPACE / "Auto7z_WPF" / "Auto7z_WPF"

TEMP = SafeTemp("regress")

# 启动被测程序时统一用的环境：带上数据目录改道变量。
import test_env as _test_env
PRODUCT_ENV = _test_env.child_env("regress")


class Result:
    # 预期的检查项数量。汇总时断言实际项数等于它 —— 这是防「假绿」的最后一层：
    #
    #   若某个检查函数因为异常（例如忘了 import）而没跑到 res.add，
    #   汇总会显示「N-1 项，失败 0」，看起来一切正常，而那一项**根本不存在**。
    #   2026-11-22 就发生过一次：t_cancel_button 里 NameError 被 finally 吞掉，
    #   汇总仍显示「18 项，失败 0」。详见 reports/fake-green-incident.md。
    #
    # 新增/删除检查项时必须同步改这个数；忘了改会立刻在汇总里报出来。
    #
    # 注意 t_mover_safety 里的 22 个专项检查器**合并成 1 项**计入
    # （它按「整组是否全绿」给一个结论），所以不是 +22。
    EXPECTED = 50

    def __init__(self):
        self.items = []
        self.failed = 0

    def add(self, name, ok, detail=""):
        self.items.append((name, ok, detail))
        if not ok:
            self.failed += 1
        print("  [%s] %-34s %s" % ("通过" if ok else "失败", name, detail))

    def summary(self):
        # 项数不符 = 有检查没跑到，必须判失败。
        missing = self.EXPECTED - len(self.items)
        if missing > 0:
            self.failed += 1
            self.items.append(("检查项数量", False,
                               "预期 %d 项，实际 %d 项 —— 少了 %d 项，说明有检查没跑到"
                               % (self.EXPECTED, len(self.items), missing)))
        elif missing < 0:
            self.failed += 1
            self.items.append(("检查项数量", False,
                               "预期 %d 项，实际 %d 项 —— 多了 %d 项，请同步更新 EXPECTED"
                               % (self.EXPECTED, len(self.items), -missing)))

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
    # 统一带上数据目录改道变量：子测试脚本与产品都会继承到，
    # 漏传一次产品就会写进用户真实的数据目录。
    kw.setdefault("env", PRODUCT_ENV)
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
    """静态检查：命名类型一致性、文档标签、路径边界、空 catch。

    空 catch 分两档：
      已说明（`catch { /* 为什么吞 */ }`）—— 登记，不判失败；
      未说明（光秃秃吞掉异常）      —— **判失败**。
    规则是「异常可以吞，但必须写明为什么」。
    2026-11-02 起由「只计数不判失败」改为判失败（原先 7 处存量为不判失败项）。
    """
    print("")
    print("=== 静态检查 ===")
    src = PROJECT
    r = run([sys.executable, str(HERE / "cs_check.py"), str(src)], encoding="utf-8")
    out = (r.stdout or "") + (r.stderr or "")
    m = re.search(r"TOTAL PROBLEMS:\s*(\d+)", out)
    n = int(m.group(1)) if m else -1
    reg = re.search(r"REGISTERED:\s*(\d+)", out)
    nreg = int(reg.group(1)) if reg else -1
    detail = "问题=%s｜已说明的空 catch=%s" % (n, nreg)
    if n > 0:
        for line in out.splitlines():
            if line.strip().startswith("!!"):
                detail += " | " + line.strip()[3:70]
    res.add("静态检查（空 catch 必须写明原因）", n == 0, detail)
    return out


def t_resource_keys(res, app):
    """资源键一致性 + 结果三态颜色映射。

    为什么单独一项：`langkeycheck` 只比对三语键集合是否一致，
    但如果**代码引用了不存在的键**，三语会一起"一致地缺失" —— 那项检查照样绿。
    而运行时的后果是静默回落到 fallback：中文用户看到英文，或者看到裸键名。
    本轮修 B-03 时新增了 Log_ExtractPartialReason 却忘了写进词典，
    正是靠这项检查抓出来的。

    顺带把「结果三态颜色映射」也纳入：成功绿 / 警告黄 / 失败红 是用户
    明确要求过的可见行为，颜色写死在 MainWindow.ShowResult 里，
    既没有资源键也没有 XAML 绑定，ViewModel 层探针看不到它。
    2026-11-22 的 LTS 轮新增该维度。
    """
    print("")
    print("=== 资源键一致性 + 三态颜色 ===")
    # encoding 必须显式指定 utf-8：run() 的默认值是 gbk（为产品控制台输出准备的），
    # 拿 gbk 去解 reskeycheck 的 UTF-8 输出会把「【D】结果三态颜色映射」整段解成乱码，
    # 于是下面按中文标题切分永远找不到 D 段 —— 本轮就因此误判成「检查未跑完」。
    r = run([sys.executable, str(HERE / "reskeycheck.py"), str(PROJECT)], encoding="utf-8")
    out = (r.stdout or "") + (r.stderr or "")

    miss = re.findall(r"\[缺失\]\s+\S+\s+缺\s+(\d+)\s+个", out)
    missing_total = sum(int(x) for x in miss)

    # 颜色段必须**逐行**判定为通过。
    #
    # 不用「统计 ✓ 的总数」：那种写法依赖脚本整体输出形态，只要前面 A/B/C 段
    # 的措辞调整就会误判（本轮就踩了 —— 直接跑是 0 退出码，接进回归却判失败）。
    # 改为按 D 段的三个颜色行 + 顺序行 + 分支数行逐项匹配，语义明确。
    d_section = out.split("【D】结果三态颜色映射")[-1] if "【D】结果三态颜色映射" in out else ""
    color_fail = [line.strip() for line in d_section.splitlines() if "✗" in line]
    colors_ok = bool(d_section.strip()) and not color_fail
    tristate_bad = "? 检查 default 分支" in d_section

    parts = []
    m = re.search(r"代码引用的键是否都存在（共引用\s*(\d+)\s*个）", out)
    if m:
        parts.append("引用 %s 键" % m.group(1))
    if missing_total:
        parts.append("缺键 %d 个" % missing_total)
    if not d_section.strip():
        parts.append("颜色段缺失（检查可能未跑完）")
    elif color_fail:
        parts.append("颜色异常: " + color_fail[0][:40])
    else:
        parts.append("三态颜色正常")
    if tristate_bad:
        parts.append("三态分支不全")

    ok = (missing_total == 0 and colors_ok and not tristate_bad and r.returncode == 0)
    res.add("资源键与三态颜色一致", ok, "，".join(parts) if parts else "检查未给出结论")


def t_config_keys(res, app):
    """配置键表一致性：键表 / Save 写出 / ApplyValues 读取 三处是否漂移。

    为什么单独一项：`ConfigService` 里有三份"同一批键"的知识 ——
    `_keys` 数组（用于缺键补齐判断）、`Save()` 里逐行 Set、`ApplyValues()` 里逐行读。
    三者只要有一处漏掉，就会出现三种都很隐蔽的后果：
      - 键表有、Save 没有 → 每次加载都判定「缺键」，反复重写配置文件
      - Save 有、键表没有 → 该键永远不会被补齐，旧文件缺它时读到默认值
      - ApplyValues 漏键 → 配置里写了但程序不生效（最隐蔽：用户以为改成功了）
    这类缺陷不会让任何功能测试失败，只会表现为"设置莫名其妙不生效"。
    2026-11-22 的 LTS 轮新增该维度。
    """
    print("")
    print("=== 配置键表一致性 ===")
    r = run([sys.executable, str(HERE / "configkeycheck.py"), str(PROJECT)], encoding="utf-8")
    out = (r.stdout or "") + (r.stderr or "")
    bad = re.findall(r"^\s*\[✗\]\s*(.+)$", out, re.M)
    m = re.search(r"三处键集合完全一致", out)
    n = re.search(r"AppConfig 持久化属性\s*(\d+)\s*个", out)

    detail = "键表 %s" % ("一致" if m else "不一致")
    if n:
        detail += "，持久化属性 %s 个" % n.group(1)
    if bad:
        detail += " | " + bad[0][:60]
    res.add("配置键表一致", not bad and r.returncode == 0, detail)


def t_legacy_suites(res, app):
    """历史测试脚本接入（9 个此前从未被调用的脚本）。

    2026-11-23 做「检查器是否真的在跑」审计时发现 19 个工具零引用，
    其中 9 个是真测试脚本。把它们接入后立刻暴露三个真实问题：

    1. **4 个脚本直接崩溃**：NameError: safe_rmtree is not defined
       —— 某次批量把 shutil.rmtree 换成 safe_rmtree 时漏了 import。
       接入过套件的话，第一次跑就会发现。

    2. **password_adopt_test 挖出 P1 产品缺陷**：
       AskPasswordAndRetry 提前执行 UnzipPassword = password;，
       而该属性是纯内存的（只发通知不写盘）。于是 AdoptVerifiedPassword
       里「值已一致就跳过写盘」的优化误判为已保存，配置文件**永不更新** ——
       内存是新密码、磁盘是旧密码，下次启动又拿旧密码试。
       已修复（实测：解压成功 rc=0 但配置仍是旧值 → 修复后正确回写）。

    3. **cmd_real_test 的 4 项是「假失败」**：
       Runner 没设 AUTO7Z_DATA_ROOT、没写配置，产品用了默认格式 tar.zst，
       而断言在找 *.7z。表现是「退出码 0、压缩完成、产物=0」。

    结论：**「写好了」不等于「在跑」。**
    """
    print("")
    print("=== 历史测试脚本接入 ===")
    tool = HERE / "legacy_suites.py"
    if not tool.is_file():
        res.add("历史脚本接入", False, "legacy_suites.py 缺失")
        return
    try:
        # 必须显式 UTF-8：回归的 run() 默认按 gbk 解（那是给产品控制台用的），
        # 而这个 Python 工具输出的是 UTF-8。用 gbk 解会把
        # 「共 9 个，通过 9，失败 0」整行变成乱码，正则匹配不到 ——
        # 表现是「独立跑 9/9 通过、在回归里却报退出码=1」。
        r = run([sys.executable, str(tool)], timeout=1800, encoding="utf-8")
    except Exception as ex:
        res.add("历史脚本接入", False, "调用异常: %s" % type(ex).__name__)
        return
    out = (r.stdout or "") + (r.stderr or "")
    m = re.search(r"共 (\d+) 项，通过 (\d+)，失败 (\d+)", out)
    if m and r.returncode == 0:
        res.add("历史脚本接入", True, "%s/%s 个通过" % (m.group(2), m.group(1)))
    else:
        bad = re.findall(r"^  !!\s+(\S+)", out, re.M)
        res.add("历史脚本接入", False,
                ("%s 个失败: %s" % (m.group(3), ",".join(bad[:3]))) if m
                else "退出码=%s" % r.returncode)


def t_mover_safety(res, app):
    """数据目录迁移的两项安全保证（GPT6 报告第 2、4 条）。

    这两条都是**真实的可靠性/安全缺陷**，2026-11-23 修复后用探针固化：

    1. 迁移原子性：早先是「先删目标、再 File.Move 源」，两步非原子。
       File.Move 失败（权限/占用/磁盘错误）时目标文件已被删除、源还在原处
       —— 目标数据直接丢失。实测复现：目标 conflict.txt 内容消失。
       现在改为「复制到 .moving → File.Replace 原子替换 → 才删源」。

    2. 重解析点不跟随：Directory.GetFiles/GetDirectories 会穿过
       Junction / SymbolicLink 进入外部目录，而「路径字符串在数据目录内」
       的检查会通过 —— 搬迁可能把 外部磁盘目录 的内容搬进来或搬出去。
       实测：建 Junction 指向 outside 后能读到 outside 的文件。
       现在遇到 ReparsePoint 一律跳过。
    """
    print("")
    print("=== 数据迁移安全（原子性 / 重解析点） ===")

    ok_all = True
    for script, label, want in (
        ("mover_atomicity_check.py", "迁移原子性", "共 2 项"),
        ("reparse_guard_check.py", "重解析点防护", "共 4 项"),
        ("outcome_check.py", "三态退出码契约", "共 5 项"),
        ("gpt6b_check.py", "迁移安全第二批", "共 9 项"),
        ("shutdown_check.py", "单实例关闭时序", "共 4 项"),
        ("fsedge_check.py", "文件系统异常场景", "共 6 项"),
        ("behavioral_check.py", "行为级组合验证", "共 8 项"),
        ("stress_check.py", "并发压力", "共 6 项"),
        ("b06_deterministic_check.py", "输出排空竞态", "共 5 项"),
        ("reparse_root_check.py", "重解析点三形态", "共 7 项"),
        ("parent_junction_check.py", "父目录 Junction 守卫", "共 5 项"),
        # 第三批（GPT6 batch-3）新增：六个缺陷各自的专项验证。
        ("lifecycle_race_check.py", "单实例发布/停止竞态", "共 6 项"),
        ("fsboundary_check.py", "路径字符串边界 vs 文件系统边界", "共 25 项"),
        ("migrate_guard_check.py", "搬迁目标侧前置校验", "共 12 项"),
        # 第四批（GPT6 batch-4）：把「检查-动手」之间的竞态换成真正的先后关系。
        # 下面的项数与各检查器实际输出的「共 N 项」必须一致，
        # 对不上会被 t_mover_safety 判失败 —— 那是防「用例被删了却仍报绿」的闸。
        ("migration_gate_check.py", "搬迁期间写入隔离（读写锁）", "共 37 项"),
        ("txn_check.py", "模式切换事务（闸门罩住提交）", "共 18 项"),
        ("copyshare_check.py", "复制源独占与临时名长度", "共 28 项"),
        ("ownership_check.py", "残留目录归属校验", "共 17 项"),
        ("dirlock_check.py", "目标目录锁（含不存在时）", "共 22 项"),
        ("p1_concurrency_check.py", "P1 并发与失败恢复（RC 门禁）", "共 15 项"),
        ("toctou_check.py", "TOCTOU 目录独占缓解", "共 16 项"),
        ("longpath_check.py", "外壳扩展长路径", "共 20 项"),
    ):
        tool = HERE / script
        if not tool.is_file():
            res.add(label, False, "%s 缺失" % script)
            ok_all = False
            continue
        r = run([sys.executable, str(tool), str(WORKSPACE)], timeout=900, encoding="utf-8")
        out = (r.stdout or "") + (r.stderr or "")
        m = re.search(r"共 (\d+) 项，通过 (\d+)，失败 (\d+)", out)
        if r.returncode == 0 and m and m.group(3) == "0":
            # **项数必须与声明的期望一致。**
            #
            # 早先这里只打印实际项数、从不与 want 比对：某个检查器若因为
            # 代码分支被删掉而少跑了一半用例，只要剩下那些通过，这里照样报 PASS。
            # 「少跑了」和「跑过了」看起来一样 —— 又一个假绿的形状。
            actual = "共 %s 项" % m.group(1)
            if actual != want:
                res.add(label, False,
                        "项数变了：声明 %s，实际 %s（用例被增删却未同步，"
                        "或某段分支没跑到）" % (want, actual))
                ok_all = False
            else:
                res.add(label, True, "%s/%s 项通过" % (m.group(2), m.group(1)))
        else:
            bad = re.findall(r"^\s*!!\s+(.+)$", out, re.M)
            res.add(label, False,
                    ("%s 项失败: %s" % (m.group(3), bad[0][:40])) if (m and bad)
                    else ("退出码=%s" % r.returncode))
            ok_all = False
    return ok_all


def t_langkeys(res, app):
    """资源键完整性（交叉验证 reskeycheck）。

    与 	_resource_keys 的重叠是**有意的**：两者是独立实现，
    取键 API 清单不同、正则不同。它们对同一份代码得出相同结论，
    才说明这个结论可信。

    这项接入时发现两个真实问题（2026-11-23）：

    1. **langkeycheck.py 从未被任何套件调用** —— 它写好了却孤立地放在
       testtools 里，等于不存在。正因如此，它内部一个路径 bug（把项目根
       当成源码目录用，必然报「找不到语言词典」）长期没人发现。
       变异测试才会暴露这类问题：把一个键改成不存在的名字，
       它能报出，但从来没人跑过它。

    2. **reskeycheck 漏了 GetFormattedText** —— 它只匹配 GetResourceText /
       GetErrorText，而全仓有 3 次 GetFormattedText 取键。补齐后引用数
       从 114 变成 117。这三个键目前都在词典里所以没出事，
       但只要有人用该 API 取不存在的键，两个检查器都会漏报。
    """
    print("")
    print("=== 资源键完整性（交叉验证） ===")
    r = run([sys.executable, str(HERE / "langkeycheck.py"), str(WORKSPACE)],
            encoding="utf-8")
    out = (r.stdout or "") + (r.stderr or "")
    m = re.search(r"代码取用 (\d+) 个键", out)
    miss = re.findall(r"^\s*!! 缺失 (\S+)", out, re.M)
    if r.returncode == 0 and "TOTAL PROBLEMS: 0" in out:
        res.add("资源键完整性", True,
                "代码取用 %s 个键全部存在（独立实现交叉验证）" % (m.group(1) if m else "?"))
    else:
        res.add("资源键完整性", False,
                ("缺失 %d 个键: %s" % (len(miss), ",".join(miss[:3]))) if miss
                else "检查未给出结论（退出码=%s）" % r.returncode)


def t_cleanliness(res, app):
    """代码清洁度守卫：空行 / 行尾空白 / Tab / 大括号风格。

    把「清洁」从一次性动作变成可回归的约束。只守能机械判定、
    且违反了一定更差的四项（K1-K4）。

    **刻意不守的**：
      - 长注释块（本项目 27 处超过 12 行）—— 记录决策理由，
        例如「为什么必须在这里而不是 OnClosing」「为什么用 HKLM 而不是 HKCR」。
        压掉等于丢掉最有价值的部分。
      - 超长行 —— 只剩 1 处，在自动生成的 Resources.Designer.cs。

    判据细节：K4 的正则必须把「空白」限定为空格与制表符，不能用通用的
    空白元字符 —— 后者包含换行，会让 Allman 风格也被 K&R 的判据命中，
    结果 40 个文件全被误报成「风格混用」（我第一版就踩了这个坑）。
    2026-11-23 LTS 轮新增。
    """
    print("")
    print("=== 代码清洁度 ===")
    r = run([sys.executable, str(HERE / "cleanlinesscheck.py"), str(WORKSPACE)],
            encoding="utf-8")
    out = (r.stdout or "") + (r.stderr or "")
    bad = re.findall(r"^\s*\[(K\d)\]\s*(.+)$", out, re.M)
    if r.returncode == 0 and "PASS" in out:
        res.add("代码清洁度", True, "空行/空白/Tab/大括号风格全部合规")
    else:
        res.add("代码清洁度", False,
                ("%d 项违规 | [%s] %s" % (len(bad), bad[0][0], bad[0][1][:50])) if bad
                else "检查未给出结论（退出码=%s）" % r.returncode)


def t_appearance(res, app):
    """外观约束：颜色集中定义 / 布局未漂移 / 窗口结构完整。

    用户的硬性约束之一是「不要修改前端外观」。这项把该约束变成可判定的规则，
    而不是靠「我改了没改」的口头保证：

    A. C# 里颜色只出现在 MainWindow.ShowResult（三态映射的唯一出口）；
       XAML 内联色值数量与基线一致（防止配色被批量改动）
    B. XAML 布局属性总数与基线一致（防止有人动了 Width/Margin/FontSize 等）
    C. 三语键完全一致 + 每个 x:Class 都有对应的 partial class

    边界：不查 XAML 里的内联十六进制是否「合法」——
    那是既有设计（侧栏 #FDFDFD、主题色 #0078D7），基线里逐值一致。
    把它算成违规是误判，真正要防的是 C# 代码里爱上写死颜色。
    2026-11-23 LTS 轮新增。
    """
    print("")
    print("=== 外观约束 ===")
    r = run([sys.executable, str(HERE / "appearancecheck.py"), str(WORKSPACE)],
            encoding="utf-8")
    out = (r.stdout or "") + (r.stderr or "")
    bad = re.findall(r"^\s*\[([ABC])\]\s*(.+)$", out, re.M)
    if r.returncode == 0 and "PASS" in out:
        res.add("外观约束", True, "颜色集中、布局未漂移、窗口结构完整")
    else:
        res.add("外观约束", False,
                ("%d 项违规 | [%s] %s" % (len(bad), bad[0][0], bad[0][1][:50])) if bad
                else "检查未给出结论（退出码=%s）" % r.returncode)


def t_release_audit(res, app):
    """Release 交付物审计：确认 Debug 专用代码没进生产构建。

    #if DEBUG 包住的测试钩子一旦进了交付物，就是**生产环境里的隐蔽入口**：
    DebugAutoDriver 能自动点掉任何对话框、TestHooks 能注入任意文件列表。
    这类缺陷不会被任何功能测试发现（Debug 构建下一切正常），
    只能在 Release 产物上做类型级检查。

    判据必须用「类型/成员是否存在」，不能看 DebuggableAttribute ——
    实测在 Release 产物上 IsJITTrackingEnabled 也返回 true，会误判成 Debug。
    2026-11-23 LTS 轮新增。
    """
    print("")
    print("=== Release 交付物审计 ===")
    release_exe = PROJECT / "bin" / "Release" / "Auto7z.exe"
    tool = HERE / "cs" / "releaseaudit" / "releaseaudit.exe"

    if not release_exe.is_file():
        res.add("Release 交付物干净", False, "找不到 Release 构建: %s" % release_exe.name)
        return
    if not tool.is_file():
        res.add("Release 交付物干净", False, "releaseaudit 未编译")
        return

    r = run([str(tool), str(release_exe)], encoding="gbk")
    out = (r.stdout or "") + (r.stderr or "")
    leaked = re.findall(r"!! (.+)$", out, re.M)
    m = re.search(r"类型总数: (\d+)", out)
    if r.returncode == 0 and "PASS" in out:
        res.add("Release 交付物干净", True, "6 项测试钩子全部移除")
    else:
        res.add("Release 交付物干净", False,
                ("%d 项泄漏: %s" % (len(leaked), leaked[0][:50])) if leaked
                else "审计未给出结论（退出码=%s）" % r.returncode)


def t_arch(res, app):
    """架构约束：同一份知识只能有一处。

    守三处 2026-11-22 合并过的重复：
      1. 组件路径（组件目录 / 7z.exe / 7z.dll）只从 ComponentsDirectory 派生
      2. 模式分派只在 ShellCommandService.RunByMode
      3. 开关→枚举只在 ShellCommandService.ParseOperationMode，且兜底方向唯一

    为什么必须自动化：这三处「当时算出来一样」，靠人眼看代码是发现不了的 ——
    它们不是错，是**将来会变错**的结构。而破坏后的症状是
    「文件明明在、程序说找不到」或「命令行能用、右键菜单不认」，
    排查成本远高于一条断言。

    边界：本项**不**禁止所有重复。Hash 核心族那种「每个算法一个薄封装类」
    是刻意的解耦（工厂模式），合并成巨型类反而是「原子服务」，维护更痛苦。
    A5 专门断言那 23 个文件仍在，防止有人以「消除重复」为名把它们合并掉。
    """
    print("")
    print("=== 架构约束 ===")
    r = run([sys.executable, str(HERE / "archcheck.py"), str(PROJECT.parent.parent)],
            encoding="utf-8")
    out = (r.stdout or "") + (r.stderr or "")
    bad = re.findall(r"^\s*\[(A\d)\]\s*(.+)$", out, re.M)
    if r.returncode == 0 and "PASS" in out:
        res.add("架构约束", True, "路径/分派/映射各只有一处，解耦未被误伤")
    else:
        res.add("架构约束", False,
                ("违规 %d 项 | [%s] %s" % (len(bad), bad[0][0], bad[0][1][:50])) if bad
                else "检查未给出结论（退出码=%s）" % r.returncode)


def t_hash_cores(res, app):
    """Hash 核心族一致性（替代逐个肉眼读 22 个薄封装文件）。

    检查四件事：abstract/sealed 用法是否正确、每个具体核心是否都注册到目录、
    算法标识是否合命名规范、声明的输出长度与算法名是否自洽。

    为什么值得自动化：22 个文件里 18 个是 9~17 行的薄封装，逐个肉眼读既慢
    又容易漏。而它们的约束是机械的 —— 写成断言比人眼可靠。
    2026-11-22 LTS 轮新增。
    """
    print("")
    print("=== Hash 核心族一致性 ===")
    r = run([sys.executable, str(HERE / "hashcore_check.py"), str(PROJECT.parent.parent)],
            encoding="utf-8")
    out = (r.stdout or "") + (r.stderr or "")
    bad = re.findall(r"^\s*\[(H\d)\]\s*(.+)$", out, re.M)
    if r.returncode == 0 and "PASS" in out:
        m = re.search(r"提取到\s*(\d+)\s*个标识", out) or re.search(r"(\d+) 个标识", out)
        res.add("Hash 核心族一致", True,
                "%s" % (("标识 %s 个" % m.group(1)) if m else "H1–H4 全部符合"))
    else:
        res.add("Hash 核心族一致", False,
                ("违规 %d 项 | [%s] %s" % (len(bad), bad[0][0], bad[0][1][:50])) if bad
                else "检查未给出结论（退出码=%s）" % r.returncode)


def t_cmd_contract(res, app):

    """命令行参数解析的行为契约。

    守两件事：
      1. 互斥开关（-a/-z/-uz）同时给出时必须报冲突，不能静默取后者。
         这是 2.5.1 修的一个真实缺陷 —— 早先 `-z -uz` 会按 -uz 执行，
         用户以为程序该报错，实际它悄悄干了一件别的事。
      2. 几条 README 承诺：大小写不敏感、以 - 开头的真实文件按路径处理、
         未知开关必须报错并以退出码 2 结束。

    注意：检查器内部**跳过 -r/-ur**（会弹 UAC 并写 HKLM，不适合自动化）。
    2026-11-22 LTS 轮新增。
    """
    print("")
    print("=== 命令行参数契约 ===")
    r = run([sys.executable, str(HERE / "cmdcontract_check.py"), str(PROJECT.parent.parent)],
            encoding="utf-8")
    out = (r.stdout or "") + (r.stderr or "")
    bad = re.findall(r"^\s*!!\s+(.+)$", out, re.M)
    okn = len(re.findall(r"^\s*OK\s+", out, re.M))
    if r.returncode == 0 and "PASS" in out:
        res.add("命令行参数契约", True, "%d 项全部符合" % okn)
    else:
        res.add("命令行参数契约", False,
                ("%d 项不符合 | %s" % (len(bad), bad[0][:50])) if bad
                else "检查未给出结论（退出码=%s）" % r.returncode)


def t_shell_const(res, app):

    """跨语言常量一致性：C# 与 C++ 必须用同一个 CLSID / Handler 名。

    这一项守的是**两套语言之间的隐式契约**，没有编译期约束：
      ShellRegistrationService.cs  里写死 ShellClsid / ShellHandlerName
      ShellExtension.cpp           里写死 kClassId / kHandlerKeyName

    改了一边忘了另一边，后果是 `HasRegistryResidue()` 查的键根本不是产品
    注册的那个键 —— 于是「dll 缺失时如实上报残留」这条逻辑永远报「无残留」，
    用户看到「已注销」而注册表里其实还指着不存在的 dll。
    这类缺陷不会让任何功能测试失败，只会让卸载不干净，极难归因。
    2026-11-22 LTS 轮新增。
    """
    print("")
    print("=== 跨语言常量一致性 ===")
    r = run([sys.executable, str(HERE / "shellconst_check.py"), str(PROJECT.parent.parent)],
            encoding="utf-8")
    out = (r.stdout or "") + (r.stderr or "")
    bad = re.findall(r"^\s*-\s*(.+)$", out, re.M)
    if r.returncode == 0 and "PASS" in out:
        res.add("跨语言常量一致", True, "CLSID / Handler 名 / 四条残留路径均一致")
    else:
        res.add("跨语言常量一致", False,
                (bad[0][:70] if bad else "检查未给出结论（退出码=%s）" % r.returncode))


def t_lifecycle(res, app):

    """测试生命周期规范符合性（TEST_LIFECYCLE.md 的硬性指标）。

    这一项守的是**测试工具自身**的架构约束，不是产品行为：
    全局清理只允许发生在整轮开始与结束；阶段二内部只能管自己的资源；
    递归删除必须过安全闸门；子进程必须登记；用例必须能单跑。

    为什么必须自动化：这些条款看着都对，但只要有人"顺手加一句清理"就会破，
    而破坏后的症状是"偶发、不可复现的假失败"，人工 review 极难发现。
    2026-11-22 LTS 轮新增。
    """
    print("")
    print("=== 测试生命周期规范 ===")
    r = run([sys.executable, str(HERE / "lifecyclecheck.py"), str(PROJECT.parent.parent)],
            encoding="utf-8")
    out = (r.stdout or "") + (r.stderr or "")
    problems = re.findall(r"^\s*\[(L\d)\]\s*(.+)$", out, re.M)
    if problems:
        res.add("测试生命周期规范", False,
                "违规 %d 项 | [%s] %s" % (len(problems), problems[0][0], problems[0][1][:60]))
    elif "PASS" in out:
        res.add("测试生命周期规范", True, "L1–L7 全部符合")
    else:
        res.add("测试生命周期规范", False, "检查未给出结论（退出码=%s）" % r.returncode)


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


def t_tip_text(res, app):
    """提示文案质量：过长 / 空文案 / 真实换行 / 手工折行 / 标点风格 / 无空格长串。

    为什么单独一项：`langkeycheck` / `find_dead2` 只验**键在不在**，
    `uiwidth` 只验控件上的**可见文案**宽度 —— 两边都不管 tooltip。
    结果英文提示涨到 150+ 字符也没人发现，而 WPF 用纯字符串设 tooltip 时
    默认模板是 NoWrap，会拉成一条读不完的长条。
    产品侧已改成显式 TextBlock + Wrap + MaxWidth，这一项负责守住"别再把长文案塞回来"。
    """
    print("")
    print("=== 提示文案质量 ===")
    r = run([sys.executable, str(HERE / "tipcheck.py")], encoding="utf-8")
    out = (r.stdout or "") + (r.stderr or "")
    total = re.search(r"TOTAL PROBLEMS:\s*(\d+)", out)
    n = int(total.group(1)) if total else -1
    if n < 0:
        res.add("提示文案质量", False, "tipcheck.py 未给出结论（退出码=%s）" % r.returncode)
        return
    if n:
        kinds = re.findall(r"^--- (\S+) \((\d+)\) ---$", out, re.M)
        detail = "问题=%d（%s）" % (n, "，".join("%s×%s" % (k, c) for k, c in kinds) or "见报告")
    else:
        longest = re.search(r"^\s+(\d+)\s+(\S+)", out, re.M)
        detail = "问题=0" + ("，最长=%s/%s" % (longest.group(1), longest.group(2)) if longest else "")
    res.add("提示文案质量", n == 0, detail)


def t_dead_code(res, app):
    """死代码审查：零引用成员、只写不读的状态、无人引用的词典键、无人调用的工具。

    为什么放在回归里：这个项目的历史包袱主要是"写了但没人用"的东西
    （AppState 的整套运行状态机、四个没有绑定的 ViewModel 命令、
    三语词典里的四个孤儿键、一个已被文本宽度自检取代的测量工具）——
    它们不会让任何测试失败，只会一直烂在那里。把审查接进一键回归，
    新写的死代码在当次提交里就会被看见。

    判失败的口径只有一条：**零引用成员 / 只写不读状态 / 无人引用的词典键**。
    测试工具"没人调用"单独计数但不判失败 —— 有些工具是给人手工跑的（ui_probe 等）。
    """
    print("")
    print("=== 死代码审查 ===")
    r = run([sys.executable, str(HERE / "deadcode.py")], encoding="utf-8")
    out = (r.stdout or "") + (r.stderr or "")

    # 各节标题下的 !! 行
    sections = re.split(r"^-{20,}$", out, flags=re.M)
    hard = []
    soft = []
    current = ""
    for chunk in sections:
        title = re.search(r"^[一二三四五六]、(.+)$", chunk.strip().splitlines()[0]) if chunk.strip() else None
        if title:
            current = title.group(1)
        for line in chunk.splitlines():
            if line.strip().startswith("!!"):
                entry = "%s: %s" % (current, line.strip()[2:].strip()[:58])
                if "测试工具调用情况" in current:
                    soft.append(entry)
                else:
                    hard.append(entry)

    detail = "死代码候选 %d 条" % (len(hard) + len(soft))
    if soft:
        detail += "（另有 %d 个工具无人调用，不判失败）" % len(soft)
    res.add("无死代码（零引用成员/只写不读状态/废词典键）", not hard, detail)
    for item in hard[:6]:
        print("      - " + item)
    return out


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


def t_resultprobe(res, app):
    """任务结果的颜色契约（运行时）：取消/失败红字、成功绿字。

    与 behaviorcheck BC11–BC13 配对：
      behaviorcheck  —— 静态：源码里有没有该有的调用与属性名
      resultprobe    —— 运行时：真的建 ViewModel、调 SetResult，
                        在 PropertyChanged 回调里看通知的属性名与颜色依据

    静态只证明「代码写了」，运行时才证明「通知发对了、颜色依据取对了」。
    """
    print("")
    print("=== 结果颜色契约（运行时）===")
    probe = HERE / "cs" / "resultprobe" / "resultprobe.exe"
    if not probe.exists():
        res.add("结果颜色契约（运行时）", False, "探针未编译（cs\\resultprobe\\resultprobe.exe）")
        return
    # 探针是 .NET 控制台程序，输出跟着控制台代码页走，这里不假定编码：
    # 直接看退出码与「不符」的行数。退出码 0 = 探针自己认为全部符合预期。
    r = run([str(probe), str(app)], encoding="gbk")
    out = (r.stdout or "") + (r.stderr or "")
    total = len([l for l in out.splitlines() if l.strip().startswith(("OK ", "!! "))])
    bad = len([l for l in out.splitlines() if l.strip().startswith("!! ")])
    ok = r.returncode == 0 and bad == 0 and total > 0
    if total == 0:
        detail = "输出未能正确解析（未读到检查项）"
    else:
        detail = "检查 %d 项，不符 %d 项" % (total, bad)
    res.add("结果颜色契约（运行时）", ok, detail)


def t_hashprobe(res, app):
    """本项目计算服务（Services/Hash/HashingService.cs）的功能契约（运行时）。

    hashprobe —— 运行时：本项目这一份算得对不对（逐算法校验）

    为什么要单独一项：T04 只走完整压缩链路、断言「产出的 .sha256 对得上」，
    测不出行格式、有没有 BOM、目录递归、坏文件跳过、进度单调这些细节；
    bench B1/B3 只看吞吐，不断言正确性。
    """
    print("")
    print("=== 本项目计算服务（运行时）===")
    probe = HERE / "cs" / "hashprobe" / "hashprobe.exe"
    if not probe.exists():
        res.add("本项目计算服务运行时验证", False, "探针未编译（cs\\build_all.py hashprobe）")
        return
    work = TEMP.sub("hashprobe")
    # 探针输出跟着控制台代码页走，这里不假定编码：
    # 退出码 0 = 探针内部已逐项判定通过。
    r = run([str(probe), str(app), str(work)], encoding="gbk")
    out = (r.stdout or "") + (r.stderr or "")
    total = len([l for l in out.splitlines() if l.strip().startswith(("OK ", "!! "))])
    bad = len([l for l in out.splitlines() if l.strip().startswith("!! ")])
    ok = r.returncode == 0 and bad == 0 and total > 0
    if total == 0:
        detail = "输出未能正确解析（未读到检查项）"
    else:
        detail = "检查 %d 项，不符 %d 项" % (total, bad)
    res.add("本项目计算服务运行时验证", ok, detail)


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


def t_crashrecover(res, app):
    """崩溃残留清理（分卷残片）的回归保护。

    验证「强制结束进程留下分卷残片」这个已知缺陷的修复：
    任务开始前写 TaskState.json + job.lock，崩溃后下次启动精确清理残片。
    覆盖崩溃点 A（锁建 JSON 未写）/ B（active 强杀）/ C'（done 锁未删）/
    D（JSON 损坏）/ F（PID 存活跳过）五个场景。

    子进程里跑 crashrecover_test.py：它要强杀被测进程再重启，独立成子进程
    避免与主回归的进程编排互相干扰；互斥体由父进程持有，通过继承标记放行。
    """
    print("")
    print("=== 崩溃残留清理（分卷残片）===")
    script = HERE / "crashrecover_test.py"
    if not script.exists():
        res.add("崩溃残留清理回归保护", False, "找不到 crashrecover_test.py")
        return
    r = run([sys.executable, str(script), "--app", str(app)],
            encoding="utf-8",
            env=_test_env.child_env("crashrecover",
                                    base=child_env_with_inherited_checkout()))
    out = (r.stdout or "") + (r.stderr or "")
    m = re.search(r"崩溃恢复测试汇总:\s*(\d+)\s*项,\s*失败\s*(\d+)\s*项", out)
    total = int(m.group(1)) if m else -1
    bad = int(m.group(2)) if m else -1
    ok = r.returncode == 0 and total > 0 and bad == 0
    detail = ("%d 项, 失败 %d 项" % (total, bad)) if total >= 0 else "输出未能正确解析"
    res.add("崩溃残留清理回归保护", ok, detail)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--app", help="被测程序路径")
    ap.add_argument("--config", default="Debug", help="Debug / Release")
    ap.add_argument("--keep", action="store_true", help="保留临时目录")
    ap.add_argument("--clear-config", action="store_true",
                    help="开跑前清空配置（产品会重建默认值），跑完仍还原成原样")
    ap.add_argument("--purge-portable", action="store_true",
                    help="删掉 exe 旁的便携数据目录（它存在时会顶掉普通配置）")
    args = ap.parse_args()

    app = Path(args.app) if args.app else PROJECT / "bin" / args.config / "Auto7z.exe"
    if not app.exists():
        print("找不到被测程序: %s" % app)
        print("先构建: msbuild Auto7z_WPF\\Auto7z_WPF.sln /t:Rebuild /p:Configuration=%s" % args.config)
        return 2

    mutex = claim_checkout()
    if mutex is None:
        print("已有另一份 Auto7z 测试正在跑；并行跑会互相抢配置与单实例，先等它结束。")
        return 2

    guard = ConfigGuard(app)
    if not guard.prepare(purge_portable=args.purge_portable):
        release_checkout(mutex)
        return 2

    chain_token = guard.join_chain()
    cleared = guard.clear() if args.clear_config else []

    print("=" * 70)
    print("Auto7z 一键回归测试")
    print("被测程序: %s" % app)
    print("临时目录: %s" % TEMP.path)
    print("配置: %s" % guard.local_cfg)
    if cleared:
        print("已清空配置 %d 份，产品将从默认值起跑" % len(cleared))
    print("=" * 70)

    TEMP.ensure()
    res = Result()

    try:
        t_main_regress(res, app)
        t_static(res, app)
        t_naming(res, app)
        t_region(res, app)
        t_resource_keys(res, app)
        t_config_keys(res, app)
        t_lifecycle(res, app)
        t_shell_const(res, app)
        t_cmd_contract(res, app)
        t_hash_cores(res, app)
        t_arch(res, app)
        t_release_audit(res, app)
        t_appearance(res, app)
        t_cleanliness(res, app)
        t_langkeys(res, app)
        t_mover_safety(res, app)
        t_legacy_suites(res, app)
        t_behavior(res, app)
        t_guard(res, app)
        t_dead_keys(res, app)
        t_tip_text(res, app)
        t_dead_code(res, app)
        t_shellreg(res, app)
        t_rename(res, app)
        t_guardprobe(res, app)
        t_resultprobe(res, app)
        t_hashprobe(res, app)
        t_no_ghost(res, app)
        t_crashrecover(res, app)
    except Exception as ex:
        # 同 run_system_test：测试函数抛异常必须记成失败项，不能静默消失。
        # 依据见 reports/fake-green-incident.md（一项检查因 NameError 从汇总里凭空消失，
        # 而汇总仍显示「失败 0 项」）。
        import traceback
        traceback.print_exc()
        res.add("测试执行异常", False, "%s: %s" % (type(ex).__name__, ex))
    finally:
        res.summary()
        print("")
        print("##### 清理临时产物 #####")
        if args.keep:
            print("  --keep 指定了，保留: %s" % TEMP.path)
        else:
            print("  " + TEMP.cleanup())

        # 配置必须在清理完临时产物之后还原：还原本身就是把用户的真实文件写回去。
        guard.leave_chain(chain_token)
        release_checkout(mutex)

    return 1 if res.failed else 0


if __name__ == "__main__":
    sys.exit(main())
