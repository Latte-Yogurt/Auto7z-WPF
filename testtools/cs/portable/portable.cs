using System;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;

// 验证便携模式切换(直接调用应用里的真实逻辑):
//   1) 勾选便携 -> 数据目录搬到程序目录的 .config\Auto7z
//   2) 取消勾选 -> 数据目录搬回 %LOCALAPPDATA%\Auto7z
//   3) 键值先保存再搬目录
class Portable
{
    static string lp;
    static StreamWriter w;

    static void L(string s) { w.WriteLine(s); w.Flush(); Console.WriteLine(s); }

    static int Main(string[] a)
    {
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
        var configPath = pathsType.GetProperty("ConfigFilePath", BindingFlags.Public | BindingFlags.Static);

        string localDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Auto7z");
        // 程序目录按 Auto7z.exe 算, 不按 portable.exe。
        // AppPaths.GetProgramDirectory() 取的是 GetEntryAssembly().Location,
        // 在测试进程里指向 portable.exe, 那是测试工具的位置, 不是程序的位置。
        string programDir = Path.GetDirectoryName(asmPath);
        string portableRoot = Path.Combine(programDir, ".config");
        string portableDir = Path.Combine(portableRoot, "Auto7z");

        // 准备一个普通模式的数据目录: 配置 + 组件目录 + 日志目录
        PrepareLocal(localDir);
        L("### 准备好的普通目录");
        DumpTree(localDir);
        L("");

        var vm = Activator.CreateInstance(vmType);
        var setPortable = vmType.GetMethod("SetPortableMode", BindingFlags.Public | BindingFlags.Instance);

        L("### 切到便携模式");
        setPortable.Invoke(vm, new object[] { true });
        L("  IsPortable = " + isPortable.GetValue(null, null));
        L("  DataDirectory = " + dataDir.GetValue(null, null));
        L("  配置里的 IsPortable = " + ReadPortable((string)configPath.GetValue(null, null)));
        L("  普通目录还在 = " + Directory.Exists(localDir));
        L("  便携根目录存在 = " + Directory.Exists(portableRoot));
        L("  便携目录内容:");
        DumpTree(portableDir);
        L("");

        L("### 切回普通模式");
        setPortable.Invoke(vm, new object[] { false });
        L("  IsPortable = " + isPortable.GetValue(null, null));
        L("  DataDirectory = " + dataDir.GetValue(null, null));
        L("  配置里的 IsPortable = " + ReadPortable((string)configPath.GetValue(null, null)));
        L("  便携目录还在 = " + Directory.Exists(portableDir));
        L("  便携根目录还在 = " + Directory.Exists(portableRoot));
        L("  普通目录内容:");
        DumpTree(localDir);
        L("");

        L("DONE");
        return 0;
    }

    static void PrepareLocal(string localDir)
    {
        if (Directory.Exists(localDir)) Directory.Delete(localDir, true);
        Directory.CreateDirectory(Path.Combine(localDir, "7-Zip"));
        Directory.CreateDirectory(Path.Combine(localDir, "Logs"));
        File.WriteAllText(Path.Combine(localDir, "Auto7z.xml"),
            "<?xml version=\"1.0\" encoding=\"utf-8\"?><Configuration><IsPortable>False</IsPortable></Configuration>");
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

    static string ReadPortable(string configPath)
    {
        if (!File.Exists(configPath)) return "(config not found)";
        try
        {
            var doc = System.Xml.Linq.XDocument.Load(configPath);
            if (doc.Root == null) return "(no root)";
            var node = doc.Root.Element("IsPortable");
            return node == null ? "(key missing)" : node.Value;
        }
        catch (Exception e) { return "read failed: " + e.Message; }
    }
}
