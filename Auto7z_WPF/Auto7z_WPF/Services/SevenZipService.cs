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
using System.Windows;
using Auto7z_WPF.Models;

namespace Auto7z_WPF.Services
{
    /// <summary>
    /// 执行外部 7-Zip 命令的返回结果。
    /// </summary>
    public sealed class CommandResult
    {
        public bool Succeeded { get; set; }
        public bool Cancelled { get; set; }
        public bool TimedOut { get; set; }

        /// <summary>7-Zip 退出码。0=成功，1=有警告但已处理完，2=致命错误，7=命令行语法错误。</summary>
        public int ExitCode { get; set; } = -1;

        public string StandardOutput { get; set; } = string.Empty;
        public string StandardError { get; set; } = string.Empty;
        public Exception Error { get; set; }

        /// <summary>
        /// 是否有警告但整体仍算完成（退出码 1）。调用方据此提示用户，但不应判定为失败。
        /// </summary>
        public bool HasWarning => ExitCode == 1;

        /// <summary>
        /// 7-Zip 是否在索要密码。
        ///
        /// 未提供密码（或密码错误）时，7-Zip 有两种表现：
        ///   未提供密码 → stdout 输出 "Enter password (will not be echoed):"，退出码 255；
        ///   密码错误   → stdout 输出 "ERROR: Data Error in encrypted file. Wrong password?"，退出码 2。
        /// 前者必须弹窗让用户输入密码后重试，否则用户只会看到毫无意义的 "Break signaled"。
        /// </summary>
        public bool RequiresPassword =>
            StandardOutput.IndexOf("Enter password", StringComparison.OrdinalIgnoreCase) >= 0
            || StandardError.IndexOf("Enter password", StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>
        /// 是否是“密码错误”（已经给了密码但不对）。
        /// </summary>
        public bool IsWrongPassword =>
            StandardOutput.IndexOf("Wrong password", StringComparison.OrdinalIgnoreCase) >= 0
            || StandardError.IndexOf("Wrong password", StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>
        /// 失败时的可读原因：优先取 7-Zip 的错误输出，其次取异常信息。
        /// </summary>
        public string GetFailureDetail()
        {
            if (Error != null) return Error.Message;

            // 优先从 stderr 里挑出真正的 ERROR 行，避免把整段输出都弹给用户
            string errorLine = FindErrorLine(StandardError) ?? FindErrorLine(StandardOutput);
            if (!string.IsNullOrWhiteSpace(errorLine)) return errorLine;

            if (!string.IsNullOrWhiteSpace(StandardError)) return StandardError.Trim();
            if (!string.IsNullOrWhiteSpace(StandardOutput)) return StandardOutput.Trim();
            return string.Empty;
        }

        /// <summary>
        /// 从 7-Zip 输出中找出第一行包含 ERROR / 警告关键字的内容。
        /// </summary>
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

    /// <summary>
    /// 7-Zip 核心业务服务。
    /// 负责：组件检查与提取、命令生成、命令执行（异步读取 + 可取消）、临时资源清理、MD5 计算。
    /// 不直接操作任何 UI 控件，也不生成任何界面文本，仅提供纯业务方法供 ViewModel 调用。
    /// </summary>
    public class SevenZipService
    {
        private static readonly string _assemblyName = Assembly.GetExecutingAssembly().GetName().Name;

        /// <summary>组件所在目录。跟随 <see cref="AppPaths"/> 的当前模式。</summary>
        private static string ComponentsPath => AppPaths.ComponentsDirectory;

        /// <summary>7z.dll 完整路径。</summary>
        private static string SevenZipDllPath => Path.Combine(ComponentsPath, "7z.dll");

        private const string ExeResourcePath = "Resources/Modules/7z.exe";
        private const string DllResourcePath = "Resources/Modules/7z.dll";

        private const int PollIntervalMs = 100;
        private const int KillGraceIterations = 50; // 杀掉进程后再等 5 秒

        /// <summary>
        /// 所有 7-Zip 命令共用的开关。
        /// -bsp1：把进度百分比打到 stdout，否则重定向输出时完全看不到进度（界面进度条会一直是 0%）；
        /// -sccUTF-8：强制以 UTF-8 输出，否则中文错误信息按 OEM 代码页输出会变成乱码。
        /// </summary>
        private const string CommonSwitches = "-bsp1 -sccUTF-8";

        /// <summary>
        /// 该格式是否支持加密。tar 不支持（7-Zip 会静默忽略 -p，密码形同虚设）。
        /// </summary>
        public static bool SupportsEncryption(ArchiveFormat format)
        {
            return format != ArchiveFormat.Tar;
        }

        /// <summary>
        /// 匹配 7-Zip 的**解压进度**行（形如 " 23% + big.bin"）。
        ///
        /// 锚定行首，避免把文件名里带 % 的内容误当成进度。
        ///
        /// 7-Zip 的 -bsp1 会输出好几种百分号行，其中三种**不是**解压进度：
        ///
        ///   "  0%"             —— 扫描存档阶段的起始行，每次解压都出现
        ///   "  0M Scan C:\...\" —— 扫描阶段的进度（0M 表示扫了 0 个文件）
        ///   "100% T a.txt"    —— 读取文件时出错（密码错就是这个）
        ///
        /// 前两种出现在**每次**解压的开头；第三种只在出错时出现。
        /// 它们混在密码交互中间，会把流程搅乱：
        /// 用户看到 "0%" 就以为开始解压了，其实程序正卡在等密码；
        /// 看到 "100% T" 就以为快好了，其实刚失败。
        ///
        /// 真正的解压进度行是 " 23% + 文件名"：
        ///   - 百分号后面必须有内容（否则是上面那三种空行）
        ///   - 标记只能是 +（正常）；T 表示这个文件出错，不算进度
        /// </summary>
        // 7-Zip 在 -bsp1 下真正的进度行有两种形态（分隔符是 \r，不是 \n）：
        //   压缩： " 20% 1 + f1.bin"
        //   解压： " 64% 23 - f30.bin"
        // 百分号后面的文件序号有时没有（" 10% + f0.bin"），所以序号可省略；
        // 标记只认 + 与 -：
        //   "100% T xxx" 是读文件出错（密码错就是它），不是进度；
        //   "  0%" / "  0M Scan ..." 是扫描起始行，也不是进度。
        private static readonly Regex _percentRegex =
            new Regex(@"^\s*(\d{1,3})\s*%(?:\s+\d+)?\s*[+\-]\s", RegexOptions.Compiled);

        /// <summary>
        /// 正在运行的 7-Zip 进程。程序退出时统一结束，
        /// 否则关闭程序后 7z.exe 仍在后台运行，还可能锁住临时组件导致无法删除。
        /// </summary>
        private static readonly List<Process> _runningProcesses = new List<Process>();
        private static readonly object _runningProcessesLock = new object();

        /// <summary>
        /// 结束所有仍在运行的 7-Zip 进程（程序退出时调用）。
        /// </summary>
        public void KillRunningProcesses()
        {
            KillAllRunningProcesses();
        }

        /// <summary>
        /// 结束所有 7-Zip 子进程（静态版，供程序退出时兜底）。
        ///
        /// 右键菜单模式没有主窗口，关闭流程走不到 ViewModel.OnClosing，
        /// 不在这里兜底的话，程序退出后 7z.exe 会留在后台继续跑。
        /// </summary>
        public static void KillAllRunningProcesses()
        {
            lock (_runningProcessesLock)
            {
                foreach (var p in _runningProcesses)
                {
                    try
                    {
                        if (p != null && !p.HasExited) p.Kill();
                    }
                    catch
                    {
                        // 进程可能已退出
                    }
                }

                _runningProcesses.Clear();
            }
        }

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

        /// <summary>7z.exe 的完整路径。</summary>
        public static string SevenZipExePath => Path.Combine(AppPaths.ComponentsDirectory, "7z.exe");

        /// <summary>7-Zip 组件所在目录。</summary>
        public static string ComponentsDirectory => AppPaths.ComponentsDirectory;

        #region 组件检查与提取

        /// <summary>
        /// 检查 7-Zip 依赖组件是否已就绪。
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
        /// 确保 7-Zip 依赖组件存在，缺失则从程序集资源中提取。
        /// 逐个文件检查，避免“exe 存在但 dll 缺失”时整体跳过提取。
        /// </summary>
        public bool EnsureComponents(out Exception error)
        {
            error = null;
            try
            {
                Directory.CreateDirectory(AppPaths.DataDirectory);
                Directory.CreateDirectory(ComponentsPath);

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

        /// <summary>
        /// 组件文件不存在或已损坏时重新释放。
        ///
        /// 只判断“文件在不在”不够：文件被截断或写坏时，
        /// 7z.exe 会报“不支持的 16 位应用程序”之类的错，
        /// 而组件永远不会被重新释放。
        /// </summary>
        /// <param name="resourcePath">程序集里的资源路径。</param>
        /// <param name="outputPath">释放到磁盘的目标路径。</param>
        private static void ExtractResourceIfMissing(string resourcePath, string outputPath)
        {
            if (IsComponentUsable(resourcePath, outputPath)) return;

            Uri resourceUri = new Uri($"pack://application:,,,/{_assemblyName};component/{resourcePath}", UriKind.Absolute);
            var resourceInfo = Application.GetResourceStream(resourceUri);
            if (resourceInfo == null || resourceInfo.Stream == null)
            {
                throw new FileNotFoundException(EnumText.GetErrorText("Err_ResourceMissing", "资源未找到: {0}", resourcePath));
            }

            // 先写临时文件再改名，避免解压中途失败留下半截的 exe/dll 被当成可用组件
            string tempPath = outputPath + ".tmp";
            using (Stream stream = resourceInfo.Stream)
            using (FileStream fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.CopyTo(fileStream);
            }

            if (File.Exists(outputPath)) File.Delete(outputPath);
            File.Move(tempPath, outputPath);
        }

        /// <summary>
        /// 判断已释放的组件文件是否还能用。
        ///
        /// 只看文件在不在是不够的：文件被截断或写坏时，
        /// 7z.exe 会报“不支持的 16 位应用程序”，而组件永远不会被重新释放。
        /// 这里拿磁盘上的文件与程序集里的资源比大小，对不上就当作损坏。
        /// </summary>
        /// <param name="resourcePath">程序集里的资源路径。</param>
        /// <param name="outputPath">已释放的组件文件路径。</param>
        /// <returns>可用返回 true。</returns>
        private static bool IsComponentUsable(string resourcePath, string outputPath)
        {
            if (!File.Exists(outputPath)) return false;

            try
            {
                var fileInfo = new FileInfo(outputPath);
                if (fileInfo.Length <= 0) return false;

                Uri resourceUri = new Uri($"pack://application:,,,/{_assemblyName};component/{resourcePath}", UriKind.Absolute);
                var resourceInfo = Application.GetResourceStream(resourceUri);
                if (resourceInfo?.Stream == null) return true;

                using (resourceInfo.Stream)
                {
                    return resourceInfo.Stream.Length == fileInfo.Length;
                }
            }
            catch
            {
                // 判断不出来时先当可用，避免反复重新释放
                return true;
            }
        }

        #endregion

        #region 压缩命令

        /// <summary>
        /// 生成压缩命令。tar + zstd 要两条，先打包再压缩。
        /// </summary>
        /// <param name="sourcePaths">待压缩的源文件/文件夹。</param>
        /// <param name="config">压缩配置。</param>
        /// <param name="outputArchivePath">最终归档文件的完整路径。</param>
        public List<string> BuildCompressCommands(string[] sourcePaths, AppConfig config, string outputArchivePath)
        {
            var commands = new List<string>();
            if (sourcePaths == null || sourcePaths.Length == 0) return commands;
            if (string.IsNullOrWhiteSpace(outputArchivePath)) return commands;
            if (config == null) return commands;

            var quotedSources = sourcePaths
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => Quote(Path.GetFullPath(p)))
                .ToList();
            if (quotedSources.Count == 0) return commands;

            // tar + zstd：7z 无法一步生成压缩的 tar，改为 tar 打包 + zstd 压缩两步
            if (config.Format == ArchiveFormat.Tar && config.Zstd)
            {
                // 中间产物是“<名字>.tar”，不能直接用 ChangeExtension：
                // 目标名本身是 .tar.zst，ChangeExtension 会得到 .tar.tar。
                string tarPath = BuildIntermediateTarPath(outputArchivePath);
                commands.Add(BuildSingleCompressCommand("tar", quotedSources, tarPath, config, applyGenericSwitches: false));
                commands.Add(BuildZstdStepCommand(tarPath, outputArchivePath));
                return commands;
            }

            string formatValue = ArchiveFormatMap.ToSwitchValue(config.Format);
            commands.Add(BuildSingleCompressCommand(formatValue, quotedSources, outputArchivePath, config, applyGenericSwitches: true));
            return commands;
        }

        /// <summary>
        /// 计算 tar+zstd 两步流程的中间产物路径（&lt；名字&gt;.tar）。
        /// </summary>
        public static string BuildIntermediateTarPath(string outputArchivePath)
        {
            if (string.IsNullOrWhiteSpace(outputArchivePath)) return outputArchivePath;

            // 形如 name.tar.zst → name.tar
            if (outputArchivePath.EndsWith(".tar.zst", StringComparison.OrdinalIgnoreCase))
            {
                return outputArchivePath.Substring(0, outputArchivePath.Length - ".zst".Length);
            }

            return Path.ChangeExtension(outputArchivePath, ".tar");
        }

        /// <summary>
        /// 生成单个压缩步骤的命令行（不含程序路径）。
        /// </summary>
        private string BuildSingleCompressCommand(string formatValue, List<string> quotedSources, string outputArchivePath, AppConfig config, bool applyGenericSwitches)
        {
            var sb = new StringBuilder();
            sb.AppendFormat("a -aoa -y {0} -t{1} {2}", CommonSwitches, formatValue, Quote(outputArchivePath));

            foreach (var source in quotedSources)
            {
                sb.Append(' ').Append(source);
            }

            if (!applyGenericSwitches) return sb.ToString();

            // 密码：tar 不支持加密（7-Zip 会忽略 -p 但语义上不成立，这里直接不下发）；
            // 空密码同样不下发，避免 7z 进入交互式输入密码而卡住。
            bool hasPassword = !string.IsNullOrEmpty(config.Password);
            if (hasPassword && SupportsEncryption(config.Format))
            {
                sb.AppendFormat(" -p{0}", Quote(config.Password));
            }

            if (config.Format == ArchiveFormat.SevenZip)
            {
                // 完全加密（同时加密文件名）：仅在设置了密码时才有意义
                if (config.IsEncFileName && hasPassword)
                {
                    sb.Append(" -mhe=on");
                }

                // 固实压缩
                sb.Append(config.IsSolidMode ? " -ms=on" : " -ms=off");
            }

            // 压缩等级：-mx= 优先于运行模式的字典设置
            sb.AppendFormat(" -mx={0}", GetCompressionLevelValue(config));

            // 运行模式：只控制线程数与字典大小，不重复下发 -mx。
            AppendRuntimeModeSwitches(sb, config);

            // 分卷：仅在启用、指定了大小、且数据量确实超过分卷大小时才切分
            AppendVolumeSwitch(sb, config);

            return sb.ToString();
        }

        private string BuildZstdStepCommand(string tarPath, string outputArchivePath)
        {
            return string.Format("a -aoa -y {0} -tzstd {1} {2}", CommonSwitches, Quote(outputArchivePath), Quote(tarPath));
        }
        #endregion

        #region 压缩参数开关


        private static int GetCompressionLevelValue(AppConfig config)
        {
            if (config.MaxLevelCompress) return 9;

            switch (config.CompressionLevel)
            {
                case CompressionLevelPreset.Store: return 0;
                case CompressionLevelPreset.Ultra: return 9;
                case CompressionLevelPreset.Normal:
                default: return 5;
            }
        }

        /// <summary>
        /// 追加运行模式相关的开关（线程数 / 字典大小）。
        ///
        /// 7-Zip 的 -md 会盖掉 -mx 预设的字典大小，跟两者的先后顺序无关。
        /// 因此只有在使用“存储”等级（-mx=0，本就不需要字典）时才下发 -md；
        /// 其余等级交给 -mx 自己决定字典，否则“标准”和“极限”会退化成同一档，
        /// 用户选择的压缩等级形同虚设。
        /// </summary>
        private static void AppendRuntimeModeSwitches(StringBuilder sb, AppConfig config)
        {
            switch (config.Format)
            {
                case ArchiveFormat.SevenZip:
                    switch (config.RuntimeMode)
                    {
                        case RuntimeModePreset.Low:
                            // 低占用：单线程 + 小字典
                            sb.Append(" -mmt1");
                            AppendDictionarySwitch(sb, config, "8m");
                            break;
                        case RuntimeModePreset.High:
                            sb.Append(" -mmt4");
                            AppendDictionarySwitch(sb, config, "32m");
                            break;
                        case RuntimeModePreset.Balanced:
                        default:
                            sb.Append(" -mmt2");
                            AppendDictionarySwitch(sb, config, "16m");
                            break;
                    }
                    break;

                case ArchiveFormat.Zip:
                    // zip 的字典由 deflate 决定，只控制线程数
                    switch (config.RuntimeMode)
                    {
                        case RuntimeModePreset.Low: sb.Append(" -mmt1"); break;
                        case RuntimeModePreset.High: sb.Append(" -mmt4"); break;
                        case RuntimeModePreset.Balanced:
                        default: sb.Append(" -mmt2"); break;
                    }
                    break;
            }
        }

        /// <summary>
        /// 在不破坏用户所选压缩等级的前提下下发字典大小。
        /// “存储”等级下 7-Zip 使用 Copy 方法，加 -md 反而会让它退回 LZMA2，
        /// 因此此时只依赖 -mx=0 本身。
        /// </summary>
        private static void AppendDictionarySwitch(StringBuilder sb, AppConfig config, string dictionary)
        {
            if (GetCompressionLevelValue(config) == 0) return;
            sb.Append(" -md" + dictionary);
        }

        private static void AppendVolumeSwitch(StringBuilder sb, AppConfig config)
        {
            // tar 不支持分卷：7-Zip 会生成 name.tar.001 但解不开，等于产出废文件
            if (config.Format == ArchiveFormat.Tar) return;

            // 分卷大小为 0（或非法）表示不分卷
            if (!long.TryParse(config.Volume, NumberStyles.Integer, CultureInfo.InvariantCulture, out long volumeMb)) return;
            if (volumeMb <= 0) return;

            // 输入为空（空文件夹或空文件）时总大小为 0，此时不分卷：
            // 否则会生成 name.7z.001 这样的单卷分卷文件，与普通压缩包的产物形式不一致。
            if (config.TotalSize <= 0) return;

            // 数据量不超过分卷大小时无需切分
            long volumeBytes = volumeMb * 1024L * 1024L;
            if (config.TotalSize <= volumeBytes) return;

            sb.AppendFormat(" -v{0}m", volumeMb);
        }
        #endregion

        #region 解压命令


        /// <summary>
        /// 生成解压命令行（不含程序路径）。
        /// 不写死 -t：7-Zip 会按文件头自动识别，这样 rar / gz / xz / cab / tar.zst 等格式也能解压。
        /// </summary>
        /// <param name="config">解压配置。密码为 null 或空时不下发 -p，
        /// 避免 7-Zip 进入交互式输入密码而卡住。</param>
        public string BuildExtractCommand(string archivePath, string destinationDirectory, AppConfig config)
        {
            if (string.IsNullOrWhiteSpace(archivePath) || string.IsNullOrWhiteSpace(destinationDirectory))
                return null;

            var sb = new StringBuilder();
            sb.AppendFormat("x -aoa -y {0} -o{1} {2}", CommonSwitches, Quote(destinationDirectory), Quote(archivePath));

            // 密码是否下发取决于“存档实际是什么格式”，而不是界面上选中的生成格式：
            // tar 存档本来就不加密，下发 -p 没有意义；
            // 其余格式（7z / zip / rar…）都要下发，否则带密码的存档根本解不开。
            if (config != null && !string.IsNullOrEmpty(config.Password) && SupportsEncryption(config.Format))
            {
                sb.AppendFormat(" -p{0}", Quote(config.Password));
            }

            return sb.ToString();
        }

        /// <summary>
        /// 判断存档是否为“分卷包”（name.7z.001 / name.7z.002 …）。
        /// 分卷包没有 name.7z 本身，必须指向第一分卷才能解压。
        /// </summary>
        public static bool IsSplitArchive(string archivePath)
        {
            if (string.IsNullOrWhiteSpace(archivePath)) return false;

            // 已经是第一分卷
            if (archivePath.EndsWith(".001", StringComparison.OrdinalIgnoreCase)) return true;

            return !File.Exists(archivePath) && File.Exists(archivePath + ".001");
        }

        /// <summary>
        /// 取分卷包的第一分卷路径（name.7z → name.7z.001）。
        /// 不是分卷包时原样返回。
        /// </summary>
        public static string ResolveFirstVolumePath(string archivePath)
        {
            if (string.IsNullOrWhiteSpace(archivePath)) return archivePath;

            if (archivePath.EndsWith(".001", StringComparison.OrdinalIgnoreCase)) return archivePath;

            if (!File.Exists(archivePath) && File.Exists(archivePath + ".001"))
            {
                return archivePath + ".001";
            }

            return archivePath;
        }

        /// <summary>
        /// 判断存档是否为“压缩的 tar”（.tar.zst / .tar.gz 等），
        /// 这类存档单步解压只会得到中间的 tar 文件，需要再解一层。
        /// </summary>
        public static bool IsCompressedTar(string archivePath)
        {
            if (string.IsNullOrWhiteSpace(archivePath)) return false;

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
        /// 按 Windows 命令行规则给参数加引号。
        /// 反斜杠必须翻倍、引号必须用反斜杠转义，
        /// 否则密码里含 \ 或 " 时 7-Zip 会报“命令行语法错误”（退出码 7）。
        /// </summary>
        private static string Quote(string value)
        {
            if (string.IsNullOrEmpty(value)) return "\"\"";

            var sb = new StringBuilder(value.Length + 2);
            sb.Append('"');
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
                    // 结尾的连续反斜杠：需要翻倍，否则会吃掉闭合引号
                    sb.Append('\\', backslashCount * 2);
                    break;
                }

                if (value[i] == '"')
                {
                    // 引号前的反斜杠：翻倍后再补一个转义反斜杠
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

        #region 命令执行


        /// <summary>
        /// 异步执行 7-Zip 命令：stdout/stderr 同时异步读取（避免管道写满互相等待），
        /// 支持取消，并在退出后返回完整输出供上层给出可读的错误原因。
        /// </summary>
        /// <param name="arguments">命令参数（不含 7z.exe 路径）。</param>
        /// <param name="progress">进度回调，参数为百分比。</param>
        /// <param name="cancellationToken">取消标记。</param>
        /// <param name="timeout">超时时间，null 表示不限制。</param>
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
                    // 重定向并立即关闭标准输入：
                    // 解压加密存档时 7-Zip 会等待用户从控制台输入密码，
                    // 本程序不提供控制台，若不关闭 stdin，7-Zip 会一直挂起等待，
                    // 表现为“解压无响应、界面卡住”。
                    // 关闭后 7-Zip 会立即输出 “Enter password …” 并以退出码 255 结束，
                    // 由上层弹窗向用户索取密码后重试。
                    RedirectStandardInput = true,
                    // 命令行已带 -sccUTF-8，7-Zip 以 UTF-8 输出；
                    // 按 UTF-8 解码。7z 的错误信息是 UTF-8，用别的编码会乱码。
                    StandardOutputEncoding = new UTF8Encoding(false),
                    StandardErrorEncoding = new UTF8Encoding(false)
                };

                process.StartInfo = startInfo;
                process.Start();
                TrackProcess(process);

                // 关闭 stdin，让 7-Zip 立刻判定“无密码可用”而不是等待输入
                try { process.StandardInput.Close(); } catch { /* 忽略 */ }

                process.OutputDataReceived += (sender, e) =>
                {
                    if (e.Data == null) return;
                    lock (stdoutLock) { stdout.AppendLine(e.Data); }

                    if (progress == null) return;

                    // 7-Zip 用 \r 覆盖同一行来刷进度，管道下偶尔一次送来多段；
                    // 这里按 \r 再拆一遍，避免中间的更新被整段丢掉。
                    foreach (string chunk in e.Data.Split('\r'))
                    {
                        var match = _percentRegex.Match(chunk);
                        if (!match.Success) continue;

                        if (int.TryParse(match.Groups[1].Value, out int percent) && percent != lastPercent)
                        {
                            lastPercent = percent;
                            progress(percent);
                        }
                    }
                };

                process.ErrorDataReceived += (sender, e) =>
                {
                    if (e.Data == null) return;
                    lock (stderrLock) { stderr.AppendLine(e.Data); }
                };

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                #endregion

                #region 进程等待与取消

                var startedAt = DateTime.UtcNow;
                bool killIssued = false;
                int iterationsAfterKill = 0;

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

                    if (killIssued && ++iterationsAfterKill > KillGraceIterations)
                    {
                        // 进程已无法回收，直接放弃等待，避免界面永久卡住
                        result.Error = new TimeoutException(EnumText.GetErrorText("Err_ProcessNotExited", "7-Zip 进程未能在取消后退出"));
                        break;
                    }
                }

                // 参数为空的 WaitForExit 会等待异步输出读取完成，保证 stdout/stderr 完整
                try { process.WaitForExit(); } catch { /* 进程已释放 */ }

                result.ExitCode = SafeGetExitCode(process);

                // 7-Zip 退出码语义：
                //   0 = 全部成功
                //   1 = 有警告，但内容已处理完成（例如个别文件被占用）——不能当失败
                //   2 = 致命错误（文件不存在、密码错误、压缩包损坏）
                //   7 = 命令行语法错误
                result.Succeeded = !result.Cancelled
                                   && !result.TimedOut
                                   && (result.ExitCode == 0 || result.ExitCode == 1);
            }
            catch (Exception ex)
            {
                result.Error = ex;
                result.Succeeded = false;
            }
            finally
            {
                lock (stdoutLock) { result.StandardOutput = stdout.ToString().Trim(); }
                lock (stderrLock) { result.StandardError = stderr.ToString().Trim(); }

                UntrackProcess(process);

                try
                {
                    if (!process.HasExited) process.Kill();
                }
                catch
                {
                    // 忽略
                }

                try { process.Dispose(); } catch { /* 忽略 */ }
            }

            return result;
        }
        #endregion

        #region 进程收尾


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
        /// 为压缩任务准备输出目录。
        /// 输出目录为空时回退到第一个源文件所在目录；如果是文件夹，则放到其父目录，
        /// 避免把输出目录建在待压缩的文件夹内部（会被一并打包并导致 7z 报错）。
        /// </summary>
        public bool TryPrepareCompressOutput(string outputPath, PendingFile firstItem, out string outputDirectory, out Exception error)
        {
            error = null;
            outputDirectory = null;

            if (firstItem == null)
            {
                error = new ArgumentException(EnumText.GetErrorText("Err_NoCompressInput", "没有可压缩的输入"));
                return false;
            }

            string candidate = FileSystemHelper.NormalizeDirectoryPath(outputPath);

            // 用户把压缩包文件路径填进了“输出路径”，退回该文件所在目录
            if (FileSystemHelper.IsExistingFile(candidate))
            {
                candidate = Path.GetDirectoryName(candidate);
            }

            // 输入是文件夹时，输出必须放在它的上一级，否则输出目录会被打进压缩包
            if (firstItem.IsDirectory)
            {
                string parent = Path.GetDirectoryName(firstItem.Path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (!string.IsNullOrWhiteSpace(parent))
                {
                    string candidateFull = string.IsNullOrWhiteSpace(candidate) ? null : Path.GetFullPath(candidate);
                    string sourceFull = Path.GetFullPath(firstItem.Path);
                    bool candidateInsideSource = candidateFull != null
                        && candidateFull.StartsWith(sourceFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

                    if (string.IsNullOrWhiteSpace(candidate) || candidateInsideSource || string.Equals(candidateFull, sourceFull, StringComparison.OrdinalIgnoreCase))
                    {
                        candidate = parent;
                    }
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

            // 在输出目录下创建一个安全命名的子文件夹，把压缩产物放进去。
            // 每次任务一个独立目录，既避免覆盖已有文件，也不会出现 "xxx (2)" 这类名字。
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

        #region 解压目录准备


        /// <summary>
        /// 为解压任务准备输出目录：压缩包同级的“压缩包名”子目录，重名时自动追加序号。
        /// 不再直接解压到压缩包所在目录，避免与用户已有文件混在一起被 -aoa 静默覆盖。
        /// </summary>
        public bool TryPrepareExtractOutput(string archivePath, out string destinationDirectory, out Exception error)
        {
            error = null;
            destinationDirectory = null;

            if (string.IsNullOrWhiteSpace(archivePath))
            {
                error = new FileNotFoundException(EnumText.GetErrorText("Err_ArchiveNotFound", "压缩文件不存在"), archivePath);
                return false;
            }

            // 分卷包没有 name.7z 本身，只有 name.7z.001 …，两者都要接受
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

            string folderName = Path.GetFileNameWithoutExtension(archivePath);
            if (string.IsNullOrWhiteSpace(folderName)) folderName = "Auto7z_Extract";

            // 分卷包：name.7z.001 的无扩展名是 name.7z，去掉分卷后缀
            if (folderName.EndsWith(".001", StringComparison.OrdinalIgnoreCase))
            {
                folderName = folderName.Substring(0, folderName.Length - 4);
            }

            // .tar.gz / .tar.zst 这类双扩展名，取到 .tar 之前的部分
            if (folderName.EndsWith(".tar", StringComparison.OrdinalIgnoreCase))
            {
                folderName = folderName.Substring(0, folderName.Length - 4);
            }

            // 解压产物放进一个安全命名的子文件夹：每次任务一个独立目录，
            // 既不会覆盖已有文件，也不会出现 "xxx (2)" 这类名字。
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

        #region 归档命名


        /// <summary>
        /// 计算压缩后的归档文件路径（输出目录 + 名称 + 扩展名），重名时自动追加序号。
        /// </summary>
        public string BuildArchiveFilePath(string outputDirectory, string baseName, AppConfig config)
        {
            string safeName = SanitizeFileName(string.IsNullOrWhiteSpace(baseName) ? "Archive" : baseName);
            string extension = ArchiveFormatMap.ToExtension(config.Format);
            if (config.Format == ArchiveFormat.Tar && config.Zstd)
            {
                extension += ".zst";
            }

            return FileSystemHelper.MakeUniqueFilePath(outputDirectory, safeName + extension);
        }

        /// <summary>
        /// 判断压缩任务是否真的产出了文件。
        /// 启用分卷时 7-Zip 生成的是 name.7z.001 / name.7z.002 …，并不会生成 name.7z 本身，
        /// 只判断 File.Exists(name.7z) 会永远失败，必须把分卷序号考虑进去。
        /// </summary>
        public bool ArchiveExists(string archivePath)
        {
            if (string.IsNullOrWhiteSpace(archivePath)) return false;
            if (File.Exists(archivePath)) return true;

            // 分卷：name.7z → name.7z.001
            if (File.Exists(archivePath + ".001")) return true;

            return false;
        }

        /// <summary>
        /// 收集压缩任务实际产出的文件（分卷时包含所有分卷）。
        /// 用于失败时清理残留产物。
        /// </summary>
        public List<string> CollectProducedFiles(string archivePath)
        {
            var files = new List<string>();
            if (string.IsNullOrWhiteSpace(archivePath)) return files;

            if (File.Exists(archivePath))
            {
                files.Add(archivePath);
                return files;
            }

            // 分卷产物：name.7z.001、name.7z.002 …
            string directory = Path.GetDirectoryName(archivePath);
            string prefix = Path.GetFileName(archivePath) + ".";
            if (Directory.Exists(directory))
            {
                try
                {
                    foreach (var f in Directory.EnumerateFiles(directory, prefix + "*"))
                    {
                        // 只认 .001/.002 这类数字后缀，避免误伤同名文件
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

            string result = sb.ToString().Trim().TrimEnd('.');
            return result.Length == 0 ? "Archive" : result;
        }

        #endregion

        #region 清理与校验

        /// <summary>
        /// 清理未完成的压缩/解压输出。
        /// </summary>
        public void CleanupUnfinished(string directoryPath)
        {
            FileSystemHelper.DeleteDirectoryQuietly(directoryPath);
        }

        /// <summary>
        /// 判断文件是否为已知的压缩格式（按文件头魔数）。
        /// </summary>
        public bool IsArchiveFile(string filePath)
        {
            return ArchiveFormatMap.DetectByMagic(filePath) != null;
        }

        /// <summary>
        /// 获取文件或文件夹的字节大小，失败返回 -1。
        /// </summary>
        public long GetSizeInBytes(string path)
        {
            return FileSystemHelper.GetSizeInBytes(path);
        }

        #endregion

        #region MD5 校验文件

        /// <summary>
        /// 计算文件或文件夹内所有文件的 MD5，并写入同名 .md5 文件。
        /// 默认不生成：仅在配置启用时由 ViewModel 调用。
        /// </summary>
        public void GenerateMd5(string filePathOrFolder, Action<int> progress, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(filePathOrFolder)) return;

            using (var md5Service = new Md5Service())
            {
                md5Service.CalculateMd5ToFile(filePathOrFolder, progress, cancellationToken);
            }
        }

        #endregion
    }
}
