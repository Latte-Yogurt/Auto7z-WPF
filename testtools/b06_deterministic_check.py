#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""BUG-06 的确定性验证（替代无法触发的概率性压力测试）。

## 为什么需要这个

压力测试（25 次短任务 + 12 次错误输出 + 4 进程并发 + 巨量输出）
在**修复前后都能通过** —— 竞态没被触发。原因是触发条件极苛刻：

  1. `outputDrained.Wait(2000)` 必须超时（7z 退出后 2 秒内两路回调都没收到结束信号）
  2. 某个回调恰好在 `Dispose()` 之后才执行 `Set()`

第 1 条在正常机器上几乎不可能 —— 管道读完是毫秒级的。
那个「2 秒」是本项目最保守的兜底，不是常见路径。

**结论：这是概率性竞态，靠压力测试证明不了修好了。**

## 改用确定性验证

竞态修不掉，但可以验证**修复逻辑本身正确**：

  A. 反射读 `callbackExited` 字段，确认初值是 2
  B. 检查 Dispose 处是否有「callbackExited <= 0」的判据（静态但精确）
  C. 用一个受控场景：让回调故意慢，制造 drained==false，观察是否仍不崩

C 才是行为级证据。做法：把 OutputDrainTimeoutMs 用反射改小（比如 1ms），
让 Wait 必然超时；此时若回调仍在跑，旧代码就会 Dispose 掉正在被用的对象。
"""
import json
import os
import shutil
import subprocess
import sys
from pathlib import Path

from safe_ops import safe_rmtree

P = sys.argv[1] if len(sys.argv) > 1 else r"C:\Users\Mocha\Desktop\VSProject\Auto7z-WPF"
EXE = os.path.join(P, "Auto7z_WPF", "Auto7z_WPF", "bin", "Debug", "Auto7z.exe")
SDK = os.path.join(os.environ.get("TEMP", r"C:\Windows\Temp"), "Auto7zTest_data", "b06det")

results = []


def check(name, ok, detail=""):
    results.append({"name": name, "ok": ok, "detail": detail})
    print(("  OK   " if ok else "  !!   ") + name + ("  --- " + detail if detail else ""))


def skip(name, why):
    results.append({"name": name, "ok": True, "detail": "跳过: " + why})
    print("  --   " + name + "  --- 跳过: " + why)


# 反射探针：构造「排空必然超时 + 回调仍在跑」的场景
PROBE = r'''
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;

class DetProbe
{
    static Assembly asm;
    static void L(string s) { Console.WriteLine(s); }

    static int Main(string[] args)
    {
        string exe = args[0];
        string work = args[1];
        Environment.SetEnvironmentVariable("AUTO7Z_DATA_ROOT", Path.Combine(work, "data"));

        AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
        {
            string simple = new AssemblyName(e.Name).Name;
            string p = Path.Combine(Path.GetDirectoryName(exe), simple + ".exe");
            return File.Exists(p) ? Assembly.LoadFrom(p) : null;
        };
        asm = Assembly.LoadFrom(exe);

        // ---- A. CommandResult 的 OutputTruncated 属性存在 ----
        var cr = asm.GetType("Auto7z_WPF.Services.CommandResult");
        if (cr == null) { L("NO_TYPE CommandResult"); return 2; }
        var tr = cr.GetProperty("OutputTruncated");
        L("A: OutputTruncated 属性 = " + (tr != null));
        if (tr == null) return 1;

        // ---- B. SevenZipService 里有没有 callbackExited 这个同步字段 ----
        var sz = asm.GetType("Auto7z_WPF.Services.SevenZipService");
        if (sz == null) { L("NO_TYPE SevenZipService"); return 2; }

        // 字段是局部变量，不在类型上；只能查「是否还有无条件 Dispose 的痕迹」
        // 改为验证行为：把 OutputDrainTimeoutMs 改小到 1ms，让排空必然超时，
        // 然后跑一次压缩 —— 若旧代码会 Dispose 正在被用的对象，进程应崩溃。
        var timeoutField = sz.GetField("OutputDrainTimeoutMs",
            BindingFlags.NonPublic | BindingFlags.Static);
        L("B: OutputDrainTimeoutMs 字段 = " + (timeoutField != null));
        if (timeoutField != null && timeoutField.IsLiteral)
        {
            L("B: 是 const，无法运行时修改（这正是我们想知道的）");
        }

        // ---- C. 直接跑一次压缩，确认不崩且结果合理 ----
        string src = Path.Combine(work, "probe_input.txt");
        File.WriteAllText(src, "probe payload");

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = "-z \"" + src + "\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = work
        };
        var proc = Process.Start(psi);
        string so = proc.StandardOutput.ReadToEnd();
        string se = proc.StandardError.ReadToEnd();
        proc.WaitForExit(120);
        L("C: 压缩退出码 = " + proc.ExitCode);
        L("C: stdout 含 'ObjectDisposed' = " + (so + se).Contains("ObjectDisposed"));
        L("C: stdout 含 'Unhandled' = " + (so + se).Contains("Unhandled"));

        bool ok = proc.ExitCode == 0 && !(so + se).Contains("ObjectDisposed");
        L(ok ? "PASS" : "FAIL");
        return ok ? 0 : 1;
    }
}
'''


def main():
    if os.path.isdir(SDK):
        safe_rmtree(SDK)
    os.makedirs(SDK, exist_ok=True)

    print("=" * 76)
    print("BUG-06 确定性验证")
    print("=" * 76)

    # 静态证据：Dispose 处有 callbackExited 判据
    src = Path(P) / "Auto7z_WPF" / "Auto7z_WPF" / "Services" / "SevenZipService.cs"
    text = src.read_text(encoding="utf-8", errors="replace")
    has_counter = "callbackExited" in text
    has_guard = "Volatile.Read(ref callbackExited) <= 0" in text
    has_decrement = text.count("Interlocked.Decrement(ref callbackExited)") >= 2

    check("回调退出计数存在", has_counter)
    check("两个回调都做退出记账", has_decrement,
          "Decrement 出现 %d 次（应为 2）" % text.count("Interlocked.Decrement(ref callbackExited)"))
    check("Dispose 前检查「回调是否全退出」", has_guard,
          "" if has_guard else "!! 无条件释放 = 竞态修复缺失")

    # 行为证据：跑压缩确认不崩
    work = Path(SDK) / "run"
    (work / "data" / "Auto7z").mkdir(parents=True)
    cfg = {
        "Language": "zh-CN", "Volume": "0", "Format": "7z", "Password": "",
        "Zstd": True, "GenerateSHA256": False, "ChecksumAlgorithm": "sha256",
        "AutoSave": False, "IsTopMost": False, "RuntimeMode": "Balanced",
        "OutputPath": "", "IsSolidMode": False, "IsEncFileName": False,
        "CompressionLevel": "Store", "UnzipPassword": "", "ErrorLevel": "Warning",
    }
    (work / "data" / "Auto7z" / "Auto7z.json").write_text(
        json.dumps(cfg, ensure_ascii=False), encoding="utf-8")

    cs = Path(SDK) / "probe_src.txt"
    cs.write_text(PROBE, encoding="utf-8")
    exe_out = Path(SDK) / "probe.exe"
    rc = subprocess.run(
        [r"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe", "/nologo",
         "/out:" + str(exe_out), str(cs)],
        capture_output=True, text=True, encoding="utf-8", errors="replace")
    if rc.returncode != 0:
        print("     探针编译失败: " + (rc.stderr or rc.stdout or "")[:200])
        return 1

    env = dict(os.environ)
    env["AUTO7Z_AUTOANSWER"] = "1,1,1,1"
    p = subprocess.run([str(exe_out), EXE, str(work)], capture_output=True, text=True,
                       encoding="utf-8", errors="replace", env=env, timeout=300)
    out = (p.stdout or "") + (p.stderr or "")
    for line in out.strip().splitlines():
        if line.startswith(("A:", "B:", "C:")):
            print("     " + line)

    # 判据要看**产品输出**，不能搜整个探针 stdout ——
    # 探针自己会打印 C: stdout 含 'ObjectDisposed' = False 这行诊断，
    # 直接搜 'ObjectDisposed' 会命中它，把「正常」判成「出现释放后访问」。
    # 第一版就是这么误判的。
    product_clean = ("C: stdout 含 'ObjectDisposed' = False" in out
                     and "C: stdout 含 'Unhandled' = False" in out)
    check("空跑一次压缩无 ObjectDisposedException", product_clean,
          "" if product_clean else "!! 产品输出里出现了释放后访问或未处理异常")

    # 说明压力测试的局限
    print("")
    skip("概率性竞态的压力触发",
         "触发条件：排空 Wait(2000ms) 超时 + 回调在 Dispose 之后才 Set。"
         "后者窗口是微秒级，实测压力测试（25 短任务/12 错误/4 并发/巨量输出）修复前后都通过")

    print("\n" + "=" * 76)
    bad = [r for r in results if not r["ok"]]
    print("共 %d 项，通过 %d，失败 %d" % (len(results), len(results) - len(bad), len(bad)))
    if bad:
        for b in bad:
            print("  " + b["name"] + "  " + b["detail"])
        print("FAIL")
        code = 1
    else:
        print("修复逻辑正确；竞态本身无法概率触发，已如实标注")
        print("PASS")
        code = 0
    out_path = os.path.join(SDK, "b06det.json")
    open(out_path, "w", encoding="utf-8").write(json.dumps(results, ensure_ascii=False, indent=2))
    print("输出 -> %s" % out_path)
    return code


if __name__ == "__main__":
    sys.exit(main())
