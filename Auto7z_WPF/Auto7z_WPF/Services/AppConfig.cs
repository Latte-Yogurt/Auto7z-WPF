using Auto7z_WPF.Models;

namespace Auto7z_WPF.Services
{
    /// <summary>
    /// 应用程序配置模型。
    /// 可持久化的设置全部使用枚举/基本类型，界面显示文本由语言资源单独提供，
    /// 因此切换语言不会让已保存的设置失效。
    /// </summary>
    public class AppConfig
    {
        #region 可持久化设置

        public string Language { get; set; } = "zh-CN";

        /// <summary>分卷大小（MB）。</summary>
        public string Volume { get; set; } = "2000";

        public ArchiveFormat Format { get; set; } = ArchiveFormat.SevenZip;

        /// <summary>压缩密码。</summary>
        public string Password { get; set; } = string.Empty;

        /// <summary>tar 是否使用 zstd 压缩。</summary>
        public bool Zstd { get; set; } = true;

        /// <summary>压缩完成后是否生成 .md5 校验文件。</summary>
        public bool GenerateMd5 { get; set; } = true;

        /// <summary>程序关闭时自动保存设置。</summary>
        public bool AutoSave { get; set; } = true;

        /// <summary>极限压缩（等价于最高压缩等级）。</summary>
        public bool MaxLevelCompress { get; set; } = false;

        public bool IsTopMost { get; set; } = false;

        /// <summary>是否为便携模式（数据目录放在程序目录的 .config 下）。</summary>
        public bool IsPortable { get; set; } = false;

        public RuntimeModePreset RuntimeMode { get; set; } = RuntimeModePreset.Balanced;

        /// <summary>输出目录；为空表示跟随输入文件所在目录。</summary>
        public string OutputPath { get; set; } = string.Empty;

        public bool IsSolidMode { get; set; } = false;

        /// <summary>是否加密文件名（仅 7z，且需要密码）。</summary>
        public bool IsEncFileName { get; set; } = false;

        public CompressionLevelPreset CompressionLevel { get; set; } = CompressionLevelPreset.Normal;

        /// <summary>解压密码。</summary>
        public string UnzipPassword { get; set; } = string.Empty;

        public long SevenZUsageCount { get; set; } = 0;

        /// <summary>
        /// 日志级别（Info / Warning / Error）。
        /// 这是配置中的隐藏开关，界面上没有对应控件，直接编辑配置文件即可调整。
        /// </summary>
        public LogService.ErrorLevel ErrorLevel { get; set; } = LogService.ErrorLevel.Warning;

        #endregion

        #region 运行时状态（不持久化）

        /// <summary>本次任务的输入总字节数，用于分卷判断。</summary>
        public long TotalSize { get; set; }

        /// <summary>归档主文件名（不含扩展名）。</summary>
        public string FileName { get; set; }

        /// <summary>当前任务状态。</summary>
        public AppState.OperationStatus CurrentRunStatus { get; set; } = AppState.OperationStatus.Normal;

        #endregion
    }
}
