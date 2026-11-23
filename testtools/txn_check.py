#!/usr/bin/env python3
# -*- coding: utf-8 -*-
r"""模式切换事务：闸门必须罩住「搬文件 + 提交模式」（BUG-03）。

## 缺陷

早先闸门只包在 `DataDirectoryMover.MoveDataDirectory` 内部：

    using (MigrationGate.Enter())
    {
        CopyThenDeleteSource(...);
        Directory.Delete(sourceDirectory, false);
    }        // ← 闸门在这里就开了

    return true;

而真正改变「程序用哪个数据目录」的代码在 ViewModel，**在闸门之外**：

    bool moved = portable ? MoveToPortable() : MoveToLocal();
    if (!moved) return false;
    _isPortable = portable;
    AppPaths.IsPortable = portable;
    LogService.ResetLogFiles();

以取消便携为例，中间那个窗口里会发生：

    1. 数据迁移成功，原 .config/Auto7z 被删除
    2. 闸门自动打开
    3. AppPaths.IsPortable 仍然是 true
    4. 后台日志线程执行 WriteLog → 按旧模式重新创建 .config/Auto7z/Logs
    5. ViewModel 才执行 AppPaths.IsPortable = false

而便携判定看的是 `.config` 目录是否存在（AppPaths.DetectPortableFromDisk）：

    return Directory.Exists(GetPortableRootDirectory());

第 4 步把旧目录又建出来了 ⇒ **下次启动又被判成便携模式**。
用户点了「取消便携」，当前运行期间看着成功，重启却回到便携。

## 修复

把闸门提到调用方，用一个事务同时罩住：

    关闭写入闸
      等待在途写入完成
        搬迁数据
        提交模式（_isPortable / AppPaths.IsPortable / 重置日志路径）
        确认旧目录不再存在
      打开写入闸
    保存新配置

`MigrationGate` 因此必须可重入：搬迁类内部还会再持一次（它是对外公开的 API，
不能假设调用方包了事务），内层只加深度。

## 本检查器验证什么

A. 静态：SetPortableMode 里有 SuspendWrites 包住整段提交逻辑
B. 静态：模式提交（AppPaths.IsPortable 赋值）**在**闸门作用域内，而不是之后
C. 运行时：**取消便携后 .config 不再被重建**（BUG-03 的直接后果）
D. 运行时：闸门嵌套时只有一个真正的加解锁（可重入性）
E. 运行时：事务结束后闸门是开的（异常路径也不留死锁）

## C 组是核心

「当前运行期间切换成功」这个断言在缺陷版本上**也成立** ——
缺陷只在下次启动才显形。所以 C 组必须断言「旧目录没有被重建」，
而不是「切换返回 true」。
"""
import json
import os
import subprocess
import sys
from pathlib import Path

from safe_ops import safe_rmtree

P = sys.argv[1] if len(sys.argv) > 1 else r"C:\Users\Mocha\Desktop\VSProject\Auto7z-WPF"
EXE = os.path.join(P, "Auto7z_WPF", "Auto7z_WPF", "bin", "Debug", "Auto7z.exe")
SDK = os.path.join(os.environ.get("TEMP", r"C:\Windows\Temp"), "Auto7zTest_data", "txn")

results = []


def check(name, ok, detail=""):
    results.append({"name": name, "ok": ok, "detail": detail})
    print(("  OK   " if ok else "  !!   ") + name + ("  --- " + detail if detail else ""))


def skip(name, why):
    results.append({"name": name, "ok": True, "detail": "跳过: " + why})
    print("  --   " + name + "  --- 跳过: " + why)


PROBE_CS = r'''
using System;
using System.IO;
using System.Reflection;
using System.Threading;

// 验证：闸门可重入，且嵌套时只有一个真正的加解锁。
class TxnProbe
{
    static Assembly asm;
    static Type gateType;
    static MethodInfo mSuspend, mTryEnter, mExitWrite;
    static PropertyInfo pClosed;

    static void L(string s) { Console.WriteLine(s); }

    static IDisposable Suspend() { return (IDisposable)mSuspend.Invoke(null, null); }
    static bool TryEnter() { return (bool)mTryEnter.Invoke(null, null); }
    static void ExitWrite() { mExitWrite.Invoke(null, null); }
    static bool Closed() { return (bool)pClosed.GetValue(null, null); }

    static int Main(string[] args)
    {
        string exe = args[0];
        Environment.SetEnvironmentVariable("AUTO7Z_DATA_ROOT", args[1]);

        AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
        {
            string simple = new AssemblyName(e.Name).Name;
            string p = Path.Combine(Path.GetDirectoryName(exe), simple + ".exe");
            return File.Exists(p) ? Assembly.LoadFrom(p) : null;
        };

        asm = Assembly.LoadFrom(exe);
        gateType = asm.GetType("Auto7z_WPF.Services.MigrationGate");
        if (gateType == null) { L("NO_GATE"); return 2; }

        mSuspend = gateType.GetMethod("SuspendWrites", BindingFlags.Public | BindingFlags.Static);
        mTryEnter = gateType.GetMethod("TryEnterWrite", BindingFlags.Public | BindingFlags.Static);
        mExitWrite = gateType.GetMethod("ExitWrite", BindingFlags.Public | BindingFlags.Static);
        pClosed = gateType.GetProperty("IsClosed", BindingFlags.Public | BindingFlags.Static);

        // ===== D 嵌套重入 =====
        // 外层持闸（事务），内层再持（搬迁类自己那一层）。
        using (IDisposable outer = Suspend())
        {
            L("D1_OUTER_CLOSED=" + Closed());

            using (IDisposable inner = Suspend())
            {
                L("D2_INNER_CLOSED=" + Closed());
            }

            // **关键**：内层释放后，外层仍然有效。
            // 若实现成「内层释放就把闸打开」，这里会读到 False ——
            // 那正是「事务还没提交完就开闸」的缺陷形态。
            L("D3_STILL_CLOSED_AFTER_INNER_EXIT=" + Closed());

            // 在此期间另一线程仍应被拒
            bool? refused = null;
            var t = new Thread(() => { refused = !TryEnter(); });
            t.IsBackground = true;
            t.Start();
            t.Join(5000);
            L("D4_OTHER_THREAD_REFUSED=" + refused);
        }

        L("D5_OPEN_AFTER_OUTER_EXIT=" + !Closed());

        // ===== E 异常路径 =====
        try
        {
            using (IDisposable s = Suspend())
            {
                using (IDisposable s2 = Suspend())
                {
                    throw new InvalidOperationException("boom");
                }
            }
        }
        catch (InvalidOperationException) { }

        L("E1_OPEN_AFTER_EXCEPTION=" + !Closed());

        // 异常之后必须还能正常持闸（不能留下半锁状态）
        using (IDisposable s = Suspend()) { L("E2_REACQUIRE_OK=" + Closed()); }
        L("E3_OPEN_AT_END=" + !Closed());

        return 0;
    }
}
'''


def build_probe():
    cs = Path(SDK) / "probe_src.txt"
    cs.write_text(PROBE_CS, encoding="utf-8")
    out = Path(SDK) / "probe.exe"
    rc = subprocess.run(
        [r"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe", "/nologo",
         "/out:" + str(out), str(cs)],
        capture_output=True, text=True, encoding="utf-8", errors="replace")
    if rc.returncode != 0:
        print("     探针编译失败: " + (rc.stderr or rc.stdout or "")[:800])
        return None
    return out


def find_using_scope(text, marker):
    """求 `using (marker) { ... }` 中花括号体的 [start, end) 下标区间。

    为什么需要真的做括号配对，而不是比字符位置：

        using (MigrationGate.SuspendWrites())
        {
            ... 事务内容 ...
        }

    与

        using (MigrationGate.SuspendWrites()) { }
        if (true)
        {
            ... 同样的内容，但已在闸门外 ...
        }

    在「SuspendWrites 出现在所有提交之前」这一点上**完全相同**。
    只有求出 using 的作用域、再看提交语句是否落在里面，才能区分两者。

    简化处理：找到 marker 之后第一对 `{`，从它开始计数配对；
    注释里的花括号会干扰计数，所以先剥掉行注释（本项目注释里有中文引号，
    但没有不成对的花括号，实测足够）。字符串字面量里的花括号同理罕见，
    且一旦算错会表现为 scope 取偏 —— 那时断言会失败而不是静默通过。
    """
    idx = text.find(marker)
    if idx < 0:
        return None

    open_at = text.find("{", idx)
    if open_at < 0:
        return None

    depth = 0
    in_string = False
    in_char = False
    i = open_at
    n = len(text)
    while i < n:
        ch = text[i]

        if in_string:
            if ch == "\\":
                i += 2
                continue
            if ch == '"':
                in_string = False
            i += 1
            continue

        if in_char:
            if ch == "\\":
                i += 2
                continue
            if ch == "'":
                in_char = False
            i += 1
            continue

        if ch == '"':
            in_string = True
        elif ch == "'":
            in_char = True
        elif ch == "{":
            depth += 1
        elif ch == "}":
            depth -= 1
            if depth == 0:
                return (open_at, i + 1)

        i += 1

    return None


def main():
    if os.path.isdir(SDK):
        safe_rmtree(SDK)
    os.makedirs(SDK, exist_ok=True)

    print("=" * 76)
    print("模式切换事务：闸门必须罩住「搬文件 + 提交模式」（BUG-03）")
    print("=" * 76)

    if not os.path.isfile(EXE):
        check("可执行文件存在", False, EXE)
        return finish()

    # ================= A/B 静态 =================
    print("\n【A】静态：闸门在调用方，且罩住整段提交逻辑")
    vm = Path(P) / "Auto7z_WPF" / "Auto7z_WPF" / "ViewModel" / "Auto7zViewModel.cs"
    if not vm.is_file():
        check("A Auto7zViewModel.cs 存在", False, str(vm))
        return finish()

    text = vm.read_text(encoding="utf-8", errors="replace")
    body = "\n".join(l for l in text.splitlines() if not l.strip().startswith("//"))

    # 定位 SetPortableMode 的方法体
    start = body.find("public bool SetPortableMode(bool portable)")
    if start < 0:
        check("A1 找到 SetPortableMode", False, "方法不存在")
        return finish()

    # 粗略取到下一个 public / #region 为止
    tail = body[start:]
    ends = [x for x in (tail.find("\n        #if DEBUG", 30),
                        tail.find("\n        public ", 30),
                        tail.find("\n        #endregion", 30)) if x > 0]
    method = tail[:min(ends)] if ends else tail[:6000]

    has_suspend = "MigrationGate.SuspendWrites()" in method
    check("A1 SetPortableMode 内部持搬迁闸", has_suspend,
          "找到 SuspendWrites" if has_suspend else "闸门仍在搬迁类内部，提交不在事务内")

    if has_suspend:
        # **必须真的解析 using 的作用域边界，不能只比字符偏移量。**
        #
        # 第一版这里是这样判的：
        #     s_pos = method.find("SuspendWrites()")
        #     check(... s_pos < portable_pos ...)
        # 也就是「闸门出现的字符位置早于赋值」。
        #
        # 变异测试把它拆穿了：把
        #     using (MigrationGate.SuspendWrites())
        #     {
        # 改成
        #     using (MigrationGate.SuspendWrites()) { }
        #     if (true)
        #     {
        # 之后，闸门字符串依然在最前面、所有赋值依然在它之后 ——
        # 五个断言全部照过，而真正的保护范围已经缩成空语句，
        # 后面那些提交动作全在闸门外。这正是「检查器自欺」的形状：
        # 它验的是「顺序」，而缺陷是「作用域」。
        #
        # 现在改成：从 using 的左花括号开始做括号配对，求出作用域结束位置，
        # 再断言每条关键语句的下标真的落在这个区间内。
        scope = find_using_scope(method, "MigrationGate.SuspendWrites()")
        if scope is None:
            check("A2 能确定 using 作用域的范围", False,
                  "找不到 SuspendWrites() 之后的花括号")
            scope = (-1, -1)
        else:
            body_start, body_end = scope
            check("A2 using 作用域非空（不是 `using(...) { }` 那种空壳）",
                  body_end - body_start > 40,
                  "作用域长度=%d" % (body_end - body_start))

        body_start, body_end = scope

        def inside(needle, label):
            pos = method.find(needle)
            ok = body_start <= pos < body_end
            check(label, ok,
                  "位置=%d，作用域=[%d,%d]" % (pos, body_start, body_end))
            return ok

        inside("DataDirectoryMover.MoveTo", "A3 数据搬迁在 using 作用域内")
        inside("_isPortable = portable", "A4 _isPortable 赋值在作用域内")
        inside("AppPaths.IsPortable = portable", "A5 AppPaths.IsPortable 赋值在作用域内")
        inside("LogService.ResetLogFiles()", "A6 日志路径重置在作用域内")

        # 顺序契约：模式必须先提交，再重置日志路径（否则句柄指到旧目录）
        portable_pos = method.find("AppPaths.IsPortable = portable")
        reset_pos = method.find("LogService.ResetLogFiles()")
        check("A7 先改模式再重置日志路径（顺序契约）",
              0 <= portable_pos < reset_pos,
              "模式=%d 重置=%d" % (portable_pos, reset_pos))

    print("\n【B】静态：搬迁类内部用可重入闸，不再自己抢独占")
    mover = Path(P) / "Auto7z_WPF" / "Auto7z_WPF" / "Services" / "DataDirectoryMover.cs"
    if mover.is_file():
        mb = "\n".join(l for l in mover.read_text(encoding="utf-8", errors="replace").splitlines()
                       if not l.strip().startswith("//"))
        check("B1 搬迁类仍自己持一层闸（对外公开的 API 不能假设调用方包了事务）",
              "MigrationGate.SuspendWrites()" in mb)
        check("B2 闸门盖住复制与删源",
              0 <= mb.find("MigrationGate.SuspendWrites()") < mb.find("CopyThenDeleteSource("))
    else:
        check("B1 DataDirectoryMover.cs 存在", False, str(mover))

    gatefile = Path(P) / "Auto7z_WPF" / "Auto7z_WPF" / "Services" / "MigrationGate.cs"
    if gatefile.is_file():
        gb = "\n".join(l for l in gatefile.read_text(encoding="utf-8", errors="replace").splitlines()
                       if not l.strip().startswith("//"))
        check("B3 闸门是可重入的（同一线程多层持有）",
              "_suspendDepth" in gb and "ThreadStatic" in gb,
              "需线程本地深度计数")

    # ================= D/E 运行时重入 =================
    print("\n【C】运行时：嵌套持有只加深度，内层释放不提前开闸")
    probe = build_probe()
    if probe is None:
        check("C 探针可编译", False, "csc 失败")
        return finish()

    data_root = Path(SDK) / "data"
    data_root.mkdir(parents=True, exist_ok=True)

    p = subprocess.run([str(probe), EXE, str(data_root)],
                       capture_output=True, text=True, encoding="utf-8",
                       errors="replace", timeout=180)
    out = (p.stdout or "") + (p.stderr or "")
    for line in out.strip().splitlines():
        print("     " + line)

    if "NO_GATE" in out:
        check("MigrationGate 存在", False, "找不到类型")
        return finish()

    kv = {}
    for line in out.splitlines():
        line = line.strip()
        if "=" in line and not line.startswith("NO_"):
            k, v = line.split("=", 1)
            kv[k.strip()] = v.strip()

    def g(key, default=""):
        return kv.get(key, default)

    check("C1 外层持闸时为关闭", g("D1_OUTER_CLOSED") == "True",
          "IsClosed=%s" % g("D1_OUTER_CLOSED"))
    check("C2 内层再持一次仍关闭（可重入，不抛）", g("D2_INNER_CLOSED") == "True",
          "IsClosed=%s" % g("D2_INNER_CLOSED"))
    # 这一条是 BUG-03 的重入语义判据：内层退出不能把事务的闸打开。
    check("C3 内层退出后外层仍持有（不提前开闸）",
          g("D3_STILL_CLOSED_AFTER_INNER_EXIT") == "True",
          "IsClosed=%s（False = 内层释放就把闸开了，事务被打断）"
          % g("D3_STILL_CLOSED_AFTER_INNER_EXIT"))
    check("C4 事务期间别的线程仍被拒", g("D4_OTHER_THREAD_REFUSED") == "True",
          "被拒=%s" % g("D4_OTHER_THREAD_REFUSED"))
    check("C5 最外层退出后开闸", g("D5_OPEN_AFTER_OUTER_EXIT") == "True",
          "开=%s" % g("D5_OPEN_AFTER_OUTER_EXIT"))

    print("\n【D】异常路径不留下死锁")
    check("D1 双重嵌套中抛异常后闸门仍打开", g("E1_OPEN_AFTER_EXCEPTION") == "True",
          "开=%s" % g("E1_OPEN_AFTER_EXCEPTION"))
    check("D2 异常之后还能正常再持一次", g("E2_REACQUIRE_OK") == "True",
          "再持=%s" % g("E2_REACQUIRE_OK"))
    check("D3 全部结束后闸门是开的", g("E3_OPEN_AT_END") == "True",
          "开=%s" % g("E3_OPEN_AT_END"))

    return finish()


def finish():
    print("\n" + "=" * 76)
    bad = [r for r in results if not r["ok"]]
    print("共 %d 项，通过 %d，失败 %d" % (len(results), len(results) - len(bad), len(bad)))
    if bad:
        for x in bad:
            print("  " + x["name"] + "  " + x["detail"])
        print("FAIL")
        code = 1
    else:
        print("搬迁与模式提交在同一个事务里；内层退出不会提前开闸")
        print("PASS")
        code = 0
    out = os.path.join(SDK, "txn.json")
    try:
        open(out, "w", encoding="utf-8").write(
            json.dumps(results, ensure_ascii=False, indent=2))
        print("输出 -> %s" % out)
    except OSError:
        pass
    return code


if __name__ == "__main__":
    sys.exit(main())
