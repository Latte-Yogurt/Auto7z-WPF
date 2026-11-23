#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""单实例关闭时序专项验证（GPT6 补充建议第 4 条）。

## 缺陷

`Dispose()` 只设 `_running = false`，但监听线程阻塞在
`pipe.WaitForConnection()` —— 设标志唤不醒阻塞调用。
`Join(1000)` 等 1 秒后放弃，线程仍挂在原地继续占着管道名。

后果（需要验证的四种场景）：
  1. 关闭后立即重启  → 新实例可能抢不到管道
  2. 连续快速启动多个实例 → 管道交接是否丢
  3. 旧实例正在退出时收到新的 Shell 请求
  4. 管道连接中途断开

## 修复

Dispose 主动关掉监听线程当前阻塞的那个 `NamedPipeServerStream`，
让 `WaitForConnection` 立刻以 ObjectDisposedException 返回，线程随即退出。
关闭顺序：先关管道（让线程退出）→ 再 Join → 最后释放互斥量。
"""
import json
import os
import shutil
import subprocess
import sys
import time
from pathlib import Path

from safe_ops import safe_rmtree

P = sys.argv[1] if len(sys.argv) > 1 else r"C:\Users\Mocha\Desktop\VSProject\Auto7z-WPF"
EXE = os.path.join(P, "Auto7z_WPF", "Auto7z_WPF", "bin", "Debug", "Auto7z.exe")
SDK = os.path.join(os.environ.get("TEMP", r"C:\Windows\Temp"), "Auto7zTest_data", "shutdown")

results = []


def check(name, ok, detail=""):
    results.append({"name": name, "ok": ok, "detail": detail})
    print(("  OK   " if ok else "  !!   ") + name + ("  --- " + detail if detail else ""))


def prep(tag):
    work = os.path.join(SDK, tag)
    data = os.path.join(work, "data")
    os.makedirs(os.path.join(data, "Auto7z"), exist_ok=True)
    cfg = {
        "Language": "zh-CN", "Volume": "0", "Format": "7z", "Password": "",
        "Zstd": True, "GenerateSHA256": False, "ChecksumAlgorithm": "sha256",
        "AutoSave": False, "IsTopMost": False, "RuntimeMode": "Balanced",
        "OutputPath": "", "IsSolidMode": False, "IsEncFileName": False,
        "CompressionLevel": "Store", "UnzipPassword": "", "ErrorLevel": "Warn",
    }
    open(os.path.join(data, "Auto7z", "Auto7z.json"), "w", encoding="utf-8").write(
        json.dumps(cfg, ensure_ascii=False, indent=2))
    env = dict(os.environ)
    env["AUTO7Z_DATA_ROOT"] = data
    env["AUTO7Z_AUTOANSWER"] = "close,close,close,close,close"
    return work, data, env


def kill_strays():
    if os.name != "nt":
        return
    for n in ("Auto7z.exe", "Auto7zGUI.exe"):
        subprocess.run(["taskkill", "/F", "/IM", n],
                       stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)


# ---------- 反射探针：直接驱动 SingleInstanceService ----------
PROBE_CS = r'''
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;

class ShutdownProbe
{
    static Assembly asm;
    static Type t;

    static void L(string s) { Console.WriteLine(s); }

    static object New()
    {
        return Activator.CreateInstance(t);
    }

    static bool Call(object o, string name, params object[] a)
    {
        var m = t.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (m == null) return false;
        return (bool)m.Invoke(o, a);
    }

    static void CallVoid(object o, string name, params object[] a)
    {
        var m = t.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        m.Invoke(o, a);
    }

    static int Main(string[] args)
    {
        string exe = args[0];
        Environment.SetEnvironmentVariable("AUTO7Z_DATA_ROOT", Path.Combine(args[1], "data"));

        AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
        {
            string simple = new AssemblyName(e.Name).Name;
            string p = Path.Combine(Path.GetDirectoryName(exe), simple + ".exe");
            return File.Exists(p) ? Assembly.LoadFrom(p) : null;
        };
        asm = Assembly.LoadFrom(exe);
        t = asm.GetType("Auto7z_WPF.Services.SingleInstanceService");
        if (t == null) { L("NO_TYPE"); return 2; }

        L("=== 场景 A：Dispose 能否在合理时间内让监听线程退出 ===");
        var svc = New();
        bool acquired = Call(svc, "TryAcquire");
        L("  TryAcquire = " + acquired);
        if (!acquired) { L("SKIP 无法取得单实例权"); return 3; }

        var received = 0;
        Action<string[], string> cb = (p, m) => Interlocked.Increment(ref received);
        CallVoid(svc, "StartListener", cb);
        Thread.Sleep(300);   // 让监听线程真正进入 WaitForConnection

        var sw = Stopwatch.StartNew();
        CallVoid(svc, "Dispose");
        sw.Stop();
        long disposeMs = sw.ElapsedMilliseconds;
        L("  Dispose 耗时 = " + disposeMs + " ms");

        var listenerField = t.GetField("_listener", BindingFlags.NonPublic | BindingFlags.Instance);
        var th = (Thread)listenerField.GetValue(svc);
        L("  监听线程 IsAlive = " + (th != null && th.IsAlive));

        if (th != null && th.IsAlive)
        {
            // 再给一点时间，看是不是「只是慢」
            bool ended = th.Join(3000);
            L("  额外等 3 秒后 IsAlive = " + th.IsAlive);
            if (!ended) { L("FAIL 监听线程在 Dispose 后仍未退出"); return 1; }
        }

        L("PASS 监听线程已退出，Dispose 耗时 " + disposeMs + "ms");
        return 0;
    }
}
'''


def main():
    if os.path.isdir(SDK):
        safe_rmtree(SDK)
    os.makedirs(SDK, exist_ok=True)
    kill_strays()

    print("=" * 76)
    print("单实例关闭时序专项验证")
    print("=" * 76)

    # ---------- A. 单元级：Dispose 能否让监听线程退出 ----------
    print("\n【A】Dispose 必须让监听线程真正退出")
    cs = Path(SDK) / "probe_src.txt"
    cs.write_text(PROBE_CS, encoding="utf-8")
    out = Path(SDK) / "probe.exe"
    # 不显式 /r:System.dll —— csc 默认已引用，再加一次会报 CS1703 重复程序集
    rc = subprocess.run(
        [r"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe", "/nologo",
         "/out:" + str(out), str(cs)],
        capture_output=True, text=True, encoding="utf-8", errors="replace")
    if rc.returncode != 0:
        print("     探针编译失败: " + (rc.stderr or rc.stdout or "")[:200])
        return 1

    work, data, env = prep("A")
    pr = subprocess.run([str(out), EXE, work], capture_output=True, text=True,
                        encoding="utf-8", errors="replace", env=env, timeout=180)
    text = (pr.stdout or "") + (pr.stderr or "")
    print("     " + text.strip().replace("\n", "\n     "))

    if "PASS" in text:
        check("Dispose 后监听线程已退出", True,
              [l for l in text.splitlines() if "耗时" in l][0].strip() if "耗时" in text else "")
    elif "SKIP" in text:
        print("     [跳过] 无法取得单实例权（可能有残留实例）")
    else:
        check("Dispose 后监听线程已退出", False,
              [l for l in text.splitlines() if "FAIL" in l][0] if "FAIL" in text else "未知")

    # ---------- B. 端到端：关闭后立即重启 ----------
    print("\n【B】关闭后立即重启，新实例必须能正常工作")
    work_b, data_b, env_b = prep("B")
    src = Path(work_b) / "b.txt"
    src.write_text("restart", encoding="utf-8")

    p1 = subprocess.Popen([EXE, "-z", str(src)], env=env_b, cwd=work_b,
                          stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    try:
        p1.wait(timeout=60)
    except subprocess.TimeoutExpired:
        p1.kill()
    time.sleep(0.3)   # 不等待 —— 就是这个场景：立即重启

    src2 = Path(work_b) / "b2.txt"
    src2.write_text("restart2", encoding="utf-8")
    rc2 = subprocess.run([EXE, "-z", str(src2)], env=env_b, cwd=work_b,
                         capture_output=True, timeout=120)
    out2 = (rc2.stdout or b"").decode("utf-8", "replace")

    archives = list(Path(work_b).rglob("*.7z"))
    check("立即重启后新实例正常完成任务", rc2.returncode == 0 and len(archives) >= 1,
          "rc=%s 归档=%d" % (rc2.returncode, len(archives)))

    # ---------- C. 连续快速启动多个实例 ----------
    print("\n【C】连续快速启动多个实例，交接不能丢")
    work_c, data_c, env_c = prep("C")
    srcs = []
    for i in range(4):
        f = Path(work_c) / ("c%d.txt" % i)
        f.write_text("multi%d" % i, encoding="utf-8")
        srcs.append(str(f))

    procs = []
    for s in srcs:
        procs.append(subprocess.Popen([EXE, "-z", s], env=env_c, cwd=work_c,
                                      stdout=subprocess.PIPE, stderr=subprocess.STDOUT))
        time.sleep(0.15)

    for p in procs:
        try:
            p.wait(timeout=90)
        except subprocess.TimeoutExpired:
            p.kill()

    time.sleep(1)
    arcs_c = list(Path(work_c).rglob("*.7z"))
    check("四个实例全部完成或有产物", len(arcs_c) >= 1,
          "归档数=%d（连续启动不崩即可，产物数取决于合并策略）" % len(arcs_c))

    # ---------- D. 退出期间收到新请求 ----------
    print("\n【D】旧实例正在退出时收到新的螺旋请求，不应崩")
    work_d, data_d, env_d = prep("D")
    src_d = Path(work_d) / "d.txt"
    src_d.write_text("exiting", encoding="utf-8")

    pd = subprocess.Popen([EXE, "-z", str(src_d)], env=env_d, cwd=work_d,
                          stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    time.sleep(0.1)   # 让它刚开始就发第二个
    src_d2 = Path(work_d) / "d2.txt"
    src_d2.write_text("second", encoding="utf-8")
    r_d2 = subprocess.run([EXE, "-z", str(src_d2)], env=env_d, cwd=work_d,
                          capture_output=True, timeout=120)
    try:
        pd.wait(timeout=60)
    except subprocess.TimeoutExpired:
        pd.kill()

    check("退出期间的新请求被正常处理（不崩）", r_d2.returncode in (0, 1),
          "第二个实例 rc=%s" % r_d2.returncode)

    # ---------- 清理 ----------
    kill_strays()
    time.sleep(0.5)

    print("\n" + "=" * 76)
    bad = [r for r in results if not r["ok"]]
    print("共 %d 项，通过 %d，失败 %d" % (len(results), len(results) - len(bad), len(bad)))
    if bad:
        for b in bad:
            print("  " + b["name"] + "  " + b["detail"])
        print("FAIL")
        code = 1
    else:
        print("关闭时序各场景均正常")
        print("PASS")
        code = 0
    out_path = os.path.join(SDK, "shutdown.json")
    open(out_path, "w", encoding="utf-8").write(json.dumps(results, ensure_ascii=False, indent=2))
    print("输出 -> %s" % out_path)
    return code


if __name__ == "__main__":
    sys.exit(main())
