using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

class BatchProgressProbe
{
    static Application app;
    static int problems = 0;

    static void L(string s) { Console.WriteLine(s); }
    static void Check(string n, bool ok, string d)
    {
        Console.WriteLine((ok ? "  OK   " : "  !!   ") + n + (d.Length > 0 ? "  --- " + d : ""));
        if (!ok) problems++;
    }

    [STAThread]
    static int Main(string[] a)
    {
        if (a.Length < 2) { L("usage: batchprog.exe <Auto7z.exe> <work>"); return 2; }
        string work = a[1];
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AUTO7Z_AUTOANSWER")))
            Environment.SetEnvironmentVariable("AUTO7Z_AUTOANSWER", "close,close,close,close,close");
        Environment.SetEnvironmentVariable("AUTO7Z_DATA_ROOT", Path.Combine(work, "data-bp"));

        AppDomain.CurrentDomain.AssemblyResolve += delegate(object s, ResolveEventArgs e)
        {
            string simple = new AssemblyName(e.Name).Name;
            string p = Path.Combine(Path.GetDirectoryName(a[0]), simple + ".exe");
            return File.Exists(p) ? Assembly.LoadFrom(p) : null;
        };
        var asm = Assembly.LoadFrom(a[0]);
        app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        try
        {
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/Auto7z;component/Resources/Languages/zh-CN.xaml", UriKind.Absolute)
            });
        }
        catch { }

        Type t = asm.GetType("Auto7z_WPF.Auto7zViewModel");
        object vm = Activator.CreateInstance(t);

        // 直接检查 _batchReportedPercent 字段在「GUI 路径起任务」后是否被复位。
        //
        // 判据不需要真跑任务：只要模拟「上一批已跑到 100%」这个残留状态，
        // 然后走 BeginXxxTask（GUI 路径 = RunTaskAsync），看字段有没有回到 0。
        FieldInfo bp = t.GetField("_batchReportedPercent", BindingFlags.NonPublic | BindingFlags.Instance);
        if (bp == null) { L("!! 找不到 _batchReportedPercent"); return 1; }

        L("=== 批量进度百分比的跨任务残留 ===");

        // 造一个「上一批跑到 100%」的残留
        bp.SetValue(vm, 100);
        L("  手动把 _batchReportedPercent 设为 100（模拟上一批跑完）");

        // 造一个可压缩的输入，走 GUI 路径起任务
        string src = Path.Combine(work, "bptest.txt");
        File.WriteAllText(src, "batch progress probe");
        t.GetMethod("SetPendingFiles").Invoke(vm, new object[] { new[] { src } });

        var modeProp = t.GetProperty("CurrentOperationMode");
        modeProp.SetValue(vm, Enum.Parse(modeProp.PropertyType, "Zip"));

        // BeginCompressTask 内部走 StartTaskInBackground → RunTaskAsync（GUI 路径）
        t.GetMethod("BeginCompressTask").Invoke(vm, null);

        // 立刻读：任务刚开始，复位（若有）应当已经发生
        // 尽早采样：任务可能几十毫秒就跑完推到 100%，采样太晚就看不到复位后的 0。
        int afterAsync = 100;
        for (int i = 0; i < 40; i++)
        {
            Thread.Sleep(2);
            int v = (int)bp.GetValue(vm);
            if (v < 100) { afterAsync = v; break; }
            afterAsync = v;
        }
        L("  GUI 路径（RunTaskAsync）起任务后最早采样: _batchReportedPercent = " + afterAsync);

        WaitIdle(vm, t);
        L("  任务完成后: _batchReportedPercent = " + bp.GetValue(vm));

        // 对照：同步路径（RunTaskAndWait）会复位
        // 同步路径对照：它本来就重置，这里主要确认对照成立。
        // 它必须在**新实例**上跑 —— 同一个 vm 连跑两次压缩，第二次的产物命名会撞车。
        try
        {
            object vm2 = Activator.CreateInstance(t);
            t.GetMethod("SetPendingFiles").Invoke(vm2, new object[] { new[] { src } });
            t.GetProperty("CurrentOperationMode").SetValue(vm2,
                Enum.Parse(t.GetProperty("CurrentOperationMode").PropertyType, "Zip"));
            bp.SetValue(vm2, 100);
            t.GetMethod("RunCompressAndWait", Type.EmptyTypes).Invoke(vm2, null);
            L("  同步路径（RunTaskAndWait）跑完后: _batchReportedPercent = " + bp.GetValue(vm2));
        }
        catch (Exception ex)
        {
            Exception inner = ex is TargetInvocationException ? ex.InnerException : ex;
            L("  [提示] 同步路径对照未跑通: " + inner.GetType().Name);
        }

        L("");
        // GUI 路径如果没复位，afterAsync 应当仍是 100 附近（任务可能已经推进到更高）
        bool asyncReset = afterAsync < 100;
        Check("GUI 路径起任务时复位了批量进度", asyncReset,
              asyncReset ? "" : "残留 100 → 新任务的进度条会一直停在 100% 不动");

        L("");
        L(problems == 0 ? "PASS" : "TOTAL PROBLEMS: " + problems);
        return problems == 0 ? 0 : 1;
    }

    static void WaitIdle(object vm, Type t)
    {
        var busy = t.GetProperty("IsBusy");
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (DateTime.UtcNow < deadline)
        {
            Pump();
            object b = busy.GetValue(vm);
            if (b == null || !(bool)b) break;
            Thread.Sleep(50);
        }
        Pump();
    }

    static void Pump()
    {
        if (app == null) return;
        for (int i = 0; i < 6; i++) { try { app.Dispatcher.Invoke(DispatcherPriority.Background, new Action(delegate { })); } catch { } Thread.Sleep(8); }
    }
}
