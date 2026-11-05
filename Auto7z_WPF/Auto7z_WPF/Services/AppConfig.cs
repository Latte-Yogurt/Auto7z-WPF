using Auto7z_WPF.Models;

namespace Auto7z_WPF.Services
{
    public class AppConfig
    {
        #region 可持久化设置

        public string Language { get; set; } = "zh-CN";

        public string Volume { get; set; } = "2000";

        public ArchiveFormat Format { get; set; } = ArchiveFormat.SevenZip;

        public string Password { get; set; } = string.Empty;

        public bool Zstd { get; set; } = true;

        public bool GenerateSHA256 { get; set; } = true;

        public bool AutoSave { get; set; } = true;

        public bool MaxLevelCompress { get; set; } = false;

        public bool IsTopMost { get; set; } = false;

        public bool IsPortable { get; set; } = false;

        public RuntimeModePreset RuntimeMode { get; set; } = RuntimeModePreset.Balanced;

        public string OutputPath { get; set; } = string.Empty;

        public bool IsSolidMode { get; set; } = false;

        public bool IsEncFileName { get; set; } = false;

        public CompressionLevelPreset CompressionLevel { get; set; } = CompressionLevelPreset.Normal;

        public string UnzipPassword { get; set; } = string.Empty;

        public long SevenZUsageCount { get; set; } = 0;

        // 界面上没有对应控件，只能直接改配置文件；仍放在这一区是因为它会被写回 Auto7z.json。
        public LogService.ErrorLevel ErrorLevel { get; set; } = LogService.ErrorLevel.Warning;

        #endregion

        #region 运行时状态（不持久化）

        // 只用于分卷判断的输入总大小：BuildCompressConfig 填入，SevenZipService.AppendVolumeSwitch 读取。
        public long TotalSize { get; set; }

        #endregion
    }
}
