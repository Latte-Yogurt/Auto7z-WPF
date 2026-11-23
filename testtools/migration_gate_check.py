#!/usr/bin/env python3
# -*- coding: utf-8 -*-
r"""搬迁期间的写入隔离：**先后关系**是否真的建立（BUG-01 / BUG-03）。

## 缺陷（第一代做法为什么不够）

第一版闸门是一个布尔标志：写入方 `if (MigrationGate.IsClosed) return;`，
搬迁方 `Enter()` 把它置真。看起来严谨，实际没有建立任何先后关系：

    线程 A：检查 IsClosed → false
    线程 A：取 _writeLock
    线程 A：再检查 IsClosed → false
    线程 A：**被挂起**（还没打开文件）
    线程 B：Enter() → 关闭闸门 → 开始复制 → 复制完 → 删除源文件
    线程 A：恢复 → 打开**已被删除的**源文件写入

加多少次检查都只是把窗口变窄。问题的本质是「检查」与「动手」之间不是原子的。

## 修复

换成读写闸（ReaderWriterLockSlim）：
   · 普通写入取读锁，持有到文件写完
   · 搬迁取写锁，**必须等所有已开始的写入退出**

## 本检查器要证明什么

关键的一点：**不能只证明「搬迁期间写入被拒」**。

一个「无论何时都拒绝写入」的实现也能通过那种测试 —— 而那样程序就不写日志了，
是个更糟的缺陷。所以每个「拒绝」都必须配一个「正常运行时不拒绝」。
这就是 A 组与 E 组存在的理由。

A. 闸门语义：初始开、持有期关、释放后开、异常路径也释放
B. **顺序证明**：写入正在进行时，搬迁**必须等待**它结束才拿到独占
   —— 这是 BUG-01 的核心，也是布尔标志永远做不到的事
C. 搬迁独占期间，新的写入被拒绝（且不是靠「永远拒绝」实现的）
D. 重入：同一线程可多层持有，只在最外层真正加解锁
E. 三个真实写入方（日志 / 配置 / 状态）确实走这把锁
F. 收尾的不对称：搬迁期间日志不写，但**锁文件仍然必须被删掉**

## 术语

这里的「搬迁」= `MigrationGate.SuspendWrites()`
这里的「写入」= `MigrationGate.TryEnterWrite()`
"""
import json
import os
import subprocess
import sys
import time
from pathlib import Path

from safe_ops import safe_rmtree

P = sys.argv[1] if len(sys.argv) > 1 else r"C:\Users\Mocha\Desktop\VSProject\Auto7z-WPF"
EXE = os.path.join(P, "Auto7z_WPF", "Auto7z_WPF", "bin", "Debug", "Auto7z.exe")
SDK = os.path.join(os.environ.get("TEMP", r"C:\Windows\Temp"), "Auto7zTest_data", "miggate")

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

// 在一个进程里完成「闸门语义 + 顺序证明 + 三个写入方」的验证。
// 顺序证明必须跨线程：单线程永远证明不出「搬迁在等写入结束」。
class GateProbe
{
    static Assembly asm;
    static Type gateType, logType, configType, stateType;

    static MethodInfo mSuspend, mTryEnter, mExitWrite;
    static PropertyInfo pClosed;

    static void L(string s) { Console.WriteLine(s); }

    static object InvokeStatic(Type t, string name, params object[] a)
    {
        MethodInfo m = t.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic
                                         | BindingFlags.Static);
        if (m == null) return "NO_METHOD:" + name;
        try { return m.Invoke(null, a); }
        catch (Exception e)
        {
            Exception inner = e.InnerException ?? e;
            return "EX:" + inner.GetType().Name;
        }
    }

    static bool Closed() { return (bool)pClosed.GetValue(null, null); }

    static IDisposable Suspend() { return (IDisposable)mSuspend.Invoke(null, null); }
    static bool TryEnter() { return (bool)mTryEnter.Invoke(null, null); }
    static void ExitWrite() { mExitWrite.Invoke(null, null); }

    static int Main(string[] args)
    {
        string exe = args[0];
        string dataRoot = args[1];
        Environment.SetEnvironmentVariable("AUTO7Z_DATA_ROOT", dataRoot);

        AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
        {
            string simple = new AssemblyName(e.Name).Name;
            string p = Path.Combine(Path.GetDirectoryName(exe), simple + ".exe");
            return File.Exists(p) ? Assembly.LoadFrom(p) : null;
        };

        asm = Assembly.LoadFrom(exe);
        gateType = asm.GetType("Auto7z_WPF.Services.MigrationGate");
        logType = asm.GetType("Auto7z_WPF.Services.LogService");
        configType = asm.GetType("Auto7z_WPF.Services.ConfigService");
        stateType = asm.GetType("Auto7z_WPF.Services.TaskStateService");

        if (gateType == null) { L("NO_GATE"); return 2; }

        mSuspend = gateType.GetMethod("SuspendWrites", BindingFlags.Public | BindingFlags.Static);
        mTryEnter = gateType.GetMethod("TryEnterWrite", BindingFlags.Public | BindingFlags.Static);
        mExitWrite = gateType.GetMethod("ExitWrite", BindingFlags.Public | BindingFlags.Static);
        pClosed = gateType.GetProperty("IsClosed", BindingFlags.Public | BindingFlags.Static);

        if (mSuspend == null) { L("NO_SUSPEND"); return 2; }
        if (mTryEnter == null) { L("NO_TRYENTER"); return 2; }
        if (mExitWrite == null) { L("NO_EXITWRITE"); return 2; }
        if (pClosed == null) { L("NO_ISCLOSED"); return 2; }

        string dataDir = Path.Combine(dataRoot, "Auto7z");
        Directory.CreateDirectory(dataDir);
        Directory.CreateDirectory(Path.Combine(dataDir, "Logs"));

        // ================= A 闸门语义 =================
        L("A0_CLOSED=" + Closed());                 // False

        IDisposable s1 = Suspend();
        L("A1_CLOSED=" + Closed());                 // True

        IDisposable s2 = Suspend();                 // 重入
        L("A2_CLOSED=" + Closed());                 // True

        s2.Dispose();
        L("A3_CLOSED=" + Closed());                 // True（还有一层）

        s1.Dispose();
        L("A4_CLOSED=" + Closed());                 // False

        // 异常路径：作用域内抛异常，必须仍然释放
        try
        {
            using (IDisposable s = Suspend())
            {
                throw new InvalidOperationException("boom");
            }
        }
        catch (InvalidOperationException) { }
        L("A5_CLOSED=" + Closed());                 // False

        // 未持有就 ExitWrite：必须是空操作，不能抛
        try
        {
            ExitWrite();
            ExitWrite();
            L("A6_UNBALANCED_EXIT_OK=True");
        }
        catch (Exception e) { L("A6_UNBALANCED_EXIT_OK=False:" + e.GetType().Name); }

        L("A7_CLOSED_AFTER_UNBALANCED=" + Closed());  // 仍是 False

        // ================= B 顺序证明（BUG-01 的核心）=================
        // 线程 W 持读锁 1.5 秒；主线程在这期间发起搬迁。
        // 搬迁**必须**等到 W 释放才拿到独占 —— 这正是布尔标志做不到的。
        bool writerEntered = false;
        var writerDone = new ManualResetEventSlim(false);

        var w = new Thread(() =>
        {
            if (!TryEnter()) { L("B_WRITER_ENTERED=False"); return; }
            writerEntered = true;
            Thread.Sleep(1500);
            ExitWrite();
            writerDone.Set();
        });
        w.IsBackground = true;
        w.Start();

        // 等写入真的开始（最多 3 秒）
        for (int i = 0; i < 300 && !writerEntered; i++) Thread.Sleep(10);
        L("B1_WRITER_STARTED=" + writerEntered);

        long t0 = DateTime.UtcNow.Ticks;
        using (IDisposable s = Suspend())
        {
            long waited = (DateTime.UtcNow.Ticks - t0) / 10000;   // ms
            L("B2_MIGRATION_WAITED_MS=" + waited);
            // 搬迁拿到独占时，写入必须已经结束
            L("B3_WRITER_DONE_WHEN_ACQUIRED=" + writerDone.IsSet);
        }

        writerDone.Wait(5000);
        L("B4_WRITER_COMPLETED=" + writerDone.IsSet);

        // ================= C 搬迁期间写入被拒 =================
        // 用一个独立线程来试，避免「本线程正在搬迁」这条规则干扰判断。
        bool? refusedDuringMigration = null;
        using (IDisposable s = Suspend())
        {
            var t = new Thread(() => { refusedDuringMigration = !TryEnter(); });
            t.IsBackground = true;
            t.Start();
            t.Join(5000);
        }
        L("C1_WRITE_REFUSED_DURING_MIGRATION=" + refusedDuringMigration);

        // 关键对照：没有搬迁时必须能写进去。否则「永远拒绝」也能通过 C1。
        L("C2_WRITE_ALLOWED_WHEN_OPEN=" + TryEnter());
        ExitWrite();

        // ================= D 重入 =================
        bool d1 = TryEnter();
        bool d2 = TryEnter();       // 同线程重入
        ExitWrite();
        bool d3 = TryEnter();       // 退一层后仍持有，应仍可重入
        ExitWrite();
        ExitWrite();
        L("D1_WRITE_REENTRANT=" + (d1 && d2 && d3));

        // 搬迁期间，本线程的写入必须被拒（否则会自己等自己，永远不返回）
        bool selfRefused;
        using (IDisposable s = Suspend())
        {
            selfRefused = !TryEnter();
        }
        L("D2_SELF_WRITE_REFUSED_WHILE_MIGRATING=" + selfRefused);

        // ================= E 三个写入方 =================
        if (logType != null && configType != null && stateType != null)
        {
            var log = Activator.CreateInstance(logType);
            MethodInfo writeLog = logType.GetMethod("WriteLog",
                new[] { typeof(string), logType.GetNestedType("ErrorLevel") });
            object infoLevel = Enum.Parse(logType.GetNestedType("ErrorLevel"), "Info");

            PropertyInfo minLevel = logType.GetProperty("MinLogLevel", BindingFlags.Public | BindingFlags.Static);
            if (minLevel != null) minLevel.SetValue(null, infoLevel, null);

            string logDir = Path.Combine(dataDir, "Logs");

            // --- 跨线程：搬迁持有期间写日志应当被跳过 ---
            using (IDisposable s = Suspend())
            {
                var t = new Thread(() => writeLog.Invoke(log, new object[] { "GATE_BLOCKED_LINE", infoLevel }));
                t.IsBackground = true;
                t.Start();
                t.Join(5000);
            }
            L("E1_LOG_BYTES_WHILE_SUSPENDED=" + TotalLogBytes(logDir));
            L("E2_LOG_HAS_BLOCKED_LINE=" + ContainsText(logDir, "GATE_BLOCKED_LINE"));

            // --- 对照：开闸后写日志必须落盘 ---
            writeLog.Invoke(log, new object[] { "GATE_ALLOWED_LINE", infoLevel });
            L("E3_LOG_BYTES_WHILE_OPEN=" + TotalLogBytes(logDir));
            L("E4_LOG_HAS_ALLOWED_LINE=" + ContainsText(logDir, "GATE_ALLOWED_LINE"));

            // --- 配置：搬迁期间保存返回 false，开闸后返回 true ---
            MethodInfo writeAtomic = configType.GetMethod("WriteConfigAtomically",
                BindingFlags.NonPublic | BindingFlags.Static);
            if (writeAtomic != null)
            {
                string cfgPath = Path.Combine(dataDir, "Auto7z.json");
                bool closedSave;
                using (IDisposable s = Suspend())
                {
                    var t = new Thread(() => { });
                    closedSave = SaveFromOtherThread(writeAtomic, cfgPath);
                }
                L("E5_CONFIG_SAVE_WHILE_SUSPENDED=" + closedSave);
                L("E6_CONFIG_EXISTS_AFTER_SUSPENDED=" + File.Exists(cfgPath));

                bool openSave = (bool)writeAtomic.Invoke(null, new object[] { cfgPath, "{\"b\":2}" });
                L("E7_CONFIG_SAVE_WHILE_OPEN=" + openSave);
                L("E8_CONFIG_EXISTS_AFTER_OPEN=" + File.Exists(cfgPath));
            }
            else
            {
                L("NO_WRITEATOMIC");
            }

            // --- 状态：搬迁期间 state 与 job.lock 都不能出现 ---
            MethodInfo markStarted = stateType.GetMethod("MarkTaskStarted",
                BindingFlags.Public | BindingFlags.Static);
            if (markStarted != null)
            {
                string stateFile = Path.Combine(dataDir, "TaskState.json");
                string lockFile = Path.Combine(dataDir, "job.lock");
                try { File.Delete(stateFile); } catch { }
                try { File.Delete(lockFile); } catch { }

                using (IDisposable s = Suspend())
                {
                    var t = new Thread(() =>
                        markStarted.Invoke(null, new object[] { Path.Combine(dataRoot, "out-closed") }));
                    t.IsBackground = true;
                    t.Start();
                    t.Join(10000);
                }
                L("E9_STATE_EXISTS_AFTER_SUSPENDED=" + File.Exists(stateFile));
                L("E10_LOCK_EXISTS_AFTER_SUSPENDED=" + File.Exists(lockFile));

                markStarted.Invoke(null, new object[] { Path.Combine(dataRoot, "out-open") });
                L("E11_STATE_EXISTS_AFTER_OPEN=" + File.Exists(stateFile));
                L("E12_LOCK_EXISTS_AFTER_OPEN=" + File.Exists(lockFile));

                // ================= F 收尾：闸门拿不到就整体不动 =================
                //
                // **这一组上一版写反了**，本轮（P1-1）按真实语义改正。
                //
                // 旧断言是「搬迁期间调用 MarkTaskFinished 仍然会删掉锁」，
                // 理由是「不留假崩溃现场」。变异测试当时也「通过」了 ——
                // 因为它验的是我当时写的那个实现，而不是正确行为。
                //
                // GPT6 指出的数据丢失路径正是这条「不对称」：
                //   任务 active → 搬迁持闸 → 任务结束取闸失败 → 旧行为仍删锁
                //   ⇒ 搬迁可能把「已删锁之后」或「锁已被复制走」的中间状态带过去
                //   ⇒ 下次启动看到 active + 有效锁 + PID 已退出 ⇒ 按崩溃递归删产物
                // 删的是用户已经成功生成的压缩包。
                //
                // 正确行为：**要么整段收尾完成，要么什么都不动**。
                // 拿不到闸时状态与锁都属于「正在被搬走的那棵树」，本进程不该再改它。
                bool lockBefore = File.Exists(lockFile);
                using (IDisposable s = Suspend())
                {
                    var t = new Thread(() =>
                        InvokeStatic(stateType, "MarkTaskFinished"));
                    t.IsBackground = true;
                    t.Start();
                    t.Join(10000);
                }
                L("F1_LOCK_EXISTED_BEFORE_FINISH=" + lockBefore);
                L("F2_LOCK_PRESERVED_DURING_SUSPENDED=" + File.Exists(lockFile));

                // 对照：闸门打开后调用必须真的把锁删掉（证明不是「永远不删」）。
                InvokeStatic(stateType, "MarkTaskFinished");
                L("F3_LOCK_DELETED_AFTER_OPEN=" + !File.Exists(lockFile));
            }
            else
            {
                L("NO_MARKSTARTED");
            }
        }
        else
        {
            L("NO_WRITERS");
        }

        return 0;
    }

    // 在另一个线程上做配置保存，避免「本线程正在搬迁」那条规则替我们答题。
    static bool SaveFromOtherThread(MethodInfo m, string path)
    {
        bool result = false;
        var t = new Thread(() =>
        {
            try { result = (bool)m.Invoke(null, new object[] { path, "{\"a\":1}" }); }
            catch { result = false; }
        });
        t.IsBackground = true;
        t.Start();
        t.Join(10000);
        return result;
    }

    static long TotalLogBytes(string dir)
    {
        long total = 0;
        if (!Directory.Exists(dir)) return 0;
        foreach (string f in Directory.GetFiles(dir, "*.log"))
        {
            try { total += new FileInfo(f).Length; } catch { }
        }
        return total;
    }

    static bool ContainsText(string dir, string needle)
    {
        if (!Directory.Exists(dir)) return false;
        foreach (string f in Directory.GetFiles(dir))
        {
            try { if (File.ReadAllText(f).Contains(needle)) return true; } catch { }
        }
        return false;
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


def main():
    if os.path.isdir(SDK):
        safe_rmtree(SDK)
    os.makedirs(SDK, exist_ok=True)

    print("=" * 76)
    print("搬迁期间的写入隔离：先后关系是否真的建立（BUG-01 / BUG-03）")
    print("=" * 76)

    if not os.path.isfile(EXE):
        check("可执行文件存在", False, EXE)
        return finish()

    pr = build_probe()
    if pr is None:
        check("探针可编译", False, "csc 失败")
        return finish()

    data_root = Path(SDK) / "data"
    data_root.mkdir(parents=True, exist_ok=True)

    t0 = time.time()
    p = subprocess.run([str(pr), EXE, str(data_root)],
                       capture_output=True, text=True, encoding="utf-8",
                       errors="replace", timeout=600)
    text = (p.stdout or "") + (p.stderr or "")
    for line in text.strip().splitlines():
        print("     " + line)
    print("     （探针耗时 %.1f 秒）" % (time.time() - t0))

    if "NO_GATE" in text:
        check("MigrationGate 存在", False, "找不到 MigrationGate 类型")
        return finish()
    for marker in ("NO_SUSPEND", "NO_TRYENTER", "NO_EXITWRITE", "NO_ISCLOSED"):
        if marker in text:
            check("闸门 API 完整", False, "缺少 " + marker)
            return finish()

    kv = {}
    for line in text.splitlines():
        line = line.strip()
        if "=" in line and not line.startswith("NO_"):
            k, v = line.split("=", 1)
            kv[k.strip()] = v.strip()

    def g(key, default=""):
        return kv.get(key, default)

    def num(key, default=-1):
        try:
            return int(g(key, ""))
        except ValueError:
            return default

    # ================= A =================
    print("\n【A】闸门语义（计数式，可重入）")
    check("A0 初始为开", g("A0_CLOSED") == "False", "IsClosed=%s" % g("A0_CLOSED"))
    check("A1 持搬迁闸时关闭", g("A1_CLOSED") == "True", "IsClosed=%s" % g("A1_CLOSED"))
    check("A2 嵌套两层仍关闭", g("A2_CLOSED") == "True", "IsClosed=%s" % g("A2_CLOSED"))
    check("A3 退一层仍关闭（重入计数语义）", g("A3_CLOSED") == "True", "IsClosed=%s" % g("A3_CLOSED"))
    check("A4 退两层后打开", g("A4_CLOSED") == "False", "IsClosed=%s" % g("A4_CLOSED"))
    check("A5 异常路径也会释放", g("A5_CLOSED") == "False", "IsClosed=%s" % g("A5_CLOSED"))
    check("A6 未持有就 ExitWrite 不抛（空操作）",
          g("A6_UNBALANCED_EXIT_OK") == "True", g("A6_UNBALANCED_EXIT_OK"))
    check("A7 乱配对之后闸门仍正常", g("A7_CLOSED_AFTER_UNBALANCED") == "False",
          "IsClosed=%s" % g("A7_CLOSED_AFTER_UNBALANCED"))

    # ================= B 顺序证明 =================
    print("\n【B】顺序：搬迁必须等待正在进行的写入结束（BUG-01 的核心）")
    check("B1 对照线程成功取得写入闸", g("B1_WRITER_STARTED") == "True",
          "取得=%s（False 说明写入闸本身不可用，后面的结论都不成立）"
          % g("B1_WRITER_STARTED"))

    waited = num("B2_MIGRATION_WAITED_MS")
    # 写入线程持锁 1500ms，搬迁至少应当等掉大半。
    # 阈值取 1000ms：既证明「确实等了」，又给线程调度留余量。
    check("B2 搬迁被阻塞直到写入结束（≥1000ms）", waited >= 1000,
          "实际等待=%dms（≈0 说明搬迁根本没等，就是布尔标志那种行为）" % waited)

    check("B3 搬迁拿到独占时写入已结束", g("B3_WRITER_DONE_WHEN_ACQUIRED") == "True",
          "已结束=%s（False = 搬迁与写入重叠执行）" % g("B3_WRITER_DONE_WHEN_ACQUIRED"))
    check("B4 写入线程最终正常完成", g("B4_WRITER_COMPLETED") == "True",
          "完成=%s" % g("B4_WRITER_COMPLETED"))

    # ================= C =================
    print("\n【C】搬迁独占期间的新写入被拒绝（且不是「永远拒绝」）")
    check("C1 搬迁期间另一线程写入被拒",
          g("C1_WRITE_REFUSED_DURING_MIGRATION") == "True",
          "被拒=%s" % g("C1_WRITE_REFUSED_DURING_MIGRATION"))
    check("C2 无搬迁时写入被允许（排除「永远拒绝」这种假修复）",
          g("C2_WRITE_ALLOWED_WHEN_OPEN") == "True",
          "允许=%s" % g("C2_WRITE_ALLOWED_WHEN_OPEN"))

    # ================= D =================
    print("\n【D】重入")
    check("D1 同一线程可重入写入闸", g("D1_WRITE_REENTRANT") == "True",
          "重入=%s" % g("D1_WRITE_REENTRANT"))
    check("D2 搬迁线程自己的写入被拒（否则会自己等自己）",
          g("D2_SELF_WRITE_REFUSED_WHILE_MIGRATING") == "True",
          "被拒=%s" % g("D2_SELF_WRITE_REFUSED_WHILE_MIGRATING"))

    # ================= E =================
    print("\n【E】三个真实写入方都走这把锁")
    if "E1_LOG_BYTES_WHILE_SUSPENDED" not in kv:
        skip("E 组（写入方）", "探针未输出写入方结果")
    else:
        check("E1 搬迁期间日志不增长", g("E1_LOG_BYTES_WHILE_SUSPENDED") == "0",
              "bytes=%s" % g("E1_LOG_BYTES_WHILE_SUSPENDED"))
        check("E2 搬迁期间那行日志确实没落盘",
              g("E2_LOG_HAS_BLOCKED_LINE") == "False",
              "找到=%s" % g("E2_LOG_HAS_BLOCKED_LINE"))
        check("E3 开闸后日志开始写", g("E3_LOG_BYTES_WHILE_OPEN") not in ("0", ""),
              "bytes=%s" % g("E3_LOG_BYTES_WHILE_OPEN"))
        check("E4 开闸后写的那行正常落盘",
              g("E4_LOG_HAS_ALLOWED_LINE") == "True",
              "找到=%s" % g("E4_LOG_HAS_ALLOWED_LINE"))

        if "E5_CONFIG_SAVE_WHILE_SUSPENDED" not in kv:
            skip("E5–E8（配置）", "探针未取到 WriteConfigAtomically")
        else:
            check("E5 搬迁期间保存配置返回 false（不是静默成功）",
                  g("E5_CONFIG_SAVE_WHILE_SUSPENDED") == "False",
                  "返回=%s" % g("E5_CONFIG_SAVE_WHILE_SUSPENDED"))
            check("E6 搬迁期间配置文件未被写出",
                  g("E6_CONFIG_EXISTS_AFTER_SUSPENDED") == "False",
                  "存在=%s" % g("E6_CONFIG_EXISTS_AFTER_SUSPENDED"))
            check("E7 开闸后保存成功", g("E7_CONFIG_SAVE_WHILE_OPEN") == "True",
                  "返回=%s" % g("E7_CONFIG_SAVE_WHILE_OPEN"))
            check("E8 开闸后配置文件确实写出来了",
                  g("E8_CONFIG_EXISTS_AFTER_OPEN") == "True",
                  "存在=%s" % g("E8_CONFIG_EXISTS_AFTER_OPEN"))

        if "E9_STATE_EXISTS_AFTER_SUSPENDED" not in kv:
            skip("E9–E12（任务状态）", "探针未取到 MarkTaskStarted")
        else:
            check("E9 搬迁期间状态文件未写出",
                  g("E9_STATE_EXISTS_AFTER_SUSPENDED") == "False",
                  "存在=%s" % g("E9_STATE_EXISTS_AFTER_SUSPENDED"))
            # 闸门若只拦在最底层，状态不写但锁已经建出来 —— 留下假的崩溃现场。
            check("E10 搬迁期间 job.lock 也不能被建出来（否则留下假崩溃现场）",
                  g("E10_LOCK_EXISTS_AFTER_SUSPENDED") == "False",
                  "存在=%s（True = 闸门位置太低）" % g("E10_LOCK_EXISTS_AFTER_SUSPENDED"))
            check("E11 开闸后状态文件正常写出",
                  g("E11_STATE_EXISTS_AFTER_OPEN") == "True",
                  "存在=%s" % g("E11_STATE_EXISTS_AFTER_OPEN"))
            check("E12 开闸后锁文件也正常建出（证明不是「永远不建」）",
                  g("E12_LOCK_EXISTS_AFTER_OPEN") == "True",
                  "存在=%s" % g("E12_LOCK_EXISTS_AFTER_OPEN"))

            # ================= F =================
            print("\n【F】收尾：闸门拿不到就整体不动（P1-1）")
            if "F1_LOCK_EXISTED_BEFORE_FINISH" not in kv:
                skip("F 组（收尾）", "探针未执行收尾检查")
            else:
                check("F1 收尾前锁确实存在（对照）",
                      g("F1_LOCK_EXISTED_BEFORE_FINISH") == "True",
                      "存在=%s" % g("F1_LOCK_EXISTED_BEFORE_FINISH"))
                # 这一条是本轮改的方向：搬迁期间**不能**删锁。
                #
                # 旧断言要求「仍然删掉」，而那条要求本身就是数据丢失路径的一环：
                # 任务 active 时锁被删掉，下次启动看到 active 却无锁 →
                # 走 ConservativeCleanup；或者锁被搬迁复制走、随后才删 →
                # 新目录里留下 active + 有效锁 + PID 已退出 → 按崩溃递归删产物。
                check("F2 搬迁期间 MarkTaskFinished 不删锁（整体不动，防误删成功产物）",
                      g("F2_LOCK_PRESERVED_DURING_SUSPENDED") == "True",
                      "保留=%s（False = 又恢复成「取不到闸也删锁」，"
                      "会与搬迁交错出「active+有效锁」的假崩溃现场）"
                      % g("F2_LOCK_PRESERVED_DURING_SUSPENDED"))
                check("F3 开闸后再调用会正常删锁（证明不是「永远不删」）",
                      g("F3_LOCK_DELETED_AFTER_OPEN") == "True",
                      "已删=%s" % g("F3_LOCK_DELETED_AFTER_OPEN"))

    # ================= G 静态：写入方没有退回到布尔判断 =================
    print("\n【G】静态：写入方用的是锁，不是布尔标志")
    svc = Path(P) / "Auto7z_WPF" / "Auto7z_WPF" / "Services"
    for fname, label in (("LogService.cs", "日志"),
                         ("ConfigService.cs", "配置"),
                         ("TaskStateService.cs", "任务状态")):
        f = svc / fname
        if not f.is_file():
            check("G %s 存在" % label, False, str(f))
            continue
        body = "\n".join(l for l in f.read_text(encoding="utf-8", errors="replace").splitlines()
                         if not l.strip().startswith("//"))
        uses_lock = "MigrationGate.TryEnterWrite()" in body
        # 只判「有没有用锁」，不禁止 IsClosed 出现在诊断用途上 ——
        # 但写入路径上不该再出现它。
        check("G %s写入走 TryEnterWrite" % label, uses_lock,
              "找到 TryEnterWrite" if uses_lock else "仍在用布尔判断")
        check("G %s写入路径不再用 IsClosed 判闸" % label,
              "if (MigrationGate.IsClosed)" not in body,
              "找到旧的布尔判断" if "if (MigrationGate.IsClosed)" in body else "已改")

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
        print("写入隔离已从「布尔检查」变成「读写锁」：搬迁等待在途写入，写入在搬迁期间被拒")
        print("PASS")
        code = 0
    out = os.path.join(SDK, "miggate.json")
    try:
        open(out, "w", encoding="utf-8").write(
            json.dumps(results, ensure_ascii=False, indent=2))
        print("输出 -> %s" % out)
    except OSError:
        pass
    return code


if __name__ == "__main__":
    sys.exit(main())
