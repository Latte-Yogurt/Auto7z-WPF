using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

// 模拟外壳扩展的行为, 验证主程序能不能正确接收:
//   1) #映射名:字节数:事件名 的共享内存传参
//   2) 一次传多个文件
//   3) -A / -Z / -U 模式开关
//   4) 单实例: 第二次启动把文件交给已有实例
class ShellProto
{
    const string MapName = "Auto7zShellTestMap";
    const string EventName = "Auto7zShellTestEvent";

    static int problems = 0;

    static void Bad(string s)
    {
        Console.WriteLine("  !! " + s);
        problems++;
    }

    // 按外壳扩展的写法把路径写进共享内存。
    // 映射句柄必须一直握着, 命名映射对象靠句柄存活。
    static string WriteMap(List<string> paths, out MemoryMappedFile mapping, out EventWaitHandle signal)
    {
        int totalChars = 1;
        foreach (string p in paths) totalChars += p.Length + 1;
        int totalBytes = totalChars * 2;

        string mapName = MapName + Guid.NewGuid().ToString("N").Substring(0, 8);
        string eventName = EventName + Guid.NewGuid().ToString("N").Substring(0, 8);

        mapping = MemoryMappedFile.CreateNew(mapName, totalBytes, MemoryMappedFileAccess.ReadWrite);
        using (var accessor = mapping.CreateViewAccessor(0, totalBytes, MemoryMappedFileAccess.ReadWrite))
        {
            byte[] buffer = new byte[totalBytes];
            int position = 2;
            buffer[0] = 0;
            buffer[1] = 0;
            foreach (string p in paths)
            {
                byte[] encoded = Encoding.Unicode.GetBytes(p);
                Buffer.BlockCopy(encoded, 0, buffer, position, encoded.Length);
                position += encoded.Length;
                buffer[position] = 0;
                buffer[position + 1] = 0;
                position += 2;
            }
            accessor.WriteArray(0, buffer, 0, totalBytes);
            accessor.Flush();
        }

        signal = new EventWaitHandle(false, EventResetMode.ManualReset, eventName);

        return "#" + mapName + ":" + totalBytes + ":" + eventName;
    }

    // 两个可执行体都算「本程序的实例」：交接与清理都要数到
    static Process[] AppProcesses()
    {
        return Process.GetProcessesByName("Auto7z")
            .Concat(Process.GetProcessesByName("Auto7zGUI"))
            .ToArray();
    }

    static int CountAppProcesses()
    {
        return AppProcesses().Length;
    }

    static int Main(string[] a)
    {
        // "共 N 个文件"那条是 Info 级的日志, 默认级别 Warning 写不出来。
        // 跑之前先调成 Info, 读完记得还原。
        string configPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Auto7z", "Auto7z.xml");
        string originalLevel = null;
        if (File.Exists(configPath))
        {
            try
            {
                var doc = System.Xml.Linq.XDocument.Load(configPath);
                var root = doc.Root;
                var node = root == null ? null : root.Element("ErrorLevel");
                if (node != null)
                {
                    originalLevel = node.Value;
                    node.Value = "Info";
                    doc.Save(configPath);
                }
            }
            catch { }
        }

        string exe = a[0];

        // 图形入口是另一个可执行体：Auto7z.exe 只做命令行（不带参数时打印用法就退出），
        // 需要「起一个常驻实例」的用例必须用 Auto7zGUI.exe。
        string guiExe = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(exe)) ?? ".", "Auto7zGUI.exe");
        if (!File.Exists(guiExe)) guiExe = exe;

        string workDir = a[1];
        string logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                     "Auto7z", "Logs");

        if (Directory.Exists(workDir)) Directory.Delete(workDir, true);
        Directory.CreateDirectory(workDir);

        MemoryMappedFile map1, map2, map3, map4;
        EventWaitHandle sig1, sig2, sig3, sig4;

        // 造几个测试文件
        var files = new List<string>();
        for (int i = 0; i < 3; i++)
        {
            string path = Path.Combine(workDir, "测试文件 " + i + ".txt");
            File.WriteAllText(path, "content " + i, Encoding.UTF8);
            files.Add(path);
        }

        // ---------- 1) 共享内存传多个文件 ----------
        Console.WriteLine("=== T1: 共享内存传 3 个文件 ===");
        string arg = WriteMap(files, out map1, out sig1);
        Console.WriteLine("  命令行参数: " + arg);
        expectedCount = 3;
        RunTask(exe, new[] { arg, "-Z" }, workDir, logDir, "共享内存传多文件");

        // ---------- 2) 长路径: 命令行放不下的场景 ----------
        Console.WriteLine("");
        Console.WriteLine("=== T2: 200 个文件(命令行会超长) ===");
        var many = new List<string>();
        for (int i = 0; i < 200; i++)
        {
            string path = Path.Combine(workDir, "长名称文件_" + i + "_" + new string('x', 40) + ".txt");
            File.WriteAllText(path, "c", Encoding.UTF8);
            many.Add(path);
        }
        string arg2 = WriteMap(many, out map2, out sig2);
        expectedCount = 200;
        RunTask(exe, new[] { arg2, "-Z" }, workDir, logDir, "200 个文件");

        // ---------- 3) 模式开关 ----------
        Console.WriteLine("");
        Console.WriteLine("=== T3: 模式开关 -Z / -U / -A ===");

        // -Z: 压缩, 传普通文件
        {
            string arg3 = WriteMap(files, out map3, out sig3);
            expectedCount = 3;
            RunTask(exe, new[] { arg3, "-Z" }, workDir, logDir, "模式 -Z");
        }

        // -U: 解压, 要传真的压缩包, 否则解不开
        {
            string zipDir = Path.Combine(workDir, "zip");
            Directory.CreateDirectory(zipDir);
            string zipPath = Path.Combine(zipDir, "包.7z");
            RunSevenZip(workDir, zipPath, files);
            if (File.Exists(zipPath))
            {
                string arg3 = WriteMap(new List<string> { zipPath }, out map3, out sig3);
                expectedCount = 1;
                expectExtractLog = true;
                RunTask(exe, new[] { arg3, "-U" }, workDir, logDir, "模式 -U");
                expectExtractLog = false;
            }
            else
            {
                Bad("模式 -U: 造不出测试用的压缩包");
            }
        }

        // -A: 自动模式, 普通文件会各自压缩, 走的是另一条日志
        {
            string arg3 = WriteMap(files, out map3, out sig3);
            expectedCount = 0;   // 自动模式不打"共 N 个文件"
            RunTask(exe, new[] { arg3, "-A" }, workDir, logDir, "模式 -A");
        }

        // ---------- 4) 直接给路径(拖到 exe 上) ----------
        Console.WriteLine("");
        Console.WriteLine("=== T4: 直接给路径 ===");
        expectedCount = 3;
        RunTask(exe, new[] { "-Z" }.Concat(files).ToArray(), workDir, logDir, "直接路径");

        // ---------- 5) 单实例 ----------
        Console.WriteLine("");
        Console.WriteLine("=== T5: 单实例 ===");
        var first = Process.Start(new ProcessStartInfo(guiExe) { UseShellExecute = false });
        Thread.Sleep(4000);
        int count = CountAppProcesses();
        Console.WriteLine("  第一个实例启动后, 进程数 = " + count);
        if (count < 1) Bad("第一个实例没起来");

        // 再起一个, 应该把文件交过去然后自己退出
        string arg4 = WriteMap(files, out map4, out sig4);
        var second = Process.Start(new ProcessStartInfo(guiExe, "\"" + arg4 + "\"") { UseShellExecute = false });
        Thread.Sleep(5000);

        bool exited = second.HasExited;
        Console.WriteLine("  第二个实例是否自己退出 = " + exited);
        if (!exited)
        {
            Bad("第二个实例没有退出, 单实例没生效");
            second.Kill();
        }

        int count2 = CountAppProcesses();
        Console.WriteLine("  交接后进程数 = " + count2 + " (应为 1)");
        if (count2 > 1) Bad("交接后还有多个实例");

        foreach (var p in AppProcesses())
        {
            try { p.Kill(); } catch { }
        }

        Console.WriteLine("");
        if (originalLevel != null && File.Exists(configPath))
        {
            try
            {
                var doc = System.Xml.Linq.XDocument.Load(configPath);
                var root = doc.Root;
                var node = root == null ? null : root.Element("ErrorLevel");
                if (node != null)
                {
                    node.Value = originalLevel;
                    doc.Save(configPath);
                }
            }
            catch { }
        }

        Console.WriteLine("TOTAL PROBLEMS: " + problems);
        return 0;
    }

    // 跑一次任务, 返回生成的 7z 数量
    static int RunTask(string exe, string[] args, string workDir, string logDir, string label)
    {
        int before = CountArchives(workDir);
        var logBefore = GetLogLengths(logDir);

        var psi = new ProcessStartInfo(exe);
        psi.Arguments = string.Join(" ", Array.ConvertAll(args, Quote));
        psi.UseShellExecute = false;
        psi.WorkingDirectory = workDir;
        // 不重定向输出: 管道缓冲区写满会把应用卡住
        //
        // stdin 必须断开: 不重定向的话子进程会继承本进程(stdin)的控制台,
        // 而控制台模式问"是否合并"时要读一行 —— 读不到就永远卡在那里,
        // 表现为"没收到文件"。
        // 断开之后读操作立刻返回 null, 程序按"没给输入"的分支走, 不会卡住。
        psi.RedirectStandardInput = true;

        Console.WriteLine("    命令行: " + psi.Arguments);

        Process p = Process.Start(psi);
        try { p.StandardInput.Close(); } catch { }

        // 右键菜单模式（共享内存传参）会弹 MessageWindow 问"是否合并为一个压缩包"。
        // 那个对话框是模态的，程序会等用户点按钮才继续；
        // 自动化环境里没有人点，任务就永远停在那里，测出来是"没收到文件"。
        // 这里替用户点一下"是"（合并），让流程能跑完。
        int answered = AutoConfirmMergeDialog(p, 8000);

        Thread.Sleep(6000);
        if (!p.HasExited) p.Kill();
        Thread.Sleep(800);

        int created = CountArchives(workDir) - before;
        int received = GetReceivedCount(logDir, logBefore);

        Console.WriteLine("  [" + label + "] 日志收到 " + received + " 个, 新生成压缩包 " + created
                          + " 个" + (answered > 0 ? " (自动点了 " + answered + " 次确认)" : ""));

        if (expectedCount > 0)
        {
            if (received == 0) Bad(label + ": 没有收到文件");
            if (received != expectedCount) Bad(label + ": 收到的文件数不对, 期望 " + expectedCount);
        }
        return created;
    }

    // 找到被测进程的对话框并按回车确认。
    //
    // 右键菜单模式（共享内存传参）会弹 MessageWindow 问"是否合并为一个压缩包"。
    // 那个对话框是模态的，程序要等用户点按钮才继续；自动化环境里没有人点，
    // 任务就永远停在那里，测出来是"没收到文件"。
    //
    // 为什么用回车而不是找按钮点：
    //   WPF 的按钮不是原生 Win32 控件，整个窗口只有一个 HwndWrapper，
    //   FindWindowEx 枚举不到按钮（已实测：枚举出来只有一个窗口，没有子按钮）。
    //   但对话框的"确定"按钮是 IsDefault="True"，回车就等于点它。
    //   所以：找到可见的对话框窗口，发一个 Enter 即可。
    static int AutoConfirmMergeDialog(Process p, int timeoutMs)
    {
        int clicked = 0;
        int waited = 0;
        bool debug = Environment.GetEnvironmentVariable("AUTO7Z_DEBUG") == "1";
        while (waited < timeoutMs && !p.HasExited)
        {
            foreach (var proc in Process.GetProcessesByName("Auto7z"))
            {
                try
                {
                    foreach (IntPtr h in EnumWindows(proc.Id))
                    {
                        if (!IsWindowVisible(h)) continue;
                        int len = GetWindowTextLength(h);
                        if (len == 0) continue;
                        var buf = new StringBuilder(len + 1);
                        GetWindowText(h, buf, buf.Capacity);
                        string title = buf.ToString();
                        if (title != "提示" && title != "資訊" && title != "Info") continue;

                        if (debug) Say("      [窗口] 标题='" + title + "'，发回车确认");
                        PostMessage(h, WM_KEYDOWN, (IntPtr)VK_RETURN, IntPtr.Zero);
                        PostMessage(h, WM_KEYUP, (IntPtr)VK_RETURN, IntPtr.Zero);
                        clicked++;
                        break;
                    }
                }
                catch { }
            }
            if (clicked > 0) break;
            Thread.Sleep(200);
            waited += 200;
        }
        return clicked;
    }

    static void Say(string s) { Console.WriteLine(s); }

    const uint WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101;
    const int VK_RETURN = 0x0D;

    [DllImport("user32.dll")]
    static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")]
    static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string cls, string title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetWindowText(IntPtr hWnd, StringBuilder s, int max);
    [DllImport("user32.dll")]
    static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll")]
    static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    static List<IntPtr> EnumWindows(int pid)
    {
        var result = new List<IntPtr>();
        EnumWindows(delegate(IntPtr h, IntPtr l)
        {
            uint p;
            GetWindowThreadProcessId(h, out p);
            if (p == (uint)pid) result.Add(h);
            return true;
        }, IntPtr.Zero);
        return result;
    }

    static int expectedCount = 0;

    // 解压模式的日志文案不一样, 单独标记
    static bool expectExtractLog = false;

    // 记录每个日志文件当前的长度, 之后只读新增的部分。
    // 普通日志与问题日志分开存, 谁新谁旧不一定, 不能只看最新的那个。
    static Dictionary<string, long> GetLogLengths(string logDir)
    {
        var lengths = new Dictionary<string, long>();
        if (Directory.Exists(logDir))
        {
            foreach (string f in Directory.GetFiles(logDir, "*.log"))
                lengths[f] = new FileInfo(f).Length;
        }
        return lengths;
    }

    // 从新增的日志里读"接收 N 个项目"
    static int GetReceivedCount(string logDir, Dictionary<string, long> before)
    {
        if (!Directory.Exists(logDir)) return 0;

        foreach (string f in Directory.GetFiles(logDir, "*.log"))
        {
            long from;
            if (!before.TryGetValue(f, out from)) from = 0;

            string text;
            using (var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                if (from >= fs.Length) continue;
                fs.Seek(from, SeekOrigin.Begin);
                byte[] buffer = new byte[fs.Length - from];
                fs.Read(buffer, 0, buffer.Length);
                text = Encoding.UTF8.GetString(buffer);
            }

            if (expectExtractLog)
            {
                // 解压模式: 看"解压尝试 1: exit=0"
                if (text.Contains("\u89e3\u538b\u5c1d\u8bd5")) return 1;
                continue;
            }

            // 控制台模式下没有主窗口, 日志里记的是"用户选择合并为单个压缩包，共 N 个文件"
            foreach (string line in text.Split('\n'))
            {
                int at = line.IndexOf("\u5171 ");
                if (at < 0) continue;
                int start = at + 2;
                int end = line.IndexOf(' ', start);
                if (end < 0) continue;
                int num;
                if (int.TryParse(line.Substring(start, end - start), out num)) return num;
            }
        }
        return 0;
    }

    static int CountArchives(string dir)
    {
        int count = 0;
        foreach (string f in Directory.GetFiles(dir, "*.7z", SearchOption.AllDirectories)) count++;
        return count;
    }

    // 用 7z 造一个测试用的压缩包
    static void RunSevenZip(string workDir, string zipPath, List<string> files)
    {
        string sevenZip = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Auto7z", "7-Zip", "7z.exe");
        if (!File.Exists(sevenZip)) return;

        var psi = new ProcessStartInfo(sevenZip);
        var quoted = new List<string>();
        quoted.Add("a");
        quoted.Add("-y");
        quoted.Add("-aoa");
        quoted.Add(Quote(zipPath));
        foreach (string f in files) quoted.Add(Quote(f));
        psi.Arguments = string.Join(" ", quoted.ToArray());
        psi.UseShellExecute = false;
        psi.WorkingDirectory = workDir;
        psi.CreateNoWindow = true;

        Process p = Process.Start(psi);
        p.WaitForExit(15000);
    }

    // 给参数加引号, 免得路径里的空格把命令行拆开
    static string Quote(string value)
    {
        return "\"" + value.Replace("\"", "\\\"") + "\"";
    }

    static int CountItems(string line)
    {
        int at = line.IndexOf("接收 ");
        if (at < 0) return 0;
        string rest = line.Substring(at + 3);
        int space = rest.IndexOf(' ');
        if (space < 0) return 0;
        int num;
        return int.TryParse(rest.Substring(0, space), out num) ? num : 0;
    }

    static string ReadTail(string path, long from)
    {
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            if (from >= fs.Length) return string.Empty;
            fs.Seek(from, SeekOrigin.Begin);
            byte[] buffer = new byte[fs.Length - from];
            fs.Read(buffer, 0, buffer.Length);
            return Encoding.UTF8.GetString(buffer);
        }
    }
}
