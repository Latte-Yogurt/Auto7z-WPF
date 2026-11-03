using System;
using System.Collections.Generic;
using System.Windows;
using Auto7z_WPF.Models;

namespace Auto7z_WPF.Services
{
    /// <summary>
    /// 枚举与资源文案之间的双向映射，界面文本一律经此取用，换语言后各处保持一致。
    /// </summary>
    public static class EnumText
    {
        #region 文案缓存
        // 后台压缩线程也要取文案，而 ResourceDictionary 只能在其所属 UI 线程访问，
        // 因此把字符串拷进只读字典，供任意线程安全读取。
        private static volatile IReadOnlyDictionary<string, string> _textCache;

        // 与文案缓存一同更新；声明为 volatile，读线程不加锁也能看到最新语言代号。
        private static volatile string _cacheLanguage;

        private const string RuntimeModeLowKey = "RuntimeMode_Low";
        private const string RuntimeModeBalancedKey = "RuntimeMode_Balanced";
        private const string RuntimeModeHighKey = "RuntimeMode_High";

        private const string LevelStoreKey = "Level_Store";
        private const string LevelNormalKey = "Level_Normal";
        private const string LevelUltraKey = "Level_Ultra";

        // 每次切换语言整体重建而不是增量合并，避免旧语言的残留键继续生效；
        // 只收录非空字符串，样式与模板不进缓存。
        public static void RebuildTextCache(ResourceDictionary dictionary, string languageCode)
        {
            // 资源键大小写固定，用 Ordinal 比较既快又不会因区域性规则误匹配。
            var cache = new Dictionary<string, string>(StringComparer.Ordinal);

            if (dictionary != null)
            {
                foreach (object key in dictionary.Keys)
                {
                    if (key is string name && !string.IsNullOrEmpty(name))
                    {
                        var value = dictionary[name] as string;
                        if (!string.IsNullOrEmpty(value)) cache[name] = value;
                    }
                }
            }

            // 先换缓存再换语言标记，读方看到新语言时文案必定已经就绪。
            _textCache = cache;
            _cacheLanguage = languageCode ?? string.Empty;
        }

        // 缓存对应的语言代号，调用方据此判断手里的本地文本是否已经过期。
        public static string CachedLanguage => _cacheLanguage ?? string.Empty;

        #endregion
        #region 资源查询与格式化
        // 缓存未命中时再查应用资源，最后退回调用方给的兜底文本，界面不会出现空串或裸露的资源键。
        public static string GetResourceText(string key, string fallback)
        {
            var cache = _textCache;
            if (!string.IsNullOrEmpty(key) && cache != null && cache.TryGetValue(key, out var cached))
            {
                if (!string.IsNullOrEmpty(cached)) return cached;
            }

            try
            {
                // Application.Current 在非 UI 线程或未启动的宿主里不可用，查不到就按兜底处理。
                var app = Application.Current;
                if (app != null)
                {
                // TryFindResource 在键不存在时返回 null 而不抛异常，正适合这种逐级回退的查找。
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

        // 占位符与参数不匹配时退回原文，文案问题不该让界面抛异常。
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
                return text;
            }
        }

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
                return text;
            }
        }


        #endregion
        #region 预设枚举文案
        // 资源键集中成常量，正向取文案与反向解析共用同一套键，避免两边写错对不上。
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

        // 资源缺失时退回枚举名，至少还能看出当前模式。
        public static string ToText(RuntimeModePreset mode)
        {
            return GetResourceText(GetRuntimeModeKey(mode), mode.ToString());
        }

        // 先认枚举名，兼容旧配置与英文环境；再按当前语言显示文本反查；都失败才回落默认值。
        public static RuntimeModePreset ParseRuntimeMode(string text)
        {
            if (!string.IsNullOrWhiteSpace(text))
            {
                // Enum.TryParse 会把任意数字当成有效枚举，必须再用 IsDefined 拦掉未定义值。
                if (Enum.TryParse(text.Trim(), true, out RuntimeModePreset parsed) && Enum.IsDefined(typeof(RuntimeModePreset), parsed))
                    return parsed;

                if (string.Equals(text.Trim(), ToText(RuntimeModePreset.Low), StringComparison.Ordinal)) return RuntimeModePreset.Low;
                if (string.Equals(text.Trim(), ToText(RuntimeModePreset.High), StringComparison.Ordinal)) return RuntimeModePreset.High;
                if (string.Equals(text.Trim(), ToText(RuntimeModePreset.Balanced), StringComparison.Ordinal)) return RuntimeModePreset.Balanced;
            }
            return RuntimeModePreset.Balanced;
        }


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

        // 压缩等级同样先认枚举名，再按当前语言的显示文本反查。
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
        #region 格式与操作模式文案
        // 存档格式的显示文本与命令行开关同源，界面显示不会和实际 -t 参数脱节。
        public static string ToText(ArchiveFormat format)
        {
            return ArchiveFormatMap.ToSwitchValue(format);
        }

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

        // 兜底文案用英文，资源缺失时仍能提示用户拖放。
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
