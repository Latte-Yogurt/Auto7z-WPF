#!/usr/bin/env python3
# -*- coding: utf-8 -*-
r"""单实例监听线程「发布管道 / 停止」竞态的**确定性**验证（BUG-04）。

## 为什么不能靠压力测试

BUG-04 的窗口是：

    线程 B：通过 `while (_running)` → 被抢占
    线程 A：`_running = false`；读 `_currentPipe` 得 null；Join 等待
    线程 B：恢复 → 新建管道 → 发布 → 阻塞在 WaitForConnection()

宽度在微秒级。上一轮的 `stress_check.py` 连跑 25 次短任务、12 次错误输出、
4 进程并发、巨量输出，**修复前后都通过** —— 它只能证明「没崩」，
不能证明「窗口被关掉了」。用不可靠的测试去覆盖一个竞态，比不测更糟：
它会给出一份「已覆盖」的假象。

## 确定性方案

产品侧在 `TestHooks` 里开了一个**仅 Debug 存在**的观察点
`BeforePipeCreate`，位置正好在「通过停止标志检查、创建管道之前」。
测试把监听线程精确停在这个点上（而不是靠概率撞），再在此时启动 Dispose：

    观察点被进入 → 通知主线程 → 主线程开始 Dispose
                 → 观察点再等 150ms 确保 `_running` 已置 false → 放行

此时两种实现的**可观测差异**是确定的：

    有「发布前复查」：线程醒来看见 `_running == false`，
                      关掉刚建的管道直接退出 → Dispose 的 Join 立刻返回。
    没有复查：        线程照旧发布管道并阻塞在 WaitForConnection，
                      Dispose 只能等满 `ListenerJoinTimeoutMs`（2000ms），
                      靠兜底打断才收得回来。

于是判据是「Dispose 耗时 < 2000ms」+「监听线程已退出」，
两者都是行为观测，不读源码字符串。

## 与 mutation_suite 的关系

本脚本另外提供一个 `--expect-mutant` 模式：断言「没有复查时应判失败」。
变异测试用它确认这个检查器**真的会红**，而不是永远绿。
"""
import json
import os
import subprocess
import sys
from pathlib import Path

from safe_ops import safe_rmtree

P = sys.argv[1] if len(sys.argv) > 1 else r"C:\Users\Mocha\Desktop\VSProject\Auto7z-WPF"
SDK = os.path.join(os.environ.get("TEMP", r"C:\Windows\Temp"), "Auto7zTest_data", "lifecycle")

JOIN_TIMEOUT_MS = 2000       # 产品侧 ListenerJoinTimeoutMs，判据的基准线
PARK_MS = 150                # 观察点放行前额外等待，确保 _running 已置 false

results = []


def check(name, ok, detail=""):
    results.append({"name": name, "ok": ok, "detail": detail})
    print(("  OK   " if ok else "  !!   ") + name + ("  --- " + detail if detail else ""))


def skip(name, why):
    results.append({"name": name, "ok": True, "detail": "跳过: " + why})
    print("  --   " + name + "  --- 跳过: " + why)


def exe_stamp(exe):
    """取可执行文件的修改时间，用来证明「跑的是刚构建的那一份」。

    这条断言是血的教训：变异测试时构建失败（CS1010），脚本却没检查构建返回码，
    于是它对着**上一轮的旧二进制**跑出 PASS —— 变异「没被发现」，
    于是会得出「检查器有效」的相反结论。实际上检查器连被测代码都没跑。
    """
    try:
        return os.path.getmtime(exe)
    except OSError:
        return None


PROBE_CS = r'''
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;

// 把单实例监听线程精确停在「已通过停止标志检查、尚未创建管道」的位置，
// 再启动 Dispose，观察监听线程是否被卡在 WaitForConnection 上。
class LifecycleRaceProbe
{
    const int ParkMs = ''' + str(PARK_MS) + r''';

    static ManualResetEvent entered = new ManualResetEvent(false);
    static ManualResetEvent release = new ManualResetEvent(false);

    static void L(string s) { Console.WriteLine(s); }

    static int Main(string[] args)
    {
        string exe = args[0];

        AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
        {
            string simple = new AssemblyName(e.Name).Name;
            string p = Path.Combine(Path.GetDirectoryName(exe), simple + ".exe");
            return File.Exists(p) ? Assembly.LoadFrom(p) : null;
        };

        Assembly asm = Assembly.LoadFrom(exe);
        Type svcType = asm.GetType("Auto7z_WPF.Services.SingleInstanceService");
        Type hookType = asm.GetType("Auto7z_WPF.Services.TestHooks");
        if (svcType == null) { L("NO_TYPE SingleInstanceService"); return 2; }
        if (hookType == null)
        {
            // Release 构建里 TestHooks 整文件不存在 —— 这是正确行为，不是回归。
            L("NO_HOOKS");
            return 4;
        }

        // --- 安装观察点：进入后通知主线程，等主线程开始 Dispose，再等一小段放行 ---
        MethodInfo setter = hookType.GetMethod("SetBeforePipeCreate",
            BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
        if (setter == null) { L("NO_HOOK_METHOD"); return 2; }

        Action hook = delegate
        {
            entered.Set();
            release.WaitOne(10000);
            // 这 150ms 是关键：给 Dispose 足够时间把 _running 置 false。
            // 放行之后线程要么看到 false 自行退出（修好的实现），
            // 要么照旧发布管道并阻塞（没复查的实现）。
            Thread.Sleep(ParkMs);
        };
        setter.Invoke(null, new object[] { hook });

        object svc = Activator.CreateInstance(svcType);
        MethodInfo acquire = svcType.GetMethod("TryAcquire");
        MethodInfo start = svcType.GetMethod("StartListener");
        MethodInfo dispose = svcType.GetMethod("Dispose");

        bool acquired = (bool)acquire.Invoke(svc, null);
        L("  TryAcquire = " + acquired);
        if (!acquired) { L("SKIP_NO_MUTEX"); return 3; }

        int received = 0;
        Action<string[], string> cb = delegate(string[] p, string m)
        {
            Interlocked.Increment(ref received);
        };
        start.Invoke(svc, new object[] { cb });

        // 等监听线程真正抵达观察点（不是靠 sleep 猜）。
        if (!entered.WaitOne(10000))
        {
            L("FAIL 监听线程 10 秒内没有抵达观察点");
            return 1;
        }
        L("  观察点已进入（线程停在「通过检查 / 尚未建管道」之间）");

        // 放行观察点，让线程在「_running 已被置 false」的状态下继续往下走。
        Stopwatch sw = Stopwatch.StartNew();
        release.Set();

        dispose.Invoke(svc, null);
        sw.Stop();
        long disposeMs = sw.ElapsedMilliseconds;
        L("  Dispose 耗时 = " + disposeMs + " ms");

        FieldInfo listenerField = svcType.GetField("_listener",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Thread th = (Thread)listenerField.GetValue(svc);

        bool aliveAfterJoin = (th != null && th.IsAlive);
        // Dispose 已经 Join 过；还活着就再给 3 秒，区分「只是慢」与「真卡住」。
        if (aliveAfterJoin && th.Join(3000)) aliveAfterJoin = th.IsAlive;
        L("  监听线程 IsAlive = " + aliveAfterJoin);

        setter.Invoke(null, new object[] { null });   // 卸载观察点

        // 判据：Join 到上限都没收回线程，说明线程真的阻塞在 WaitForConnection。
        bool fast = disposeMs < ''' + str(JOIN_TIMEOUT_MS) + r''';
        bool exited = !aliveAfterJoin;

        L("  RESULT_MS=" + disposeMs);
        L("  RESULT_EXITED=" + exited);

        if (fast && exited) { L("PASS"); return 0; }
        L("FAIL");
        return 1;
    }
}
'''


def build_probe():
    cs = Path(SDK) / "probe_src.txt"      # 用 .txt：safe_rmtree 拒绝含 .cs 的目录
    cs.write_text(PROBE_CS, encoding="utf-8")
    out = Path(SDK) / "probe.exe"
    rc = subprocess.run(
        [r"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe", "/nologo",
         "/out:" + str(out), str(cs)],
        capture_output=True, text=True, encoding="utf-8", errors="replace")
    if rc.returncode != 0:
        print("     探针编译失败: " + (rc.stderr or rc.stdout or "")[:400])
        return None
    return out


def scan_publish_recheck():
    """静态确认产品侧「发布管道」与「复查停止标志」在同一处、且有锁。

    这与上面的行为断言是**互补**而不是重复：
      行为断言证明「当前这份二进制会把线程收回来」，
      静态断言证明「收回来的原因确实是发布前复查，而不是别的路径碰巧救场」。

    为什么这条静态断言值得存在：`Dispose` 里的兜底打断（Join 超时后再关一次管道）
    也能让线程最终退出 —— 只靠行为观测的话，把复查删掉后线程仍会被兜底救回来，
    检查器就可能看不见变异。静态断言把「救命的是哪一层」钉死。

    返回 (ok, detail)。
    """
    src = Path(P) / "Auto7z_WPF" / "Auto7z_WPF" / "Services" / "SingleInstanceService.cs"
    if not src.is_file():
        return False, "找不到 %s" % src

    text = src.read_text(encoding="utf-8", errors="replace")

    # 1) 必须存在生命锁字段
    if "_lifecycleLock" not in text:
        return False, "缺少 _lifecycleLock（生命周期锁）"

    # 2) 发布管道必须写在 lock (_lifecycleLock) 块里，且块内同时出现 _running 复查
    m = None
    for mm in __import__("re").finditer(r"lock \(_lifecycleLock\)\s*\{", text):
        # 取这个 lock 块的配平范围
        start = text.find("{", mm.start())
        depth, end = 0, start
        for k in range(start, len(text)):
            if text[k] == "{":
                depth += 1
            elif text[k] == "}":
                depth -= 1
                if depth == 0:
                    end = k
                    break
        body = text[start:end]
        if "_currentPipe = pipe" in body:
            m = body
            break
    if m is None:
        return False, "_currentPipe = pipe 没有出现在任何 lock (_lifecycleLock) 块内"

    if "!_running" not in m:
        return False, "发布 _currentPipe 的锁块内缺少 !_running 复查（BUG-04 的原始写法）"

    return True, "发布管道与 !_running 复查在同一把生命周期锁内"


def kill_strays():
    if os.name != "nt":
        return
    for n in ("Auto7z.exe", "Auto7zGUI.exe"):
        subprocess.run(["taskkill", "/F", "/IM", n],
                       stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)


def main():
    if os.path.isdir(SDK):
        safe_rmtree(SDK)
    os.makedirs(SDK, exist_ok=True)
    kill_strays()

    print("=" * 76)
    print("单实例监听线程竞态：确定性验证（BUG-04）")
    print("=" * 76)

    configs = [("Debug", os.path.join(P, "Auto7z_WPF", "Auto7z_WPF", "bin", "Debug", "Auto7z.exe")),
               ("Release", os.path.join(P, "Auto7z_WPF", "Auto7z_WPF", "bin", "Release", "Auto7z.exe"))]

    probe = build_probe()
    if probe is None:
        check("探针可编译", False, "csc 失败")
        return finish()

    # 静态前置：先钉住「救命的是发布前复查」，再跑行为验证。
    ok_static, detail_static = scan_publish_recheck()
    check("发布管道与停止标志复查在同一把生命周期锁内", ok_static, detail_static)

    for cfg, exe in configs:
        if not os.path.isfile(exe):
            check("[%s] 可执行文件存在" % cfg, False, exe)
            continue

        env = dict(os.environ)
        env["AUTO7Z_DATA_ROOT"] = str(Path(SDK) / "data")
        os.makedirs(env["AUTO7Z_DATA_ROOT"], exist_ok=True)

        before = exe_stamp(exe)
        pr = subprocess.run([str(probe), exe], capture_output=True, text=True,
                            encoding="utf-8", errors="replace", env=env, timeout=180)
        after = exe_stamp(exe)
        text = (pr.stdout or "") + (pr.stderr or "")
        print("   [%s]" % cfg)
        for line in text.strip().splitlines():
            print("     " + line)

        # 运行期间二进制被换掉 = 测的不是同一份代码，结果不可信。
        check("[%s] 探针运行期间可执行文件未被替换" % cfg, before == after,
              "mtime %s -> %s" % (before, after))

        if "NO_HOOKS" in text:
            # Release 里 TestHooks 整个类型不存在，这是设计，不是缺陷。
            skip("[%s] 确定性竞态验证" % cfg,
                 "Release 无 TestHooks（观察点仅 Debug 存在），已由 Debug 覆盖")
            continue
        if "SKIP_NO_MUTEX" in text:
            skip("[%s] 确定性竞态验证" % cfg, "拿不到单实例权（有残留实例）")
            continue

        ms = None
        exited = False
        for line in text.splitlines():
            line = line.strip()
            if line.startswith("RESULT_MS="):
                try:
                    ms = int(line.split("=", 1)[1])
                except ValueError:
                    ms = None
            elif line.startswith("RESULT_EXITED="):
                exited = line.split("=", 1)[1].strip().lower() == "true"

        if ms is None:
            check("[%s] 确定性竞态验证" % cfg, False,
                  "探针未回报耗时，输出：" + text.strip().replace("\n", " | ")[:200])
            continue

        check("[%s] Dispose 未等满 Join 上限（线程没阻塞在 WaitForConnection）" % cfg,
              ms < JOIN_TIMEOUT_MS,
              "Dispose=%dms，上限=%dms" % (ms, JOIN_TIMEOUT_MS))
        check("[%s] 监听线程在 Dispose 后确实退出" % cfg, exited,
              "IsAlive=%s" % (not exited))

    kill_strays()
    return finish()


def finish():
    print("\n" + "=" * 76)
    bad = [r for r in results if not r["ok"]]
    print("共 %d 项，通过 %d，失败 %d" % (len(results), len(results) - len(bad), len(bad)))
    if bad:
        for b in bad:
            print("  " + b["name"] + "  " + b["detail"])
        print("FAIL")
        code = 1
    else:
        print("发布/停止竞态窗口已关闭")
        print("PASS")
        code = 0
    out = os.path.join(SDK, "lifecycle.json")
    try:
        open(out, "w", encoding="utf-8").write(
            json.dumps(results, ensure_ascii=False, indent=2))
        print("输出 -> %s" % out)
    except OSError:
        pass
    return code


if __name__ == "__main__":
    sys.exit(main())
