using System;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

// 验证:
// 1) MessageWindow 询问模式按钮文字是"是/否"
// 2) MessageWindow 错误模式按钮文字是"确定", 且居中
// 3) PasswordWindow 设置提示后文字确实变了
// 4) 两种模式能来回切
class Verify
{
    static string lp;
    static StreamWriter w;

    static void L(string s) { w.WriteLine(s); w.Flush(); Console.WriteLine(s); }

    [STAThread]
    static int Main(string[] a)
    {
        lp = Path.Combine(a[0], "verify.txt");
        w = new StreamWriter(lp, false, Encoding.UTF8);
        w.AutoFlush = true;

        var asm = Assembly.LoadFrom(a[1]);
        AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
        {
            string simple = new AssemblyName(e.Name).Name;
            string p = Path.Combine(Path.GetDirectoryName(a[1]), simple + ".exe");
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

        var mwType = asm.GetType("Auto7z_WPF.MessageWindow");
        var pwType = asm.GetType("Auto7z_WPF.PasswordWindow");

        L("##### MessageWindow 按钮与布局 #####");
        var win = (Window)Activator.CreateInstance(mwType);
        win.Show();
        D(app);

        L("[1] 初始(询问模式)");
        Dump(win);

        L("[2] 切到错误模式(ApplyOkButton)");
        mwType.GetMethod("ApplyOkButton").Invoke(win, null);
        D(app);
        Dump(win);

        L("[3] 切回询问模式(ApplyYesNoButtons) —— 验证能切回来");
        mwType.GetMethod("ApplyYesNoButtons").Invoke(win, null);
        D(app);
        Dump(win);

        L("");
        L("##### PasswordWindow 提示切换 #####");
        var pw = (Window)Activator.CreateInstance(pwType);
        pw.Show();
        D(app);
        var prompt = pw.FindName("PromptText") as TextBlock;
        L("  初始提示: '" + (prompt == null ? "(null)" : prompt.Text) + "'");

        pwType.GetMethod("SetPrompt").Invoke(pw, new object[] { "密码错误，第 2 次尝试，重新询问密码" });
        D(app);
        L("  设置后提示: '" + (prompt == null ? "(null)" : prompt.Text) + "'");

        pwType.GetMethod("SetPrompt").Invoke(pw, new object[] { "该压缩包已加密，请输入密码：" });
        D(app);
        L("  再设置回默认: '" + (prompt == null ? "(null)" : prompt.Text) + "'");

        L("");
        L("DONE");
        return 0;
    }

    static void Dump(Window win)
    {
        var c = win.FindName("ConfirmButton") as FrameworkElement;
        var x = win.FindName("CancelButton") as FrameworkElement;
        var ph = win.FindName("PlaceHolderGrid") as FrameworkElement;
        var cb = win.FindName("ConfirmButton") as Button;
        var xb = win.FindName("CancelButton") as Button;

        string cText = cb == null ? "?" : cb.Content.ToString();
        string cVis = c == null ? "?" : c.Visibility.ToString();
        string cCol = c == null ? "?" : Grid.GetColumn(c).ToString();
        string cX = c == null ? "?" : c.TranslatePoint(new Point(0, 0), win).X.ToString("F1");
        L("  ConfirmButton: 内容='" + cText + "' 可见=" + cVis + " 列=" + cCol + " X=" + cX);
        L("  CancelButton : 内容='" + (xb == null ? "?" : xb.Content) + "' 可见=" + (x == null ? "?" : x.Visibility.ToString()));
        L("  PlaceHolder : 可见=" + (ph == null ? "?" : ph.Visibility.ToString()));
        if (c != null)
        {
            double center = c.TranslatePoint(new Point(0, 0), win).X + c.ActualWidth / 2;
            double winCenter = (win.ActualWidth - 16) / 2 + 8;
            L(string.Format("  确定按钮中心 {0:F1} / 窗口内容中心 {1:F1}", center, winCenter));
        }
    }

    static void D(Application app)
    {
        for (int i = 0; i < 20; i++)
        {
            app.Dispatcher.Invoke(DispatcherPriority.Background, new Action(delegate { }));
            System.Threading.Thread.Sleep(20);
        }
    }
}
