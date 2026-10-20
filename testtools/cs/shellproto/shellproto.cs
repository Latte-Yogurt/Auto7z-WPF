using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
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
        var first = Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false });
        Thread.Sleep(4000);
        int count = Process.GetProcessesByName("Auto7z").Length;
        Console.WriteLine("  第一个实例启动后, 进程数 = " + count);
        if (count < 1) Bad("第一个实例没起来");

        // 再起一个, 应该把文件交过去然后自己退出
        string arg4 = WriteMap(files, out map4, out sig4);
        var second = Process.Start(new ProcessStartInfo(exe, "\"" + arg4 + "\"") { UseShellExecute = false });
        Thread.Sleep(5000);

        bool exited = second.HasExited;
        Console.WriteLine("  第二个实例是否自己退出 = " + exited);
        if (!exited)
        {
            Bad("第二个实例没有退出, 单实例没生效");
            second.Kill();
        }

        int count2 = Process.GetProcessesByName("Auto7z").Length;
        Console.WriteLine("  交接后进程数 = " + count2 + " (应为 1)");
        if (count2 > 1) Bad("交接后还有多个实例");

        foreach (var p in Process.GetProcessesByName("Auto7z"))
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

        Console.WriteLine("    命令行: " + psi.Arguments);

        Process p = Process.Start(psi);
        Thread.Sleep(6000);
        if (!p.HasExited) p.Kill();
        Thread.Sleep(800);

        int created = CountArchives(workDir) - before;
        int received = GetReceivedCount(logDir, logBefore);

        Console.WriteLine("  [" + label + "] 日志收到 " + received + " 个, 新生成压缩包 " + created + " 个");

        if (expectedCount > 0)
        {
            if (received == 0) Bad(label + ": 没有收到文件");
            if (received != expectedCount) Bad(label + ": 收到的文件数不对, 期望 " + expectedCount);
        }
        return created;
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

    static void RunTaskOld(string exe, string[] args, string workDir, string logDir, string label)
    {
        // 清掉上一次的日志, 便于确认这次收到了什么
        string newestLog = null;
        DateTime newest = DateTime.MinValue;
        if (Directory.Exists(logDir))
        {
            foreach (string f in Directory.GetFiles(logDir, "*.log"))
            {
                DateTime t = File.GetLastWriteTime(f);
                if (t > newest) { newest = t; newestLog = f; }
            }
        }

        long before = newestLog != null ? new FileInfo(newestLog).Length : 0;

        var psi = new ProcessStartInfo(exe);
        psi.Arguments = string.Join(" ", Array.ConvertAll(args, Quote));
        psi.UseShellExecute = false;
        psi.WorkingDirectory = workDir;

        Process p = Process.Start(psi);
        Thread.Sleep(6000);
        if (!p.HasExited) p.Kill();
        Thread.Sleep(800);

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
