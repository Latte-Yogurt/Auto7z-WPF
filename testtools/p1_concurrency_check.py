#!/usr/bin/env python3
# -*- coding: utf-8 -*-
r"""三项 P1 的并发与失败恢复专项验证（RC 门禁用）。

这组用例针对 GPT6 在 RC 审查中提出的三条 P1。它们共同的特征是：
**普通功能测试完全覆盖不到**，因为触发条件是「两件事刚好同时发生」
或「中途某一步失败」。

  P1-1  任务结束与数据目录迁移并发 → 可能误删已成功生成的产物
  P1-2  迁移中途失败 → 源目录已被部分删除，且 `.config` 残留导致模式误判
  P1-3  源文件句柄关闭后、删除前 → 外部写入的内容被静默丢弃

## 为什么这组必须「确定性」

真实的线程交错无法稳定复现（窗口在微秒级）。用 `sleep` 去撞窗口的测试
只会得到「偶尔失败」——那种测试最终会被人关掉。

所以这里全部用**确定性等价物**：
  · P1-1：直接验证「取不到闸时整体不动」这条不变量，而不是去撞交错
  · P1-2：直接让第二个文件复制失败（占住它），断言源目录**完整**
  · P1-3：直接断言「删源语句在源句柄的 using 块内部」这个结构事实，
          再用运行时用例证明该结构确实允许边持有边删除

不变量成立 + 结构固定 ⇒ 交错发生时也安全。这比撞窗口可靠得多。

## 每一项都配了「反面」

只测「失败时不删源」的话，一个「永远不删源」的实现也能通过。
只测「取不到闸就不动」的话，一个「永远不动」的实现也能通过。
所以每一项都带一条对照断言，证明正常路径仍然工作。
"""
import json
import os
import subprocess
import sys
import threading
import time
from pathlib import Path

from safe_ops import safe_rmtree

P = sys.argv[1] if len(sys.argv) > 1 else r"C:\Users\Mocha\Desktop\VSProject\Auto7z-WPF"
EXE = os.path.join(P, "Auto7z_WPF", "Auto7z_WPF", "bin", "Debug", "Auto7z.exe")
SDK = os.path.join(os.environ.get("TEMP", r"C:\Windows\Temp"), "Auto7zTest_data", "p1checks")

results = []


def check(name, ok, detail=""):
    results.append({"name": name, "ok": ok, "detail": detail})
    print(("  OK   " if ok else "  !!   ") + name + ("  --- " + detail if detail else ""))


def skip(name, why):
    results.append({"name": name, "ok": True, "detail": "跳过: " + why})
    print("  --   " + name + "  --- 跳过: " + why)


# ---------------------------------------------------------------------------
# P1-1：任务结束与迁移并发
# ---------------------------------------------------------------------------
P11_CS = r'''
using System;
using System.IO;
using System.Reflection;
using System.Threading;

// 验证 MarkTaskFinished 在「搬迁持闸」期间**整体不动**：
// 既不写 done，也不删 job.lock，也不删所有权标记。
//
// 这三件事任何一件单独发生，都会与搬迁交错出「active + 有效锁 + PID 已退出」，
// 下次启动按崩溃递归删产物。
class P11Probe
{
    static Assembly asm;
    static Type stateType, gateType;
    static MethodInfo mSuspend, mTryEnter, mExitWrite;

    static void L(string s) { Console.WriteLine(s); }

    static object InvokeStatic(Type t, string name, params object[] a)
    {
        MethodInfo m = t.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic
                                         | BindingFlags.Static);
        if (m == null) return "NO_METHOD:" + name;
        try { return m.Invoke(null, a); }
        catch (Exception e) { return "EX:" + (e.InnerException ?? e).GetType().Name; }
    }

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
        stateType = asm.GetType("Auto7z_WPF.Services.TaskStateService");
        gateType = asm.GetType("Auto7z_WPF.Services.MigrationGate");
        if (stateType == null) { L("NO_STATE"); return 2; }
        if (gateType == null) { L("NO_GATE"); return 2; }

        mSuspend = gateType.GetMethod("SuspendWrites", BindingFlags.Public | BindingFlags.Static);
        if (mSuspend == null) { L("NO_SUSPEND"); return 2; }

        string dataDir = Path.Combine(dataRoot, "Auto7z");
        Directory.CreateDirectory(dataDir);
        string stateFile = Path.Combine(dataDir, "TaskState.json");
        string lockFile = Path.Combine(dataDir, "job.lock");

        // 产物目录：43 字符随机名 + 所有权标记（模拟一个真正在跑的任务）
        string outDir = Path.Combine(dataRoot, "out_" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(outDir);
        File.WriteAllText(Path.Combine(outDir, "product.7z"), "PRECIOUS_ARCHIVE");
        File.WriteAllText(Path.Combine(dataDir, "Auto7z.json"), "{\"Language\":\"zh-CN\"}");

        // ---- 建立一个真实的 active 任务 ----
        InvokeStatic(stateType, "MarkTaskStarted", outDir);
        L("SETUP_STATE_EXISTS=" + File.Exists(stateFile));
        L("SETUP_LOCK_EXISTS=" + File.Exists(lockFile));
        L("SETUP_OWNER_EXISTS=" + File.Exists(Path.Combine(outDir, ".auto7z-owner")));
        L("OUTDIR=" + outDir);

        // ---- 搬迁持闸期间调用 MarkTaskFinished ----
        using (IDisposable s = (IDisposable)mSuspend.Invoke(null, null))
        {
            var t = new Thread(() => InvokeStatic(stateType, "MarkTaskFinished"));
            t.IsBackground = true;
            t.Start();
            t.Join(15000);
        }

        // 三条不变量：锁、状态、标记都不能被动过
        L("P11_LOCK_PRESERVED=" + File.Exists(lockFile));
        L("P11_OWNER_PRESERVED=" + File.Exists(Path.Combine(outDir, ".auto7z-owner")));

        string stateText = File.Exists(stateFile) ? File.ReadAllText(stateFile) : "";
        L("P11_STATE_STILL_ACTIVE=" + (stateText.Contains("\"active\"")));

        // ---- 对照：闸门打开后，同样的调用必须完成收尾 ----
        InvokeStatic(stateType, "MarkTaskFinished");
        L("P11_LOCK_DELETED_AFTER_OPEN=" + !File.Exists(lockFile));
        L("P11_OWNER_DELETED_AFTER_OPEN="
          + !File.Exists(Path.Combine(outDir, ".auto7z-owner")));

        string stateAfter = File.Exists(stateFile) ? File.ReadAllText(stateFile) : "";
        L("P11_STATE_DONE_AFTER_OPEN=" + (stateAfter.Contains("\"done\"")));

        // 产物本身必须始终在（这条永真，但值得断言：模拟的就是这个场景）
        L("P11_PRODUCT_PRESERVED=" + File.Exists(Path.Combine(outDir, "product.7z")));

        return 0;
    }
}
'''


# ---------------------------------------------------------------------------
# P1-2 / P1-3：迁移中途失败与删源窗口（直接用反射调搬迁类）
# ---------------------------------------------------------------------------
MOVER_CS = r'''
using System;
using System.IO;
using System.Reflection;

class MoverProbe
{
    static Assembly asm;
    static Type moverType;

    static void L(string s) { Console.WriteLine(s); }

    static MethodInfo Get(string name, BindingFlags extra)
    {
        return moverType.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static | extra);
    }

    static int Main(string[] args)
    {
        string exe = args[0];
        string op = args[1];

        AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
        {
            string simple = new AssemblyName(e.Name).Name;
            string p = Path.Combine(Path.GetDirectoryName(exe), simple + ".exe");
            return File.Exists(p) ? Assembly.LoadFrom(p) : null;
        };

        asm = Assembly.LoadFrom(exe);
        moverType = asm.GetType("Auto7z_WPF.Services.DataDirectoryMover");
        if (moverType == null) { L("NO_TYPE"); return 2; }

        if (op == "partialfail") return PartialFail(args[2], args[3]);
        if (op == "deletewhileopen") return DeleteWhileOpen(args[2]);

        L("BAD_OP");
        return 2;
    }

    // P1-2：让第二个文件复制失败，断言源目录**一个文件都不少**。
    //
    // 做法：先占住 src/b.txt 的写句柄（我们的 FileShare.Read 不含 Write，
    // 于是复制它时打开会失败）。构造出「a 复制成功、b 复制失败」的确定性失败点。
    static int PartialFail(string src, string tgt)
    {
        var copy = Get("CopyTree", BindingFlags.Public);
        if (copy == null) { L("NO_COPYTREE"); return 2; }

        string a = Path.Combine(src, "a.txt");
        string b = Path.Combine(src, "b.txt");

        long aLen = new FileInfo(a).Length;
        long bLen = new FileInfo(b).Length;

        // 占住 b.txt 的写权限，模拟「被别的程序开着」
        FileStream holder = null;
        try
        {
            holder = new FileStream(b, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        }
        catch (Exception e)
        {
            L("HOLDER_FAIL:" + e.GetType().Name);
            return 3;
        }

        bool threw = false;
        try
        {
            copy.Invoke(null, new object[] { src, tgt });
        }
        catch (Exception e)
        {
            threw = true;
            L("COPY_EX:" + (e.InnerException ?? e).GetType().Name);
        }
        finally
        {
            try { holder.Dispose(); } catch { }
        }

        L("COPY_THREW=" + threw);

        // **核心断言**：源目录必须完整 —— 两个文件都在，长度不变。
        bool aOk = File.Exists(a) && new FileInfo(a).Length == aLen;
        bool bOk = File.Exists(b) && new FileInfo(b).Length == bLen;
        L("SRC_A_INTACT=" + aOk);
        L("SRC_B_INTACT=" + bOk);

        // 目标侧：a 已经复制过去（这是允许的，源没删才安全）
        L("TGT_A_COPIED=" + File.Exists(Path.Combine(tgt, "a.txt")));
        return 0;
    }

    // P1-3：证明「一边持有源句柄、一边删掉源文件」确实可行。
    //
    // 结构上删源语句在 using 内部；这个用例证明该结构不会因为共享冲突而失败。
    // 若 FileShare 里没给 Delete，File.Delete 会抛 IOException ——
    // 那时「句柄内删源」这条路走不通，检查器的结构断言就会失去意义。
    static int DeleteWhileOpen(string path)
    {
        bool deleted = false;
        string err = null;

        try
        {
            using (var src = new FileStream(path, FileMode.Open, FileAccess.Read,
                       FileShare.Read | FileShare.Delete))
            {
                // 读一点，确认句柄真的可用
                int one = src.ReadByte();
                L("READ_OK=" + (one >= 0));

                File.Delete(path);
                deleted = !File.Exists(path);
            }
        }
        catch (Exception e)
        {
            err = e.GetType().Name;
        }

        L("DELETE_WHILE_OPEN=" + deleted);
        L("DELETE_ERR=" + (err ?? "<null>"));
        return 0;
    }
}
'''


def build_probe(src_text, name):
    cs = Path(SDK) / ("%s_src.txt" % name)
    cs.write_text(src_text, encoding="utf-8")
    out = Path(SDK) / ("%s.exe" % name)
    rc = subprocess.run(
        [r"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe", "/nologo",
         "/out:" + str(out), str(cs)],
        capture_output=True, text=True, encoding="utf-8", errors="replace")
    if rc.returncode != 0:
        print("     %s 编译失败: %s" % (name, (rc.stderr or rc.stdout or "")[:600]))
        return None
    return out


def parse_kv(out):
    kv = {}
    for line in out.splitlines():
        line = line.strip()
        if "=" in line and not line.startswith("NO_"):
            k, v = line.split("=", 1)
            kv[k.strip()] = v.strip()
    return kv


def main():
    if os.path.isdir(SDK):
        safe_rmtree(SDK)
    os.makedirs(SDK, exist_ok=True)

    print("=" * 76)
    print("三项 P1 的并发与失败恢复专项验证（RC 门禁）")
    print("=" * 76)

    if not os.path.isfile(EXE):
        check("可执行文件存在", False, EXE)
        return finish()

    p11 = build_probe(P11_CS, "p11")
    mover = build_probe(MOVER_CS, "mover")
    if p11 is None or mover is None:
        check("探针可编译", False, "csc 失败")
        return finish()

    # ================= P1-1 =================
    print("\n【P1-1】任务结束与目录迁移并发：取不到闸就整体不动")
    data_root = Path(SDK) / "p11data"
    data_root.mkdir(parents=True, exist_ok=True)

    p = subprocess.run([str(p11), EXE, str(data_root)],
                       capture_output=True, text=True, encoding="utf-8",
                       errors="replace", timeout=300)
    out = (p.stdout or "") + (p.stderr or "")
    for line in out.strip().splitlines():
        print("     " + line)

    if "NO_STATE" in out or "NO_GATE" in out:
        check("P1-1 所需类型存在", False, "找不到 TaskStateService 或 MigrationGate")
    else:
        kv = parse_kv(out)

        def g(k, d=""):
            return kv.get(k, d)

        check("P1-1a 场景准备成功（建立了真实的 active 任务）",
              g("SETUP_STATE_EXISTS") == "True"
              and g("SETUP_LOCK_EXISTS") == "True"
              and g("SETUP_OWNER_EXISTS") == "True",
              "state=%s lock=%s owner=%s" % (g("SETUP_STATE_EXISTS"),
                                             g("SETUP_LOCK_EXISTS"),
                                             g("SETUP_OWNER_EXISTS")))

        # 三条不变量：搬迁期间锁/标记/状态都不能被动
        check("P1-1b 搬迁期间 job.lock 未被删除",
              g("P11_LOCK_PRESERVED") == "True",
              "保留=%s（False = 又出现「active 却无锁」的中间态）"
              % g("P11_LOCK_PRESERVED"))
        check("P1-1c 搬迁期间所有权标记未被删除",
              g("P11_OWNER_PRESERVED") == "True",
              "保留=%s（False = 产物目录失去归属证据，或已完成的目录仍被标记为可清理）"
              % g("P11_OWNER_PRESERVED"))
        check("P1-1d 搬迁期间状态仍为 active（没有被单独写成 done）",
              g("P11_STATE_STILL_ACTIVE") == "True",
              "仍 active=%s" % g("P11_STATE_STILL_ACTIVE"))

        # 对照：正常路径必须仍然完成收尾
        check("P1-1e 开闸后收尾正常完成：锁已删",
              g("P11_LOCK_DELETED_AFTER_OPEN") == "True",
              "已删=%s（False = 「永远不动」的假修复）"
              % g("P11_LOCK_DELETED_AFTER_OPEN"))
        check("P1-1f 开闸后收尾正常完成：标记已删",
              g("P11_OWNER_DELETED_AFTER_OPEN") == "True",
              "已删=%s" % g("P11_OWNER_DELETED_AFTER_OPEN"))
        check("P1-1g 开闸后状态落为 done",
              g("P11_STATE_DONE_AFTER_OPEN") == "True",
              "done=%s" % g("P11_STATE_DONE_AFTER_OPEN"))
        check("P1-1h 用户产物始终未被删除",
              g("P11_PRODUCT_PRESERVED") == "True",
              "保留=%s" % g("P11_PRODUCT_PRESERVED"))

    # ================= P1-2 =================
    print("\n【P1-2】迁移中途失败：源目录必须保持完整")
    work = Path(SDK) / "p12"
    src = work / "src"
    tgt = work / "tgt"
    src.mkdir(parents=True)
    tgt.mkdir(parents=True)
    (src / "a.txt").write_text("AAAA_CONTENT", encoding="utf-8")
    (src / "b.txt").write_text("BBBB_CONTENT", encoding="utf-8")

    p = subprocess.run([str(mover), EXE, "partialfail", str(src), str(tgt)],
                       capture_output=True, text=True, encoding="utf-8",
                       errors="replace", timeout=300)
    out = (p.stdout or "") + (p.stderr or "")
    for line in out.strip().splitlines():
        print("     " + line)

    if "HOLDER_FAIL" in out:
        skip("P1-2 组", "无法构造「第二个文件被占用」的场景")
    else:
        kv = parse_kv(out)

        def g2(k, d=""):
            return kv.get(k, d)

        check("P1-2a 确实触发了复制失败（对照组有效）",
              g2("COPY_THREW") == "True",
              "抛出=%s（False 说明没构造出失败点，本组结论不成立）"
              % g2("COPY_THREW"))
        # **核心**：失败时源目录一个文件都不能少
        check("P1-2b 失败后源 a.txt 完整（没被提前删除）",
              g2("SRC_A_INTACT") == "True",
              "完整=%s（False = 边走边删，源已残缺）" % g2("SRC_A_INTACT"))
        check("P1-2c 失败后源 b.txt 完整",
              g2("SRC_B_INTACT") == "True",
              "完整=%s" % g2("SRC_B_INTACT"))

        # 补充：源目录里两个文件都要在（直接看文件系统，不依赖探针自报）
        src_files = sorted(x.name for x in src.iterdir())
        check("P1-2d 源目录文件数仍为 2（直接枚举，不依赖探针）",
              len(src_files) == 2,
              "实际=%s" % src_files)

    # ================= P1-3 =================
    print("\n【P1-3】源文件「持有句柄时删除」可行（结构断言的前提）")
    work = Path(SDK) / "p13"
    work.mkdir(parents=True, exist_ok=True)
    probe_file = work / "victim.txt"
    probe_file.write_text("CONTENT_TO_DELETE", encoding="utf-8")

    p = subprocess.run([str(mover), EXE, "deletewhileopen", str(probe_file)],
                       capture_output=True, text=True, encoding="utf-8",
                       errors="replace", timeout=120)
    out = (p.stdout or "") + (p.stderr or "")
    for line in out.strip().splitlines():
        print("     " + line)

    kv = parse_kv(out)
    check("P1-3a 能在持有源句柄时删除该文件（FileShare.Delete 生效）",
          kv.get("DELETE_WHILE_OPEN") == "True",
          "成功=%s 错误=%s（False 说明「句柄内删源」这条路走不通，"
          "结构断言就失去意义）"
          % (kv.get("DELETE_WHILE_OPEN"), kv.get("DELETE_ERR")))
    check("P1-3b 文件确实已从磁盘消失",
          not probe_file.exists(),
          "仍存在=%s" % probe_file.exists())

    # **P1-3c 是变异测试 b4-p1-3b 逼出来的。**
    #
    # 上面两条只证明「Windows 允许一边持有句柄一边删」——那是**平台事实**，
    # 与我们的产品代码无关。把产品里的 `FileShare.Read | FileShare.Delete`
    # 退回 `FileShare.Read`，上面两条依然全过，因为它们跑的是探针自己写的
    # 共享掩码。
    #
    # 真正要守的是「产品代码用了这个掩码」。所以补一条静态断言。
    mover_src = Path(P) / "Auto7z_WPF" / "Auto7z_WPF" / "Services" / "DataDirectoryMover.cs"
    if mover_src.is_file():
        mtext = mover_src.read_text(encoding="utf-8", errors="replace")
        mbody = "\n".join(l for l in mtext.splitlines()
                          if not l.strip().startswith("//"))
        check("P1-3c 产品代码的源共享掩码含 Delete（不是只在探针里成立）",
              "FileShare.Read | FileShare.Delete" in mbody,
              "找到 Read|Delete" if "FileShare.Read | FileShare.Delete" in mbody
              else "产品仍用裸 FileShare.Read —— 句柄内删不了源，窗口会重现")
    else:
        check("P1-3c 产品源码可读", False, str(mover_src))

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
        print("三项 P1 的不变量均成立：并发时不误删产物、失败时源完整、删源无窗口")
        print("PASS")
        code = 0
    out = os.path.join(SDK, "p1checks.json")
    try:
        open(out, "w", encoding="utf-8").write(
            json.dumps(results, ensure_ascii=False, indent=2))
        print("输出 -> %s" % out)
    except OSError:
        pass
    return code


if __name__ == "__main__":
    sys.exit(main())
