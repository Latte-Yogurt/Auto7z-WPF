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
            Console.WriteLine("  需要 AUTO7Z_DATA_ROOT 已改道到临时目录，否则拒绝运行。");
            return 2;
        }

        // 必须最先检查：这个探针会递归删数据目录，隔离没到位时一步都不能往下走。
        if (!EnsureIsolated()) return 4;

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

        string localDir = Path.Combine(DataRoot(), "Auto7z");

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

        // 兜底闸门：切换之后产品自己报告的数据目录不能是真实的那个。
        // 上面 EnsureIsolated 已经挡住了"没改道就开跑"，这里是第二道 ——
        // 万一将来产品的数据根推导多出一条分支（比如某天真的去读 LOCALAPPDATA），
        // 也不至于静默地把用户数据搬走。
        //
        // 判据必须取**产品报告的 DataDirectory**，不能拿"某个目录在不在"去猜：
        // 切到便携后改道根下的 Auto7z 本来就会消失（数据搬走了），
        // 而用户真实目录一直存在 —— 按存在性判断会把正常情况误判成越界（已踩过）。
        string realRoot = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string realData = Path.GetFullPath(Path.Combine(realRoot, "Auto7z"));

        L("### 切到便携模式");
        setPortable.Invoke(vm, new object[] { true });
        CheckNotRealData(dataDir, realData, "切到便携之后");
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
        CheckNotRealData(dataDir, realData, "切回普通模式之后");
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

    /// <summary>
    /// 产品的数据根目录。被测程序支持 AUTO7Z_DATA_ROOT 改道（见 AppPaths.DataRootVariable），
    /// 测试用它把配置、日志、7-Zip 组件统一写进 %TEMP% 下的受控目录。
    /// </summary>
    static string DataRoot()
    {
        string redirect = Environment.GetEnvironmentVariable("AUTO7Z_DATA_ROOT");
        if (!string.IsNullOrEmpty(redirect) && Path.IsPathRooted(redirect)) return redirect;

        return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    }

    /// <summary>
    /// 硬性要求：本探针只在**改道到临时目录**时才能跑。
    ///
    /// 理由是一次真实事故：本探针为了准备夹具会递归删除数据根下的 Auto7z 目录，
    /// 并直接反射调 SetPortableMode —— 而产品的「切回普通模式」会把数据搬回
    /// %LOCALAPPDATA%\Auto7z。两条路径都落在用户真实的数据目录上。
    /// 实测确认过：不带改道变量跑一次，用户的 Auto7z.json / TaskState.json /
    /// 7-Zip 组件全被清掉重建（见 testtools\README.md 的「数据目录隔离」）。
    ///
    /// 所以这里直接拒绝启动，而不是"尽量小心地删"。少跑一个探针是可接受的，
    /// 删掉用户真实配置不可接受。
    /// </summary>
    static bool EnsureIsolated()
    {
        string redirect = Environment.GetEnvironmentVariable("AUTO7Z_DATA_ROOT");
        if (!string.IsNullOrEmpty(redirect) && Path.IsPathRooted(redirect)) return true;

        Console.WriteLine("!! 拒绝运行：没有设置 " + DataRootVariable + "。");
        Console.WriteLine("   本探针会递归删除数据目录、并驱动便携模式搬迁，");
        Console.WriteLine("   不带改道时这些操作全部落在用户真实的 %LOCALAPPDATA%\\Auto7z 上。");
        Console.WriteLine("   请通过 test_env.isolate_data_root(...) 设置改道后再跑，");
        Console.WriteLine("   或直接用 run_cs_probes.py / run_regress.py（它们已自动设置）。");
        return false;
    }

    /// <summary>改道变量名，与产品 Services/AppPaths.cs 的 DataRootVariable 对齐。</summary>
    const string DataRootVariable = "AUTO7Z_DATA_ROOT";

    /// <summary>
    /// 切换之后核对产品报告的数据目录：落在真实的 %LOCALAPPDATA%\Auto7z 上就立即中止。
    ///
    /// 做成静态方法而不是局部函数：本探针由 .NET Framework 4.0 的 csc.exe 编译（C# 5），
    /// 局部函数要 C# 7，写进去会直接编不过。
    /// </summary>
    static void CheckNotRealData(PropertyInfo dataDirProperty, string realData, string when)
    {
        object reported = dataDirProperty.GetValue(null, null);
        if (reported == null) return;

        string actual;
        try
        {
            actual = Path.GetFullPath(reported.ToString());
        }
        catch (Exception)
        {
            // 取不到规范路径说明不了"越界"，按最坏情况放行后续的显式断言，不在这里中断。
            return;
        }

        if (!string.Equals(actual, realData, StringComparison.OrdinalIgnoreCase)) return;

        L("  !! " + when + "：产品报告的数据目录是真实的 " + realData + "，立即中止");
        Environment.Exit(5);
    }

    static void PrepareLocal(string localDir)
    {
        if (Directory.Exists(localDir))
        {
            string full = Path.GetFullPath(localDir);
            string local = Path.GetFullPath(DataRoot());
            if (!full.StartsWith(local, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("目录不在数据根目录下, 拒绝删除: " + full);
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
