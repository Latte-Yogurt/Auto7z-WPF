using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;

// UI 文本自检
//   1) 运行时: 把不含图片的窗口(MessageWindow / PasswordWindow)量一遍
//   2) 静态: 把三个词典里的每个键都量一遍宽度
class UiWidth
{
    static string lp;
    static StreamWriter w;
    static int problems = 0;

    static void L(string s) { w.WriteLine(s); w.Flush(); }

    [STAThread]
    static int Main(string[] a)
    {
        string ws = a[0];
        string asmPath = a[1];
        string langDir = a[2];
        lp = Path.Combine(ws, "uiwidth.txt");
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

        foreach (string loc in new[] { "zh-CN", "en-US", "zh-TW" })
        {
            SwitchLanguage(app, loc);
            L("");
            L("========== 运行时 " + loc + " ==========");
            CheckWindow(asm, "Auto7z_WPF.MessageWindow", "MessageWindow", loc);
            CheckWindow(asm, "Auto7z_WPF.PasswordWindow", "PasswordWindow", loc);
        }

        // 主窗口与关于窗口带图片资源, 在这个宿主里创建不出来,
        // 改成静态分析 XAML: 把绑定到资源键的文本量一遍
        L("");
        L("========== 静态 XAML 文本检查 ==========");
        CheckXamlText(Path.Combine(Path.GetDirectoryName(langDir), ".."), langDir);

        L("");
        L("========== 词典文本宽度 ==========");
        CheckDictionary(langDir, 284);

        L("");
        L("TOTAL PROBLEMS: " + problems);
        Console.WriteLine("TOTAL PROBLEMS: " + problems);
        return 0;
    }

    static void SwitchLanguage(Application app, string loc)
    {
        foreach (var d in app.Resources.MergedDictionaries
                 .Where(x => x.Source != null
                     && x.Source.OriginalString.ToLowerInvariant().Contains("languages")).ToList())
        {
            app.Resources.MergedDictionaries.Remove(d);
        }
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/Auto7z;component/Resources/Languages/" + loc + ".xaml", UriKind.Absolute)
        });
    }

    static void CheckDictionary(string langDir, double available)
    {
        foreach (string loc in new[] { "zh-CN", "en-US", "zh-TW" })
        {
            string path = Path.Combine(langDir, loc + ".xaml");
            if (!File.Exists(path)) { L("  !! 缺少词典: " + path); problems++; continue; }

            L("");
            L("[" + loc + "]  超过 " + available.ToString("F0") + "px 的文案:");
            var text = File.ReadAllText(path, Encoding.UTF8);
            var matches = Regex.Matches(text, "x:Key=\"([^\"]+)\"[^>]*>([^<]*)<");
            int found = 0;

            foreach (Match m in matches)
            {
                string key = m.Groups[1].Value;
                string value = m.Groups[2].Value;
                if (string.IsNullOrWhiteSpace(value)) continue;

                // 悬停提示与日志不显示在固定宽度的控件里, 不受宽度限制
                if (key.StartsWith("Tip_") || key.StartsWith("Log_") || key.StartsWith("Err_"))
                    continue;

                // 控制台文案只打在 cmd 里, 不进任何固定宽度的控件, 同样不受宽度限制
                if (key.StartsWith("Console_"))
                    continue;

                // 会换行显示的文案: 对话框正文与主窗口结果提示
                // PasswordPrompt / PasswordWrongAttempt 显示在 PasswordWindow 的提示区，
                // 那里是 TextWrapping="Wrap"，会换行，不受单行宽度限制
                if (key == "MultiFilePackQuery" || key == "Result_PartialWarning" ||
                    key == "PasswordPrompt" || key == "PasswordWrongAttempt")
                    continue;

                double width = MeasureWidth(value);
                if (width > available)
                {
                    L("  !! 过长  " + key.PadRight(28) + " " + width.ToString("F0")
                      + "px   '" + Trunc(value) + "'");
                    problems++;
                    found++;
                }
            }
            if (found == 0) L("  (无)");
        }
    }

    // 静态检查 XAML: 找出绑定了资源键且宽度固定的控件, 用该语言的文案量宽度
    static void CheckXamlText(string projectDir, string langDir)
    {
        foreach (string loc in new[] { "zh-CN", "en-US", "zh-TW" })
        {
            string dictPath = Path.Combine(langDir, loc + ".xaml");
            if (!File.Exists(dictPath)) continue;

            var dict = ReadDictionary(dictPath);

            L("");
            L("[" + loc + "]");
            int problemsHere = 0;

            foreach (string xaml in new[] { "MainWindow.xaml", "AboutWindow.xaml" })
            {
                string path = Path.Combine(projectDir, xaml);
                if (!File.Exists(path)) continue;

                string text = File.ReadAllText(path, Encoding.UTF8);
                foreach (string tag in FindControlTags(text))
                {
                    string key = ReadAttribute(tag, "Content");
                    if (key == null || !key.StartsWith("{DynamicResource ")) continue;
                    key = key.Substring("{DynamicResource ".Length).TrimEnd('}').Trim();

                    if (!dict.ContainsKey(key)) continue;
                    string content = dict[key];
                    if (string.IsNullOrWhiteSpace(content)) continue;

                    string widthText = ReadAttribute(tag, "Width");
                    if (widthText == null) continue;
                    int fixedWidth;
                    if (!int.TryParse(widthText, out fixedWidth)) continue;

                    double need = MeasureWidth(content);
                    if (need > fixedWidth)
                    {
                        L("  !! 溢出  " + xaml.PadRight(16) + key.PadRight(26)
                          + " 需要 " + need.ToString("F0") + "px 固定宽 " + fixedWidth
                          + "px  '" + Trunc(content) + "'");
                        problemsHere++;
                    }
                }
            }

            if (problemsHere == 0) L("  (无)");
        }
    }

    // 读词典: 键 -> 文案
    static Dictionary<string, string> ReadDictionary(string path)
    {
        var dict = new Dictionary<string, string>();
        string text = File.ReadAllText(path, Encoding.UTF8);
        int pos = 0;
        while (true)
        {
            int keyStart = text.IndexOf("x:Key=", pos, StringComparison.Ordinal);
            if (keyStart < 0) break;

            int quoteStart = text.IndexOf('"', keyStart + 6);
            if (quoteStart < 0) break;
            int quoteEnd = text.IndexOf('"', quoteStart + 1);
            if (quoteEnd < 0) break;
            string key = text.Substring(quoteStart + 1, quoteEnd - quoteStart - 1);

            int gt = text.IndexOf('>', quoteEnd);
            if (gt < 0) break;
            int lt = text.IndexOf('<', gt);
            if (lt < 0) break;

            dict[key] = text.Substring(gt + 1, lt - gt - 1);
            pos = lt + 1;
        }
        return dict;
    }

    // 找出所有控件的起始标签
    static List<string> FindControlTags(string text)
    {
        var result = new List<string>();
        string[] names = { "CheckBox", "ComboBox", "TextBox", "Button", "RadioButton" };

        int pos = 0;
        while (pos < text.Length)
        {
            int lt = text.IndexOf('<', pos);
            if (lt < 0) break;

            string name = null;
            foreach (string n in names)
            {
                if (text.Length > lt + n.Length + 1 &&
                    text.Substring(lt + 1, n.Length) == n &&
                    (text[lt + 1 + n.Length] == ' ' || text[lt + 1 + n.Length] == '\t'))
                {
                    name = n;
                    break;
                }
            }
            if (name == null) { pos = lt + 1; continue; }

            int gt = text.IndexOf('>', lt);
            if (gt < 0) break;
            result.Add(text.Substring(lt, gt - lt + 1));
            pos = gt + 1;
        }
        return result;
    }

    // 从标签里读属性值
    static string ReadAttribute(string tag, string attribute)
    {
        int idx = tag.IndexOf(attribute + "=", StringComparison.Ordinal);
        if (idx < 0) return null;

        int quoteStart = tag.IndexOf('"', idx);
        if (quoteStart < 0) return null;
        int quoteEnd = tag.IndexOf('"', quoteStart + 1);
        if (quoteEnd < 0) return null;

        return tag.Substring(quoteStart + 1, quoteEnd - quoteStart - 1);
    }

    static void CheckWindow(Assembly asm, string typeName, string label, string loc)
    {
        var type = asm.GetType(typeName);
        if (type == null) { L("[" + label + "] 类型不存在"); problems++; return; }

        Window win;
        try
        {
            win = (Window)Activator.CreateInstance(type);
            win.Show();
            Drain(win);
        }
        catch (Exception e)
        {
            L("[" + label + "] 创建失败: " + RootMessage(e));
            problems++;
            return;
        }

        L("");
        L("[" + loc + "] " + label + "  窗口 " + win.ActualWidth + "x" + win.ActualHeight);

        var items = new List<FrameworkElement>();
        Collect(win, items);

        foreach (var e in items)
        {
            if (!e.IsVisible || e.ActualWidth <= 0) continue;

            string content = GetText(e);
            if (string.IsNullOrEmpty(content)) continue;

            var tb = e as TextBlock;
            if (tb != null && tb.TextWrapping == TextWrapping.Wrap) continue;

            double need = MeasureWidth(content, e);
            if (need > e.ActualWidth + 1.5)
            {
                L("  !! 溢出   " + Name(e) + "  需要 " + need.ToString("F0")
                  + "px 可用 " + e.ActualWidth.ToString("F0")
                  + "px  文字='" + Trunc(content) + "'");
                problems++;
            }
        }

        var contentItems = items.Where(IsContentElement).ToList();
        for (int i = 0; i < contentItems.Count; i++)
        {
            for (int j = i + 1; j < contentItems.Count; j++)
            {
                var ea = contentItems[i];
                var eb = contentItems[j];
                if (IsAncestor(ea, eb) || IsAncestor(eb, ea)) continue;

                var ra = ToScreenRect(ea, win);
                var rb = ToScreenRect(eb, win);
                var inter = System.Windows.Rect.Intersect(ra, rb);
                if (inter.Width > 1 && inter.Height > 1)
                {
                    L("  !! 遮挡   " + Name(ea) + "('" + Trunc(GetText(ea)) + "') 压住  "
                      + Name(eb) + "('" + Trunc(GetText(eb)) + "')  "
                      + inter.Width.ToString("F0") + "x" + inter.Height.ToString("F0") + "px");
                    problems++;
                }
            }
        }

        win.Close();
    }

    static bool IsContentElement(FrameworkElement e)
    {
        if (!e.IsVisible || e.ActualWidth <= 0 || e.ActualHeight <= 0) return false;

        bool hasText = !string.IsNullOrEmpty(GetText(e));
        bool hasName = !string.IsNullOrEmpty(e.Name);
        if (!hasText && !hasName) return false;

        string typeName = e.GetType().Name;
        string[] ignored = { "AdornerLayer", "CaretElement", "ContentPresenter", "border",
                             "PART_ContentHost", "PART_ScrollContentPresenter", "TextBoxView",
                             "WindowBorder", "Path", "Rectangle", "Grid", "StackPanel", "Panel" };
        if (ignored.Contains(typeName)) return false;
        if (e.Name == "AdornerLayer" || e.Name == "CaretElement") return false;

        return true;
    }

    static void Collect(DependencyObject root, List<FrameworkElement> list)
    {
        if (root == null) return;
        var fe = root as FrameworkElement;
        if (fe != null) list.Add(fe);
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++) Collect(VisualTreeHelper.GetChild(root, i), list);
    }

    static bool IsAncestor(FrameworkElement maybe, FrameworkElement child)
    {
        var parent = VisualTreeHelper.GetParent(child);
        while (parent != null)
        {
            if (ReferenceEquals(parent, maybe)) return true;
            parent = VisualTreeHelper.GetParent(parent);
        }
        return false;
    }

    static System.Windows.Rect ToScreenRect(FrameworkElement e, Window win)
    {
        var p = e.TranslatePoint(new Point(0, 0), win);
        return new System.Windows.Rect(p.X, p.Y, e.ActualWidth, e.ActualHeight);
    }

    static string Name(FrameworkElement e)
    {
        if (!string.IsNullOrEmpty(e.Name)) return e.Name;
        string t = e.GetType().Name;
        if (t == "TextBlock") return "TextBlock";
        if (t == "Button") return "Button";
        if (t == "CheckBox") return "CheckBox";
        return t;
    }

    static string GetText(FrameworkElement e)
    {
        var tb = e as TextBlock;
        if (tb != null) return tb.Text ?? "";
        var btn = e as Button;
        if (btn != null) return btn.Content as string ?? "";
        var cb = e as CheckBox;
        if (cb != null) return cb.Content as string ?? "";
        return "";
    }

    static string Trunc(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = s.Replace("\r", " ").Replace("\n", " ");
        return s.Length > 44 ? s.Substring(0, 44) + "..." : s;
    }

    static double MeasureWidth(string content) { return MeasureWidth(content, null); }

    // 用元素自己的字体量, 否则量出来的宽度和实际排版对不上
    static double MeasureWidth(string content, FrameworkElement e)
    {
        try
        {
            FontFamily family = new FontFamily("Microsoft YaHei Light");
            double size = 13;
            FontStyle style = FontStyles.Normal;
            FontWeight weight = FontWeights.Normal;

            var tb = e as TextBlock;
            if (tb != null)
            {
                family = tb.FontFamily; size = tb.FontSize;
                style = tb.FontStyle; weight = tb.FontWeight;
            }
            else if (e != null)
            {
                family = TextElement.GetFontFamily(e);
                size = TextElement.GetFontSize(e);
                style = TextElement.GetFontStyle(e);
                weight = TextElement.GetFontWeight(e);
            }

            var ft = new FormattedText(content, System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface(family, style, weight, FontStretches.Normal),
                size, Brushes.Black, 1.0);
            return ft.WidthIncludingTrailingWhitespace;
        }
        catch { return 0; }
    }

    static string RootMessage(Exception e)
    {
        while (e.InnerException != null) e = e.InnerException;
        return e.GetType().Name + ": " + e.Message;
    }

    static void Drain(Window win)
    {
        for (int i = 0; i < 25; i++)
        {
            win.Dispatcher.Invoke(DispatcherPriority.Background, new Action(delegate { }));
            System.Threading.Thread.Sleep(15);
        }
    }
}
