using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

class CrossPollutionProbe
{
    static Application app;
    static int problems = 0;

    static void L(string s) { Console.WriteLine(s); }
    static void Check(string n, bool ok, string d)
    {
        Console.WriteLine((ok ? "  OK   " : "  !!   ") + n + (d.Length > 0 ? "  --- " + d : ""));
        if (!ok) problems++;
    }

    [STAThread]
    static int Main(string[] a)
    {
        if (a.Length < 2) { L("usage: crosspollute.exe <Auto7z.exe> <work>"); return 2; }
        string work = a[1];
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AUTO7Z_AUTOANSWER")))
            Environment.SetEnvironmentVariable("AUTO7Z_AUTOANSWER", "close,close,close,close,close");
        Environment.SetEnvironmentVariable("AUTO7Z_DATA_ROOT", Path.Combine(work, "data-cpp"));

        AppDomain.CurrentDomain.AssemblyResolve += delegate(object s, ResolveEventArgs e)
        {
            string simple = new AssemblyName(e.Name).Name;
            string p = Path.Combine(Path.GetDirectoryName(a[0]), simple + ".exe");
            return File.Exists(p) ? Assembly.LoadFrom(p) : null;
        };
        var asm = Assembly.LoadFrom(a[0]);
        // Release 构建没有 DebugAutoDriver，本探针依赖它自动点掉模态对话框。
        // 没有它时任务会永久阻塞在等待上 —— 实测 Release 下超时 180 秒。
        // 这里显式跳过，而不是让调用方等满超时再报一个看不懂的失败。
        //
        // 判定用「驱动类型是否存在」而不是读构建配置：这是 Release 与 Debug
        // 在程序集层面唯一可靠的差异（DebuggableAttribute 在两边都可能是 true）。
        if (asm.GetType("Auto7z_WPF.Services.DebugAutoDriver") == null)
        {
            Console.WriteLine("RELEASE_NO_TEST_HOOK 本探针需要 DebugAutoDriver 自动应答，Release 构建没有它");
            return 3;
        }

        app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        try {
            app.Resources.MergedDictionaries.Add(new ResourceDictionary {
                Source = new Uri("pack://application:,,,/Auto7z;component/Resources/Languages/zh-CN.xaml", UriKind.Absolute)
            });
        } catch { }

        Type t = asm.GetType("Auto7z_WPF.Auto7zViewModel");
        object vm = Activator.CreateInstance(t);

        // 探针必须自给自足：不能假设调用方（run_cs_probes 的临时目录）里已有测试归档。
        // 上一版假设 work 里有 broken.7z，套件跑时该文件不存在，前置条件直接失败。
        string srcTxt = Path.Combine(work, "src.txt");
        File.WriteAllText(srcTxt, "payload for cross-pollution probe");

        // 自己造一个损坏的 .7z：把合法归档的中间 32 字节抹零。
        string broken = Path.Combine(work, "broken.7z");
        if (!File.Exists(broken))
        {
            byte[] raw = new byte[512];
            // 用一段带 7z 文件头魔数的假数据，保证 7z 会尝试解析并失败。
            byte[] magic = { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C };
            Array.Copy(magic, raw, magic.Length);
            for (int i = magic.Length; i < raw.Length; i++) raw[i] = (byte)(i & 0xFF);
            File.WriteAllBytes(broken, raw);
        }

        // 步骤 1：解压一个损坏归档 → 失败 → LastTaskFailed=true
        L("[1] 解压损坏归档（制造 LastTaskFailed=true）");
        SetMode(vm, t, "UnZip");
        SetPending(vm, t, new[] { broken });
        Run(vm, t, "BeginExtractTask");
        L("     failed=" + Get<bool>(vm,t,"IsResultFailed") + " warning=" + Get<bool>(vm,t,"IsResultWarning")
          + " LastTaskFailed=" + Get<bool>(vm,t,"LastTaskFailed"));
        bool ltf = Get<bool>(vm, t, "LastTaskFailed");
        Check("前置条件：LastTaskFailed 已被置位", ltf, "");

        // 步骤 2：压缩一个干净文件 → 应当完全成功
        L("[2] 压缩干净文件（若被污染会显示为警告）");
        SetMode(vm, t, "Zip");
        SetPending(vm, t, new[] { srcTxt });
        Run(vm, t, "BeginCompressTask");
        bool failed = Get<bool>(vm, t, "IsResultFailed");
        bool warn = Get<bool>(vm, t, "IsResultWarning");
        string text = Get<string>(vm, t, "ResultText");
        L("     failed=" + failed + " warning=" + warn + " text=" + text);
        Check("干净压缩判为完全成功（未被上一轮污染）", !failed && !warn,
              "failed=" + failed + " warning=" + warn + " text=" + text);

        L("");
        L(problems == 0 ? "PASS" : "TOTAL PROBLEMS: " + problems);
        return problems == 0 ? 0 : 1;
    }

    static T Get<T>(object vm, Type t, string n)
    {
        var p = t.GetProperty(n);
        if (p == null) return default(T);
        object v = p.GetValue(vm);
        return v == null ? default(T) : (T)v;
    }
    static void SetMode(object vm, Type t, string m)
    {
        var p = t.GetProperty("CurrentOperationMode");
        p.SetValue(vm, Enum.Parse(p.PropertyType, m));
    }
    static void SetPending(object vm, Type t, string[] ps)
    {
        t.GetMethod("SetPendingFiles").Invoke(vm, new object[] { ps });
    }
    static void Run(object vm, Type t, string entry)
    {
        t.GetMethod(entry).Invoke(vm, null);
        var busy = t.GetProperty("IsBusy");
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (DateTime.UtcNow < deadline)
        {
            Pump();
            object b = busy.GetValue(vm);
            if (b == null || !(bool)b) break;
            Thread.Sleep(50);
        }
        Pump();
    }
    static void Pump()
    {
        if (app == null) return;
        for (int i = 0; i < 6; i++) { try { app.Dispatcher.Invoke(DispatcherPriority.Background, new Action(delegate { })); } catch { } Thread.Sleep(8); }
    }
}