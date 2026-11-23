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

    /// <summary>
    /// 产品的数据根目录。被测程序支持 AUTO7Z_DATA_ROOT 改道（见 AppPaths.DataRootVariable），
    /// 测试用它把配置、日志、7-Zip 组件统一写进 %TEMP% 下的受控目录 ——
    /// 不跟随这个变量的话，探针会去读用户真实的那份目当，得出一份"配置没生效"的假结论。
    /// </summary>
    static string DataRoot()
    {
        string redirect = Environment.GetEnvironmentVariable("AUTO7Z_DATA_ROOT");
        if (!string.IsNullOrEmpty(redirect) && Path.IsPathRooted(redirect)) return redirect;

        return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
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

        string configPath = Path.Combine(DataRoot(), "Auto7z", "Auto7z.json");
        // 配置是标准 JSON。这里用简单的字符串定位改一个键：
        // 探针是独立 exe，不该为了改一个值把整个 JSON 解析器搬进来。
        //
        // 配置文件不存在时必须先落一份最小配置：没有这一份，下面的 patch 无处可写，
        // ErrorLevel 会停在出厂的 Warning，Info 级的「共 N 个文件」永远不进日志，
        // 后三项断言就会误报「没有收到文件」。这不是产品缺陷 —— 产品读到坏配置或
        // 缺配置时会自己重建，探针只要先给它一个起点即可。
        // 这个坑的真实来源：portable 探针会清空整个 %LOCALAPPDATA%\Auto7z，
        // 紧随其后跑 shellproto 就必然撞上。
        string originalConfig = null;
        if (File.Exists(configPath))
        {
            try
            {
                originalConfig = File.ReadAllText(configPath, new UTF8Encoding(false));
            }
            catch { }
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(configPath));
            string seed = originalConfig ?? "{}";

            // **必须同时钉住两个键：ErrorLevel=Info、Language=zh-CN。**
            //
            // 这条注释是踩了两次坑才写全的，两个键各自让四条断言集体误报过一次：
            //
            // ① ErrorLevel（第一次）：
            //    早先是 `ReplaceJsonString(seed, "ErrorLevel", "Info") ?? 插入一条`。
            //    `portable` 探针会清空并重建数据目录，它写下的配置里 ErrorLevel 是
            //    "Warning"。替换若没命中，兜底分支只会**再插入一条**同名键，
            //    得到含重复键的 JSON（不同解析器取值不一致）。
            //    现在：先删掉所有已存在的键，再统一插入一条，结果唯一。
            //
            // ② Language（第二次，更隐蔽）：
            //    即使日志级别对了，`portable` 还会把 Language 落成 "en-US"。
            //    于是产品用英文写日志（"The user chose to merge, 3 file(s)…"），
            //    而本探针下面按中文「共 N 个文件」去数 —— 数出来永远是 0。
            //    两次失败的**表面症状完全一样**（「没有收到文件」），
            //    但原因一个是级别、一个是语言。只修一个会以为没修好。
            //    现在一并钉成 zh-CN：断言依赖的是中文日志格式，就得保证产品说中文。
            //
            // 这两个键都是「探针自身的前提」，不是产品行为，所以由探针负责设好。
            string cleaned = RemoveJsonKey(seed, "ErrorLevel");
            cleaned = RemoveJsonKey(cleaned, "Language");

            int close = cleaned.LastIndexOf('}');
            if (close < 0)
            {
                cleaned = "{}";
                close = 1;
            }

            string body = cleaned.Substring(0, close).TrimEnd();
            bool needsComma = body.Length > 1 && !body.EndsWith("{");
            string patched = body
                             + (needsComma ? "," : "")
                             + "\"ErrorLevel\": \"Info\","
                             + "\"Language\": \"zh-CN\""
                             + cleaned.Substring(close);

            File.WriteAllText(configPath, patched, new UTF8Encoding(false));
        }
        catch { }

        string exe = a[0];

        string guiExe = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(exe)) ?? ".", "Auto7zGUI.exe");
        if (!File.Exists(guiExe)) guiExe = exe;

        string workDir = a[1];
        string logDir = Path.Combine(DataRoot(), "Auto7z", "Logs");

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

    /// <summary>
    /// 从扁平 JSON 里删掉一个键（含它的值与相邻逗号）。
    ///
    /// 只需处理本项目配置这一种形态：顶层扁平对象、值全是字符串/数字/布尔。
    /// 键不存在时原样返回 —— 调用方可以「先删再插」，无论原来有没有都对。
    ///
    /// 为什么需要它：只做「替换」时，若替换没命中就会插入第二条同名键，
    /// 产出的 JSON 含重复键，不同解析器取值不一致（先到 / 后到）。
    /// 先删干净再插一条，结果唯一。
    /// </summary>
    static string RemoveJsonKey(string text, string key)
    {
        string needle = "\"" + key + "\"";
        int position = text.IndexOf(needle, StringComparison.Ordinal);
        if (position < 0) return text;

        int colon = text.IndexOf(':', position + needle.Length);
        if (colon < 0) return text;

        int valueStart = colon + 1;
        while (valueStart < text.Length && char.IsWhiteSpace(text[valueStart])) valueStart++;
        if (valueStart >= text.Length) return text;

        int valueEnd;
        if (text[valueStart] == '"')
        {
            valueEnd = text.IndexOf('"', valueStart + 1);
            if (valueEnd < 0) return text;
            valueEnd++;                       // 包含收尾引号
        }
        else
        {
            // 数字 / true / false / null：吃到第一个逗号或右花括号为止。
            valueEnd = valueStart;
            while (valueEnd < text.Length && text[valueEnd] != ',' && text[valueEnd] != '}')
                valueEnd++;
        }

        int removeStart = position;
        int removeEnd = valueEnd;

        // 吃掉紧随其后的逗号（如果有），避免留下 ",}" 这种残迹；
        // 若它是最后一个键，则反过来吃掉它前面的那个逗号。
        int after = removeEnd;
        while (after < text.Length && char.IsWhiteSpace(text[after])) after++;
        if (after < text.Length && text[after] == ',')
        {
            removeEnd = after + 1;
        }
        else
        {
            int before = removeStart - 1;
            while (before >= 0 && char.IsWhiteSpace(text[before])) before--;
            if (before >= 0 && text[before] == ',') removeStart = before;
        }

        return text.Substring(0, removeStart) + text.Substring(removeEnd);
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

        // 这里**不能**死等固定秒数再 Kill。
        //
        // 早先的写法是 Sleep(6000) → 没退出就 Kill → Sleep(800) → 去日志里找「共 N 个文件」。
        // 这条链上每一步都是赌：机器一忙、或者 200 个文件那一档稍慢一点，进程就会在写出那行日志
        // 之前被杀掉，于是日志里读到 0 个文件，报「没有收到文件」—— 而产品其实完全正常。
        // 多轮复核里那三次无法复现的失败，来源就在这里：它测的是「6 秒内能不能跑完」，
        // 不是「共享内存传参是否正确」。
        //
        // 改成按条件等待：每 200ms 看一眼日志，凑够期望值就往下走；
        // 进程自己退出了也往下走（该写的日志已经落盘）。只有真正等满超时才 Kill。
        int received = WaitForReceivedCount(p, logDir, logBefore, 20000);

        // 收尾：给进程一点时间体面退出，退不掉才动手。Kill 之后已有的日志不会丢
        // （产品每写一行就开关一次文件流），所以这里不影响上面读到的结果。
        if (!p.WaitForExit(3000))
        {
            try { p.Kill(); } catch { }
            try { p.WaitForExit(3000); } catch { }
        }

        int created = CountArchives(workDir) - before;

        Console.WriteLine("  [" + label + "] 日志收到 " + received + " 个, 新生成压缩包 " + created
                          + " 个" + (answered > 0 ? " (自动点了 " + answered + " 次确认)" : ""));

        if (expectedCount > 0)
        {
            if (received == 0) Bad(label + ": 没有收到文件");
            if (received != expectedCount) Bad(label + ": 收到的文件数不对, 期望 " + expectedCount);
        }
        return created;
    }

    /// <summary>
    /// 轮询日志，直到收到期望数量的条目、进程退出、或超时。
    /// 返回最后一次读到的数量（超时也返回实际值，供调用方如实报告）。
    /// </summary>
    static int WaitForReceivedCount(Process p, string logDir, Dictionary<string, long> before, int timeoutMs)
    {
        int waited = 0;
        int last = 0;

        while (waited < timeoutMs)
        {
            last = GetReceivedCount(logDir, before);

            // 凑够了就立刻继续：这是正常路径，不该再多等一秒。
            if (expectedCount > 0 && last >= expectedCount) return last;

            // 进程已经退出且日志不再变化：该写的都写完了，结论只能按当前值下。
            if (p.HasExited)
            {
                // 再给落盘留一点时间，然后读最后一次。
                Thread.Sleep(300);
                return GetReceivedCount(logDir, before);
            }

            Thread.Sleep(200);
            waited += 200;
        }

        return last;
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
        string sevenZip = Path.Combine(DataRoot(), "Auto7z", "7-Zip", "7z.exe");
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
