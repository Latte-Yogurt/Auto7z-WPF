using Auto7z_WPF.Models;

namespace Auto7z_WPF.Services
{
    public class AppConfig
    {
        #region 可持久化设置

        // 默认值必须与 ConfigService 的兜底同源：配置读不出来时走的是这里，
        // 而正常读取时走的是 ConfigService 的 LanguageService.FallbackLanguage。
        // 两处各自写死一个字符串，迟早会漂移成「读失败就变中文」这类问题。
        public string Language { get; set; } = LanguageService.FallbackLanguage;

        public string Volume { get; set; } = "2000";

        public ArchiveFormat Format { get; set; } = ArchiveFormat.SevenZip;

        public string Password { get; set; } = string.Empty;

        public bool Zstd { get; set; } = true;

        public bool GenerateSHA256 { get; set; } = true;

        // 校验文件的算法标识（sha256 / sha3-256 / blake3 …），对应 HashAlgorithms 的目录项。
        // 存标识而不是显示名：显示名跟语言走，标识不跟。
        //
        // 初值取 HashAlgorithms.Default.Id，不写死字符串：默认算法只在 HashAlgorithms 里定义一次，
        // 这里跟着走，改默认值时不会漏掉本处（HashAlgorithms 不反向依赖 AppConfig，无循环初始化）。
        public string ChecksumAlgorithm { get; set; } = HashAlgorithms.Default.Id;

        public bool AutoSave { get; set; } = true;

        public bool IsTopMost { get; set; } = false;

        // 便携模式不在这里：它由「程序目录旁有没有 .config\Auto7z」这个磁盘事实决定，
        // 不再是一条可以写进配置、并可能与实际目录自相矛盾的记录（见 AppPaths）。

        public RuntimeModePreset RuntimeMode { get; set; } = RuntimeModePreset.Balanced;

        public string OutputPath { get; set; } = string.Empty;

        public bool IsSolidMode { get; set; } = false;

        public bool IsEncFileName { get; set; } = false;

        public CompressionLevelPreset CompressionLevel { get; set; } = CompressionLevelPreset.Normal;

        public string UnzipPassword { get; set; } = string.Empty;

        // 界面上没有对应控件，只能直接改配置文件；仍放在这一区是因为它会被写回 Auto7z.json。
        public LogService.ErrorLevel ErrorLevel { get; set; } = LogService.ErrorLevel.Warning;

        #endregion

        #region 运行时状态（不持久化）

        // 只用于分卷判断的输入总大小：BuildCompressConfig 填入，SevenZipService.AppendVolumeSwitch 读取。
        public long TotalSize { get; set; }

        #endregion
    }
}
