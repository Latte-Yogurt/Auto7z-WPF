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
        string programDir = Path.GetDirectoryName(asmPath);
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

        L("### 收尾：检查右键菜单注册");
        L("  系统里没有右键菜单注册（注册同步已被跳过），无需注销（不会弹 UAC）");
        L("");

        L("DONE");
        return 0;
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

    static string ReadPortable(string configPath)
    {
        if (!File.Exists(configPath)) return "(config not found)";
        try
        {
            // 配置是标准 JSON。这里做最小解析而不是引 System.Text.Json：
            // 探针是独立 exe，不该为了读一个键带进额外的依赖。
            string text = File.ReadAllText(configPath, Encoding.UTF8);
            int key = text.IndexOf("\"IsPortable\"", StringComparison.Ordinal);
            if (key < 0) return "(key missing)";

            int colon = text.IndexOf(':', key);
            if (colon < 0) return "(key missing)";

            int start = colon + 1;
            while (start < text.Length && (text[start] == ' ' || text[start] == '\t' ||
                                           text[start] == '\r' || text[start] == '\n'))
            {
                start++;
            }

            // 值可能是原生 true/false，也可能被手工写成带引号的 "True"
            if (start < text.Length && text[start] == '"')
            {
                int end = text.IndexOf('"', start + 1);
                return end < 0 ? "(unterminated)" : text.Substring(start + 1, end - start - 1);
            }

            int stop = start;
            while (stop < text.Length && text[stop] != ',' && text[stop] != '}' &&
                   text[stop] != '\r' && text[stop] != '\n')
            {
                stop++;
            }

            return text.Substring(start, stop - start).Trim();
        }
        catch (Exception e) { return "read failed: " + e.Message; }
    }
}
