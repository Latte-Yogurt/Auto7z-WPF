using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Auto7z_WPF.Models
{
    public sealed class PendingFile
    {
        private bool _sizeKnown;

        public PendingFile(string path, long sizeInBytes, bool isDirectory)
        {
            Path = path;
            SizeInBytes = sizeInBytes;
            IsDirectory = isDirectory;
            _sizeKnown = true;
        }

        // 界面线程只做存在性检查，目录字节数留到任务线程再算：
        // 拖一个几万文件的目录进来时，递归遍历整棵树足以让窗口冻住。
        public static PendingFile CreateLazy(string path, bool isDirectory)
        {
            var file = new PendingFile(path, 0, isDirectory);
            file._sizeKnown = false;
            return file;
        }

        public string Path { get; }

        public long SizeInBytes { get; private set; }

        public bool SizeKnown => _sizeKnown;

        public void SetSize(long sizeInBytes)
        {
            // 只允许从未知变为已知：大小一旦统计过就不再改，
            // 否则并发的两次统计会互相覆盖，分卷判断跟着抖。
            if (_sizeKnown) return;
            _sizeKnown = true;
            SizeInBytes = sizeInBytes < 0 ? 0 : sizeInBytes;
        }

        public bool IsDirectory { get; }

        public string Name => System.IO.Path.GetFileName(Path);
    }

    public sealed class PendingFileBatch
    {
        public PendingFileBatch(IEnumerable<PendingFile> files)
        {
            Files = (files ?? Enumerable.Empty<PendingFile>()).ToList();
        }

        public IReadOnlyList<PendingFile> Files { get; }

        public bool IsEmpty => Files.Count == 0;

        public long TotalSizeInBytes => Files.Sum(f => f.SizeInBytes);

        // 这个大小只用于分卷判断，晚一点算没有副作用，所以整批一次补齐即可。
        public void EnsureSizes(Func<string, long> compute)
        {
            if (compute == null) return;

            foreach (var file in Files)
            {
                if (!file.SizeKnown) file.SetSize(compute(file.Path));
            }
        }
    }
}
