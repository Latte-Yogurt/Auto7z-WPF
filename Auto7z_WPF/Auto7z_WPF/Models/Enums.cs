namespace Auto7z_WPF.Models
{
    /// <summary>
    /// 归档格式。持久化时使用名称（7z/zip/tar），与界面显示和语言无关。
    /// </summary>
    public enum ArchiveFormat
    {
        SevenZip = 0,
        Zip = 1,
        Tar = 2
    }

    /// <summary>
    /// 压缩等级。持久化时使用名称，界面文本由语言资源提供。
    /// </summary>
    public enum CompressionLevelPreset
    {
        Store = 0,
        Normal = 1,
        Ultra = 2
    }

    /// <summary>
    /// 运行模式（资源占用档位）。持久化时使用名称，界面文本由语言资源提供。
    /// </summary>
    public enum RuntimeModePreset
    {
        Low = 0,
        Balanced = 1,
        High = 2
    }

    /// <summary>
    /// 主界面操作模式。
    /// </summary>
    public enum OperationMode
    {
        Auto = 0,
        Zip = 1,
        UnZip = 2
    }

    /// <summary>
    /// 程序本次的运行方式。
    ///
    /// 三种方式在「要不要界面」「信息输出到哪」「怎么问用户」上完全不同，
    /// 所以显式区分开，不要靠「有没有主窗口」去反推。
    /// </summary>
    public enum RunMode
    {
        /// <summary>
        /// 图形界面模式：用户双击启动。主窗口在场，
        /// 进度显示在主窗口的提示区，需要确认时弹对话框。
        /// </summary>
        Gui = 0,

        /// <summary>
        /// 命令行模式：用户在 cmd 里带参数启动。
        /// 一切信息写到控制台，多文件合并用控制台问答（Y/N）。
        /// </summary>
        Console = 1,

        /// <summary>
        /// 右键菜单模式：用户在资源管理器里右键触发。
        /// 永远不建主窗口；进度用独立的进度窗口显示，
        /// 需要确认时弹小对话框。
        /// </summary>
        ShellMenu = 2
    }
}
