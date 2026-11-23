using System;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;

// colorprobe —— 结果行颜色映射（运行时，进程内）
//
// 守的是「结果行三态着色」：成功绿、失败红、成功但有警告黄。
//
// 为什么必须进程内、必须拿到真实 MainWindow：
//   ShowResult 是私有方法，颜色直接写在 MainText.Foreground 上，既不在 ViewModel 里、
//   也没有 XAML 绑定。ViewModel 层探针（resultprobe / ltfprobe）只能看到
//   IsResultFailed / IsResultWarning 两个标志，看不到真正刷到界面上的颜色 ——
//   而「三态被压成两态、警告被涂成红色」这类回归恰恰只体现在颜色上。
//
// 怎么拿到颜色：走产品自己提供的 **Debug 测试钩子** ——
//   MainWindow.ProbeResultAppearance / ProbeClearResult / ProbeResultForegroundIsLocal
// 它们整段包在 #if DEBUG 里，Release 交付物中不存在。
// 不用反射 Application._resourceAssembly 那种内部实现：那个私有静态字段
// 随 WPF 版本变动就可能失效，钩子则是跟着产品源码一起编译的。
//
// 被测程序是 Release 构建时，钩子反射不到 —— 那属于正确行为，本探针以退出码 3 跳过。
//
// 用法: colorprobe.exe <Auto7z.exe 路径>
// 退出码: 0 = 全部符合；1 = 有不符；2 = 用法/环境不满足；3 = Release 无测试钩子（跳过）
class ColorProbe
{
    static int problems = 0;

    static void L(string s) { Console.WriteLine(s); }
    static void Ok(string s) { L("  OK    " + s); }
    static void Bad(string s) { problems++; L("  !!    " + s); }

    static void Check(string name, bool cond, string detail)
    {
        string suffix = string.IsNullOrEmpty(detail) ? "" : "  (" + detail + ")";
        if (cond) Ok(name + suffix); else Bad(name + suffix);
    }

    [STAThread]
    static int Main(string[] a)
    {
        if (a.Length < 1)
        {
            Console.WriteLine("用法: colorprobe.exe <Auto7z.exe 路径>");
            Console.WriteLine("  验证结果行三态着色：成功绿 / 失败红 / 成功但有警告黄。");
            return 2;
        }

        string asmPath = Path.GetFullPath(a[0]);
        if (!File.Exists(asmPath))
        {
            Console.WriteLine("被测程序不存在: " + asmPath);
            return 2;
        }

        AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
        {
            string simple = new AssemblyName(e.Name).Name;
            string p = Path.Combine(Path.GetDirectoryName(asmPath), simple + ".exe");
            return File.Exists(p) ? Assembly.LoadFrom(p) : null;
        };

        var asm = Assembly.LoadFrom(asmPath);

        // 构造 MainWindow 时，里面的相对 pack URI（pack://application:,,,/Resources/Images/…）
        // 解析的是「应用资源程序集」= 本探针自己，产品那批图片一张都取不到。
        // 这个 setter 在 WPF 初始化后锁死，只能反射改私有静态字段。
        //
        // 注意：这一步**只为了能构造出窗口**。颜色断言走的是产品的 Debug 测试钩子
        // （ProbeResultAppearance 等），不依赖这个内部字段 —— 所以即使哪天 WPF 改了
        // 字段名，最坏结果也只是 MainWindow 构造失败并报错，而不是悄悄测错。
        ForceResourceAssembly(asm);

        // 先确认钩子存在。Release 构建里它被 #if DEBUG 编掉，反射回来是 null —— 这是正确的。
        var winType = asm.GetType("Auto7z_WPF.MainWindow");
        var sevType = asm.GetType("Auto7z_WPF.Models.ResultSeverity");
        if (winType == null || sevType == null)
        {
            Console.WriteLine("!! 找不到 MainWindow 或 ResultSeverity");
            return 1;
        }

        var probeAppearance = winType.GetMethod("ProbeResultAppearance");
        var probeClear = winType.GetMethod("ProbeClearResult");
        var probeIsLocal = winType.GetMethod("ProbeResultForegroundIsLocal");

        if (probeAppearance == null || probeClear == null || probeIsLocal == null)
        {
            Console.WriteLine("### RELEASE_NO_TEST_HOOK");
            Console.WriteLine("被测程序没有 Debug 测试钩子（ProbeResultAppearance 等）——");
            Console.WriteLine("这通常意味着它是 Release 构建。Release 不带测试开关是正确行为，本项跳过。");
            return 3;
        }

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

        // MainWindow 的样式全部来自产品的 UIResource.xaml（OperationMenuButtonStyle 等），
        // 不并入的话构造时就抛「无法找到名为 … 的资源」。语言词典同理：
        // 控件上的 DynamicResource 会在构造期解析 Header/Content。
        try
        {
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/Auto7z;component/Resources/WindowDirectory/UIResource.xaml",
                                 UriKind.Absolute)
            });
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/Auto7z;component/Resources/Languages/zh-CN.xaml",
                                 UriKind.Absolute)
            });
        }
        catch (Exception e)
        {
            Console.WriteLine("!! 加载产品资源字典失败: " + RootMessage(e));
            return 2;
        }

        int rc = 1;
        app.Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                rc = Run(asm, app, winType, sevType, probeAppearance, probeClear, probeIsLocal);
            }
            catch (Exception e)
            {
                Console.WriteLine("!! 检查过程抛异常: " + RootMessage(e));
                rc = 1;
            }
            finally { app.Dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
        }));
        app.Run();

        L("");
        L(problems == 0 ? "全部符合预期 ✓  (TOTAL PROBLEMS: 0)" : "TOTAL PROBLEMS: " + problems);
        return rc;
    }

    static int Run(Assembly asm, Application app, Type winType, Type sevType,
        MethodInfo probeAppearance, MethodInfo probeClear, MethodInfo probeIsLocal)
    {
        L("=== 结果行颜色映射（运行时，进程内，走 Debug 测试钩子）===");
        L("被测程序: " + asm.Location);
        L("");

        // ---- 探针自检：先证明钩子真能调通，否则下面的结论都没意义 ----
        L("【探针自检】");
        Window win;
        try
        {
            win = (Window)Activator.CreateInstance(winType);
            win.Show();
            Drain(app);
        }
        catch (Exception e)
        {
            L("!! MainWindow 构造失败: " + RootMessage(e));
            return 1;
        }

        try
        {
            Check("找到 ProbeResultAppearance", probeAppearance != null, "");
            Check("找到 ProbeClearResult", probeClear != null, "");
            Check("找到 ProbeResultForegroundIsLocal", probeIsLocal != null, "");

            var ps = probeAppearance.GetParameters();
            // 签名: (string text, ResultSeverity severity, out byte r, out byte g, out byte b, out string shownText)
            Check("ProbeResultAppearance 有 6 个参数（text, severity, r, g, b, shownText）",
                ps.Length == 6, "实际 " + ps.Length);
            if (ps.Length != 6)
            {
                return 1;
            }
            Check("第二参数是 ResultSeverity", ps[1].ParameterType == sevType, ps[1].ParameterType.Name);

            // 钩子能真的返回颜色（不是恒返回 0,0,0）
            object sevSuccess = Enum.Parse(sevType, "Success");
            byte r, g, b; string shown;
            Invoke(probeAppearance, win, "自检文本", sevSuccess, out r, out g, out b, out shown);
            Check("钩子返回了非透明颜色", !(r == 0 && g == 0 && b == 0), Rgb(r, g, b));
            Check("钩子回报的文本与传入一致", shown == "自检文本", "\"" + (shown ?? "null") + "\"");

            // ---- 三态颜色映射 ----
            L("");
            L("【三态颜色映射】");
            object success = Enum.Parse(sevType, "Success");
            object warning = Enum.Parse(sevType, "Warning");
            object failed = Enum.Parse(sevType, "Failed");

            byte sr, sg, sb, wr, wg, wb, fr, fg, fb;
            string dummy;

            probeClear.Invoke(win, null);
            Invoke(probeAppearance, win, "成功文本", success, out sr, out sg, out sb, out dummy);
            Check("成功 是绿色", IsGreen(sr, sg, sb), Rgb(sr, sg, sb));

            probeClear.Invoke(win, null);
            Invoke(probeAppearance, win, "警告文本", warning, out wr, out wg, out wb, out dummy);
            Check("警告 是黄色", IsYellow(wr, wg, wb), Rgb(wr, wg, wb));

            probeClear.Invoke(win, null);
            Invoke(probeAppearance, win, "失败文本", failed, out fr, out fg, out fb, out dummy);
            Check("失败 是红色", IsRed(fr, fg, fb), Rgb(fr, fg, fb));

            // ---- 三色互异：防止「三个状态都映射到同一颜色」也能通过 ----
            L("");
            L("【三态互异】");
            bool sameSW = sr == wr && sg == wg && sb == wb;
            bool sameSF = sr == fr && sg == fg && sb == fb;
            bool sameWF = wr == fr && wg == fg && wb == fb;
            Check("成功 ≠ 警告", !sameSW, Rgb(sr, sg, sb) + " vs " + Rgb(wr, wg, wb));
            Check("成功 ≠ 失败", !sameSF, Rgb(sr, sg, sb) + " vs " + Rgb(fr, fg, fb));
            Check("警告 ≠ 失败", !sameWF, Rgb(wr, wg, wb) + " vs " + Rgb(fr, fg, fb));

            // ---- 空文本回落 ----
            L("");
            L("【空文本清空】");
            probeClear.Invoke(win, null);
            Invoke(probeAppearance, win, "", success, out sr, out sg, out sb, out dummy);
            object local = probeIsLocal.Invoke(win, null);
            Check("空文本后 Foreground 无本地值（回落到样式）",
                local is bool && !(bool)local,
                local is bool ? ((bool)local ? "仍有本地值" : "UnsetValue") : "返回类型异常");

            L("");
            return problems == 0 ? 0 : 1;
        }
        finally
        {
            try { win.Close(); } catch (Exception) { }
        }
    }

    static void Invoke(MethodInfo m, Window win, string text, object severity,
        out byte r, out byte g, out byte b, out string shown)
    {
        object[] args = new object[] { text, severity, (byte)0, (byte)0, (byte)0, null };
        m.Invoke(win, args);
        r = (byte)args[2];
        g = (byte)args[3];
        b = (byte)args[4];
        shown = args[5] as string;
    }

    // 判定不写死精确 RGB：允许合理偏移，但必须明确落在色相范围内。
    // 这样「稍微调过色值」不会误报，而「绿变红」「黄变红」这类真实回归一定被抓到。
    static bool IsGreen(byte r, byte g, byte b) { return g > 100 && g > r + 40 && g > b + 40; }
    static bool IsRed(byte r, byte g, byte b) { return r > 100 && r > g + 40 && r > b + 40; }

    static bool IsYellow(byte r, byte g, byte b)
    {
        return r > 100 && g > 70 && b < 100
               && Math.Abs(r - g) < 120
               && b < r - 40 && b < g - 30;
    }

    static string Rgb(byte r, byte g, byte b)
    {
        return string.Format("#{0:X2}{1:X2}{2:X2}", r, g, b);
    }

    static void Drain(Application app)
    {
        app.Dispatcher.Invoke(DispatcherPriority.Background, new Action(delegate { }));
    }

    /// <summary>
    /// 把 WPF 的「应用资源程序集」指向被测产品，让 MainWindow 里的相对 pack URI 能解析到产品的图片。
    /// 只影响窗口能否被构造出来；颜色断言走 Debug 测试钩子，与这个内部字段无关。
    /// </summary>
    static void ForceResourceAssembly(Assembly asm)
    {
        try
        {
            FieldInfo field = typeof(Application).GetField("_resourceAssembly",
                BindingFlags.NonPublic | BindingFlags.Static);
            if (field != null) field.SetValue(null, asm);
            else Console.WriteLine("[提示] 读不到 Application._resourceAssembly");
        }
        catch (Exception e)
        {
            Console.WriteLine("[提示] 强制设置 ResourceAssembly 失败: " + e.Message);
        }
    }

    static string RootMessage(Exception e)
    {
        Exception x = e;
        while (x.InnerException != null) x = x.InnerException;
        return x.Message;
    }
}
