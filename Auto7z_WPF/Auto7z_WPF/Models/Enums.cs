namespace Auto7z_WPF.Models
{
    public enum ArchiveFormat
    {
        SevenZip = 0,
        Zip = 1,
        Tar = 2
    }

    public enum CompressionLevelPreset
    {
        Store = 0,
        Normal = 1,
        Ultra = 2
    }

    public enum RuntimeModePreset
    {
        Low = 0,
        Balanced = 1,
        High = 2
    }

    public enum OperationMode
    {
        Auto = 0,
        Zip = 1,
        UnZip = 2
    }

    /// <summary>
    /// 结果行的三态：成功、成功但有警告、失败。
    ///
    /// 只留「失败/成功」两态时，带警告的成功会被涂成红色 —— 那与语义相反：
    /// 归档确实产出了，只是少了几个条目，退出码给 1 已经足够表达「未完全成功」。
    ///
    /// <see cref="Success"/> 必须是 0：越界值走 default 分支时落在这里，
    /// 意味着「认不出的严重度按成功处理」，而不是把它误报成失败。
    /// </summary>
    public enum ResultSeverity
    {
        Success = 0,
        Warning = 1,
        Failed = 2
    }

    // 运行方式由编译期与启动方式共同决定，不能按「有没有主窗口」反推：
    // 三种模式连「信息往哪写、怎么问用户」都不同，混用会把控制台输出漏进 GUI 进程。
    public enum RunMode
    {
        Gui = 0,

        Console = 1,

        ShellMenu = 2
    }
}
