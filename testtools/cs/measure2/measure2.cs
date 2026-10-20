using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;

// 测量各语言下对话框内文本宽度, 检查是否超出可用宽度
class Measure2
{
    static double Width(string text, string font, double size, FontWeight weight)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        var tf = new Typeface(new FontFamily(font), FontStyles.Normal, weight, FontStretches.Normal);
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, tf, size, Brushes.Black, 1.0);
        return ft.WidthIncludingTrailingWhitespace;
    }

    static string ReadKey(string file, string key)
    {
        foreach (string line in File.ReadAllLines(file, Encoding.UTF8))
        {
            string n = "x:Key=\"" + key + "\">";
            int i = line.IndexOf(n);
            if (i < 0) continue;
            int s = i + n.Length;
            int e = line.IndexOf('<', s);
            if (e < 0) continue;
            return line.Substring(s, e - s);
        }
        return null;
    }

    static int Main(string[] a)
    {
        string langDir = a[0];

        // MessageWindow: 300x150, Grid Margin=8 -> 可用 284
        // PasswordWindow: 300x150, Grid Margin=8 -> 可用 284
        const double avail = 284;

        // 需要检查的文本
        string[] keys = {
            "MultiFilePackQuery", "PasswordPromptText", "PasswordWrongAttempt",
            "YesButton", "NoButton", "OkButton", "ConfirmButton", "CancelTaskButton"
        };

        int problems = 0;

        foreach (string loc in new[] { "zh-CN", "en-US", "zh-TW" })
        {
            string f = Path.Combine(langDir, loc + ".xaml");
            Console.WriteLine("=== " + loc + " ===");

            foreach (string key in keys)
            {
                string text = ReadKey(f, key);
                if (text == null) { Console.WriteLine("  " + key + ": (缺失)"); problems++; continue; }

                // 标题用 13px 粗体, 正文与按钮用 13px 常规
                bool isTitle = key == "MultiFilePackQuery" || key == "PasswordPromptText" || key == "PasswordWrongAttempt";
                double w = Width(text, "Microsoft YaHei Light", 13, isTitle ? FontWeights.Normal : FontWeights.Normal);

                // 可换行的文本按 284 宽折行后不溢出; 不可换行的按钮文本单行
                bool wraps = isTitle;
                string verdict;
                if (wraps)
                {
                    int lines = (int)Math.Ceiling(w / avail);
                    verdict = string.Format("折 {0} 行, 行高约 {1}px", lines, lines * 17);
                    if (lines * 17 > 100) { verdict += "  *** 超过可用高度 ***"; problems++; }
                }
                else
                {
                    verdict = w > avail ? string.Format("*** 溢出 {0:F0}px ***", w - avail) : "单行 OK";
                    if (w > avail) problems++;
                }

                Console.WriteLine(string.Format("  {0,-22} {1,6:F0}px  {2}", key, w, verdict));
            }
            Console.WriteLine("");
        }

        Console.WriteLine(problems == 0 ? "全部文本无溢出" : string.Format("发现 {0} 处问题", problems));
        return 0;
    }
}
