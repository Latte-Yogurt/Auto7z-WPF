using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading;

class LangCheck
{
    static int Main(string[] a)
    {
        if (a.Length < 1)
        {
            Console.WriteLine("用法: langcheck.exe <Auto7z.exe 路径>");
            Console.WriteLine("  打印支持的语言，并验证系统语言 -> 界面语言的映射。");
            return 2;
        }

        string asmPath = a[0];
        var asm = Assembly.LoadFrom(asmPath);
        var ls = asm.GetType("Auto7z_WPF.Services.LanguageService");
        var resolve = ls.GetMethod("ResolveSystemLanguage", BindingFlags.Public | BindingFlags.Static);
        var supported = ls.GetMethod("GetSupportedLanguages", BindingFlags.Public | BindingFlags.Static);

        Console.WriteLine("支持的语言: " + string.Join(", ", (string[])supported.Invoke(null, null)));
        Console.WriteLine("");

        string[] cultures = {
            "zh-CN", "zh-TW", "en-US",
            "zh-Hans", "zh-Hans-CN", "zh-Hant", "zh-Hant-TW",
            "zh", "zh-HK", "zh-MO", "zh-SG",
            "en", "en-GB", "en-AU", "de-DE", "ja-JP", "fr-FR", "ko-KR", "ru-RU"
        };

        Console.WriteLine(string.Format("{0,-12} -> {1}", "系统UI语言", "界面语言"));
        Console.WriteLine("-----------------------------------");
        foreach (string c in cultures)
        {
            var culture = new CultureInfo(c);
            CultureInfo.CurrentUICulture = culture;
            string result = (string)resolve.Invoke(null, null);
            Console.WriteLine(string.Format("{0,-12} -> {1}", c, result));
        }
        return 0;
    }
}
