using System;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;

class Portable
{
    static string lp;
    static StreamWriter w;

    static void L(string s) { w.WriteLine(s); w.Flush(); Console.WriteLine(s); }

    static int Main(string[] a)
    {
        if (a.Length < 2)
        {
            Console.WriteLine("用法: portable.exe <报告输出目录> <Auto7z.exe 路径>");
            Console.WriteLine("  验证切到便携模式、切回普通模式，以及取消后 .config 是否被删。");
            Console.WriteLine("  需要 Debug 构建（依赖 SkipShellMenuSync 跳过注册，避免弹 UAC）。");
            return 2;
        }

        string ws = a[0];
        string asmPath = a[1];

        lp = Path.Combine(ws, "portable.txt");
        w = new StreamWriter(lp, false, Encoding.UTF8);
        w.AutoFlush = true;

        var asm = Assembly.LoadFrom(asmPath);
        AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
        {
            string simple = new AssemblyName(e.Name).Name;
            string p = Path.Combine(Path.GetDirectoryName(asmPath), simple + ".exe");
            return File.Exists(p) ? Assembly.LoadFrom(p) : null;
        };

        var app = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new System.Windows.ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/Auto7z;component/Resources/WindowDirectory/UIResource.xaml", UriKind.Absolute)
        });
        app.Resources.MergedDictionaries.Add(new System.Windows.ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/Auto7z;component/Resources/Languages/zh-CN.xaml", UriKind.Absolute)
        });

        var pathsType = asm.GetType("Auto7z_WPF.Services.AppPaths");
        var vmType = asm.GetType("Auto7z_WPF.Auto7zViewModel");

        var isPortable = pathsType.GetProperty("IsPortable", BindingFlags.Public | BindingFlags.Static);
        var dataDir = pathsType.GetProperty("DataDirectory", BindingFlags.Public | BindingFlags.Static);
        var detectPortable = pathsType.GetMethod("DetectPortableFromDisk",
            BindingFlags.Public | BindingFlags.Static);
        int problems = 0;

        string localDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Auto7z");

        // 便携根必须按「入口程序集」算，而不是按被测的 Auto7z.exe 位置算。
        // 产品里 AppPaths.GetProgramDirectory() 用的就是 Assembly.GetEntryAssembly().Location，
        // 而本探针的入口是 portable.exe 自己 —— 两者位置不同（探针在 testtools\cs\portable，
        // 被测程序在 bin\Debug）。按 asmPath 算会去检查另一个目录，结论完全不可信。
        string programDir = Path.GetDirectoryName(
            System.Reflection.Assembly.GetEntryAssembly().Location);
        string portableRoot = Path.Combine(programDir, ".config");
        string portableDir = Path.Combine(portableRoot, "Auto7z");

        PrepareLocal(localDir);
        L("### 准备好的普通目录");
        DumpTree(localDir);
        L("");

        var vm = Activator.CreateInstance(vmType);
        var setPortable = vmType.GetMethod("SetPortableMode", BindingFlags.Public | BindingFlags.Instance);

        var skipProp = vmType.GetProperty("SkipShellMenuSync", BindingFlags.Public | BindingFlags.Instance);
        if (skipProp == null || !skipProp.CanWrite)
        {
            L("### RELEASE_NO_TEST_HOOK");
            L("  被测程序是 Release 构建，没有 SkipShellMenuSync。");
            L("  便携模式测试需要跳过右键菜单注册（否则会弹 UAC），");
            L("  请改用 Debug 构建：--config Debug");
            return 3;
        }

        skipProp.SetValue(vm, true, null);
        L("### 已跳过右键菜单注册同步（避免 UAC）");
        L("");

        L("### 切到便携模式");
        setPortable.Invoke(vm, new object[] { true });
        L("  IsPortable = " + isPortable.GetValue(null, null));
        L("  DataDirectory = " + dataDir.GetValue(null, null));
        L("  便携目录在磁盘上 = " + Directory.Exists(portableDir));
        L("  普通目录还在 = " + Directory.Exists(localDir));
        L("  便携根目录存在 = " + Directory.Exists(portableRoot));
        L("  便携目录内容:");
        DumpTree(portableDir);
        L("");

        L("### 切回普通模式");
        setPortable.Invoke(vm, new object[] { false });
        L("  IsPortable = " + isPortable.GetValue(null, null));
        L("  DataDirectory = " + dataDir.GetValue(null, null));
        L("  便携目录在磁盘上 = " + Directory.Exists(portableDir));
        L("  便携根目录还在 = " + Directory.Exists(portableRoot));
        L("  普通目录内容:");
        DumpTree(localDir);
        L("");

        // 关键断言一：便携判定看的是 .config 目录本身，不是它下面的 Auto7z。
        // 所以「取消便携」必须真的把 .config 删掉，否则下次启动又被判成便携、
        // 数据被搬回程序目录 —— 用户会发现便携模式取消不掉。
        bool cancelledClean = !Directory.Exists(portableRoot);
        L("### 取消便携后 .config 已删除 = " + cancelledClean);
        if (cancelledClean)
        {
            L("  OK：下次启动会判定为非便携");
        }
        else
        {
            L("  !! .config 仍存在，下次启动会被判回便携模式");
            problems++;
        }

        // 关键断言二：只留一个空 .config、没有 Auto7z 子目录，必须仍判为便携。
        // 这是「把程序拷到移动盘、旁边留个空 .config」的自然用法；
        // 程序没运行时不会有人替它建 Auto7z 子目录。
        L("### 只有空 .config 时的判定");
        Directory.CreateDirectory(portableRoot);
        bool emptyPortable = (bool)detectPortable.Invoke(null, null);
        L("  DetectPortableFromDisk = " + emptyPortable);
        if (emptyPortable)
        {
            L("  OK：空 .config 即视为便携");
        }
        else
        {
            L("  !! 空 .config 未被判为便携，用户拷走后数据仍写进 %LOCALAPPDATA%");
            problems++;
        }
        try { Directory.Delete(portableRoot, true); } catch { }
        L("");

        L("### 收尾：检查右键菜单注册");
        L("  系统里没有右键菜单注册（注册同步已被跳过），无需注销（不会弹 UAC）");
        L("");
        L("TOTAL PROBLEMS: " + problems);
        L("DONE");
        return problems == 0 ? 0 : 1;
    }

    static void PrepareLocal(string localDir)
    {
        if (Directory.Exists(localDir))
        {
            string full = Path.GetFullPath(localDir);
            string local = Path.GetFullPath(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
            if (!full.StartsWith(local, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("目录不在 %LOCALAPPDATA% 下, 拒绝删除: " + full);
            if (!full.EndsWith("Auto7z", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("目录名不是 Auto7z, 拒绝删除: " + full);

            Directory.Delete(full, true);
        }
        Directory.CreateDirectory(Path.Combine(localDir, "7-Zip"));
        Directory.CreateDirectory(Path.Combine(localDir, "Logs"));
        // 配置是标准 JSON；这里手写一份最小可读文件，产品会补齐缺失的键。
        File.WriteAllText(Path.Combine(localDir, "Auto7z.json"),
            "{\n  \"IsPortable\": false\n}\n", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(localDir, "Logs", "probe.log"), "x");
    }

    static void DumpTree(string root)
    {
        if (!Directory.Exists(root)) { L("    (not found)"); return; }
        foreach (string f in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
            L("    " + f.Substring(root.Length).TrimStart('\\'));
        foreach (string d in Directory.GetDirectories(root, "*", SearchOption.AllDirectories))
            L("    [DIR] " + d.Substring(root.Length).TrimStart('\\'));
    }
}
