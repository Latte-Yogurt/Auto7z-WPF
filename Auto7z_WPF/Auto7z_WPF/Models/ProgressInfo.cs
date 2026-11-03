namespace Auto7z_WPF.Models
{
    public sealed class ProgressInfo
    {
        // 在这里夹紧而不是让调用方各自判断：7-Zip 的百分比流偶尔会越界，
        // 一旦把 >100 传给进度条，WPF 会直接抛异常。
        public ProgressInfo(int percent, ProgressStage stage)
        {
            Percent = percent < 0 ? 0 : (percent > 100 ? 100 : percent);
            Stage = stage;
        }

        public int Percent { get; }

        public ProgressStage Stage { get; }
    }

    public enum ProgressStage
    {
        Idle = 0,
        Compressing = 1,
        Extracting = 2,
        CalculatingSHA256 = 3,
        Finished = 4,
        Failed = 5
    }
}
