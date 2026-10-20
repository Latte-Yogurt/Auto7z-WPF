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
}
