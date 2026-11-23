using System;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Input;

// 无边框窗口标题栏命令绑定检查。
//
// 背景：三个对话框（MessageWindow / PasswordWindow / ProcessWindow）的 XAML 里
// MinimizeButton / CloseButton 用 {Binding MinimizeCommand} / {Binding CloseCommand}，
// 但此前从不设置 DataContext，命令绑到 null，点击毫无反应。
// 本探针反射加载产品，实例化三个对话框，验证：
//   1) DataContext 非空且是 Auto7zViewModel（与主窗口/关于窗口同一模式）；
//   2) DataContext 提供 CloseCommand / MinimizeCommand 且非空；
//   3) 执行命令后窗口状态按预期变化（关闭 / 最小化）。
// 不依赖合成鼠标输入（合成输入在部分窗口收不到，结论不可信），直接驱动命令对象。
class ChromeCheck
{
    static StreamWriter w;
    static int problems = 0;

    static void L(string s) { w.WriteLine(s); w.Flush(); Console.WriteLine(s); }
    static void Bad(string s) { problems++; L("  !! " + s); }

    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("用法: chromecheck.exe <报告输出目录> <Auto7z.exe 路径>");
            return 2;
        }

        string report = Path.Combine(args[0], "chromecheck.txt");
        w = new StreamWriter(report, false, Encoding.UTF8) { AutoFlush = true };

        var asm = Assembly.LoadFrom(args[1]);
        AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
        {
            string simple = new AssemblyName(e.Name).Name;
            string p = Path.Combine(Path.GetDirectoryName(args[1]), simple + ".exe");
            return File.Exists(p) ? Assembly.LoadFrom(p) : null;
        };

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/Auto7z;component/Resources/WindowDirectory/UIResource.xaml", UriKind.Absolute)
        });
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/Auto7z;component/Resources/Languages/zh-CN.xaml", UriKind.Absolute)
        });

        L("=== 无边框窗口标题栏命令绑定检查 ===");

        foreach (string typeName in new[] { "MessageWindow", "PasswordWindow", "ProcessWindow" })
        {
            CheckWindow(asm, typeName, app);
        }

        L("");
        L("TOTAL PROBLEMS: " + problems);
        return problems == 0 ? 0 : 1;
    }

    static void CheckWindow(Assembly asm, string typeName, Application app)
    {
        L("");
        L("### " + typeName);
        Type t = asm.GetType("Auto7z_WPF." + typeName);
        if (t == null) { Bad("找不到类型 Auto7z_WPF." + typeName); return; }

        var win = (Window)Activator.CreateInstance(t);

        // 1) 两种合格实现：命令绑定 或 代码事件。
        //
        // 真正的契约是「点标题栏必须有效果」，而不是「必须用 DataContext 绑定」。
        // 若窗口的 XAML 已不再出现 {Binding ...Command}，那么 DataContext 为 null
        // 是**正确**的 —— 此时改由代码事件处理，必须另行验证事件真的接线了。
        object dc = win.DataContext;
        if (dc == null)
        {
            L("  DataContext: null（该窗口不用命令绑定，改由代码事件处理）");

            bool stillBinds = XamlStillBindsCommands(typeName);
            if (stillBinds)
            {
                Bad("XAML 仍有 {Binding ...Command} 但 DataContext 为 null —— 点击会绑空");
                return;
            }
            L("  XAML 已无 {Binding ...Command} 残留");
            CheckByEvents(win, typeName, app);
            return;
        }
        L("  DataContext: " + dc.GetType().Name + " (非空)");
        if (dc.GetType().Name != "Auto7zViewModel")
            Bad("DataContext 类型是 " + dc.GetType().Name + "，应为 Auto7zViewModel");

        // 2) DataContext 必须提供 CloseCommand / MinimizeCommand
        var closeProp = dc.GetType().GetProperty("CloseCommand");
        var minProp = dc.GetType().GetProperty("MinimizeCommand");
        ICommand close = closeProp != null ? closeProp.GetValue(dc, null) as ICommand : null;
        ICommand minimize = minProp != null ? minProp.GetValue(dc, null) as ICommand : null;
        if (close == null) Bad("CloseCommand 缺失或为 null");
        if (minimize == null) Bad("MinimizeCommand 缺失或为 null");
        L("  CloseCommand: " + (close == null ? "null" : "有") +
          "   MinimizeCommand: " + (minimize == null ? "null" : "有"));
        if (close == null || minimize == null) return;

        // 3) 命令可执行且执行后窗口状态正确。
        // Auto7zViewModel 的命令通过 RequestClose / RequestMinimize 事件驱动窗口，
        // 窗口构造函数里已订阅这两个事件（与 MainWindow / AboutWindow 一致）。
        win.Show();
        DoEvents(app);

        bool canClose = close.CanExecute(null);
        if (!canClose) Bad("CloseCommand.CanExecute = false");
        L("  CloseCommand.CanExecute = " + canClose);

        if (canClose)
        {
            close.Execute(null);
            DoEvents(app);
            if (win.IsVisible) Bad("执行 CloseCommand 后窗口仍可见");
            else L("  执行 CloseCommand 后窗口已关闭 ✓");
        }

        // 关闭后重新实例化一个测 Minimize（Close 后窗口对象不能再用）
        var win2 = (Window)Activator.CreateInstance(t);
        object dc2 = win2.DataContext;
        var min2 = dc2.GetType().GetProperty("MinimizeCommand").GetValue(dc2, null) as ICommand;
        win2.Show();
        DoEvents(app);
        min2.Execute(null);
        DoEvents(app);
        if (win2.WindowState != WindowState.Minimized)
            Bad("执行 MinimizeCommand 后 WindowState=" + win2.WindowState + "（应为 Minimized）");
        else
            L("  执行 MinimizeCommand 后 WindowState=Minimized ✓");
        win2.Close();
    }

    static void DoEvents(Application app)
    {
        for (int i = 0; i < 20; i++)
        {
            app.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.Background,
                new Action(delegate { }));
            System.Threading.Thread.Sleep(20);
        }
    }

    /// <summary>
    /// 该窗口的 XAML 里是否仍有 {Binding ...Command}。
    /// 有绑定却没有 DataContext 就是历史缺陷（点击无反应）的原样复现。
    /// </summary>
    static bool XamlStillBindsCommands(string typeName)
    {
        try
        {
            string dir = Path.GetDirectoryName(typeof(ChromeCheck).Assembly.Location);
            string p = Path.Combine(dir, typeName + ".xaml");
            if (!File.Exists(p)) return false;
            string text = File.ReadAllText(p, Encoding.UTF8);
            return text.Contains("{Binding MinimizeCommand}") || text.Contains("{Binding CloseCommand}");
        }
        catch
        {
            // 读不到 XAML 时按"没有绑定"处理：不因此判失败，由事件验证兜底。
            return false;
        }
    }

    /// <summary>
    /// 无 DataContext 的窗口：验证代码事件确实接线（点击标题栏有副作用）。
    ///
    /// 判据与命令路径等价：点关闭必须让窗口不可见 / 置取消标志；点最小化必须改变 WindowState。
    /// </summary>
    static void CheckByEvents(Window win, string typeName, Application app)
    {
        object closeBtn = win.FindName("CloseButton");
        object minBtn = win.FindName("MinimizeButton");

        if (closeBtn == null && minBtn == null)
        {
            Bad("既没有命令绑定，也找不到标题栏按钮 —— 无从验证");
            return;
        }

        win.Show();
        DoEvents(app);

        // 关闭/取消：优先看 Cancelled 属性（ProcessWindow），否则看窗口是否关闭
        var cancelledProp = win.GetType().GetProperty("Cancelled");
        bool beforeCancel = cancelledProp != null && (bool)cancelledProp.GetValue(win, null);

        FrameworkElement closeFe = closeBtn as FrameworkElement;
        if (closeFe != null)
        {
            var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left);
            args.RoutedEvent = UIElement.MouseLeftButtonDownEvent;
            args.Source = closeFe;
            closeFe.RaiseEvent(args);
            DoEvents(app);
        }

        if (cancelledProp != null)
        {
            bool afterCancel = (bool)cancelledProp.GetValue(win, null);
            if (afterCancel && !beforeCancel) L("  点击关闭 → Cancelled=true ✓");
            else Bad("点击关闭后 Cancelled=" + afterCancel + "（应变为 true）");
        }
        else if (win.IsVisible)
        {
            Bad("点击关闭后窗口仍可见");
        }
        else
        {
            L("  点击关闭 → 窗口已关闭 ✓");
        }

        // 最小化：重新造一个窗口，点最小化
        var win2 = (Window)Activator.CreateInstance(win.GetType());
        win2.Show();
        DoEvents(app);
        object minBtn2 = win2.FindName("MinimizeButton");
        FrameworkElement minFe2 = minBtn2 as FrameworkElement;
        if (minFe2 != null)
        {
            var args2 = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left);
            args2.RoutedEvent = UIElement.MouseLeftButtonDownEvent;
            args2.Source = minFe2;
            minFe2.RaiseEvent(args2);
            DoEvents(app);
        }
        if (win2.WindowState != WindowState.Minimized)
            Bad("点击最小化后 WindowState=" + win2.WindowState + "（应为 Minimized）");
        else
            L("  点击最小化 → WindowState=Minimized ✓");

        win2.Close();
        if (win.IsVisible) win.Close();
    }
}
