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
    /// 单次 7z.exe 调用的结果快照，调用方据此区分成功、取消、超时与密码错误。
    /// </summary>
    public sealed class CommandResult
    {
        public bool Succeeded { get; set; }
        public bool Cancelled { get; set; }
        public bool TimedOut { get; set; }

        public int ExitCode { get; set; } = -1;

        public string StandardOutput { get; set; } = string.Empty;
        public string StandardError { get; set; } = string.Empty;
        public Exception Error { get; set; }

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

        private static string ComponentsPath => AppPaths.ComponentsDirectory;

        private static string SevenZipDllPath => Path.Combine(ComponentsPath, "7z.dll");

        // 资源名必须与 csproj 里的逻辑路径一致，改名后提取只会静默失败。
        private const string ExeResourcePath = "Resources/Modules/7z.exe";
        private const string DllResourcePath = "Resources/Modules/7z.dll";

        // 用轮询而非阻塞等待，才能边等边响应取消与超时；杀进程后再给若干次轮询的宽限。
        private const int PollIntervalMs = 100;

        private const int KillGraceIterations = 50;

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
        /// 终止所有仍在运行的 7z 进程，只清空跟踪表，不等待进程真正退出。
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
        public static string SevenZipExePath => Path.Combine(AppPaths.ComponentsDirectory, "7z.exe");

        public static string ComponentsDirectory => AppPaths.ComponentsDirectory;


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
            }

            if (File.Exists(outputPath)) File.Delete(outputPath);
            File.Move(tempPath, outputPath);
        }


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
            catch
            {
                return true;
            }
        }

        private static long GetEmbeddedResourceLength(string resourcePath)
        {
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

        private string BuildSingleCompressCommand(string formatValue, List<string> quotedSources, string outputArchivePath, AppConfig config, bool applyGenericSwitches)
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
            if (config.MaxLevelCompress) return 9;

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
        /// <summary>
        /// 仅在归档体积确实超过分卷大小时才追加 -v，避免产出无意义的单卷。
        /// </summary>
        private static void AppendVolumeSwitch(StringBuilder sb, AppConfig config)
        {
            if (!TryGetVolumeMegabytes(config.Format, config.Volume, out long volumeMb)) return;

            // 总大小未知时分卷数量无法预估，按不分卷处理，界面上的分卷开关也据此禁用。
            if (config.TotalSize <= 0) return;

            long volumeBytes = volumeMb * 1024L * 1024L;
            if (config.TotalSize <= volumeBytes) return;

            sb.AppendFormat(" -v{0}m", volumeMb);
        }

        /// <summary>
        /// 把配置里的分卷大小解析成 MB，tar 与非法值、零值都视为不可分卷。
        /// </summary>
        private static bool TryGetVolumeMegabytes(ArchiveFormat format, string volume, out long volumeMb)
        {
            volumeMb = 0;

            // tar 是连续字节流，切分后会失去可解析的结构；分卷大小为 0 或非法时同样拒绝，
            // 这两类输入都要在体积预估之前就被挡掉。
            if (format == ArchiveFormat.Tar) return false;

            if (!long.TryParse(volume, NumberStyles.Integer, CultureInfo.InvariantCulture, out volumeMb)) return false;

            return volumeMb > 0;
        }

        /// <summary>
        /// 判断给定格式与配置是否允许分卷，供界面与体积预估共享同一套判定。
        /// </summary>
        public static bool CanSplitVolume(ArchiveFormat format, string volume)
        {
            return TryGetVolumeMegabytes(format, volume, out _);
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

            // e.Data == null 是异步读取端唯一可靠的流已读完信号。只 WaitForExit 不够：
            // 带超时的 WaitForExit 不保证回调已经跑完，缺了这个标记，输出会被截断。
            Action onStreamEnd = () =>
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
                    if (e.Data == null)
                    {
                        onStreamEnd();
                        return;
                    }

                    lock (stdoutLock) { stdout.AppendLine(e.Data); }

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
                };

                process.ErrorDataReceived += (sender, e) =>
                {
                    if (e.Data == null)
                    {
                        onStreamEnd();
                        return;
                    }

                    lock (stderrLock) { stderr.AppendLine(e.Data); }
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
                lock (stdoutLock) { result.StandardOutput = stdout.ToString().Trim(); }
                lock (stderrLock) { result.StandardError = stderr.ToString().Trim(); }

                UntrackProcess(process);

                try
                {
                    if (!process.HasExited) process.Kill();
                }
                catch
                {
                    // 有意忽略：进程可能在这两句之间自己退出了，杀不掉不影响下面的结果收集
                }

                try { process.Dispose(); } catch { /* 有意忽略：句柄可能已被回收，重复释放失败可忽略 */ }
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
                try
                {
                    outputDrained.Wait(OutputDrainTimeoutMs);
                }
                // 信号量已被释放时忽略即可，此时输出已经收完，不必把收尾动作变成异常。
                catch (ObjectDisposedException)
                {
                    // 事件已被释放（正常收尾），视为排空完成
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
        /// <param name="firstItem">第一个待压缩项，用于推断源根目录。</param>
        /// <param name="outputDirectory">准备好的输出目录。</param>
        /// <returns>准备成功时为 true。</returns>
        public bool TryPrepareCompressOutput(string outputPath, PendingFile firstItem, out string outputDirectory, out Exception error)
        {
            error = null;
            outputDirectory = null;

            if (firstItem == null)
            {
                error = new ArgumentException(EnumText.GetErrorText("Err_NoCompressInput", "没有可压缩的输入"));
                return false;
            }

            // 用户可能选的是目录，也可能直接选了一个已存在的归档文件，两种都要归一到目录。
            string candidate = FileSystemHelper.NormalizeDirectoryPath(outputPath);

            if (FileSystemHelper.IsExistingFile(candidate))
            {
                candidate = Path.GetDirectoryName(candidate);
            }

            // 输出目录若落在源目录内部，7z 会把正在写入的归档再压进自己，
            // 轻则体积暴涨，重则把输出目录越滚越大直到失败。
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
        /// 把「一次任务产出的整组文件」算成**一份**校验文件。
        ///
        /// 这是校验文件的正确形态：一个归档（尤其分卷）是多文件组成的产物，
        /// 校验清单必须把这些文件**全部列在一份 .sha256 里**，每行一条。
        /// 早先的实现对每个分卷各调一次，产出 7 个 x.7z.001.sha256 … x.7z.007.sha256 ——
        /// 那样谁也没法用它校验一整套包：校验工具拿到的是 7 份只含一行的清单，
        /// 用户还得手工拼起来，而这正是校验文件存在的意义所在。
        /// </summary>
        /// <param name="producedFiles">本次产出的全部文件，按顺序列入清单。</param>
        /// <param name="checksumFilePath">要写出的 .sha256 路径。</param>
        public void GenerateSHA256ForSet(IReadOnlyList<string> producedFiles, string checksumFilePath,
            Action<int> progress, CancellationToken cancellationToken)
        {
            if (producedFiles == null || producedFiles.Count == 0) return;
            if (string.IsNullOrWhiteSpace(checksumFilePath)) return;

            using (var sha256Service = new SHA256Service())
            {
                var lines = sha256Service.CalculateSHA256(
                    producedFiles.ToArray(), progress, cancellationToken);

                // 只写一份，且把全部行一次写进去；单文件时与旧行为完全一致。
                if (lines != null && lines.Length > 0)
                {
                    sha256Service.WriteSHA256File(checksumFilePath, lines);
                }
            }
        }
        #endregion


    }
}
