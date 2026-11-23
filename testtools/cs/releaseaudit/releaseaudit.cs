using System;
using System.Reflection;

// Release 交付物审计：确认 Debug 专用代码没有进生产构建。
//
// 为什么单独一项：`#if DEBUG` 包住的测试钩子一旦进了交付物，就是**生产环境里的
// 隐蔽入口** —— DebugAutoDriver 能自动点掉任何对话框、TestHooks 能注入任意文件列表。
// 这类缺陷不会被任何功能测试发现（Debug 构建下一切正常），
// 只能在 Release 产物上做类型级检查。
//
// 判据必须用「类型/成员是否存在」，**不能**用 DebuggableAttribute.IsJITTrackingEnabled：
// 实测它在 Release 产物上也会返回 true（.NET Framework 的 pdb 相关设置所致），
// 用那条判据会把干净的 Release 误判成 Debug。
class ReleaseAudit
{
    static int problems = 0;

    static void Check(string n, bool ok, string d)
    {
        Console.WriteLine((ok ? "  OK   " : "  !!   ") + n + (d.Length > 0 ? "  --- " + d : ""));
        if (!ok) problems++;
    }

    static int Main(string[] a)
    {
        if (a.Length < 1) { Console.WriteLine("用法: releaseaudit.exe <Auto7z.exe> [--expect-debug]"); return 2; }
        bool expectDebug = a.Length > 1 && a[1] == "--expect-debug";

        Assembly asm = Assembly.LoadFrom(a[0]);
        Console.WriteLine("程序集: " + asm.GetName().Name + " " + asm.GetName().Version);
        Console.WriteLine("文件:   " + a[0]);
        Console.WriteLine("期望:   " + (expectDebug ? "Debug（钩子应存在）" : "Release（钩子应全部消失）"));
        Console.WriteLine("");

        // Debug 专用类型
        string[] debugTypes = {
            "Auto7z_WPF.Services.DebugAutoDriver",
            "Auto7z_WPF.Services.TestHooks",
        };

        // Debug 专用成员：(类型, 成员名)
        string[][] debugMembers = {
            new[] { "Auto7z_WPF.Auto7zViewModel", "SkipShellMenuSync" },
            new[] { "Auto7z_WPF.MainWindow", "ProbeResultAppearance" },
            new[] { "Auto7z_WPF.MainWindow", "ProbeClearResult" },
            new[] { "Auto7z_WPF.MainWindow", "ProbeResultForegroundIsLocal" },
        };

        Console.WriteLine("=== Debug 专用类型 ===");
        foreach (string n in debugTypes)
        {
            bool exists = asm.GetType(n) != null;
            if (expectDebug) Check(n + " 存在", exists, "");
            else Check(n + " 已从交付物移除", !exists, exists ? "!! 测试类型进了生产构建" : "");
        }

        Console.WriteLine("");
        Console.WriteLine("=== Debug 专用成员 ===");
        foreach (string[] pair in debugMembers)
        {
            Type t = asm.GetType(pair[0]);
            if (t == null) { Check(pair[0] + " 类型缺失", false, "找不到类型"); continue; }

            PropertyInfo p = t.GetProperty(pair[1], BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            MethodInfo m = t.GetMethod(pair[1], BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            bool exists = p != null || m != null;

            string shortName = pair[0].Replace("Auto7z_WPF.", "") + "." + pair[1];
            if (expectDebug) Check(shortName + " 存在", exists, "");
            else Check(shortName + " 已移除", !exists, exists ? "!! 测试钩子进了生产构建" : "");
        }

        Console.WriteLine("");
        Console.WriteLine("类型总数: " + asm.GetTypes().Length);
        Console.WriteLine("");
        Console.WriteLine(problems == 0 ? "PASS" : "TOTAL PROBLEMS: " + problems);
        return problems == 0 ? 0 : 1;
    }
}