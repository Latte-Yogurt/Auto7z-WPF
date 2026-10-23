using System;
using System.Windows;
using Auto7z_WPF.Models;

namespace Auto7z_WPF.Services
{
    /// <summary>
    /// 枚举与界面显示文本之间的转换。
    /// 界面文本只用于显示和读取下拉框，配置持久化一律使用枚举名称，
    /// 因此切换语言不会让已保存的设置失效。
    /// </summary>
    public static class EnumText
    {
        // 运行模式资源键
        private const string RuntimeModeLowKey = "RuntimeMode_Low";
        private const string RuntimeModeBalancedKey = "RuntimeMode_Balanced";
        private const string RuntimeModeHighKey = "RuntimeMode_High";

        // 压缩等级资源键
        private const string LevelStoreKey = "Level_Store";
        private const string LevelNormalKey = "Level_Normal";
        private const string LevelUltraKey = "Level_Ultra";

        public static string GetResourceText(string key, string fallback)
        {
            try
            {
                var app = Application.Current;
                if (app != null)
                {
                    var value = app.TryFindResource(key) as string;
                    if (!string.IsNullOrEmpty(value)) return value;
                }
            }
            catch
            {
                // 资源系统不可用时退回默认文本
            }
            return fallback;
        }

        /// <summary>
        /// 取带占位符的资源文本，并做格式化。
        ///
        /// 与 <see cref="GetErrorText"/> 的区别：那个是给错误消息用的，
        /// 写日志时带着 "Err_" / "Log_" 前缀区分；这个是给界面上显示的
        /// 普通文案用的，两边各管各的，避免一处改动牵连另一处。
        /// </summary>
        /// <param name="key">资源键。</param>
        /// <param name="fallback">默认文本（可含 {0} 占位符）。</param>
        /// <param name="arg">格式化参数。</param>
        /// <returns>格式化后的文本。</returns>
        public static string GetFormattedText(string key, string fallback, params object[] arg)
        {
            string text = GetResourceText(key, fallback);
            if (string.IsNullOrEmpty(text)) return text;
            if (arg == null || arg.Length == 0) return text;

            try
            {
                return string.Format(text, arg);
            }
            catch
            {
                // 占位符不匹配时直接返回原文
                return text;
            }
        }

        /// <summary>
        /// 取带占位符的错误消息文本，并做格式化。
        /// 取不到资源时退回默认文本。
        /// </summary>
        /// <param name="key">资源键。</param>
        /// <param name="fallback">默认文本（可含 {0} 占位符）。</param>
        /// <param name="arg">格式化参数。</param>
        public static string GetErrorText(string key, string fallback, params object[] arg)
        {
            string text = GetResourceText(key, fallback);
            if (string.IsNullOrEmpty(text)) return text;

            if (arg == null || arg.Length == 0) return text;

            try
            {
                return string.Format(text, arg);
            }
            catch
            {
                // 占位符不匹配时直接返回原文
                return text;
            }
        }

        #region 运行模式

        public static string GetRuntimeModeKey(RuntimeModePreset mode)
        {
            switch (mode)
            {
                case RuntimeModePreset.Low: return RuntimeModeLowKey;
                case RuntimeModePreset.High: return RuntimeModeHighKey;
                case RuntimeModePreset.Balanced:
                default: return RuntimeModeBalancedKey;
            }
        }

        public static string ToText(RuntimeModePreset mode)
        {
            return GetResourceText(GetRuntimeModeKey(mode), mode.ToString());
        }

        public static RuntimeModePreset ParseRuntimeMode(string text)
        {
            if (!string.IsNullOrWhiteSpace(text))
            {
                // 先按枚举名解析（配置文件中的持久化形式）
                if (Enum.TryParse(text.Trim(), true, out RuntimeModePreset parsed) && Enum.IsDefined(typeof(RuntimeModePreset), parsed))
                    return parsed;

                // 再按当前语言的显示文本解析（用户直接编辑 XML 的情况）
                if (string.Equals(text.Trim(), ToText(RuntimeModePreset.Low), StringComparison.Ordinal)) return RuntimeModePreset.Low;
                if (string.Equals(text.Trim(), ToText(RuntimeModePreset.High), StringComparison.Ordinal)) return RuntimeModePreset.High;
                if (string.Equals(text.Trim(), ToText(RuntimeModePreset.Balanced), StringComparison.Ordinal)) return RuntimeModePreset.Balanced;
            }
            return RuntimeModePreset.Balanced;
        }

        #endregion

        #region 压缩等级

        public static string GetCompressionLevelKey(CompressionLevelPreset level)
        {
            switch (level)
            {
                case CompressionLevelPreset.Store: return LevelStoreKey;
                case CompressionLevelPreset.Ultra: return LevelUltraKey;
                case CompressionLevelPreset.Normal:
                default: return LevelNormalKey;
            }
        }

        public static string ToText(CompressionLevelPreset level)
        {
            return GetResourceText(GetCompressionLevelKey(level), level.ToString());
        }

        public static CompressionLevelPreset ParseCompressionLevel(string text)
        {
            if (!string.IsNullOrWhiteSpace(text))
            {
                if (Enum.TryParse(text.Trim(), true, out CompressionLevelPreset parsed) && Enum.IsDefined(typeof(CompressionLevelPreset), parsed))
                    return parsed;

                if (string.Equals(text.Trim(), ToText(CompressionLevelPreset.Store), StringComparison.Ordinal)) return CompressionLevelPreset.Store;
                if (string.Equals(text.Trim(), ToText(CompressionLevelPreset.Ultra), StringComparison.Ordinal)) return CompressionLevelPreset.Ultra;
                if (string.Equals(text.Trim(), ToText(CompressionLevelPreset.Normal), StringComparison.Ordinal)) return CompressionLevelPreset.Normal;
            }
            return CompressionLevelPreset.Normal;
        }

        #endregion

        #region 压缩格式

        public static string ToText(ArchiveFormat format)
        {
            return ArchiveFormatMap.ToSwitchValue(format);
        }

        #endregion

        #region 操作模式

        public static string GetOperationModeKey(OperationMode mode)
        {
            switch (mode)
            {
                case OperationMode.Zip: return "ZipModeText";
                case OperationMode.UnZip: return "UnZipModeText";
                case OperationMode.Auto:
                default: return "AutoModeText";
            }
        }

        /// <summary>
        /// 操作模式对应的主界面默认提示文本。
        /// </summary>
        public static string ToText(OperationMode mode)
        {
            string fallback;
            switch (mode)
            {
                case OperationMode.Zip: fallback = "Drag & Drop to Compress"; break;
                case OperationMode.UnZip: fallback = "Drag & Drop to Decompress"; break;
                case OperationMode.Auto:
                default: fallback = "Drag & Drop to Compress/Decompress"; break;
            }
            return GetResourceText(GetOperationModeKey(mode), fallback);
        }

        #endregion
    }
}
