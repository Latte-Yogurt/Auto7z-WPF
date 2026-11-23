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

class TabOrder
{
    static string lp;
    static StreamWriter w;
    static int problems = 0;

    static void L(string s) { w.WriteLine(s); w.Flush(); }
    static void Bad(string s) { problems++; L("  !! " + s); }

    [STAThread]
    static int Main(string[] a)
    {
        if (a.Length < 3)
        {
            Console.WriteLine("用法: taborder.exe <报告输出目录> <Auto7z.exe 路径> <XAML 目录>");
            Console.WriteLine("  检查 Tab 顺序：静态 TabIndex 与运行时可达性。");
            return 2;
        }

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

        // MainWindow 的图片走相对 pack URI（pack://application:,,,/Resources/Images/...），
        // 解析目标是「应用资源程序集」= 本探针自己，产品那批图一张都取不到，构造直接抛异常。
        // 该属性的 setter 在 WPF 初始化后就锁死，只能反射改私有静态字段。
        ForceResourceAssembly(asm);

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
            new[] { "Auto7z_WPF.MainWindow", "MainWindow" },
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
        L("TOTAL PROBLEMS: " + problems);
        L("DONE");
        return problems == 0 ? 0 : 1;
    }

    /// <summary>
    /// 把 WPF 的「应用资源程序集」指向被测产品，让 MainWindow 里的相对 pack URI 能解析到产品的图片。
    /// 不这么做时构造 MainWindow 会抛 IOException（找不到 resources/images/layla256.png），
    /// 于是主窗口这条最重要的 Tab 走查只能长期空缺。
    /// </summary>
    static void ForceResourceAssembly(Assembly asm)
    {
        try
        {
            FieldInfo field = typeof(Application).GetField("_resourceAssembly",
                BindingFlags.NonPublic | BindingFlags.Static);
            if (field == null)
            {
                L("[提示] 读不到 Application._resourceAssembly，MainWindow 可能构造失败");
                return;
            }
            field.SetValue(null, asm);
        }
        catch (Exception e)
        {
            L("[提示] 强制设置 ResourceAssembly 失败: " + e.Message);
        }
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
            Bad("[" + label + "] 创建失败: " + RootMessage(e));
            return;
        }

        L("");
        L("[" + label + "] 实际 Tab 走查");

        // 展开侧栏：收起态下侧栏整块 Collapsed，那十几个控件的顺序根本测不到。
        ExpandSidePanel(win, app, label);

        // Tab 键真正走的是 KeyboardNavigation 的 MoveFocus(Next)，合成 KeyEventArgs 不会挪焦点。
        // 这里直接调 MoveFocus，走的正是按 Tab 时那段代码。
        var walked = new List<FrameworkElement>();
        IInputElement current = win;
        win.Focus();
        Drain(app);

        int guard = 0;
        while (guard++ < 80)
        {
            var ui = current as UIElement;
            if (ui == null) break;
            ui.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
            Drain(app);
            // MoveFocus 只是**发起**焦点转移，Keyboard.FocusedElement 的更新是异步的。
            // 系统繁忙时（例如刚有别的进程抢占前台）读到的可能还是 null ——
            // 实测这是「走查中焦点丢失」的唯一成因，它让本探针在整轮回归里偶发失败
            // （同一份代码连跑三次，可能一两次失败）。
            //
            // 注意：重试的是「读取焦点」，不是「跳过检查」。
            // 真丢了焦点（控件被禁用、窗口失活）连续重试后仍是 null，照常报错。
            var fe = Keyboard.FocusedElement as FrameworkElement;
            for (int retry = 0; fe == null && retry < 12; retry++)
            {
                System.Threading.Thread.Sleep(15);
                Drain(app);
                fe = Keyboard.FocusedElement as FrameworkElement;
            }
            if (fe == null) { Bad("[" + label + "] 走查中焦点丢失"); break; }

            if (walked.Count > 1 && ReferenceEquals(fe, walked[0]))
            {
                L("    (回到起点，共 " + walked.Count + " 站)");
                break;
            }
            if (walked.Contains(fe))
            {
                Bad("[" + label + "] 走查重复命中 " + Name(fe) + " —— 环内有站点被跳过或 TabIndex 冲突");
                break;
            }
            walked.Add(fe);
            current = fe;
        }

        if (walked.Count == 0)
        {
            Bad("[" + label + "] 一个可聚焦控件都没走到");
            win.Close();
            return;
        }

        for (int i = 0; i < walked.Count; i++)
        {
            L(string.Format("    {0,2}. {1}", i + 1, Describe(walked[i])));
        }

        // 可见、可聚焦、却没被走到 —— Tab 到不了就是真问题（键盘只有鼠标的用户会被卡住）。
        var all = new List<FrameworkElement>();
        Collect(win, all);
        var missed = new List<FrameworkElement>();
        foreach (var e in all)
        {
            if (!IsReachableFocusable(e)) continue;
            if (walked.Contains(e)) continue;
            missed.Add(e);
        }

        if (missed.Count == 0)
        {
            L("    可见可聚焦控件全部走到 ✓");
        }
        else
        {
            foreach (var e in missed) Bad("[" + label + "] 可见可聚焦但 Tab 到不了: " + Describe(e));
        }

        win.Close();
        Drain(app);
    }

    /// <summary>
    /// 主窗口收起时侧栏是 Collapsed，侧栏里的控件全不在可视化树里，走查会只剩「关于/设置」两站。
    /// 这里调产品自己的 SettingBtn_Click 把它展开 —— 走的是真实的展开路径（含动画）。
    /// </summary>
    static void ExpandSidePanel(Window win, Application app, string label)
    {
        var sidePanel = win.FindName("SidePanel") as FrameworkElement;
        if (sidePanel == null || sidePanel.IsVisible) return;

        var settingBtn = win.FindName("SettingButton") as Button;
        if (settingBtn == null) return;

        MethodInfo handler = win.GetType().GetMethod("SettingBtn_Click",
            BindingFlags.Instance | BindingFlags.NonPublic);
        if (handler == null) { L("    [提示] 找不到 SettingBtn_Click，侧栏没能展开"); return; }

        try
        {
            handler.Invoke(win, new object[] { settingBtn, new RoutedEventArgs() });
        }
        catch (Exception e)
        {
            L("    [提示] 展开侧栏失败: " + RootMessage(e));
            return;
        }

        // 展开动画 0.2s，等它跑完再走查，否则控件位置与可见性都还在中间态。
        for (int i = 0; i < 30; i++)
        {
            app.Dispatcher.Invoke(DispatcherPriority.Background, new Action(delegate { }));
            System.Threading.Thread.Sleep(20);
        }

        if (sidePanel.IsVisible) L("    (已展开侧栏，宽度 " + win.ActualWidth.ToString("F0") + ")");
        else L("    [提示] 侧栏仍未可见，本次走查只覆盖收起态");
    }

    /// <summary>
    /// 与 Tab 是否可达直接相关的「可聚焦」判据。
    ///
    /// 和 Focusable 属性的区别在于：Window 本身 Focusable=true 但 Tab 不会停在它身上；
    /// 不可见的控件（未选中页的内容）也不该被算成「漏掉」。
    /// </summary>
    static bool IsReachableFocusable(FrameworkElement e)
    {
        if (e is Window) return false;
        if (!e.IsVisible || !e.IsEnabled) return false;

        var ctl = e as Control;
        if (ctl != null && !ctl.IsTabStop) return false;
        return e.Focusable;
    }

    static string RootMessage(Exception e)
    {
        while (e.InnerException != null) e = e.InnerException;
        return e.GetType().Name + ": " + e.Message;
    }

    static void CheckStatic(string path, string label)
    {
        L("");
        L("[" + label + "]");
        if (!File.Exists(path)) { L("    (文件不存在)"); return; }

        string text = File.ReadAllText(path, Encoding.UTF8);

        var items = new List<KeyValuePair<int, string>>();

        // TabItem 必须在扫描范围里：侧栏那三个页签自己也要参与 Tab 排序，
        // 而且它们的编号决定了「页内控件的号必须落在自己页签之后、下一个页签之前」。
        // 早先漏了 TabItem，于是页签编号改了也没人看得出来。
        var pattern = new Regex(
            "<(CheckBox|ComboBox|TextBox|Button|RadioButton|PasswordBox|TabItem)\\s[^>]*?x:Name=\"([^\"]+)\"[^>]*>",
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
                tabIndex * 1000 + order,
                name.PadRight(26) + "TabIndex=" + tabIndex + (tabIndex < 0 ? "   (未设置 —— 排到所有编号控件之后)" : "")));
            order++;
        }

        foreach (var kv in items.OrderBy(x => x.Key))
        {
            L("    " + kv.Value);
        }

        // 同一页里出现重复编号：WPF 会按可视化树先后决定同号控件的顺序，
        // 那个顺序与 TabIndex 无关、也与视觉顺序无关 —— 是典型的「Tab 跳着走」来源。
        var dup = new Dictionary<int, List<string>>();
        foreach (Match m in pattern.Matches(text))
        {
            var ti = Regex.Match(m.Value, "TabIndex=\"(\\d+)\"");
            if (!ti.Success) continue;
            int idx = int.Parse(ti.Groups[1].Value);
            if (!dup.ContainsKey(idx)) dup[idx] = new List<string>();
            dup[idx].Add(m.Groups[2].Value);
        }

        foreach (var kv in dup.OrderBy(x => x.Key))
        {
            if (kv.Value.Count <= 1) continue;

            // SolidMode / Zstd 共用 23 是有意的：两者互斥（前者仅 7z、后者仅 tar），
            // 任何时刻至多一个参与排序，视觉上又是同一行。其余重复都算问题。
            bool allowed = (kv.Key == 23
                            && kv.Value.Count == 2
                            && kv.Value.Contains("CheckBox_SolidMode")
                            && kv.Value.Contains("CheckBox_Zstd"));
            if (allowed)
            {
                L("    (共用 TabIndex=" + kv.Key + "：" + string.Join(" / ", kv.Value)
                  + " —— 两者互斥，任何时刻只有一个可见，属有意为之)");
            }
            else
            {
                Bad("[" + label + "] TabIndex=" + kv.Key + " 被多个控件共用: "
                    + string.Join(", ", kv.Value) + " —— 同号控件按可视化树先后排序，与视觉顺序无关");
            }
        }
    }

    static void Collect(DependencyObject root, List<FrameworkElement> list)
    {
        if (root == null) return;
        var fe = root as FrameworkElement;
        // Window 自身不算「站点」：它 Focusable=true，但 Tab 不会停在它身上。
        if (fe != null && !(fe is Window)) list.Add(fe);
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++) Collect(VisualTreeHelper.GetChild(root, i), list);
    }

    static string Name(FrameworkElement e)
    {
        return string.IsNullOrEmpty(e.Name) ? e.GetType().Name : e.Name;
    }

    static string Describe(FrameworkElement e)
    {
        string name = Name(e);
        string text = "";
        var tb = e as TextBlock; if (tb != null) text = tb.Text;
        var btn = e as Button; if (btn != null) text = btn.Content as string ?? "";
        var cb = e as CheckBox; if (cb != null) text = cb.Content as string ?? "";
        var rb = e as RadioButton; if (rb != null) text = rb.Content as string ?? "";
        // 页签的可见文案在 Header 上，不看它的话三个 TabItem 在报告里长得一模一样。
        var tab = e as TabItem; if (tab != null) text = tab.Header as string ?? "";
        var ctl = e as Control;
        int tabIndex = ctl != null ? ctl.TabIndex : -1;
        return name.PadRight(24) + " tab=" + tabIndex.ToString().PadRight(5) + " '" + text + "'";
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
