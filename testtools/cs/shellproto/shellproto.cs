using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

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
        if (a.Length < 2)
        {
            Console.WriteLine("用法: shellproto.exe <Auto7z.exe 路径> <工作目录>");
            Console.WriteLine("  验证共享内存传参、模式开关、单实例交接。");
            return 2;
        }

        string configPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Auto7z", "Auto7z.json");
        // 配置是标准 JSON。这里用简单的字符串定位改一个键：
        // 探针是独立 exe，不该为了改一个值把整个 JSON 解析器搬进来。
        string originalConfig = null;
        if (File.Exists(configPath))
        {
            try
            {
                originalConfig = File.ReadAllText(configPath, new UTF8Encoding(false));
                string patched = ReplaceJsonString(originalConfig, "ErrorLevel", "Info");
                if (patched != null) File.WriteAllText(configPath, patched, new UTF8Encoding(false));
            }
            catch { }
        }

        string exe = a[0];

        string guiExe = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(exe)) ?? ".", "Auto7zGUI.exe");
        if (!File.Exists(guiExe)) guiExe = exe;

        string workDir = a[1];
        string logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                     "Auto7z", "Logs");

        if (Directory.Exists(workDir)) Directory.Delete(workDir, true);
        Directory.CreateDirectory(workDir);

        MemoryMappedFile map1, map2, map3, map4;
        EventWaitHandle sig1, sig2, sig3, sig4;

        var files = new List<string>();
        for (int i = 0; i < 3; i++)
        {
            string path = Path.Combine(workDir, "测试文件 " + i + ".txt");
            File.WriteAllText(path, "content " + i, Encoding.UTF8);
            files.Add(path);
        }

        Console.WriteLine("=== T1: 共享内存传 3 个文件 ===");
        string arg = WriteMap(files, out map1, out sig1);
        Console.WriteLine("  命令行参数: " + arg);
        expectedCount = 3;
        RunTask(exe, new[] { arg, "-z" }, workDir, logDir, "共享内存传多文件");

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
        RunTask(exe, new[] { arg2, "-z" }, workDir, logDir, "200 个文件");

        Console.WriteLine("");
        Console.WriteLine("=== T3: 模式开关 -z / -uz / -a ===");

        {
            string arg3 = WriteMap(files, out map3, out sig3);
            expectedCount = 3;
            RunTask(exe, new[] { arg3, "-z" }, workDir, logDir, "模式 -z");
        }

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
                RunTask(exe, new[] { arg3, "-uz" }, workDir, logDir, "模式 -uz");
                expectExtractLog = false;
            }
            else
            {
                Bad("模式 -uz: 造不出测试用的压缩包");
            }
        }

        {
            string arg3 = WriteMap(files, out map3, out sig3);
            expectedCount = 0;
            RunTask(exe, new[] { arg3, "-a" }, workDir, logDir, "模式 -a");
        }

        Console.WriteLine("");
        Console.WriteLine("=== T4: 直接给路径 ===");
        expectedCount = 3;
        RunTask(exe, new[] { "-z" }.Concat(files).ToArray(), workDir, logDir, "直接路径");

        Console.WriteLine("");
        Console.WriteLine("=== T5: 单实例 ===");
        var first = Process.Start(new ProcessStartInfo(guiExe) { UseShellExecute = false });
        Thread.Sleep(4000);
        int count = CountAppProcesses();
        Console.WriteLine("  第一个实例启动后, 进程数 = " + count);
        if (count < 1) Bad("第一个实例没起来");

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
        if (originalConfig != null && File.Exists(configPath))
        {
            try
            {
                // 整份还原：改键只动了 ErrorLevel，但还原整份文本最保险，
                // 不会漏掉产品在运行期间自己写回的其他键。
                File.WriteAllText(configPath, originalConfig, new UTF8Encoding(false));
            }
            catch { }
        }

        Console.WriteLine("TOTAL PROBLEMS: " + problems);
        return 0;
    }

    // 把标准 JSON 里某个字符串键的值换掉；找不到就返回 null。
    static string ReplaceJsonString(string text, string key, string value)
    {
        string needle = "\"" + key + "\"";
        int position = text.IndexOf(needle, StringComparison.Ordinal);
        if (position < 0) return null;

        int colon = text.IndexOf(':', position + needle.Length);
        if (colon < 0) return null;

        int start = colon + 1;
        while (start < text.Length && (text[start] == ' ' || text[start] == '\t' ||
                                       text[start] == '\r' || text[start] == '\n'))
        {
            start++;
        }

        if (start >= text.Length || text[start] != '"') return null;

        int end = text.IndexOf('"', start + 1);
        if (end < 0) return null;

        return text.Substring(0, start + 1) + value + text.Substring(end);
    }

    static int RunTask(string exe, string[] args, string workDir, string logDir, string label)
    {
        int before = CountArchives(workDir);
        var logBefore = GetLogLengths(logDir);

        var psi = new ProcessStartInfo(exe);
        psi.Arguments = string.Join(" ", Array.ConvertAll(args, Quote));
        psi.UseShellExecute = false;
        psi.WorkingDirectory = workDir;
        psi.RedirectStandardInput = true;

        Console.WriteLine("    命令行: " + psi.Arguments);

        Process p = Process.Start(psi);
        try { p.StandardInput.Close(); } catch { }

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
    static extern int GetWindowText(IntPtr hWnd, StringBuilder s, int max);
    [DllImport("user32.dll")]
    static extern int GetWindowTextLength(IntPtr hWnd);

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

    static bool expectExtractLog = false;

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
                if (text.Contains("\u89e3\u538b\u5c1d\u8bd5")) return 1;
                continue;
            }

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

    static string Quote(string value)
    {
        return "\"" + value.Replace("\"", "\\\"") + "\"";
    }
}
