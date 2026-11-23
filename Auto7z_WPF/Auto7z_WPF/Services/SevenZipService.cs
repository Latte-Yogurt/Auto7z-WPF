using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Auto7z_WPF.Models;

namespace Auto7z_WPF.Services
{
    #region 命令结果模型
    /// <summary>
    /// 单次 7z.exe 调用的结局。**五态**，而不是一个 bool。
    ///
    /// 为什么需要这个枚举（2026-11-23 加）：
    /// 早先只有一个 <c>Succeeded</c>，它同时回答了两个不同的问题 ——
    /// 「7-Zip 跑完了吗」和「是不是完全成功」。退出码 1（有警告但产物已出）
    /// 让这两个答案分道扬镳：任务**完成**了，但不是**完全成功**。
    /// 一个 bool 装不下，于是靠调用方各自去看 <c>HasWarning</c> 补偿 ——
    /// 这种「字段 + 约定」的写法，漏看一处就会把部分成功报成完全成功。
    ///
    /// 现在把结论显式化。调用方读 <see cref="CommandResult.Outcome"/> 即可，
    /// 不需要自己去拼 <c>Succeeded</c> / <c>HasWarning</c> / <c>Cancelled</c> / <c>TimedOut</c>。
    /// </summary>
    public enum CommandOutcome
    {
        /// <summary>还没跑（初始值）。</summary>
        Unknown = 0,

        /// <summary>完全成功：退出码 0，无警告。</summary>
        Success = 1,

        /// <summary>
        /// 部分成功：退出码 1，7-Zip 报有警告但产物已经产出
        /// （典型情形是部分文件被跳过）。
        ///
        /// **这一态必须与 Success 分开**：界面上它应当着黄色、命令行退出码应当是 1。
        /// 把它并进 Success 就是「部分文件被跳过却看起来一切正常」。
        /// </summary>
        Partial = 2,

        /// <summary>完全失败：严重错误、命令行错误、内存不足等。</summary>
        Failed = 3,

        /// <summary>用户取消。</summary>
        Cancelled = 4,

        /// <summary>超时（进程未在预算内退出）。</summary>
        TimedOut = 5,
    }

    /// <summary>
    /// 单次 7z.exe 调用的结果快照，调用方据此区分成功、取消、超时与密码错误。
    /// </summary>
    public sealed class CommandResult
    {
        /// <summary>
        /// 7-Zip 进程**跑完了**且产物已产出（含退出码 1 的警告情形）。
        ///
        /// **注意语义**：它回答的是「有没有正常结束」，不是「是不是完全成功」。
        /// 判「完全成功」请用 <see cref="Outcome"/> == <see cref="CommandOutcome.Success"/>，
        /// 或 <see cref="HasWarning"/>。这个区分是刻意的：
        /// 退出码 1 时归档确实产出了，后续的校验文件生成等步骤应当继续执行，
        /// 只是最终结论要标成「部分成功」。
        /// </summary>
        public bool Succeeded { get; set; }

        public bool Cancelled { get; set; }
        public bool TimedOut { get; set; }

        public int ExitCode { get; set; } = -1;

        public string StandardOutput { get; set; } = string.Empty;
        public string StandardError { get; set; } = string.Empty;
        public Exception Error { get; set; }

        /// <summary>
        /// 输出是否**完整**收完。false 表示排空超时，`StandardOutput` / `StandardError`
        /// 可能是截断的。
        ///
        /// 为什么不能只看退出码：进程已经退出、退出码也拿到了，
        /// 但异步读取回调可能还没把管道里的数据全部交出来。
        /// 此时如果照常返回，调用方会拿一份**看起来正常但内容缺失**的输出，
        /// 而 `FindErrorLine` 之类的解析正是在这份输出上做的 —— 会得到错误结论。
        ///
        /// 2026-11-23 新增。原先 `outputDrained.Wait(...)` 的返回值被直接丢弃。
        /// </summary>
        public bool OutputTruncated { get; set; }

        /// <summary>
        /// 本次调用的结局。**这是调用方唯一需要读的字段。**
        ///
        /// 判据顺序有讲究：取消 → 超时 → 失败 → 部分 → 成功。
        /// 取消与超时优先于退出码，因为这两种情况下退出码没有意义
        /// （进程可能是被我们杀掉的，退出码取决于杀法）。
        /// </summary>
        public CommandOutcome Outcome
        {
            get
            {
                if (Cancelled) return CommandOutcome.Cancelled;
                if (TimedOut) return CommandOutcome.TimedOut;
                if (!Succeeded) return CommandOutcome.Failed;

                // 走到这里说明进程正常结束且产物已出，只剩「干净」与「带警告」之分。
                return ExitCode == 1 ? CommandOutcome.Partial : CommandOutcome.Success;
            }
        }

        // 退出码 1 是完成但带警告，归档已经产出，因此仍按成功处理。
        public bool HasWarning => ExitCode == 1;

        // 密码提示可能落在 stdout 也可能落在 stderr，两路都要查。
        public bool RequiresPassword =>
            StandardOutput.IndexOf("Enter password", StringComparison.OrdinalIgnoreCase) >= 0
            || StandardError.IndexOf("Enter password", StringComparison.OrdinalIgnoreCase) >= 0;

        public bool IsWrongPassword =>
            StandardOutput.IndexOf("Wrong password", StringComparison.OrdinalIgnoreCase) >= 0
            || StandardError.IndexOf("Wrong password", StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>
        /// 按异常、关键错误行、原始输出的优先级提取失败原因，供界面直接展示。
        /// </summary>
        /// <returns>失败描述；两路输出都为空时返回空字符串。</returns>
        public string GetFailureDetail()
        {
            if (Error != null) return Error.Message;

            // 先在 stderr 里找，7-Zip 的致命错误通常只写这一路。
            string errorLine = FindErrorLine(StandardError) ?? FindErrorLine(StandardOutput);
            if (!string.IsNullOrWhiteSpace(errorLine)) return errorLine;

            if (!string.IsNullOrWhiteSpace(StandardError)) return StandardError.Trim();
            if (!string.IsNullOrWhiteSpace(StandardOutput)) return StandardOutput.Trim();
            return string.Empty;
        }

        private static string FindErrorLine(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;

            foreach (var raw in text.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;

                if (line.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase)
                    || line.StartsWith("System ERROR", StringComparison.OrdinalIgnoreCase)
                    || line.IndexOf("Wrong password", StringComparison.OrdinalIgnoreCase) >= 0
                    || line.IndexOf("Data Error", StringComparison.OrdinalIgnoreCase) >= 0
                    || line.IndexOf("Cannot open", StringComparison.OrdinalIgnoreCase) >= 0
                    || line.IndexOf("Unexpected end of archive", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return line;
                }
            }

            return null;
        }
    }
    #endregion

    /// <summary>
    /// 7z.exe 的唯一调用入口，负责命令行拼装、进程驱动与输出收集。
    /// 实例不持有状态，进程跟踪表与资源长度缓存都是静态的。
    /// </summary>
    public class SevenZipService
    {
        #region 常量与进程跟踪
        private static readonly string _assemblyName = Assembly.GetExecutingAssembly().GetName().Name;

        // 组件路径（ComponentsDirectory / SevenZipExePath / SevenZipDllPath）统一定义在
        // 「组件部署与资源提取」region 里，此处不再重复计算 —— 见那里的说明。

        // 资源名必须与 csproj 里的逻辑路径一致，改名后提取只会静默失败。
        private const string ExeResourcePath = "Resources/Modules/7z.exe";
        private const string DllResourcePath = "Resources/Modules/7z.dll";

        // 用轮询而非阻塞等待，才能边等边响应取消与超时；杀进程后再给若干次轮询的宽限。
        private const int PollIntervalMs = 100;

        private const int KillGraceIterations = 50;

        // 关闭程序时等待 7z 真正退出的总预算。取 3 秒：正常被 Kill 的进程
        // 通常几十毫秒内就没了，3 秒足够覆盖杀毒/句柄回收造成的迟滞；
        // 再长就会让「关窗口」这个动作明显卡顿，那是更严重的体验问题。
        private const int KillWaitBudgetMs = 3000;

        // 单次命令收尾时等待 7z 退出的上限。比关闭路径短：
        // 这条路径每个命令都会走一遍，等太久会拖慢正常任务。
        private const int ExitWaitBudgetMs = 1500;

        private const int OutputDrainTimeoutMs = 2000;

        // ProcessorCount 至少按 1 算，否则 -mmt0 会被 7-Zip 解释成自动。
        private static readonly int _processorCount = Math.Max(1, Environment.ProcessorCount);

        // -bsp1 让进度走 stdout 而不是原地重绘，解析才有稳定输入；-sccUTF-8 固定字符集，避免中文路径乱码。
        private const string CommonSwitches = "-bsp1 -sccUTF-8";

        public static bool SupportsEncryption(ArchiveFormat format)
        {
            return format != ArchiveFormat.Tar;
        }

        // 只认行首百分比，错误行里的百分号不会被误当成进度。
        private static readonly Regex _percentRegex =
            new Regex(@"^\s*(\d{1,3})\s*%(?:\s+\d+)?\s*[+\-]\s", RegexOptions.Compiled);

        #endregion
        #region 进程与资源跟踪
        // 跟踪表让应用能在退出时清理子进程，否则 7z 会变成孤儿进程继续写归档。
        private static readonly List<Process> _runningProcesses = new List<Process>();
        private static readonly object _runningProcessesLock = new object();

        // 资源长度要读完整流才知道，缓存后避免每次启动校验都重读几十 MB。
        private static readonly Dictionary<string, long> _resourceLengths = new Dictionary<string, long>();
        private static readonly object _resourceLengthLock = new object();

        public void KillRunningProcesses()
        {
            KillAllRunningProcesses();
        }

        /// <summary>
        /// 终止所有仍在运行的 7z 进程，并**等待**它们真正退出。
        ///
        /// 审查报告 P1-3：原先这里只发 Kill 请求就立刻清空跟踪表。Kill() 是
        /// 「请求终止」，不等于进程已经没了 —— 程序随后退出时，短时间窗口内
        /// 仍可能有 7z.exe 在写盘。那种孤儿进程会占到文件句柄，让半成品目录
        /// 删不掉，用户看到的是「关掉了程序但文件被占用」。
        ///
        /// 现在的做法：
        ///   1. 对每个跟踪的进程发 Kill；
        ///   2. 逐个 WaitForExit，总等待时间受 <see cref="KillWaitBudgetMs"/> 限制；
        ///   3. 仍未确认退出的记一条警告日志（含 PID），但不阻塞退出流程；
        ///   4. 无论是否确认退出都 Dispose，释放句柄。
        ///
        /// 不无限等待是有意的：这是程序关闭路径，卡死比留一个孤儿更糟。
        /// </summary>
        public static void KillAllRunningProcesses()
        {
            List<Process> snapshot;
            lock (_runningProcessesLock)
            {
                snapshot = new List<Process>(_runningProcesses);
                _runningProcesses.Clear();
            }

            if (snapshot.Count == 0) return;

            // 先全部发 Kill，再统一等：串行「杀一个等一个」会让总时间随进程数累加。
            foreach (var p in snapshot)
            {
                try
                {
                    if (p != null && !p.HasExited) p.Kill();
                }
                catch
                {
                    // 进程可能已退出，或已被系统回收。
                }
            }

            var deadline = Environment.TickCount + KillWaitBudgetMs;
            var survivors = new List<int>();

            foreach (var p in snapshot)
            {
                if (p == null) continue;
                try
                {
                    int remaining = deadline - Environment.TickCount;
                    if (remaining < 0) remaining = 0;

                    // HasExited 为 true 时不进 WaitForExit，避免无谓等待。
                    if (!p.HasExited && !p.WaitForExit(remaining))
                    {
                        survivors.Add(p.Id);
                    }
                }
                catch
                {
                    // 拿不到 Id 或等待失败都按「无法确认」处理，但不加入 survivors，
                    // 因为这类异常通常意味着进程对象已经失效（进程确实没了）。
                }
            }

            if (survivors.Count > 0)
            {
                // 这里在静态上下文里跑，拿不到 LogService 实例，所以走 Trace ——
                // 与文件末尾 PeriodicTrace 的既有做法一致，LogService 挂的监听器
                // 若在就会接住，没挂也不会抛。
                try
                {
                    Trace.WriteLine(string.Format(
                        "[Auto7z] 退出时仍有 {0} 个 7z 进程未在 {1} 毫秒内确认退出：PID {2}",
                        survivors.Count, KillWaitBudgetMs,
                        string.Join(", ", survivors)));
                }
                catch
                {
                    // 日志不可用不能影响退出流程。
                }
            }

            // 释放句柄：不 Dispose 会让 Process 对象持有已退出进程的内核句柄，
            // 关闭路径上泄漏句柄没有意义。
            foreach (var p in snapshot)
            {
                try { p?.Dispose(); } catch { /* 释放失败无碍，进程即将随程序结束 */ }
            }
        }

        // 注册与注销都在后台回调线程里发生，必须加锁。
        private static void TrackProcess(Process process)
        {
            lock (_runningProcessesLock)
            {
                _runningProcesses.Add(process);
            }
        }

        private static void UntrackProcess(Process process)
        {
            lock (_runningProcessesLock)
            {
                _runningProcesses.Remove(process);
            }
        }
        #endregion

        #region 组件部署与资源提取
        // 组件目录的唯一出处：其余路径全部从它派生。
        //
        // 早先有三处各自计算同一个值（私有 ComponentsPath、公开 ComponentsDirectory、
        // 以及 SevenZipExePath 里又写一遍 Path.Combine(AppPaths.ComponentsDirectory, ...)）。
        // 那些写法当时算出来一样，但「同一个事实写三遍」意味着将来认定「组件该放别处」时
        // 要改三处，漏一处就出现「检查的路径」与「启动的路径」不是同一个 ——
        // 表现是明明文件在、程序却说找不到组件，或反过来。
        public static string ComponentsDirectory => AppPaths.ComponentsDirectory;

        /// <summary>7z.exe 的绝对路径。</summary>
        public static string SevenZipExePath => Path.Combine(ComponentsDirectory, "7z.exe");

        /// <summary>7z.dll 的绝对路径。</summary>
        public static string SevenZipDllPath => Path.Combine(ComponentsDirectory, "7z.dll");


        /// <summary>
        /// 只检查两个组件文件是否存在，不校验大小与可执行性。
        /// </summary>
        public bool CheckComponentsExist()
        {
            try
            {
                return File.Exists(SevenZipExePath) && File.Exists(SevenZipDllPath);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 确保 7z.exe 与 7z.dll 就位，缺失或长度不符时从嵌入资源重新释放。
        /// </summary>
        public bool EnsureComponents(out Exception error)
        {
            error = null;
            try
            {
                Directory.CreateDirectory(AppPaths.DataDirectory);
                Directory.CreateDirectory(ComponentsDirectory);

                ExtractResourceIfMissing(ExeResourcePath, SevenZipExePath);
                ExtractResourceIfMissing(DllResourcePath, SevenZipDllPath);

                return File.Exists(SevenZipExePath) && File.Exists(SevenZipDllPath);
            }
            catch (Exception ex)
            {
                error = ex;
                return false;
            }
        }

        #endregion

        #region 组件释放与健康检查

        private static void ExtractResourceIfMissing(string resourcePath, string outputPath)
        {
            if (IsComponentUsable(resourcePath, outputPath)) return;

            var resourceStream = GetResourceStream(resourcePath);
            if (resourceStream == null)
            {
                throw new FileNotFoundException(EnumText.GetErrorText("Err_ResourceMissing", "资源未找到: {0}", resourcePath));
            }

            // 不直接写目标文件：中途崩溃会留下半截的 7z.exe，下次既跑不起来又会被判为已存在。
            string tempPath = outputPath + ".tmp";
            using (Stream stream = resourceStream)
            using (FileStream fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.CopyTo(fileStream);
                // 落盘后再替换：否则断电或强制结束可能留下一个长度对但内容残缺的临时文件，
                // 而它一旦被替换成正式组件，下次启动会因长度不符重新解压，或更糟——直接拿去执行。
                fileStream.Flush(true);
            }

            // 用 File.Replace 而不是「先删旧、再移动」：
            // 旧写法在 Delete 成功、Move 失败之间留下一个**组件缺失**的窗口
            // （目标文件被占用、杀毒软件短暂锁定都会触发）。File.Replace 是一次原子替换，
            // 失败时旧文件原样保留，程序仍能跑；而先删后移失败就什么都没有了。
            //
            // 这里不用 File.Exists 判断「目标在不在」：它对同名目录占位、路径过长、
            // 权限不足一律返回 false。若真有东西占着这个路径，照它的答案去 Move
            // 会撞上 IOException，而错误信息只会说"文件已存在"，看不出真正原因。
            // 改用 GetAttributes 明确区分三态，把「占位但不是我们以为的文件」直接报清楚。
            FileAttributes existingAttributes;
            bool targetExists = true;
            try
            {
                existingAttributes = File.GetAttributes(outputPath);
            }
            catch (FileNotFoundException)
            {
                targetExists = false;
                existingAttributes = default(FileAttributes);
            }
            catch (DirectoryNotFoundException)
            {
                targetExists = false;
                existingAttributes = default(FileAttributes);
            }
            catch (Exception ex)
            {
                // 无法确认目标状态：不冒险覆盖，如实上报并保留临时文件供下次使用。
                throw new IOException(
                    EnumText.GetErrorText("Err_ComponentReplaceFailed",
                        "无法替换组件 {0}：旧文件已保留。请关闭正在使用它的程序后重试。", outputPath),
                    ex);
            }

            if (targetExists)
            {
                // 占位的是目录：File.Replace 与 Move 都会失败，且错误信息会误导。
                // 这是「组件位置被别的东西占了」的部署问题，必须原样报出来。
                if ((existingAttributes & FileAttributes.Directory) == FileAttributes.Directory)
                {
                    throw new IOException(
                        EnumText.GetErrorText("Err_ComponentPathOccupied",
                            "组件位置被同名目录占用，无法写入 {0}。请移走该目录后重试。", outputPath));
                }

                try
                {
                    File.Replace(tempPath, outputPath, null);
                    return;
                }
                catch (IOException ex)
                {
                    // 目标被占用等情况下 File.Replace 会失败。
                    //
                    // 这里**不再**退回「先删旧、再移动」：那会把刚消除的缺失窗口原样引回来
                    // —— 旧文件删掉、新文件移不过去时，组件就彻底没了。
                    // 正确的取舍是保留旧组件并报出明确错误：旧版本可能仍可用，
                    // 即使不可用，也比「什么都没有」更容易让下一次运行自愈。
                    throw new IOException(
                        EnumText.GetErrorText("Err_ComponentReplaceFailed",
                            "无法替换组件 {0}：旧文件已保留。请关闭正在使用它的程序后重试。", outputPath),
                        ex);
                }
                catch (UnauthorizedAccessException ex)
                {
                    // 只读或权限问题：同样保留旧文件，把原因如实抛给调用方。
                    throw new IOException(
                        EnumText.GetErrorText("Err_ComponentReplaceFailed",
                            "无法替换组件 {0}：旧文件已保留。请关闭正在使用它的程序后重试。", outputPath),
                        ex);
                }
            }

            // 走到这里说明目标本来就不存在（正常首次释放），
            // 不存在「先删旧」这一步，移动是安全的。
            try
            {
                File.Move(tempPath, outputPath);
            }
            finally
            {
                try
                {
                    if (File.Exists(tempPath)) File.Delete(tempPath);
                }
                catch
                {
                    // 临时文件残留不影响组件本身可用性，下次解压会覆盖同名临时文件。
                }
            }
        }

        #endregion

        #region 组件资源流读取

        /// <summary>
        /// 从程序集的 .g.resources 清单里取资源流，未命中或读取失败时返回 null。
        /// </summary>
        private static Stream GetResourceStream(string resourcePath)
        {
            try
            {
                var assembly = Assembly.GetExecutingAssembly();

                // 不走 pack:// URI：那套解析依赖 WPF 资源系统与 Application 的初始化时序，
                // 在没有 Application 的上下文里会抛异常而不是返回 null，直读清单则任何时机都能用。
                using (Stream listStream = assembly.GetManifestResourceStream(_assemblyName + ".g.resources"))
                {
                    if (listStream == null) return null;

                    using (var reader = new System.Resources.ResourceReader(listStream))
                    {
                        foreach (System.Collections.DictionaryEntry entry in reader)
                        {
                            // 清单里的条目名以小写形式存放，比较必须忽略大小写才能命中。
                            if (!(entry.Key is string name)) continue;
                            if (!string.Equals(name, resourcePath, StringComparison.OrdinalIgnoreCase)) continue;

                            // 在 using 之内 return 一个 Stream 看起来可疑：外层两个 using 会在
                            // 本方法返回时 Dispose，那个 Stream 是否还能读取决于 ResourceReader 的实现。
                            //
                            // 实测确认可用（tools/streamlife.cs 探针）：ResourceReader 对
                            // .g.resources 里的 Stream 条目返回的是**内存流**，生命周期不依赖
                            // 外层 listStream / reader。实测在 using 之外仍能读到完整的
                            // 557568 字节且首两字节是 PE 的 MZ 头。
                            //
                            // 这条依赖是 .NET Framework 的实现细节，没有文档承诺。所以调用方
                            // ExtractResourceIfMissing 必须**在 using(stream) 内**用完整份数据
                            // （它确实这么做了），不能把 Stream 存起来稍后再读。
                            return entry.Value as Stream;
                        }
                    }
                }
            }
            catch
            {
                return null;
            }

            return null;
        }

        private static bool IsComponentUsable(string resourcePath, string outputPath)
        {
            if (!File.Exists(outputPath)) return false;

            try
            {
                var fileInfo = new FileInfo(outputPath);
                if (fileInfo.Length <= 0) return false;

                long expected = GetEmbeddedResourceLength(resourcePath);
                return expected <= 0 || expected == fileInfo.Length;
            }
            // 探测失败时按可用处理：宁可沿用现有文件，也不要因一次读取异常就反复重写组件。
            //
            // 但这是一个**有意的保守选择**，代价是「无法确认」被当成「可用」：
            // 若组件其实已损坏，后续 7z 调用才会以别的方式失败，现场很难回溯到这里。
            // 因此留下一条日志 —— 排查时可以确认「当时确实跳过了组件健康检查」。
            catch (Exception ex)
            {
                ReportComponentProbeFailure(outputPath, ex);
                return true;
            }
        }

        /// <summary>
        /// 记录一次「组件健康检查无法完成」的事件。这里是静态上下文，
        /// 拿不到实例上的日志服务，所以退到 Trace —— 它会被 LogService 的监听器接住（若已挂载），
        /// 否则至少进 DebugView，不会彻底消失。
        /// </summary>
        private static void ReportComponentProbeFailure(string outputPath, Exception ex)
        {
            try
            {
                System.Diagnostics.Trace.TraceWarning(
                    "无法校验内置组件，按可用处理：" + outputPath + " —— " + ex.GetType().Name + ": " + ex.Message);
            }
            catch
            {
                // 记录日志本身失败不应影响解压流程。
            }
        }

        #endregion

        #region 组件资源长度查询

        private static long GetEmbeddedResourceLength(string resourcePath)
        {
            // 长度会被反复查询（每次启动都要判定组件是否可用），这里只算一次：
            // 嵌入资源在本进程生命周期内不会改变，重复读取 5 MB 资源纯属浪费。
            lock (_resourceLengthLock)
            {
                if (_resourceLengths.TryGetValue(resourcePath, out long cached)) return cached;
            }

            long length = -1;
            try
            {
                using (Stream stream = GetResourceStream(resourcePath))
                {
                    if (stream != null) length = stream.Length;
                }
            }
            catch
            {
                length = -1;
            }

            lock (_resourceLengthLock)
            {
                _resourceLengths[resourcePath] = length;
            }

            return length;
        }
        #endregion

        #region 压缩命令拼装

        /// <summary>
        /// 生成一条或两条压缩命令，返回多条时调用方必须按顺序执行。
        /// </summary>
        public List<string> BuildCompressCommands(string[] sourcePaths, AppConfig config, string outputArchivePath)
        {
            var commands = new List<string>();
            if (sourcePaths == null || sourcePaths.Length == 0) return commands;
            if (string.IsNullOrWhiteSpace(outputArchivePath)) return commands;
            if (config == null) return commands;

            // 先转成绝对路径：7z.exe 的工作目录与本进程不同，相对路径会解析到意料之外的位置。
            var quotedSources = sourcePaths
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => Quote(Path.GetFullPath(p)))
                .ToList();
            if (quotedSources.Count == 0) return commands;

            // 7-Zip 无法一步产出 .tar.zst，只能先打 tar 再压 zstd，因此这里返回两条命令。
            if (config.Format == ArchiveFormat.Tar && config.Zstd)
            {
                string tarPath = BuildIntermediateTarPath(outputArchivePath);
                // 中间 tar 不套通用开关：密码、分卷、字典只对最终容器有意义，
                // 加到中间文件上要么被忽略，要么直接让第一步失败。
                commands.Add(BuildSingleCompressCommand("tar", quotedSources, tarPath, config, applyGenericSwitches: false));
                commands.Add(BuildZstdStepCommand(tarPath, outputArchivePath));
                return commands;
            }

            string formatValue = ArchiveFormatMap.ToSwitchValue(config.Format);
            commands.Add(BuildSingleCompressCommand(formatValue, quotedSources, outputArchivePath, config, applyGenericSwitches: true));
            return commands;
        }

        public static string BuildIntermediateTarPath(string outputArchivePath)
        {
            if (string.IsNullOrWhiteSpace(outputArchivePath)) return outputArchivePath;

            // 先剥掉 .zst 再交给 ChangeExtension，否则 x.tar.zst 会被换成 x.tar.tar。
            if (outputArchivePath.EndsWith(".tar.zst", StringComparison.OrdinalIgnoreCase))
            {
                return outputArchivePath.Substring(0, outputArchivePath.Length - ".zst".Length);
            }

            return Path.ChangeExtension(outputArchivePath, ".tar");
        }

        private string BuildSingleCompressCommand(string formatValue,
            List<string> quotedSources, string outputArchivePath, AppConfig config,
            bool applyGenericSwitches)
        {
            var sb = new StringBuilder();
            sb.AppendFormat("a -aoa -y {0} -t{1} {2}", CommonSwitches, formatValue, Quote(outputArchivePath));

            foreach (var source in quotedSources)
            {
                sb.Append(' ').Append(source);
            }

            if (!applyGenericSwitches) return sb.ToString();

            // 空密码等同未设置密码；tar 不支持加密，两种情况都不应下发 -p。
            bool hasPassword = !string.IsNullOrEmpty(config.Password);
            if (hasPassword && SupportsEncryption(config.Format))
            {
                // 已知取舍：密码以 `-p<明文>` 形式出现在 7z.exe 的命令行里，
                // 同机器上的其他进程（任务管理器、WMI、Process Explorer）能看到它。
                //
                // 这不是"忘了做安全"，而是 7-Zip 命令行接口的现实：它支持从 stdin
                // 读密码（收到 "Enter password:" 提示时写入），但那条路径要求边读输出
                // 边喂输入、且不同版本提示时机不一致，实测在批量任务里很脆弱 ——
                // 失败时会变成"密码没传进去，压缩不带加密地完成了"，那是更坏的结果
                // （用户以为加了密，实际没有）。宁可接受"本机可见"，也不要冒"静默未加密"。
                //
                // 若将来要改：正确的方向是用 7-Zip 的 -p 配合进程创建时清空命令行，
                // 或改用 7z.dll 的 COM/原生接口（本项目已随包 7z.dll）。
                sb.AppendFormat(" -p{0}", Quote(config.Password));
            }

            // -mhe=on 加密的是文件头，没有密码时下发它没有意义。
            if (config.Format == ArchiveFormat.SevenZip)
            {
                if (config.IsEncFileName && hasPassword)
                {
                    sb.Append(" -mhe=on");
                }

                // -ms 是 7z 独有的固实块开关，其他格式没有这个概念。
                sb.Append(config.IsSolidMode ? " -ms=on" : " -ms=off");
            }

            sb.AppendFormat(" -mx={0}", GetCompressionLevelValue(config));

            AppendRuntimeModeSwitches(sb, config);

            AppendVolumeSwitch(sb, config);

            return sb.ToString();
        }

        private string BuildZstdStepCommand(string tarPath, string outputArchivePath)
        {
            return string.Format("a -aoa -y {0} -tzstd {1} {2}", CommonSwitches, Quote(outputArchivePath), Quote(tarPath));
        }


        private static int GetCompressionLevelValue(AppConfig config)
        {
            switch (config.CompressionLevel)
            {
                case CompressionLevelPreset.Store: return 0;
                case CompressionLevelPreset.Ultra: return 9;
                case CompressionLevelPreset.Normal:
                default: return 5;
            }
        }

        private static void AppendRuntimeModeSwitches(StringBuilder sb, AppConfig config)
        {
            sb.AppendFormat(" -mmt{0}", GetThreadCount(config.RuntimeMode));

            // -md 依赖 LZMA 系编码器，只有 7z 容器接受，其他格式下发会被拒绝，所以这里提前返回。
            if (config.Format != ArchiveFormat.SevenZip) return;

            AppendDictionarySwitch(sb, config);
        }

        private static int GetThreadCount(RuntimeModePreset mode)
        {
            switch (mode)
            {
                case RuntimeModePreset.Low: return 1;
                case RuntimeModePreset.High: return _processorCount;
                // 默认模式取一半核心但至少留 2 个，否则双核以下会退化成单线程。
                default: return Math.Max(2, _processorCount / 2);
            }
        }

        private static void AppendDictionarySwitch(StringBuilder sb, AppConfig config)
        {
            int level = GetCompressionLevelValue(config);
            // 存储级别走 Copy 编码器，没有字典概念，此时下发 -md 只会被忽略或报参数错误。
            if (level == 0) return;

            // 只有极限级别才把字典提到高档，否则内存占用会随字典大小线性膨胀。
            bool maximum = level >= 9;
            string dictionary;
            switch (config.RuntimeMode)
            {
                case RuntimeModePreset.Low: dictionary = maximum ? "16m" : "4m"; break;
                case RuntimeModePreset.High: dictionary = maximum ? "64m" : "32m"; break;
                default: dictionary = maximum ? "32m" : "16m"; break;
            }

            sb.Append(" -md").Append(dictionary);
        }
        #endregion

        #region 分卷判定
        /// <summary>分卷大小（配置文本）的解析结果。</summary>
        public enum VolumeSpecState
        {
            /// <summary>未填，或格式本身不支持分卷（tar）。静默不分卷，不是配置问题。</summary>
            Disabled = 0,

            /// <summary>显式填了 0：这是文档约定的「禁用分卷」，合法配置，值得留一条 Info 痕迹。</summary>
            ExplicitlyOff = 1,

            /// <summary>合法且应当尝试分卷，字节值有效。</summary>
            Valid = 2,

            /// <summary>配置值非法：不是纯数字、超出 long、或换算成字节后溢出。按不分卷处理。</summary>
            Invalid = 3
        }

        // 分卷大小以 MB 计，换算成字节必须落在 long 里：再大就溢出成负数，
        // 让"体积没超过分卷就不分卷"这条守卫恒不成立。
        private const long MaxVolumeMegabytes = long.MaxValue / (1024L * 1024L);

        /// <summary>
        /// 解析分卷大小并算出最终字节值。纯函数：不写日志、不改状态，
        /// 供界面判定、体积预估与命令拼装共用同一套规则。
        ///
        /// 判据顺序即"先算出最终值，再判断这个值是否合法"：
        /// 1) 只接受**纯数字**（不接受正负号、千分位、小数、空白以外的任何字符）；
        /// 2) 数字能装进 long；
        /// 3) 0 是合法的「禁用分卷」，单独报 <see cref="VolumeSpecState.ExplicitlyOff"/>；
        /// 4) 换算成字节后不溢出 long —— 这一步等价于"算出来放不下就是不合法"。
        /// </summary>
        /// <param name="volumeBytes">Valid 时给出最终的字节数，其余情况为 0。</param>
        public static VolumeSpecState ResolveVolumeSpec(ArchiveFormat format, string volume, out long volumeBytes)
        {
            volumeBytes = 0;

            // tar 是连续字节流，切分后失去可解析结构：无论填什么都按不分卷处理，这不是配置错误。
            if (format == ArchiveFormat.Tar) return VolumeSpecState.Disabled;

            // 未填与"填 0"是两回事：前者是没配置，后者是用户明确关掉了分卷。分开报，日志才有信息量。
            if (string.IsNullOrWhiteSpace(volume)) return VolumeSpecState.Disabled;

            string text = volume.Trim();

            // 仅数字：逐字符判定，避免 NumberStyles 的宽松模式放过 "+5"、"1,000"、"5.5" 之类。
            if (!IsAllDigits(text)) return VolumeSpecState.Invalid;

            if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out long volumeMb))
            {
                // 全是数字但超出 long 能表示的十进制范围。
                return VolumeSpecState.Invalid;
            }

            // 0 在文档里就是「禁用分卷」的开关值，属合法配置。
            if (volumeMb == 0) return VolumeSpecState.ExplicitlyOff;

            // 先判上界再相乘：这一步就是"算出最终字节值，发现放不下 → 不合法"。
            if (volumeMb > MaxVolumeMegabytes) return VolumeSpecState.Invalid;

            volumeBytes = volumeMb * 1024L * 1024L;
            return VolumeSpecState.Valid;
        }

        private static bool IsAllDigits(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;

            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] < '0' || text[i] > '9') return false;
            }

            return true;
        }

        /// <summary>
        /// 仅在归档体积确实超过分卷大小时才追加 -v，避免产出无意义的单卷。
        /// 非 Valid 一律不分卷；留痕由任务入口统一处理，这里保持纯函数不写日志。
        /// </summary>
        private static void AppendVolumeSwitch(StringBuilder sb, AppConfig config)
        {
            if (ResolveVolumeSpec(config.Format, config.Volume, out long volumeBytes) != VolumeSpecState.Valid)
            {
                return;
            }

            // 总大小未知时分卷数量无法预估，按不分卷处理，界面上的分卷开关也据此禁用。
            if (config.TotalSize <= 0) return;

            if (config.TotalSize <= volumeBytes) return;

            sb.AppendFormat(" -v{0}m", volumeBytes / (1024L * 1024L));
        }
        #endregion


        #region 解压命令与路径识别
        /// <summary>
        /// 拼装解压命令，目标目录不存在时由 7-Zip 自行创建。
        /// </summary>
        public string BuildExtractCommand(string archivePath, string destinationDirectory, AppConfig config)
        {
            if (string.IsNullOrWhiteSpace(archivePath) || string.IsNullOrWhiteSpace(destinationDirectory))
                return null;

            var sb = new StringBuilder();
            sb.AppendFormat("x -aoa -y {0} -o{1} {2}", CommonSwitches, Quote(destinationDirectory), Quote(archivePath));

            // tar 不接受 -p，即使配置里存有密码也不能下发，否则整条解压命令会失败。
            if (config != null && !string.IsNullOrEmpty(config.Password) && SupportsEncryption(config.Format))
            {
                sb.AppendFormat(" -p{0}", Quote(config.Password));
            }

            return sb.ToString();
        }

        /// <summary>
        /// 把任意一卷换回它的逻辑名（x.7z.001 / x.7z.002 → x.7z）。
        ///
        /// 分卷有两种叫法：逻辑名（x.7z）与卷名（x.7z.001）。7-Zip 只接受卷名，
        /// 而用户在界面上看到的、拖进来的往往是逻辑名，两边必须能互相换算。
        /// 不是卷名形式的路径原样返回。
        /// </summary>
        public static string GetLogicalArchiveName(string archivePath)
        {
            if (string.IsNullOrWhiteSpace(archivePath)) return archivePath;

            // 只认纯数字后缀，且至少三位：x.bin.001 是分卷，x.v1.7 不是。
            int dot = archivePath.LastIndexOf('.');
            if (dot <= 0 || dot == archivePath.Length - 1) return archivePath;

            string suffix = archivePath.Substring(dot + 1);
            if (suffix.Length < 3) return archivePath;

            foreach (char ch in suffix)
            {
                if (ch < '0' || ch > '9') return archivePath;
            }

            return archivePath.Substring(0, dot);
        }

        /// <summary>
        /// 判断是否为分卷，既认 .001，也认任意卷号（.002 起）与逻辑名落地的情形。
        ///
        /// 中间卷与末卷同样是分卷的一部分：用户从资源管理器里多选时，
        /// 送进来的可能压根不是首卷，只认 .001 会把它们当成普通文件。
        /// </summary>
        public static bool IsSplitArchive(string archivePath)
        {
            if (string.IsNullOrWhiteSpace(archivePath)) return false;

            string logical = GetLogicalArchiveName(archivePath);
            if (!string.Equals(logical, archivePath, StringComparison.OrdinalIgnoreCase))
            {
                // 已经是一卷的卷名：只要首卷还在，这套分卷就成立。
                return File.Exists(logical + ".001");
            }

            // 界面上选中的可能是逻辑名 x.7z，而磁盘上只有 x.7z.001，必须回退探测。
            return !File.Exists(archivePath) && File.Exists(archivePath + ".001");
        }

        /// <summary>
        /// 取这套分卷的首卷：7-Zip 只认 .001，给它中间卷会直接失败。
        ///
        /// 无论进来的是逻辑名还是任意一卷，都统一指回首卷；
        /// 首卷不在时原样返回，让下游按「文件不存在」报错，而不是解出一个空目录。
        /// </summary>
        public static string ResolveFirstVolumePath(string archivePath)
        {
            if (string.IsNullOrWhiteSpace(archivePath)) return archivePath;

            string logical = GetLogicalArchiveName(archivePath);
            string firstVolume = logical + ".001";

            if (File.Exists(firstVolume)) return firstVolume;

            // 首卷不在时不猜：原样返回，由调用方报「压缩文件不存在」。
            return archivePath;
        }

        #endregion
        #region 压缩 tar 判定
        /// <summary>
        /// 判断是否为压缩过的 tar，这类文件是两层容器，不能靠单一 -t 硬指定格式。
        /// </summary>
        public static bool IsCompressedTar(string archivePath)
        {
            if (string.IsNullOrWhiteSpace(archivePath)) return false;

            // 长后缀与短后缀是同一种格式的两种写法，两套都要认。
            string name = Path.GetFileName(archivePath).ToLowerInvariant();
            return name.EndsWith(".tar.zst", StringComparison.Ordinal)
                || name.EndsWith(".tar.gz", StringComparison.Ordinal)
                || name.EndsWith(".tar.bz2", StringComparison.Ordinal)
                || name.EndsWith(".tar.xz", StringComparison.Ordinal)
                || name.EndsWith(".tzst", StringComparison.Ordinal)
                || name.EndsWith(".tgz", StringComparison.Ordinal)
                || name.EndsWith(".tbz2", StringComparison.Ordinal)
                || name.EndsWith(".txz", StringComparison.Ordinal);
        }

        /// <summary>
        /// 把「一套分卷」归并成一条输入：同一逻辑名下的多卷只留首卷。
        ///
        /// 自动模式一次拿到的常常是整套分卷（资源管理器多选）。逐条判断的话，
        /// 只有首卷认得出是压缩包，其余卷会掉进压缩分支被重新压一遍 ——
        /// 既解不开原包，又多出一堆垃圾产物。所以分卷必须按「套」聚合。
        ///
        /// 顺序无关：无论送进来的第一项是 .001 还是 .007，结果都是首卷。
        /// 同一套出现多次时只保留第一次出现的位置，其余丢弃。
        /// </summary>
        public static List<string> CollapseSplitVolumes(IEnumerable<string> paths)
        {
            var result = new List<string>();
            if (paths == null) return result;

            // 已归并的逻辑名。分大小写不敏感比较：Windows 上 x.7z 与 X.7Z 是同一个文件。
            var seenSets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string path in paths)
            {
                if (string.IsNullOrWhiteSpace(path)) continue;

                string logical = GetLogicalArchiveName(path);
                if (!string.Equals(logical, path, StringComparison.OrdinalIgnoreCase))
                {
                    // 是某一卷：整套只收首卷一次。
                    if (seenSets.Add(logical))
                    {
                        result.Add(ResolveFirstVolumePath(path));
                    }
                    continue;
                }

                // 逻辑名 + 磁盘上只有 .001 落地：同样按分卷处理，补成首卷。
                if (!File.Exists(path) && File.Exists(path + ".001"))
                {
                    if (seenSets.Add(path))
                    {
                        result.Add(ResolveFirstVolumePath(path));
                    }
                    continue;
                }

                result.Add(path);
            }

            return result;
        }

        #endregion
        #region 命令行参数转义
        private static string Quote(string value)
        {
            // 空值也产出 ""，否则命令行里会少一个位置参数，后面的开关全部错位。
            if (string.IsNullOrEmpty(value)) return "\"\"";

            var sb = new StringBuilder(value.Length + 2);
            sb.Append('"');
            // 反斜杠必须成对翻倍：以反斜杠结尾的目录路径会把后面的引号转义掉，
            // 参数边界一旦消失，紧随其后的开关就会被吞进路径里。
            for (int i = 0; i < value.Length; i++)
            {
                char ch = value[i];
                int backslashCount = 0;
                while (i < value.Length && value[i] == '\\')
                {
                    backslashCount++;
                    i++;
                }

                if (i == value.Length)
                {
                    sb.Append('\\', backslashCount * 2);
                    break;
                }

                if (value[i] == '"')
                {
                    sb.Append('\\', backslashCount * 2 + 1);
                    sb.Append('"');
                }
                else
                {
                    sb.Append('\\', backslashCount);
                    sb.Append(value[i]);
                }
            }
            sb.Append('"');
            return sb.ToString();
        }
        #endregion


        #region 进程启动与输出排空
        /// <summary>
        /// 执行一次 7z.exe 并收集双路输出，超时或取消都通过杀死进程结束。
        /// </summary>
        /// <param name="arguments">已拼装好的命令行参数。</param>
        /// <param name="progress">进度回调，可为 null。</param>
        /// <param name="cancellationToken">取消令牌。</param>
        /// <param name="timeout">超时时长，为 null 表示不限时。</param>
        /// <returns>本次调用的结果。</returns>
        public CommandResult ExecuteCommand(string arguments, Action<int> progress, CancellationToken cancellationToken, TimeSpan? timeout = null)
        {
            var result = new CommandResult();

            if (string.IsNullOrWhiteSpace(arguments))
            {
                result.Error = new ArgumentException(EnumText.GetErrorText("Err_EmptyCommand", "命令行为空"));
                return result;
            }

            if (!File.Exists(SevenZipExePath))
            {
                result.Error = new FileNotFoundException(EnumText.GetErrorText("Err_SevenZipExeMissing", "未找到 7z.exe：{0}", SevenZipExePath));
                return result;
            }

            Process process = new Process();
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            var stdoutLock = new object();
            var stderrLock = new object();
            int lastPercent = -1;

            // 单路结束不代表输出收完，必须两路都收到结束标记才算排空。
            var outputDrained = new ManualResetEventSlim(false);
            int drainedStreams = 0;

            // 回调是否**全部退出**的标志。与「是否收到两路结束信号」是两件事：
            //
            //   · `drainedStreams` 在回调**收到 null** 时递增；
            //   · `callbackExited` 在回调**即将返回**时递减（从 2 开始）。
            //
            // 排空超时时 `drainedStreams` 可能没到 2，说明还有回调没收到结束信号；
            // 此时那个回调可能正卡在 `outputDrained.Set()` 前面 —— 主线程若已 Dispose，
            // 它就会抛 ObjectDisposedException。
            //
            // 早先的代码在注释里承认了这个竞态，然后依赖「它发生在后台线程、可以忽略」。
            // 那不是解决，是赌。现在显式同步（BUG-06 的修复）：
            // **只有两个回调都确认退出，才 Dispose 事件。** 否则留给 GC。
            int callbackExited = 2;

            // e.Data == null 是异步读取端唯一可靠的流已读完信号。只 WaitForExit 不够：
            // 带超时的 WaitForExit 不保证回调已经跑完，缺了这个标记，输出会被截断。
            Action markStreamEnd = () =>
            {
                if (Interlocked.Increment(ref drainedStreams) >= 2) outputDrained.Set();
            };

            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = SevenZipExePath,
                    Arguments = arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    RedirectStandardInput = true,
                    StandardOutputEncoding = new UTF8Encoding(false),
                    StandardErrorEncoding = new UTF8Encoding(false)
                };

                process.StartInfo = startInfo;
                process.Start();
                TrackProcess(process);

                // 立刻关闭 stdin：7z 只在需要输入时才读它，保持打开会让进程停在等待输入而不退出。
                try { process.StandardInput.Close(); } catch { /* 有意忽略：进程可能已自行退出，关不上不影响读取输出 */ }

                process.OutputDataReceived += (sender, e) =>
                {
                    // 无论走哪条 return，回调退出时都要记账（见 callbackExited 的说明）。
                    try
                    {
                        if (e.Data == null)
                        {
                            markStreamEnd();
                            return;
                        }

                        lock (stdoutLock)
                        {
                            stdout.AppendLine(e.Data);
                        }

                        if (progress == null) return;

                        // 一行里可能塞了多个用回车分隔的进度片段，先拆开再逐段匹配。
                        foreach (string chunk in e.Data.Split('\r'))
                        {
                            // 只在百分比真正变化时上报，避免同一个数字反复刷新界面。
                            var match = _percentRegex.Match(chunk);
                            if (!match.Success) continue;

                            if (int.TryParse(match.Groups[1].Value, out int percent) && percent != lastPercent)
                            {
                                lastPercent = percent;
                                progress(percent);
                            }
                        }
                    }
                    finally
                    {
                        Interlocked.Decrement(ref callbackExited);
                    }
                };

                process.ErrorDataReceived += (sender, e) =>
                {
                    // 同上：回调退出时记账。
                    try
                    {
                        if (e.Data == null)
                        {
                            markStreamEnd();
                            return;
                        }

                        lock (stderrLock)
                        {
                            stderr.AppendLine(e.Data);
                        }
                    }
                    finally
                    {
                        Interlocked.Decrement(ref callbackExited);
                    }
                };

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                // 等待与收尾下沉到 WaitForProcess，让取消、超时、异常走同一条收尾路径。
                WaitForProcess(process, result, cancellationToken, timeout, outputDrained);
            }
            catch (Exception ex)
            {
                result.Error = ex;
                result.Succeeded = false;
            }
            // 无论成败都要取输出、注销跟踪并释放句柄，否则句柄会随每次调用累积。
            finally
            {
                lock (stdoutLock)
                {
                    result.StandardOutput = stdout.ToString().Trim();
                }
                lock (stderrLock)
                {
                    result.StandardError = stderr.ToString().Trim();
                }

                UntrackProcess(process);

                // 收尾时必须先确认进程真的退出了，再释放句柄。
                //
                // 早先这里只发 Kill 就 Dispose。Kill 是「请求终止」，进程可能
                // 还在退出过程中；Dispose 之后我们就再也没法观察它，调用方
                // 会看到「任务结束了但 7z.exe 还在」。审查报告 P1-3 说的正是
                // 这个 ——— 关闭路径上等待，取消路径上同样要等待。
                //
                // 注意：KillAllRunningProcesses 可能已经在别的线程上等过它，
                // 此时 HasExited 为真，下面的分支直接跳过，不会重复等待。
                try
                {
                    if (!process.HasExited && !process.WaitForExit(ExitWaitBudgetMs))
                    {
                        // 仍在退出中：再发一次 Kill（可能上一次被系统拒绝），
                        // 但不无限等 —— 这是收尾路径，卡住比留残余更糟。
                        // Kill 抛异常只可能是进程刚好自己退出了（杀不存在的进程会抛），
                        // 那正是我们想要的结果，因此忽略即可。
                        try { process.Kill(); } catch { /* 进程已自行退出，无需处理 */ }

                        // **Kill 只是发出终止请求，不等于进程已经退出。**
                        //
                        // 早先这里发完 Kill 就往下走，紧接着 Dispose ——
                        // 之后再也没法观察它，调用方会看到「任务结束了但 7z.exe 还在」。
                        // 现在补一次有界等待；仍不退出的，把情况记进 result.Error，
                        // 而不是把 Dispose 当成进程终止的替代。
                        if (!process.WaitForExit(KillWaitBudgetMs))
                        {
                            result.Error = new TimeoutException(EnumText.GetErrorText(
                                "Err_ProcessStillAlive",
                                "7-Zip 进程在强制终止后仍未退出（可能残留在系统中）：PID {0}",
                                SafeGetProcessId(process)));
                        }
                    }
                }
                catch
                {
                    // 进程对象可能已失效（进程确实没了），无需处理。
                }

                try { process.Dispose(); } catch { /* 有意忽略：句柄可能已被回收，重复释放失败可忽略 */ }

                // ManualResetEventSlim 在 Wait() 时可能创建底层内核事件句柄。
                // 频繁压缩/解压时若不释放，这些同步资源会逐渐累积。
                //
                // **但不能无条件 Dispose（BUG-06 的修复）。**
                //
                // 排空超时时 `drained == false`，意味着至少有一路回调
                // 还没收到结束信号 —— 它可能正卡在 `outputDrained.Set()` 前面。
                // 此时若主线程 Dispose，那个回调就会抛 ObjectDisposedException。
                // 早先的注释承认了这个竞态，然后依赖「它发生在后台线程、可以忽略」——
                // 那是赌，不是解决。
                //
                // 现在用 `callbackExited` 显式同步：只有两个回调都确认退出才 Dispose。
                // 还有回调在跑时**不释放**，让它随 GC 回收 —— 泄漏一个事件对象
                // 远好过在别人正在用的对象上拔电源。
                if (Volatile.Read(ref callbackExited) <= 0)
                {
                    try { outputDrained.Dispose(); } catch { /* 已释放或状态异常，忽略 */ }
                }
                else
                {
                    // 有回调尚未退出：交给 GC。这里刻意不记日志 ——
                    // 它发生在「输出排空超时」这一条已经上报过的路径上，
                    // 再记一条只会让日志变吵而不增加信息。
                }
            }

            return result;
        }
        #endregion


        #region 进程等待与收尾
        /// <summary>
        /// 轮询等待进程退出，期间响应取消与超时，退出后再等输出排空。
        /// </summary>
        private void WaitForProcess(Process process, CommandResult result,
            CancellationToken cancellationToken, TimeSpan? timeout,
            ManualResetEventSlim outputDrained)
        {
            var startedAt = DateTime.UtcNow;
            bool killIssued = false;
            int iterationsAfterKill = 0;

            // 用带超时的轮询而不是阻塞 WaitForExit，否则等待期间既响应不了取消也判断不了超时。
            while (!process.WaitForExit(PollIntervalMs))
            {
                if (cancellationToken.IsCancellationRequested && !killIssued)
                {
                    killIssued = true;
                    result.Cancelled = true;
                    TryKill(process);
                }

                if (!killIssued && timeout.HasValue && DateTime.UtcNow - startedAt > timeout.Value)
                {
                    killIssued = true;
                    result.TimedOut = true;
                    TryKill(process);
                }

                // 杀掉后仍会有短暂残留，超过宽限次数说明强杀没生效，只能带着错误返回。
                if (killIssued && ++iterationsAfterKill > KillGraceIterations)
                {
                    result.Error = new TimeoutException(EnumText.GetErrorText("Err_ProcessNotExited", "7-Zip 进程未能在取消后退出"));
                    break;
                }
            }

            // 排空必须放在确认进程退出之后，否则等的是一个永远不会到来的结束标记；
            // 放在 finally 释放进程对象之前，则是为了避免信号量被提前回收。
            if (HasExited(process))
            {
                bool drained = false;
                try
                {
                    // **必须检查返回值。** 早先这一行是 `outputDrained.Wait(...)`，
                    // 丢弃了 bool。超时后仍然继续执行，把可能**被截断**的输出
                    // 当成完整结果返回 —— 而 FindErrorLine / RequiresPassword /
                    // IsWrongPassword 全都在这份输出上做判断，截断会让它们得出错误结论。
                    drained = outputDrained.Wait(OutputDrainTimeoutMs);
                }
                // 信号量已被释放时忽略即可，此时输出已经收完，不必把收尾动作变成异常。
                catch (ObjectDisposedException)
                {
                    // 事件已被释放（正常收尾），视为排空完成
                    drained = true;
                }

                if (!drained)
                {
                    // 明确标记为「输出可能不完整」，而不是静默当成功。
                    // 调用方据此决定是否采信输出里的解析结果。
                    //
                    // 本类不持有日志依赖（它是纯执行层），所以把情况记在 result 上，
                    // 由 ViewModel 决定怎么上报。
                    result.OutputTruncated = true;
                }
            }

            // 退出码取不到时是 -1，由下面的成功判定统一归为失败。
            result.ExitCode = SafeGetExitCode(process);

            result.Succeeded = !result.Cancelled
                               && !result.TimedOut
                               && (result.ExitCode == 0 || result.ExitCode == 1);
        }


        private static bool HasExited(Process process)
        {
            try
            {
                return process.HasExited;
            }
            catch
            {
                return true;
            }
        }

        private static int SafeGetExitCode(Process process)
        {
            try
            {
                return process.HasExited ? process.ExitCode : -1;
            }
            catch
            {
                return -1;
            }
        }

        /// <summary>
        /// 去掉路径末尾的分隔符，但**保留根目录自身的那个**。
        ///
        /// `C:\`        → `C:\`   （盘符根不能变成 `C:`，那是「当前目录在 C 盘」的意思）
        /// `C:\a\`      → `C:\a`
        /// `\\srv\share\` → `\\srv\share`
        /// </summary>
        /// <param name="path">已规范化的绝对路径。</param>
        /// <returns>去掉末尾多余分隔符的路径。</returns>
        private static string TrimTrailingSeparators(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;

            string trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (trimmed.Length == 0) return path;

            // `C:` 这种形态要补回分隔符，否则语义从「C 盘根」变成「C 盘当前目录」。
            if (trimmed.Length == 2 && trimmed[1] == ':') return trimmed + Path.DirectorySeparatorChar;

            return trimmed;
        }

        /// <summary>
        /// <paramref name="candidate"/> 是否位于 <paramref name="root"/> **内部**（不含等于）。
        ///
        /// 这是全类唯一的路径包含判断，处理了这几类边界：
        ///   · 磁盘根目录（`C:\`）—— 早先 `root + 分隔符` 会拼出 `C:\\`，判定失效
        ///   · UNC 共享根（`\\server\share`）
        ///   · 末尾多余分隔符
        ///   · 大小写差异
        ///   · 前缀相同但不同层级（`C:\a` 与 `C:\ab` 不算包含）
        ///
        /// 调用方须先各自 `Path.GetFullPath`。
        /// </summary>
        /// <param name="candidate">待判断的路径。</param>
        /// <param name="root">容器路径。</param>
        /// <returns>candidate 在 root 内部时返回 true。</returns>
        private static bool IsPathInside(string candidate, string root)
        {
            if (string.IsNullOrEmpty(candidate) || string.IsNullOrEmpty(root)) return false;

            string c = TrimTrailingSeparators(candidate);
            string r = TrimTrailingSeparators(root);

            // 相等不算「在内部」。
            if (string.Equals(c, r, StringComparison.OrdinalIgnoreCase)) return false;

            // 两边都统一到「无末尾分隔符 + 一个分隔符」的形式再比前缀。
            // 关键点：先把 r 的末尾分隔符规整成恰好一个，再拼到 c 的比较串里。
            string prefix = r.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                ? r
                : r + Path.DirectorySeparatorChar;

            return c.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>取进程 PID，失败时返回 -1。只用于日志与错误信息。</summary>
        private static int SafeGetProcessId(Process process)
        {
            try
            {
                return process.Id;
            }
            catch
            {
                return -1;
            }
        }

        private static void TryKill(Process process)
        {
            try
            {
                if (!process.HasExited) process.Kill();
            }
            catch
            {
                // 进程可能已退出
            }
        }
        #endregion


        #region 输出目录准备
        /// <summary>
        /// 准备压缩产物的落地目录，必要时把落在源目录内部的候选路径退到父目录。
        /// </summary>
        /// <param name="items">本次要压缩的全部输入项。混合选择文件与目录时必须传全 —— 只看第一个会漏判。</param>
        /// <param name="outputDirectory">准备好的输出目录。</param>
        /// <returns>准备成功时为 true。</returns>
        public bool TryPrepareCompressOutput(string outputPath, IReadOnlyList<PendingFile> items, out string outputDirectory, out Exception error)
        {
            error = null;
            outputDirectory = null;

            if (items == null || items.Count == 0 || items[0] == null)
            {
                error = new ArgumentException(EnumText.GetErrorText("Err_NoCompressInput", "没有可压缩的输入"));
                return false;
            }

            PendingFile firstItem = items[0];

            // 用户可能选的是目录，也可能直接选了一个已存在的归档文件，两种都要归一到目录。
            string candidate = FileSystemHelper.NormalizeDirectoryPath(outputPath);

            if (FileSystemHelper.IsExistingFile(candidate))
            {
                candidate = Path.GetDirectoryName(candidate);
            }

            // 输出目录若落在**任意一个**待压缩目录内部，7z 会把正在写入的归档再压进自己，
            // 轻则体积暴涨，重则把输出目录越滚越大直到失败。
            //
            // 这里必须逐个检查全部输入，而不是只看 items[0]：混合选择「一个文件 + 一个目录」时，
            // 若第一个是普通文件、输出目录恰在第二个目录内部，只查第一个就完全发现不了，
            // 结果就是归档被压进自己。这类自包含在支持多选的工具里很容易踩到。
            string candidateFull = string.IsNullOrWhiteSpace(candidate) ? null : Path.GetFullPath(candidate);
            foreach (PendingFile item in items)
            {
                if (item == null || !item.IsDirectory) continue;

                string itemParent = Path.GetDirectoryName(item.Path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (string.IsNullOrWhiteSpace(itemParent)) continue;

                string sourceFull = Path.GetFullPath(item.Path);

                // **不能用 `sourceFull + 分隔符` 做前缀判断。**
                //
                // 源是磁盘根目录时（`C:\`），`Path.GetFullPath` 返回的字符串**已经**以
                // 分隔符结尾，再拼一个就变成 `C:\\` —— 而实际候选 `C:\Auto7zOutput`
                // 并不以 `C:\\` 开头，判定为「不在源内」，自包含检查被整个绕过。
                // 实测确认：
                //     源=C:\  候选=C:\Auto7zOutput  prefix=C:\\  StartsWith=False
                //
                // 统一走 IsPathInside，它先把两边都规范化去掉末尾分隔符再比。
                bool candidateInsideSource = candidateFull != null
                    && IsPathInside(candidateFull, sourceFull);
                bool candidateIsSource = candidateFull != null
                    && string.Equals(TrimTrailingSeparators(candidateFull),
                                     TrimTrailingSeparators(sourceFull),
                                     StringComparison.OrdinalIgnoreCase);

                if (string.IsNullOrWhiteSpace(candidate) || candidateInsideSource || candidateIsSource)
                {
                    // 退到这个输入目录的父目录：那是当前候选位置里唯一保证在源之外的选择。
                    candidate = itemParent;
                    candidateFull = Path.GetFullPath(candidate);
                }
            }

            if (string.IsNullOrWhiteSpace(candidate))
            {
                candidate = Path.GetDirectoryName(firstItem.Path);
            }

            if (string.IsNullOrWhiteSpace(candidate))
            {
                error = new ArgumentException(EnumText.GetErrorText("Err_UnknownOutputDir", "无法确定输出目录"));
                return false;
            }

            if (!Directory.Exists(candidate))
            {
                error = new DirectoryNotFoundException(EnumText.GetErrorText("Err_OutputDirNotFound", "输出目录不存在：{0}", candidate));
                return false;
            }

            if (!FileSystemHelper.IsDirectoryWritable(candidate))
            {
                error = new UnauthorizedAccessException(EnumText.GetErrorText("Err_OutputDirNotWritable", "输出目录不可写：{0}", candidate));
                return false;
            }

            // 每任务一个随机子目录，避免并发任务互相覆盖同名归档。
            string safeFolder = Path.Combine(candidate, FileSystemHelper.GenerateSafeFolderName());
            try
            {
                Directory.CreateDirectory(safeFolder);
            }
            catch (Exception ex)
            {
                error = new IOException(EnumText.GetErrorText("Err_OutputDirNotWritable", "输出目录不可写：{0}", candidate), ex);
                return false;
            }

            outputDirectory = safeFolder;
            return true;
        }

        #endregion

        #region 解压输出目录准备

        /// <summary>
        /// 准备解压目标目录，固定放在归档所在目录下的随机子目录里。
        /// </summary>
        /// <param name="destinationDirectory">准备好的目标目录。</param>
        public bool TryPrepareExtractOutput(string archivePath, out string destinationDirectory, out Exception error)
        {
            error = null;
            destinationDirectory = null;

            if (string.IsNullOrWhiteSpace(archivePath))
            {
                error = new FileNotFoundException(EnumText.GetErrorText("Err_ArchiveNotFound", "压缩文件不存在"), archivePath);
                return false;
            }

            // 分卷归档只有 .001 会真正落地，存在性检查必须连后缀一起看。
            if (!File.Exists(archivePath) && !File.Exists(archivePath + ".001"))
            {
                error = new FileNotFoundException(EnumText.GetErrorText("Err_ArchiveNotFound", "压缩文件不存在"), archivePath);
                return false;
            }

            string parent = Path.GetDirectoryName(archivePath);
            if (string.IsNullOrWhiteSpace(parent))
            {
                error = new ArgumentException(EnumText.GetErrorText("Err_UnknownExtractDir", "无法确定解压目录"));
                return false;
            }

            if (!FileSystemHelper.IsDirectoryWritable(parent))
            {
                error = new UnauthorizedAccessException(EnumText.GetErrorText("Err_ExtractDirNotWritable", "压缩文件所在目录不可写：{0}", parent));
                return false;
            }

            // 先用逻辑名去掉卷号：只剥 .001 的话，.002 及以后会把卷号带进目录名，
            // 三位以上的卷号（.010）还会剥不干净。逻辑名这一层对所有卷号一视同仁。
            string folderName = Path.GetFileName(GetLogicalArchiveName(archivePath));
            if (string.IsNullOrWhiteSpace(folderName)) folderName = "Auto7z_Extract";

            if (folderName.EndsWith(".tar", StringComparison.OrdinalIgnoreCase))
            {
                folderName = folderName.Substring(0, folderName.Length - 4);
            }

            string safeFolder = Path.Combine(parent, FileSystemHelper.GenerateSafeFolderName());
            try
            {
                Directory.CreateDirectory(safeFolder);
            }
            catch (Exception ex)
            {
                error = new IOException(EnumText.GetErrorText("Err_ExtractDirNotWritable", "压缩文件所在目录不可写：{0}", parent), ex);
                return false;
            }

            destinationDirectory = safeFolder;
            return true;
        }
        #endregion


        #region 归档命名与产物收集
        /// <summary>
        /// 生成归档的最终路径，同名时自动追加序号，绝不覆盖已有文件。
        /// </summary>
        public string BuildArchiveFilePath(string outputDirectory, string baseName, AppConfig config)
        {
            string safeName = SanitizeFileName(string.IsNullOrWhiteSpace(baseName) ? "Archive" : baseName);
            string extension = ArchiveFormatMap.ToExtension(config.Format);
            // .tar.zst 是复合扩展名，格式映射只给出 .tar，需要再补一段。
            if (config.Format == ArchiveFormat.Tar && config.Zstd)
            {
                extension += ".zst";
            }

            return FileSystemHelper.MakeUniqueFilePath(outputDirectory, safeName + extension);
        }

        /// <summary>
        /// 判断归档是否已经产出，分卷归档只检查首卷。
        /// </summary>
        public bool ArchiveExists(string archivePath)
        {
            if (string.IsNullOrWhiteSpace(archivePath)) return false;
            if (File.Exists(archivePath)) return true;

            if (File.Exists(archivePath + ".001")) return true;

            return false;
        }

        public List<string> CollectProducedFiles(string archivePath)
        {
            var files = new List<string>();
            if (string.IsNullOrWhiteSpace(archivePath)) return files;

            if (File.Exists(archivePath))
            {
                files.Add(archivePath);
                return files;
            }

            string directory = Path.GetDirectoryName(archivePath);
            string prefix = Path.GetFileName(archivePath) + ".";
            if (Directory.Exists(directory))
            {
                try
                {
                    foreach (var f in Directory.EnumerateFiles(directory, prefix + "*"))
                    {
                        // 只认纯数字后缀：归档名可能是别的文件名的前缀，不能让无关文件混进产物列表。
                        string suffix = f.Substring((Path.Combine(directory, prefix)).Length);
                        if (suffix.Length > 0 && suffix.All(char.IsDigit)) files.Add(f);
                    }
                }
                catch
                {
                    // 枚举失败忽略
                }
            }

            return files;
        }

        private static string SanitizeFileName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(name.Length);
            foreach (var ch in name)
            {
                sb.Append(Array.IndexOf(invalid, ch) >= 0 ? '_' : ch);
            }

            // Windows 不允许文件名以点结尾，去掉尾点后再判空。
            string result = sb.ToString().Trim().TrimEnd('.');
            return result.Length == 0 ? "Archive" : result;
        }
        #endregion


        #region 清理与哈希计算
        /// <summary>
        /// 删除未完成的中间目录，失败时静默忽略。
        /// </summary>
        public void CleanupUnfinished(string directoryPath)
        {
            FileSystemHelper.DeleteDirectoryQuietly(directoryPath);
        }

        /// <summary>
        /// 按文件头魔数判断是否为已知归档，不依赖扩展名。
        /// </summary>
        public bool IsArchiveFile(string filePath)
        {
            return ArchiveFormatMap.DetectByMagic(filePath) != null;
        }

        public long GetSizeInBytes(string path)
        {
            return FileSystemHelper.GetSizeInBytes(path);
        }

        /// <summary>
        /// 体积统计，并回报读不到的子树数量。调用方据此把「统计不完整」如实写进日志 ——
        /// 这个值只用于判断要不要分卷，读失败不该中止压缩，但「少算了」必须能被看见。
        /// </summary>
        public long GetSizeInBytesDetailed(string path, out int skippedDirectoryCount)
        {
            return FileSystemHelper.GetSizeInBytesDetailed(path, out skippedDirectoryCount);
        }


        #endregion


    }
}
