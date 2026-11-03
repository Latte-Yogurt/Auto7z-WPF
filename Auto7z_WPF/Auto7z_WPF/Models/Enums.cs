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

    // 运行方式是编译期与启动方式共同决定的，不靠"有没有主窗口"反推：
    // 三者连"信息往哪写、怎么问用户"都不一样，混用会把控制台输出漏进 GUI 进程。
    public enum RunMode
    {
        Gui = 0,

        Console = 1,

        ShellMenu = 2
    }
}
