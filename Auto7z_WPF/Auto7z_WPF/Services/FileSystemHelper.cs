using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Auto7z_WPF.Services
{
    /// <summary>
    /// 文件系统相关的公共辅助方法。集中处理路径规范化、可写性探测与大小统计，
    /// 避免各 Service 各自实现一套。
    /// </summary>
    public static class FileSystemHelper
    {
        private const string ProbeFileName = "Auto7z_write_probe.tmp";

        /// <summary>
        /// 安全文件夹名的自增序号。系统时钟精度不足时用于保证名称不重复。
        /// </summary>
        private static long _folderNameCounter;

        /// <summary>
        /// 规范化用户输入的目录路径。
        /// 用户可能粘贴了带引号的路径、或从拖放获得的 &lt；文件路径&gt；，
        /// 这里统一去掉引号与首尾空白，保证后续 Path.Combine 得到合法的目录名。
        /// </summary>
        public static string NormalizeDirectoryPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return string.Empty;

            string value = path.Trim().Trim('"').Trim();
            if (value.Length == 0) return string.Empty;

            try
            {
                return Path.GetFullPath(value);
            }
            catch
            {
                // 非法字符等：保持原样，交由后续校验给出明确提示
                return value;
            }
        }

        /// <summary>
        /// 校验“输出路径”输入是否可用。
        ///
        /// 规则：
        ///   空 → 合法（表示跟随输入文件所在目录，由上层决定）
        ///   指向已存在的文件 → 视为误填，退回其所在目录
        ///   指向已存在的目录 → 合法
        ///   不存在的路径 → 非法（拼写错误或目录未创建）
        ///
        /// 返回 true 表示该输入可用（outDirectory 为最终使用的目录）。
        /// </summary>
        public static bool TryValidateOutputPath(string outputPath, out string outDirectory)
        {
            outDirectory = null;

            if (string.IsNullOrWhiteSpace(outputPath)) return true;

            string candidate = NormalizeDirectoryPath(outputPath);
            if (string.IsNullOrWhiteSpace(candidate)) return true;

            try
            {
                // 已存在的文件：用户误填了文件名，退回其所在目录
                if (File.Exists(candidate))
                {
                    string dir = Path.GetDirectoryName(candidate);
                    if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
                    outDirectory = dir;
                    return true;
                }

                // 已存在的目录：直接使用
                if (Directory.Exists(candidate))
                {
                    outDirectory = candidate;
                    return true;
                }

                // 路径不存在 → 非法
                return false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 判断路径是否为“已存在的文件”。用于识别把文件路径误填进输出目录的情况。
        /// </summary>
        public static bool IsExistingFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            try
            {
                return File.Exists(path);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 探测目录是否可写（实际创建并删除一个探针文件）。
        /// </summary>
        public static bool IsDirectoryWritable(string directoryPath)
        {
            if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath))
                return false;

            string probePath = Path.Combine(directoryPath, ProbeFileName);
            try
            {
                using (var stream = new FileStream(probePath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    stream.WriteByte(0);
                }
                File.Delete(probePath);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 统计文件或文件夹的字节数。失败返回 -1。
        /// 返回字节而不是截断后的 MB，避免小文件被算成 0 导致分卷判断失效。
        /// </summary>
        public static long GetSizeInBytes(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return -1;

            try
            {
                if (File.Exists(path)) return new FileInfo(path).Length;

                if (Directory.Exists(path))
                {
                    long total = 0;
                    foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                    {
                        try
                        {
                            total += new FileInfo(file).Length;
                        }
                        catch
                        {
                            // 单个文件读不到大小，跳过，不打断整体统计
                        }
                    }
                    return total;
                }
            }
            catch
            {
                return -1;
            }

            return -1;
        }

        /// <summary>
        /// 递归删除目录内容。用于清理未完成的任务输出与临时组件。
        /// </summary>
        public static void DeleteDirectoryQuietly(string directoryPath)
        {
            if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath)) return;

            try
            {
                Directory.Delete(directoryPath, true);
            }
            catch
            {
                // 被占用或权限不足时保留，交由下次启动重试
            }
        }

        /// <summary>
        /// 生成一个安全的文件夹名。
        ///
        /// 以 UtcNow.Ticks 为种子，取 SHA256 后转 Base64，
        /// 再把 / + 替换成 _ - 并去掉结尾的 =，
        /// 得到的字符串只含 URL 安全字符，可安全用作目录名。
        ///
        /// 种子里额外混入一个进程内自增序号：
        /// Windows 系统时钟的实际精度约为 15.6ms，
        /// 同一时刻多次取 UtcNow.Ticks 会得到相同值，仅靠时间戳无法保证不重名。
        /// </summary>
        public static string GenerateSafeFolderName()
        {
            // UtcNow.Ticks 是 64 位整数，精度到 100ns
            string seed = DateTime.UtcNow.Ticks.ToString();

            // 进程内自增计数：弥补系统时钟精度不足导致的种子重复
            long seq = Interlocked.Increment(ref _folderNameCounter);
            seed += "#" + seq.ToString(CultureInfo.InvariantCulture);

            byte[] bytes = Encoding.UTF8.GetBytes(seed);
            using (var sha256 = SHA256.Create())
            {
                string base64 = Convert.ToBase64String(sha256.ComputeHash(bytes));
                return base64.Replace('/', '_').Replace('+', '-').TrimEnd('=');
            }
        }

        /// <summary>
        /// 生成“不重名”的文件路径：若目标已存在则追加 (2)、(3)……
        ///
        /// 正常流程下每次任务都会写入一个全新的安全命名文件夹，不会重名；
        /// 这里保留作为并发写入时的最后一道防线。
        /// </summary>
        public static string MakeUniqueFilePath(string directory, string fileName)
        {
            string candidate = Path.Combine(directory, fileName);
            if (!File.Exists(candidate)) return candidate;

            string name = Path.GetFileNameWithoutExtension(fileName);
            string extension = Path.GetExtension(fileName);
            int index = 2;
            while (File.Exists(candidate))
            {
                candidate = Path.Combine(directory, $"{name} ({index}){extension}");
                index++;
            }
            return candidate;
        }
    }
}
