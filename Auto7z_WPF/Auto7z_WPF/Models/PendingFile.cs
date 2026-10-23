using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Auto7z_WPF.Models
{
    /// <summary>
    /// 拖入/传入的单个条目（文件或文件夹）及其大小快照。
    /// 大小在“接收拖放”时计算一次，避免任务执行期间重复递归遍历目录。
    /// </summary>
    public sealed class PendingFile
    {
        public PendingFile(string path, long sizeInBytes, bool isDirectory)
        {
            Path = path;
            SizeInBytes = sizeInBytes;
            IsDirectory = isDirectory;
        }

        /// <summary>完整路径。</summary>
        public string Path { get; }

        /// <summary>大小（字节）。无法读取时为 0。</summary>
        public long SizeInBytes { get; }

        /// <summary>是否为文件夹。</summary>
        public bool IsDirectory { get; }

        /// <summary>名称（文件含扩展名，文件夹为目录名）。</summary>
        public string Name => System.IO.Path.GetFileName(Path);
    }

    /// <summary>
    /// 一次任务的输入批次。
    /// </summary>
    public sealed class PendingFileBatch
    {
        public PendingFileBatch(IEnumerable<PendingFile> files)
        {
            Files = (files ?? Enumerable.Empty<PendingFile>()).ToList();
        }

        public IReadOnlyList<PendingFile> Files { get; }

        public bool IsEmpty => Files.Count == 0;

        /// <summary>
        /// 批次总大小（字节）。成功统计的条目累加，读不到大小的条目返回 null 标记。
        /// </summary>
        public long TotalSizeInBytes => Files.Sum(f => f.SizeInBytes);
    }
}
