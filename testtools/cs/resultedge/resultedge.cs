using System;
using System.Reflection;
using System.Windows.Threading;

// resultedge —— SetResult 三态重载的边界行为（运行时，进程内）
//
// 补的是「正常路径都对、但边界没人测」这一维度。三态重写时最容易漏掉的不是主流程，
// 而是这些：枚举越界值走哪个分支、同文本不同 severity 时标志是否还更新、
// 离开 Warning 后黄标会不会残留（残留就会一直黄下去）。
//
// 用法: resultedge.exe <Auto7z.exe 路径>
// 退出码: 0 = 全部符合；1 = 有不符；2 = 用法/环境不满足
//
// 覆盖的边界（都是「没想过会发生、但可能发生」的）：
//   1. 枚举越界值 (ResultSeverity)99 → 走 default 分支还是抛异常？
//   2. 同文本不同 severity → 标志位是否仍然更新？（SetProperty 文本不变返回 false）
//   3. null 文本 → 是否变成空串而不是 NullReferenceException？
//   4. 连续快速调用 → 最后一次的状态是否胜出？
//   5. 纯 Warning 之后再 Success → 警告标志是否被清掉？（残留会一直黄下去）
class ResultEdge
{
    static int problems = 0;
    static void Ok(string s) { Console.WriteLine("  OK    " + s); }
    static void Bad(string s) { problems++; Console.WriteLine("  !!    " + s); }
    static void Check(string n, bool c, string d)
    {
        string suf = string.IsNullOrEmpty(d) ? "" : "  (" + d + ")";
        if (c) Ok(n + suf); else Bad(n + suf);
    }

    [STAThread]
    static int Main(string[] a)
    {
        if (a.Length < 1)
        {
            Console.WriteLine("用法: resultedge.exe <Auto7z.exe 路径>");
            Console.WriteLine("  验证 SetResult 三态重载在边界输入下的行为。");
            return 2;
        }
        if (!System.IO.File.Exists(a[0]))
        {
            Console.WriteLine("被测程序不存在: " + a[0]);
            return 2;
        }
        var asm = Assembly.LoadFrom(a[0]);
        AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
        {
            string nm = new AssemblyName(e.Name).Name;
            string p = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(a[0]), nm + ".exe");
            return System.IO.File.Exists(p) ? Assembly.LoadFrom(p) : null;
        };

        var app = new ApplicationShim();
        var disp = Dispatcher.CurrentDispatcher;
        int rc = 1;
        disp.BeginInvoke(new Action(() =>
        {
            try { rc = Run(asm); }
            catch (Exception e) { Console.WriteLine("EX: " + Root(e)); rc = 1; }
            finally { disp.BeginInvokeShutdown(DispatcherPriority.Background); }
        }));
        Dispatcher.Run();
        return rc;
    }

    static int Run(Assembly asm)
    {
        var t = asm.GetType("Auto7z_WPF.Auto7zViewModel");
        var sev = asm.GetType("Auto7z_WPF.Models.ResultSeverity");
        var pWarn = t.GetProperty("IsResultWarning");
        var pFail = t.GetProperty("IsResultFailed");
        var pText = t.GetProperty("ResultText");
        var mSev = t.GetMethod("SetResult", BindingFlags.Public | BindingFlags.Instance,
            null, new[] { typeof(string), sev }, null);
        var mBool = t.GetMethod("SetResult", BindingFlags.Public | BindingFlags.Instance,
            null, new[] { typeof(string), typeof(bool) }, null);

        if (mSev == null) { Console.WriteLine("!! 没有三态重载"); return 1; }

        object vm = Activator.CreateInstance(t);
        object S = Enum.Parse(sev, "Success");
        object W = Enum.Parse(sev, "Warning");
        object F = Enum.Parse(sev, "Failed");

        Console.WriteLine("=== SetResult 三态边界行为 ===");
        Console.WriteLine("");

        // --- 1. 枚举越界值 ---
        Console.WriteLine("【1. 枚举越界 (ResultSeverity)99】");
        try
        {
            object bogus = Enum.ToObject(sev, 99);
            mSev.Invoke(vm, new[] { (object)"越界", bogus });
            bool w = (bool)pWarn.GetValue(vm);
            bool f = (bool)pFail.GetValue(vm);
            Check("越界值不抛异常", true, "");
            Check("越界值既不置警告也不置失败（走 default=成功）", !w && !f,
                "warning=" + w + " failed=" + f);
        }
        catch (Exception e)
        {
            Bad("越界值抛了异常: " + Root(e));
        }

        // --- 2. 同文本不同 severity ---
        Console.WriteLine("");
        Console.WriteLine("【2. 同文本、不同 severity】");
        mSev.Invoke(vm, new[] { (object)"同一句话", S });
        mSev.Invoke(vm, new[] { (object)"同一句话", W });
        bool w2 = (bool)pWarn.GetValue(vm);
        bool f2 = (bool)pFail.GetValue(vm);
        Check("文本不变时警告标志仍然更新为 true", w2, "warning=" + w2);
        Check("文本不变时失败标志仍为 false", !f2, "failed=" + f2);

        mSev.Invoke(vm, new[] { (object)"同一句话", F });
        bool w3 = (bool)pWarn.GetValue(vm);
        bool f3 = (bool)pFail.GetValue(vm);
        Check("切到 Failed 时警告标志被清掉", !w3, "warning=" + w3);
        Check("切到 Failed 时失败标志为 true", f3, "failed=" + f3);

        // --- 3. null 文本 ---
        Console.WriteLine("");
        Console.WriteLine("【3. null 文本】");
        try
        {
            mSev.Invoke(vm, new object[] { null, S });
            string txt = (string)pText.GetValue(vm);
            Check("null 文本变成空串而不是抛异常", txt == string.Empty,
                txt == null ? "null" : "\"" + txt + "\"");
        }
        catch (Exception e) { Bad("null 文本抛异常: " + Root(e)); }

        // --- 4. 连续调用，最后一次胜出 ---
        Console.WriteLine("");
        Console.WriteLine("【4. 连续快速调用】");
        mSev.Invoke(vm, new[] { (object)"A", F });
        mSev.Invoke(vm, new[] { (object)"B", W });
        mSev.Invoke(vm, new[] { (object)"C", S });
        bool w4 = (bool)pWarn.GetValue(vm);
        bool f4 = (bool)pFail.GetValue(vm);
        string t4 = (string)pText.GetValue(vm);
        Check("最后一次 Success 之后两个标志都清空", !w4 && !f4,
            "warning=" + w4 + " failed=" + f4);
        Check("最后一次的文本胜出", t4 == "C", "\"" + t4 + "\"");

        // --- 5. Warning 后接 Success，警告标志必须清掉 ---
        Console.WriteLine("");
        Console.WriteLine("【5. Warning → Success 的标志清理】");
        mSev.Invoke(vm, new[] { (object)"警告态", W });
        bool w5a = (bool)pWarn.GetValue(vm);
        mSev.Invoke(vm, new[] { (object)"成功态", S });
        bool w5b = (bool)pWarn.GetValue(vm);
        Check("进入 Warning 时置位", w5a, "warning=" + w5a);
        Check("离开 Warning 时清除（否则会一直黄下去）", !w5b, "warning=" + w5b);

        // --- 6. 两态重载必须**不存在** ---
        //
        // 颜色契约只认三态枚举：Success / Warning / Failed 三个成员是唯一入口。
        // 只要还留着 SetResult(string, bool)，调用点就能绕过枚举表达结论 ——
        // 那种旁路会把「成功但带警告」压成红或绿两种颜色之一，而 colorprobe
        // 只测得到走过枚举的路径，测不到旁路。删掉重载，这条约束就变成编译期强制。
        Console.WriteLine("");
        Console.WriteLine("【6. 两态重载必须不存在】");
        Check("不存在 SetResult(string, bool) 两态重载", mBool == null,
            mBool == null ? "已移除" : "仍存在 —— 会绕过三态枚举");

        Console.WriteLine("");
        Console.WriteLine(problems == 0 ? "全部符合预期 ✓" : "TOTAL PROBLEMS: " + problems);
        return problems == 0 ? 0 : 1;
    }

    // 只用 Dispatcher，不需要 Application 实例（ViewModel 层不碰 WPF 窗口）。
    class ApplicationShim { }

    static string Root(Exception e)
    {
        Exception x = e;
        while (x.InnerException != null) x = x.InnerException;
        return x.Message;
    }
}
