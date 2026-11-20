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

    /// <summary>
    /// 一次体积统计的结果：字节数 + 因打不开/枚举中断而整棵跳过的子树数量。
    ///
    /// 用结构体而不是 `out` 参数，是因为 C# 7.0 的 lambda 不能声明 `out` 参数
    /// （`Func&lt;string, out int, long&gt;` 无法写成 lambda），而调用方恰恰需要
    /// 把一个「带 skipped 明细」的统计函数当参数传进来。
    /// </summary>
    public struct SizeProbeResult
    {
        /// <summary>统计到的字节数；-1 表示整条路径都读不出来。</summary>
        public long Bytes;

        /// <summary>读到了、但这几棵子树被跳过；&gt; 0 即「没读全」。</summary>
        public int SkippedDirectories;
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
            EnsureSizes(compute, out _);
        }

        /// <summary>
        /// 补齐体积，并回报有多少条目的体积没读全。
        ///
        /// 这个数字只用于留痕，不影响压缩本身：体积算少一点只会让分卷判断偏保守，
        /// 而读失败不该让一次压缩中断。但「算少了」必须能被看见，
        /// 否则用户遇到「该分卷却没分」时日志里一个字都没有。
        /// </summary>
        public void EnsureSizes(Func<string, long> compute, out int incompleteCount)
        {
            incompleteCount = 0;
            if (compute == null) return;

            foreach (var file in Files)
            {
                if (file.SizeKnown) continue;

                long size = compute(file.Path);
                file.SetSize(size);

                // SetSize 会把负数夹成 0，所以必须在这里判断原始返回值。
                if (size < 0) incompleteCount++;
            }
        }

        /// <summary>
        /// 带明细的版本：统计函数同时回报该路径下被跳过的子树数量。
        ///
        /// 两种不完整都计入 incompleteCount：
        /// - 整条路径读不出来（<see cref="SizeProbeResult.Bytes"/> 为 -1）；
        /// - 路径本身统计到了，但其中若干棵子树被跳过（SkippedDirectories &gt; 0）。
        /// 后者尤其容易漏掉 —— 早先连计数都没有，`GetSizeInBytes` 只回一个数字，
        /// 调用方分不清「确实只有这么大」与「少读了几棵子树」。
        /// </summary>
        public void EnsureSizes(Func<string, SizeProbeResult> compute, out int incompleteCount)
        {
            incompleteCount = 0;
            if (compute == null) return;

            foreach (var file in Files)
            {
                if (file.SizeKnown) continue;

                SizeProbeResult probe = compute(file.Path);
                file.SetSize(probe.Bytes);

                if (probe.Bytes < 0 || probe.SkippedDirectories > 0) incompleteCount++;
            }
        }
    }
}
