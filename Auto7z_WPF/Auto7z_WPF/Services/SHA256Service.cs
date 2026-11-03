using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Auto7z_WPF.Services
{
    /// <summary>
    /// SHA256 计算服务。提供文件与目录的 SHA256 计算，支持大文件流式读取与进度回调。
    /// 进度基于“已处理字节 / 总字节”，一次任务内单调递增，不再每个文件都从 0 跳到 100。
    /// </summary>
    public class SHA256Service : IDisposable
    {
        // 1MB 缓冲区。这个尺寸会落到大对象堆上，但顺序扫描大文件时它明显快于小缓冲，取舍是故意的。
        private const int BufferSize = 1024 * 1024;
        private const int UiRefreshMs = 100;           // 进度回调节流间隔

        private static readonly char[] _hexLower = "0123456789abcdef".ToCharArray();

        // 必须显式用 CNG 实现：.NET Framework 4.7.2 上 SHA256.Create() 解析出来的是托管实现
        // （SHA256Managed），本机实测只有约 129 MB/s；SHA256Cng 走 BCrypt，同一台机器约 1190 MB/s，
        // 相差近 9 倍。哈希是本项目最热的循环之一（每个产物都要算一遍），这个选择直接决定大文件的耗时。
        //
        // 用工厂方法而不是直接 new SHA256Cng()：某些精简环境（如部分 Windows PE）里 CNG 提供程序不可用，
        // 那时要能退回托管实现而不是让整个校验功能失败。
        private static readonly Func<HashAlgorithm> _createHash = CreateHashAlgorithm();

        private static Func<HashAlgorithm> CreateHashAlgorithm()
        {
            try
            {
                // 先真的建一次：只判断类型存在是不够的，BCrypt 提供程序打不开时会在这里抛。
                using (var probe = new SHA256Cng())
                {
                    return () => new SHA256Cng();
                }
            }
            catch (Exception)
            {
                // CNG 不可用时退回托管实现：慢，但结果同样是标准 SHA256。
                return () => (HashAlgorithm)new SHA256Managed();
            }
        }

        /// <summary>
        /// 计算一个或多个文件/文件夹内所有文件的 SHA256。
        /// </summary>
        /// <param name="paths">要计算 SHA256 的文件或文件夹路径数组。</param>
        /// <param name="progress">进度回调，参数为当前百分比（0-100），可为 null。</param>
        /// <param name="cancellationToken">取消标记。</param>
        /// <returns>每个文件的 SHA256 结果字符串，格式为 "hex *filename"。</returns>
        public string[] CalculateSHA256(string[] paths, Action<int> progress = null, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (paths == null || paths.Length == 0) return new string[0];

            // 先展开为具体文件列表，并统计总大小，用于整体进度百分比
            var files = new List<string>();
            foreach (var path in paths)
            {
                if (string.IsNullOrWhiteSpace(path)) continue;
                try
                {
                    string fullPath = Path.GetFullPath(path);
                    if (File.Exists(fullPath))
                    {
                        files.Add(fullPath);
                    }
                    else if (Directory.Exists(fullPath))
                    {
                        files.AddRange(Directory.EnumerateFiles(fullPath, "*", SearchOption.AllDirectories));
                    }
                }
                catch
                {
                    // 无法访问的路径直接跳过
                }
            }

            long totalSize = 0;
            var fileSizes = new long[files.Count];
            for (int i = 0; i < files.Count; i++)
            {
                try
                {
                    fileSizes[i] = new FileInfo(files[i]).Length;
                    totalSize += fileSizes[i];
                }
                catch
                {
                    fileSizes[i] = 0;
                }
            }

             var results = new List<string>(files.Count);
             byte[] buffer = new byte[BufferSize];
             long processedSize = 0;
             DateTime lastUi = DateTime.MinValue;
             int lastPercent = -1;

             for (int i = 0; i < files.Count; i++)
             {
                 cancellationToken.ThrowIfCancellationRequested();

                 string filePath = files[i];
                 try
                 {
                     using (var sha256 = _createHash())
                     using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.SequentialScan))
                     {
                        int bytesRead;
                        while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            cancellationToken.ThrowIfCancellationRequested();

                            sha256.TransformBlock(buffer, 0, bytesRead, buffer, 0);
                            processedSize += bytesRead;

                            var now = DateTime.UtcNow;
                            if ((now - lastUi).TotalMilliseconds >= UiRefreshMs)
                            {
                                lastUi = now;
                                int percent = GetPercent(processedSize, totalSize);
                                if (percent != lastPercent)
                                {
                                    lastPercent = percent;
                                    progress?.Invoke(percent);
                                }
                            }
                        }

                        sha256.TransformFinalBlock(buffer, 0, 0);
                        results.Add(string.Format("{0} *{1}", ToHexLower(sha256.Hash), Path.GetFileName(filePath)));
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    // 单个文件失败（被占用/无权限）时用已完成的大小补齐进度，不打断整体流程
                    processedSize += fileSizes[i];
                }

                int current = GetPercent(processedSize, totalSize);
                if (current != lastPercent)
                {
                    lastPercent = current;
                    progress?.Invoke(current);
                }
            }

            progress?.Invoke(100);
            return results.ToArray();
        }

        private static int GetPercent(long processed, long total)
        {
            if (total <= 0) return 100;
            int percent = (int)(processed * 100 / total);
            if (percent < 0) return 0;
            return percent > 100 ? 100 : percent;
        }

        /// <summary>
        /// 将 SHA256 结果写入文件。
        /// </summary>
        /// <param name="sha256Path">输出的 .sha256 文件路径。</param>
        /// <param name="sha256Results">SHA256 计算结果数组。</param>
        /// <param name="encoding">输出文件编码，默认 UTF-8（无 BOM）。</param>
        public void WriteSHA256File(string sha256Path, IEnumerable<string> sha256Results, Encoding encoding = null)
        {
            if (string.IsNullOrWhiteSpace(sha256Path) || sha256Results == null) return;

            // 默认用 UTF-8（无 BOM）：GBK 遇到非中文字符集的文件名会产生乱码/丢字，
            // 而 BOM 会让不少校验解析软件把首行哈希当成无效内容
            encoding = encoding ?? new UTF8Encoding(false);

            using (var writer = new StreamWriter(sha256Path, false, encoding))
            {
                foreach (var sha256 in sha256Results)
                {
                    writer.WriteLine(sha256);
                }
            }
        }

        /// <summary>
        /// 计算文件或文件夹内所有文件的 SHA256，并为每个文件写入同名 .sha256 文件。
        /// </summary>
        public void CalculateSHA256ToFile(string filePathOrFolder, Action<int> progress = null, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (string.IsNullOrWhiteSpace(filePathOrFolder)) return;

            if (File.Exists(filePathOrFolder))
            {
                var results = CalculateSHA256(new[] { filePathOrFolder }, progress, cancellationToken);
                if (results.Length > 0)
                {
                    WriteSHA256File(filePathOrFolder + ".sha256", results);
                }
                return;
            }

            if (!Directory.Exists(filePathOrFolder)) return;

            var files = Directory.GetFiles(filePathOrFolder, "*", SearchOption.AllDirectories)
                .Where(f => !f.EndsWith(".sha256", StringComparison.OrdinalIgnoreCase))
                .ToArray();

            int total = files.Length;
            for (int i = 0; i < total; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var results = CalculateSHA256(new[] { files[i] }, null, cancellationToken);
                    if (results.Length > 0)
                    {
                        WriteSHA256File(files[i] + ".sha256", results);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    // 忽略单个文件失败
                }

                progress?.Invoke(total > 0 ? (i + 1) * 100 / total : 100);
            }
        }

        /// <summary>
        /// 查表把字节转小写 hex，避免多次字符串分配。
        /// </summary>
        private static string ToHexLower(byte[] bytes)
        {
            if (bytes == null) return string.Empty;

            var chars = new char[bytes.Length * 2];
            for (int i = 0; i < bytes.Length; i++)
            {
                byte b = bytes[i];
                chars[i * 2] = _hexLower[b >> 4];
                chars[i * 2 + 1] = _hexLower[b & 0x0F];
            }
            return new string(chars);
        }

        public void Dispose()
        {
            // 本类不持有需要释放的非托管资源，保留 Dispose 以支持 using 语法。
            GC.SuppressFinalize(this);
        }
    }
}
