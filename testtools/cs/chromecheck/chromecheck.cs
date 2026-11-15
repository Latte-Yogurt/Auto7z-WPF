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

        // 1) DataContext 必须非空，且是 Auto7zViewModel（与主窗口/关于窗口同模式）
        object dc = win.DataContext;
        if (dc == null)
        {
            Bad("DataContext 为 null —— {Binding} 命令全部绑空");
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
}
