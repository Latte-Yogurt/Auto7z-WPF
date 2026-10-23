namespace Auto7z_WPF.Models
{
    /// <summary>
    /// 进度上报数据。文本只在 UI 线程通过语言资源生成，Service 层不接触本地化文本。
    /// </summary>
    public sealed class ProgressInfo
    {
        public ProgressInfo(int percent, ProgressStage stage)
        {
            Percent = percent < 0 ? 0 : (percent > 100 ? 100 : percent);
            Stage = stage;
        }

        /// <summary>百分比（0-100）。</summary>
        public int Percent { get; }

        /// <summary>当前阶段。</summary>
        public ProgressStage Stage { get; }
    }

    /// <summary>
    /// 任务阶段，供 ViewModel 映射为本地化文本。
    /// </summary>
    public enum ProgressStage
    {
        Idle = 0,
        Compressing = 1,
        Extracting = 2,
        CalculatingMd5 = 3,
        Finished = 4,
        Failed = 5
    }
}
