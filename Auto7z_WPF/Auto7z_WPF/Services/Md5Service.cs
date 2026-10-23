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
    /// MD5 计算服务。提供文件与目录的 MD5 计算，支持大文件流式读取与进度回调。
    /// 进度基于“已处理字节 / 总字节”，一次任务内单调递增，不再每个文件都从 0 跳到 100。
    /// </summary>
    public class Md5Service : IDisposable
    {
        private const int BufferSize = 1024 * 1024;    // 1MB 缓冲区，避免进入大对象堆
        private const int UiRefreshMs = 100;           // 进度回调节流间隔

        private static readonly char[] _hexLower = "0123456789abcdef".ToCharArray();

        /// <summary>
        /// 计算一个或多个文件/文件夹内所有文件的 MD5。
        /// </summary>
        /// <param name="paths">要计算 MD5 的文件或文件夹路径数组。</param>
        /// <param name="progress">进度回调，参数为当前百分比（0-100），可为 null。</param>
        /// <param name="cancellationToken">取消标记。</param>
        /// <returns>每个文件的 MD5 结果字符串，格式为 "hex *filename"。</returns>
        public string[] CalculateMd5(string[] paths, Action<int> progress = null, CancellationToken cancellationToken = default(CancellationToken))
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
                    using (var md5 = MD5.Create())
                    using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.SequentialScan))
                    {
                        int bytesRead;
                        while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            cancellationToken.ThrowIfCancellationRequested();

                            md5.TransformBlock(buffer, 0, bytesRead, buffer, 0);
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

                        md5.TransformFinalBlock(buffer, 0, 0);
                        results.Add(string.Format("{0} *{1}", ToHexLower(md5.Hash), Path.GetFileName(filePath)));
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
        /// 将 MD5 结果写入文件。
        /// </summary>
        /// <param name="md5Path">输出的 .md5 文件路径。</param>
        /// <param name="md5Results">MD5 计算结果数组。</param>
        /// <param name="encoding">输出文件编码，默认 UTF-8（带 BOM）。</param>
        public void WriteMd5File(string md5Path, IEnumerable<string> md5Results, Encoding encoding = null)
        {
            if (string.IsNullOrWhiteSpace(md5Path) || md5Results == null) return;

            // 默认用 UTF-8：GBK 遇到非中文字符集的文件名会产生乱码/丢字
            encoding = encoding ?? new UTF8Encoding(true);

            using (var writer = new StreamWriter(md5Path, false, encoding))
            {
                foreach (var md5 in md5Results)
                {
                    writer.WriteLine(md5);
                }
            }
        }

        /// <summary>
        /// 计算文件或文件夹内所有文件的 MD5，并为每个文件写入同名 .md5 文件。
        /// </summary>
        public void CalculateMd5ToFile(string filePathOrFolder, Action<int> progress = null, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (string.IsNullOrWhiteSpace(filePathOrFolder)) return;

            if (File.Exists(filePathOrFolder))
            {
                var results = CalculateMd5(new[] { filePathOrFolder }, progress, cancellationToken);
                if (results.Length > 0)
                {
                    WriteMd5File(filePathOrFolder + ".md5", results);
                }
                return;
            }

            if (!Directory.Exists(filePathOrFolder)) return;

            var files = Directory.GetFiles(filePathOrFolder, "*", SearchOption.AllDirectories)
                .Where(f => !f.EndsWith(".md5", StringComparison.OrdinalIgnoreCase))
                .ToArray();

            int total = files.Length;
            for (int i = 0; i < total; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var results = CalculateMd5(new[] { files[i] }, null, cancellationToken);
                    if (results.Length > 0)
                    {
                        WriteMd5File(files[i] + ".md5", results);
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
        }
    }
}
