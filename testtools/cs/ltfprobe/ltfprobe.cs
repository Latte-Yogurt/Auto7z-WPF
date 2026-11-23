using System;
using System.IO;
using System.Reflection;
using System.Windows.Threading;

// ltfprobe —— 任务结果三态契约 + LastTaskFailed 跨任务泄漏（运行时，进程内）
//
// 它守两件用户可见的事：
//   1. 结果行必须是三态（成功绿 / 失败红 / 成功但有警告黄）。
//      只有一个 bool 时「成功但有警告」会被挤进失败档，界面只能涂红。
//   2. LastTaskFailed 不得跨任务泄漏。
//      它一旦被上一次失败/部分成功置位且没有在新任务入口复位，之后每一次
//      干净的解压收尾都会读到 true，于是「解压完成」被写成
//      「解压已完成，但部分条目被跳过」—— 明明什么都没跳过。
//      这条路径不写日志（走的是成功分支），所以日志里查不到痕迹。
//
// 为什么必须进程内 + Dispatcher：
//   - LastTaskFailed 是 ViewModel 实例字段。命令行模式每次独立进程，字段天然从
//     false 起跑，复现不出泄漏；泄漏只发生在「同一个 ViewModel 连续跑两个任务」
//     —— 也就是 GUI 与右键模式。
//   - RunTaskAsync 是 async，await 后的续体要回到原上下文；没有 Dispatcher 循环时
//     会在非 UI 线程上继续，属性通知与 UpdateProgress 直接抛异常。
//
// 用法:
//   ltfprobe.exe <Auto7z.exe>          自建测试包，跑完整检查
//   ltfprobe.exe <Auto7z.exe> --keep   保留临时目录（调试用）
//
// 退出码: 0 = 全部成立；1 = 有不成立；2 = 用法/环境不满足
class LtfProbe
{
    static int failures = 0;
    static string tempRoot;

    static void Check(string name, bool actual, bool expected)
    {
        bool ok = actual == expected;
        if (!ok) failures++;
        Console.WriteLine("  {0} {1}: 实际={2} 预期={3}",
            ok ? "OK  " : "!!  ", name, actual, expected);
    }

    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length < 1)
        {
            Console.WriteLine("用法: ltfprobe.exe <Auto7z.exe> [--keep]");
            Console.WriteLine("  验证：结果三态契约 + LastTaskFailed 不跨任务泄漏");
            return 2;
        }

        bool keep = Array.IndexOf(args, "--keep") >= 0;
        string appPath = Path.GetFullPath(args[0]);
        if (!File.Exists(appPath))
        {
            Console.WriteLine("被测程序不存在: " + appPath);
            return 2;
        }

        tempRoot = Path.Combine(Path.GetTempPath(),
            "Auto7zTest_ltfprobe_" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(tempRoot);

        try
        {
            // 先造一个干净的包（用被测程序自己压，保证是它认识的格式）
            string archive = BuildArchive(appPath);
            if (archive == null)
            {
                Console.WriteLine("!! 造测试包失败");
                return 2;
            }

            var asm = Assembly.LoadFrom(appPath);
            AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
            {
                string simple = new AssemblyName(e.Name).Name;
                string p = Path.Combine(Path.GetDirectoryName(appPath), simple + ".exe");
                return File.Exists(p) ? Assembly.LoadFrom(p) : null;
            };

            var dispatcher = Dispatcher.CurrentDispatcher;
            int rc = 1;
            dispatcher.BeginInvoke(new Action(() =>
            {
                try { rc = RunChecks(asm, dispatcher, archive); }
                catch (Exception e)
                {
                    Console.WriteLine("!! 检查过程抛异常: " + e.GetBaseException().Message);
                    rc = 1;
                }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            }));
            Dispatcher.Run();
            return rc;
        }
        finally
        {
            if (!keep)
            {
                try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true); }
                catch (Exception) { }
            }
            else
            {
                Console.WriteLine("");
                Console.WriteLine("保留临时目录: " + tempRoot);
            }
        }
    }

    static string BuildArchive(string appPath)
    {
        string src = Path.Combine(tempRoot, "payload.txt");
        File.WriteAllText(src, "CLEAN-PAYLOAD", new System.Text.UTF8Encoding(false));

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = appPath,
            Arguments = "-z \"" + src + "\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        var proc = System.Diagnostics.Process.Start(psi);
        proc.StandardOutput.ReadToEnd();
        proc.WaitForExit(180000);

        var hits = Directory.GetFiles(tempRoot, "payload.7z", SearchOption.AllDirectories);
        return hits.Length > 0 ? hits[0] : null;
    }

    static void PumpUntil(Dispatcher d, Func<bool> done, int maxMs)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < maxMs)
        {
            d.Invoke(new Action(() => { }), DispatcherPriority.Background);
            System.Threading.Thread.Sleep(20);
            if (done()) return;
        }
    }

    static int RunChecks(Assembly asm, Dispatcher d, string archive)
    {
        var t = asm.GetType("Auto7z_WPF.Auto7zViewModel");
        var ltf = t == null ? null : t.GetProperty("LastTaskFailed");
        var isFailed = t == null ? null : t.GetProperty("IsResultFailed");
        var isWarn = t == null ? null : t.GetProperty("IsResultWarning");
        var textProp = t == null ? null : t.GetProperty("ResultText");
        var busy = t == null ? null : t.GetProperty("IsBusy");
        var modeProp = t == null ? null : t.GetProperty("CurrentOperationMode");
        var setPending = t == null ? null : t.GetMethod("SetPendingFiles");
        var beginExtract = t == null ? null : t.GetMethod("BeginExtractTask");
        var opModeType = asm.GetType("Auto7z_WPF.Models.OperationMode");

        if (t == null || ltf == null || isFailed == null || textProp == null ||
            modeProp == null || setPending == null || beginExtract == null || opModeType == null)
        {
            Console.WriteLine("!! 缺少必需的成员（ViewModel / LastTaskFailed / IsResultFailed /");
            Console.WriteLine("   ResultText / CurrentOperationMode / SetPendingFiles / BeginExtractTask）");
            return 1;
        }

        Console.WriteLine("=== 任务结果三态契约 + LastTaskFailed 跨任务泄漏 ===");
        Console.WriteLine("被测程序: " + asm.Location);
        Console.WriteLine("");

        Console.WriteLine("【探针自检】");
        Check("IsResultWarning 存在（三态能表达）", isWarn != null, true);
        if (isWarn == null) { Console.WriteLine("失败 1 项"); return 1; }

        object vm = Activator.CreateInstance(t);
        Check("ViewModel 能构造", vm != null, true);

        // ---- 三态契约（直接驱动 SetResult，验证标记互斥）----
        var setBool = t.GetMethod("SetResult", BindingFlags.Public | BindingFlags.Instance,
            null, new[] { typeof(string), typeof(bool) }, null);
        var severityType = asm.GetType("Auto7z_WPF.Models.ResultSeverity");
        var setSev = severityType == null ? null :
            t.GetMethod("SetResult", BindingFlags.Public | BindingFlags.Instance,
                null, new[] { typeof(string), severityType }, null);

        Console.WriteLine("");
        Console.WriteLine("【三态着色契约】");
        Check("存在 SetResult(string, ResultSeverity) 重载", setSev != null, true);

        if (setBool != null && setSev != null)
        {
            setBool.Invoke(vm, new object[] { "成功", false });
            Check("成功：不红", (bool)isFailed.GetValue(vm), false);
            Check("成功：不黄", (bool)isWarn.GetValue(vm), false);

            setBool.Invoke(vm, new object[] { "失败", true });
            Check("失败：红", (bool)isFailed.GetValue(vm), true);
            Check("失败：不黄", (bool)isWarn.GetValue(vm), false);

            object warnVal = Enum.ToObject(severityType, 1);   // ResultSeverity.Warning
            setSev.Invoke(vm, new object[] { "警告", warnVal });
            Check("警告：不红（警告不算失败）", (bool)isFailed.GetValue(vm), false);
            Check("警告：黄", (bool)isWarn.GetValue(vm), true);
        }

        // ---- 跨任务泄漏（真实解压任务）----
        Console.WriteLine("");
        Console.WriteLine("【LastTaskFailed 跨任务泄漏】");
        Console.WriteLine("  步骤 1：人为置位 LastTaskFailed=true（模拟上一次部分成功）");
        ltf.GetSetMethod(true).Invoke(vm, new object[] { true });
        Check("置位生效（前提成立）", (bool)ltf.GetValue(vm), true);

        Console.WriteLine("  步骤 2：解压一个完全干净的包");
        modeProp.SetValue(vm, Enum.Parse(opModeType, "UnZip"));
        setPending.Invoke(vm, new object[] { new[] { archive } });
        beginExtract.Invoke(vm, null);
        PumpUntil(d, () => busy == null || !(bool)busy.GetValue(vm), 60000);

        bool failed = (bool)isFailed.GetValue(vm);
        bool warned = (bool)isWarn.GetValue(vm);
        string text = (string)textProp.GetValue(vm);

        Console.WriteLine("  结束: IsResultFailed=" + failed + " IsResultWarning=" + warned +
                          " ResultText=" + (text ?? "(null)"));
        Check("任务结束后不再忙", busy == null || !(bool)busy.GetValue(vm), true);
        Check("干净解压不红", failed, false);
        Check("干净解压不黄", warned, false);
        Check("结果文本不含「部分文件被跳过」",
              text == null || !text.Contains("部分文件被跳过"), true);
        Check("结果文本不含「部分条目被跳过」",
              text == null || !text.Contains("部分条目被跳过"), true);

        Console.WriteLine("");
        if (failures == 0)
        {
            Console.WriteLine("全部符合预期 ✓");
            return 0;
        }
        Console.WriteLine("失败 " + failures + " 项");
        return 1;
    }
}
