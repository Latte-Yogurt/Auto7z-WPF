using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

// Tab 顺序检查
//   1) 运行时: 走一圈 Tab, 看焦点落在哪些控件上
//   2) 静态: 解析 XAML 里的 TabIndex, 按 (TabIndex, 出现顺序) 排出的序列
class TabOrder
{
    static string lp;
    static StreamWriter w;

    static void L(string s) { w.WriteLine(s); w.Flush(); }

    [STAThread]
    static int Main(string[] a)
    {
        string ws = a[0];
        string asmPath = a[1];
        string xamlDir = a[2];
        lp = Path.Combine(ws, "taborder.txt");
        w = new StreamWriter(lp, false, Encoding.UTF8);
        w.AutoFlush = true;

        AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
        {
            string simple = new AssemblyName(e.Name).Name;
            string p = Path.Combine(Path.GetDirectoryName(asmPath), simple + ".exe");
            return File.Exists(p) ? Assembly.LoadFrom(p) : null;
        };

        var asm = Assembly.LoadFrom(asmPath);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/Auto7z;component/Resources/WindowDirectory/UIResource.xaml", UriKind.Absolute)
        });
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/Auto7z;component/Resources/Languages/zh-CN.xaml", UriKind.Absolute)
        });

        foreach (string[] pair in new[]
        {
            new[] { "Auto7z_WPF.MessageWindow", "MessageWindow" },
            new[] { "Auto7z_WPF.PasswordWindow", "PasswordWindow" },
        })
        {
            CheckRuntime(asm, pair[0], pair[1], app);
        }

        L("");
        L("========== 静态 TabIndex (按 TabIndex 排序) ==========");
        foreach (string[] pair in new[]
        {
            new[] { "MainWindow.xaml", "MainWindow" },
            new[] { "MessageWindow.xaml", "MessageWindow" },
            new[] { "PasswordWindow.xaml", "PasswordWindow" },
            new[] { "AboutWindow.xaml", "AboutWindow" },
        })
        {
            CheckStatic(Path.Combine(xamlDir, pair[0]), pair[1]);
        }

        L("");
        L("DONE");
        return 0;
    }

    static void CheckRuntime(Assembly asm, string typeName, string label, Application app)
    {
        var type = asm.GetType(typeName);
        if (type == null) return;

        Window win;
        try
        {
            win = (Window)Activator.CreateInstance(type);
            win.Show();
            Drain(app);
        }
        catch (Exception e)
        {
            L("[" + label + "] 创建失败: " + e.Message);
            return;
        }

        L("");
        L("[" + label + "] 实际 Tab 顺序:");

        var visited = new List<FrameworkElement>();

        // 找到全部可聚焦控件, 按 TabIndex 排好序
        var all = new List<FrameworkElement>();
        Collect(win, all);
        var focusables = all.Where(IsFocusable).ToList()
            .OrderBy(e => { var c = e as Control; return (c != null && c.TabIndex >= 0) ? c.TabIndex : int.MaxValue; })
            .ToList();

        L("    可聚焦控件 " + focusables.Count + " 个 (按 TabIndex 排序):");
        foreach (var e in focusables) L("      " + Describe(e));

        // 逐个设焦点, 看能否拿到
        L("    逐个设焦点:");
        foreach (var e in focusables)
        {
            bool ok = e.Focus();
            Drain(app);
            var got = Keyboard.FocusedElement as FrameworkElement;
            L(string.Format("      {0,-22} Focus()={1,-5} 实际焦点={2}",
                string.IsNullOrEmpty(e.Name) ? e.GetType().Name : e.Name,
                ok, got == null ? "(无)" : (string.IsNullOrEmpty(got.Name) ? got.GetType().Name : got.Name)));
        }

        if (visited.Count == 0) L("    (没有可聚焦控件)");

        win.Close();
    }

    static void CheckStatic(string path, string label)
    {
        L("");
        L("[" + label + "]");
        if (!File.Exists(path)) { L("    (文件不存在)"); return; }

        string text = File.ReadAllText(path, Encoding.UTF8);

        var items = new List<KeyValuePair<int, string>>();
        var pattern = new Regex(
            "<(CheckBox|ComboBox|TextBox|Button|RadioButton|PasswordBox)\\s[^>]*?x:Name=\"([^\"]+)\"[^>]*>",
            RegexOptions.Singleline);

        int order = 0;
        foreach (Match m in pattern.Matches(text))
        {
            string tag = m.Value;
            string name = m.Groups[2].Value;
            int tabIndex = -1;
            var ti = Regex.Match(tag, "TabIndex=\"(\\d+)\"");
            if (ti.Success) tabIndex = int.Parse(ti.Groups[1].Value);
            items.Add(new KeyValuePair<int, string>(
                tabIndex * 1000 + order, name + "   TabIndex=" + tabIndex));
            order++;
        }

        foreach (var kv in items.OrderBy(x => x.Key))
        {
            L("    " + kv.Value);
        }
    }

    static bool IsFocusable(FrameworkElement e)
    {
        if (!e.IsVisible) return false;
        if (e.Focusable) return true;
        return e is TextBoxBase || e is ButtonBase || e is ComboBox || e is CheckBox;
    }

    static void Collect(DependencyObject root, List<FrameworkElement> list)
    {
        if (root == null) return;
        var fe = root as FrameworkElement;
        if (fe != null) list.Add(fe);
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++) Collect(VisualTreeHelper.GetChild(root, i), list);
    }

    static string Describe(FrameworkElement e)
    {
        string name = string.IsNullOrEmpty(e.Name) ? e.GetType().Name : e.Name;
        string text = "";
        var tb = e as TextBlock; if (tb != null) text = tb.Text;
        var btn = e as Button; if (btn != null) text = btn.Content as string ?? "";
        var cb = e as CheckBox; if (cb != null) text = cb.Content as string ?? "";
        var rb = e as RadioButton; if (rb != null) text = rb.Content as string ?? "";
        var ctl = e as Control;
        int tabIndex = ctl != null ? ctl.TabIndex : -1;
        return name.PadRight(22) + " tab=" + tabIndex + "  '" + text + "'";
    }

    static void Drain(Application app)
    {
        for (int i = 0; i < 20; i++)
        {
            app.Dispatcher.Invoke(DispatcherPriority.Background, new Action(delegate { }));
            System.Threading.Thread.Sleep(15);
        }
    }
}
