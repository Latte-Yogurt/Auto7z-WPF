using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Auto7z_WPF.Models;

namespace Auto7z_WPF.Services
{
    /// <summary>
    /// 应用配置的读写入口。配置是标准 JSON：顶层一个对象，每个设置项对应一个键。
    /// 键序固定、缩进两格，用户可以手工编辑，任何现代 JSON 解析器都能读。
    /// </summary>
    public class ConfigService
    {
        public string ConfigPath => AppPaths.ConfigFilePath;

        #region 键表
        // 键表集中在一处：读取、写回、缺键补齐、默认值全部从这张表推导，
        // 四处各写一份默认值必然漏改一处而静默漂移。
        private static readonly ConfigKey[] _keys =
        {
            new ConfigKey("Language", false),
            new ConfigKey("Volume", false),
            new ConfigKey("Format", false),
            new ConfigKey("Password", false),
            new ConfigKey("Zstd", true),
            new ConfigKey("GenerateSHA256", true),
            new ConfigKey("AutoSave", true),
            new ConfigKey("MaxLevelCompress", true),
            new ConfigKey("IsTopMost", true),
            new ConfigKey("IsPortable", true),
            new ConfigKey("RuntimeMode", false),
            new ConfigKey("OutputPath", false),
            new ConfigKey("IsSolidMode", true),
            new ConfigKey("IsEncFileName", true),
            new ConfigKey("CompressionLevel", false),
            new ConfigKey("UnzipPassword", false),
            new ConfigKey("SevenZUsageCount", false),
            new ConfigKey("ErrorLevel", false)
        };

        private sealed class ConfigKey
        {
            public readonly string Name;

            // 布尔键在写出时按原生 true/false，而不是带引号的 "True"。
            public readonly bool IsBoolean;

            public ConfigKey(string name, bool isBoolean)
            {
                Name = name;
                IsBoolean = isBoolean;
            }
        }
        #endregion

        #region 配置加载与保存
        /// <summary>
        /// 读配置。文件缺失或损坏时落一份默认文件并返回默认值；
        /// 单个键解析失败只回落该键的默认值，不整份作废。
        /// </summary>
        public AppConfig Load()
        {
            var config = new AppConfig();

            // 读取失败不抛给调用方：程序必须始终能启动。
            try
            {
                var values = LoadValues();
                if (values == null) return config;

                // 旧版本留下的、当前版本已不用的键在这里剔除，
                // 让文件只描述当前版本，避免用户以为某个旧键还在生效。
                var cleaned = RemoveDeprecated(values);

                // 缺键补齐：读回来的键数少于键表就说明文件是旧版或手改少了，
                // 补一份完整的写回，保证磁盘与代码一致。
                if (cleaned.Count != values.Count || HasMissingKeys(values))
                {
                    SaveDocument(BuildDocument(values));
                    values = LoadValues();
                    if (values == null) return config;
                }

                ApplyValues(config, values);
            }
            catch (Exception)
            {
                // 读取失败保持默认值
            }

            return config;
        }

        public bool Save(AppConfig config)
        {
            if (config == null) return false;

            try
            {
                // 以现有文件为基底再覆盖已知键：用户手工加的扩展键得以保留。
                var values = LoadValues() ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                RemoveDeprecated(values);

                Set(values, "Language", config.Language);
                Set(values, "Volume", config.Volume);
                Set(values, "Format", ArchiveFormatMap.ToSwitchValue(config.Format));
                Set(values, "Password", config.Password);
                Set(values, "Zstd", config.Zstd.ToString());
                Set(values, "GenerateSHA256", config.GenerateSHA256.ToString());
                Set(values, "AutoSave", config.AutoSave.ToString());
                Set(values, "MaxLevelCompress", config.MaxLevelCompress.ToString());
                Set(values, "IsTopMost", config.IsTopMost.ToString());
                Set(values, "IsPortable", config.IsPortable.ToString());
                Set(values, "RuntimeMode", config.RuntimeMode.ToString());
                Set(values, "OutputPath", config.OutputPath ?? string.Empty);
                Set(values, "IsSolidMode", config.IsSolidMode.ToString());
                Set(values, "IsEncFileName", config.IsEncFileName.ToString());
                Set(values, "CompressionLevel", config.CompressionLevel.ToString());
                Set(values, "UnzipPassword", config.UnzipPassword);
                Set(values, "SevenZUsageCount", config.SevenZUsageCount.ToString(CultureInfo.InvariantCulture));
                Set(values, "ErrorLevel", config.ErrorLevel.ToString());

                return SaveDocument(BuildDocument(values));
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>只改单个键，其余内容原样保留。</summary>
        public bool Update(string key, string value)
        {
            if (string.IsNullOrWhiteSpace(key)) return false;

            try
            {
                var values = LoadValues();
                if (values == null) return false;

                RemoveDeprecated(values);
                Set(values, key, value);
                return SaveDocument(BuildDocument(values));
            }
            catch (Exception)
            {
                return false;
            }
        }
        #endregion

        #region 配置文档读写
        // 读完整份配置为键值表；文件不存在时先落默认文件，损坏时重建。
        private Dictionary<string, string> LoadValues()
        {
            if (!File.Exists(ConfigPath))
            {
                if (!CreateDefaultConfig(ConfigPath)) return null;
            }

            try
            {
                string text = File.ReadAllText(ConfigPath, new System.Text.UTF8Encoding(false));

                var parsed = JsonConfig.Parse(text);
                if (parsed != null) return parsed;
            }
            catch (IOException)
            {
                // 读不出来就重建，与解析失败同路处理。
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }

            // 解析失败：文件损坏，重建一份默认配置。
            if (!CreateDefaultConfig(ConfigPath, forceOverwrite: true)) return null;

            try
            {
                return JsonConfig.Parse(File.ReadAllText(ConfigPath, new System.Text.UTF8Encoding(false)));
            }
            catch (Exception)
            {
                return null;
            }
        }

        // 把键值表按固定键序写成 JSON 文本；不在键表里的扩展键排到最后并保持相对顺序。
        private static string BuildDocument(Dictionary<string, string> values)
        {
            var entries = new List<KeyValuePair<string, string>>(_keys.Length + 8);
            var isBoolean = new List<bool>(_keys.Length + 8);

            foreach (var key in _keys)
            {
                // 缺键按默认值写出：调用方可能只填了部分键。
                string value = values.TryGetValue(key.Name, out string existing) ? existing : DefaultValue(key.Name);
                entries.Add(new KeyValuePair<string, string>(key.Name, value));
                isBoolean.Add(key.IsBoolean);
            }

            foreach (var pair in values)
            {
                if (IsKnownKey(pair.Key)) continue;
                entries.Add(pair);
                isBoolean.Add(false);
            }

            return JsonConfig.Build(entries, isBoolean);
        }

        // 先写临时文件再原子替换：写到一半崩溃不会留下半截 JSON。
        private bool SaveDocument(string content)
        {
            string tempPath = ConfigPath + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath));

                JsonConfig.WriteFile(tempPath, content);

                if (File.Exists(ConfigPath))
                {
                    File.Replace(tempPath, ConfigPath, null, true);
                }
                else
                {
                    File.Move(tempPath, ConfigPath);
                }

                return true;
            }
            catch
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* 有意忽略：残留的临时文件删不掉不影响配置写入结果 */ }
                return false;
            }
        }
        #endregion

        #region 键值应用与容错
        // 把键值表灌进 AppConfig，逐键做校验与回落。
        private static void ApplyValues(AppConfig config, Dictionary<string, string> values)
        {
            config.Language = GetValue(values, "Language", LanguageService.FallbackLanguage, LanguageService.GetSupportedLanguages());
            config.Volume = GetValue(values, "Volume", "2000", null);
            config.Format = ParseFormat(GetValue(values, "Format", "7z", null));
            config.Password = GetValue(values, "Password", string.Empty, null);
            config.Zstd = ParseBool(values, "Zstd", true);
            config.GenerateSHA256 = ParseBool(values, "GenerateSHA256", true);
            config.AutoSave = ParseBool(values, "AutoSave", true);
            config.MaxLevelCompress = ParseBool(values, "MaxLevelCompress", false);
            config.IsTopMost = ParseBool(values, "IsTopMost", false);
            config.IsPortable = ParseBool(values, "IsPortable", false);
            config.RuntimeMode = ParseRuntimeMode(GetValue(values, "RuntimeMode", "Balanced", null));
            config.OutputPath = GetValue(values, "OutputPath", string.Empty, null);
            config.IsSolidMode = ParseBool(values, "IsSolidMode", false);
            config.IsEncFileName = ParseBool(values, "IsEncFileName", false);
            config.CompressionLevel = ParseCompressionLevel(GetValue(values, "CompressionLevel", CompressionLevelPreset.Normal.ToString(), null));
            config.UnzipPassword = GetValue(values, "UnzipPassword", string.Empty, null);
            config.SevenZUsageCount = ParseLong(values, "SevenZUsageCount", 0);
            config.ErrorLevel = ParseErrorLevel(GetValue(values, "ErrorLevel", "Warning", null));
        }

        // 取值统一做空值与白名单校验，非法内容回落到默认值，调用方不必再判空。
        private static string GetValue(Dictionary<string, string> values, string key, string defaultValue, string[] validValues)
        {
            string value = values.TryGetValue(key, out string raw) ? raw : null;
            if (string.IsNullOrWhiteSpace(value)) return defaultValue;

            value = value.Trim();

            if (validValues != null && !Array.Exists(validValues, v => v.Equals(value, StringComparison.OrdinalIgnoreCase)))
            {
                return defaultValue;
            }

            return value;
        }

        private static bool ParseBool(Dictionary<string, string> values, string key, bool defaultValue)
        {
            string value = values.TryGetValue(key, out string raw) ? raw : null;
            if (string.IsNullOrWhiteSpace(value)) return defaultValue;

            // 布尔解析失败时保持默认值而不是当作 false，否则拼错的 true 会静默改掉行为。
            if (bool.TryParse(value.Trim(), out bool result))
            {
                return result;
            }

            return defaultValue;
        }

        private static long ParseLong(Dictionary<string, string> values, string key, long defaultValue)
        {
            string value = values.TryGetValue(key, out string raw) ? raw : null;
            if (string.IsNullOrWhiteSpace(value)) return defaultValue;

            // 使用量统计不接受负数，负数与非法文本同样按默认值处理，避免统计值倒退。
            if (long.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long result) && result >= 0)
            {
                return result;
            }

            return defaultValue;
        }
        #endregion

        #region 格式与枚举解析
        // 无法识别的格式只记一条警告并继续按 7z 处理，配置异常不该阻断启动。
        private static ArchiveFormat ParseFormat(string text)
        {
            ArchiveFormat? parsed = ArchiveFormatMap.Parse(text);
            if (parsed.HasValue) return parsed.Value;

            new LogService().WriteLog(
                EnumText.GetErrorText("Log_UnknownFormat", "配置中的压缩格式无法识别，已按 7z 处理：{0}", text ?? string.Empty),
                LogService.ErrorLevel.Warning);

            return ArchiveFormat.SevenZip;
        }

        private static LogService.ErrorLevel ParseErrorLevel(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return LogService.ErrorLevel.Warning;

            switch (text.Trim().ToLowerInvariant())
            {
                case "info": return LogService.ErrorLevel.Info;
                case "warning": return LogService.ErrorLevel.Warning;
                case "error": return LogService.ErrorLevel.Error;
            }

            return LogService.ErrorLevel.Warning;
        }

        // 运行模式与压缩等级的文本先认枚举名（英文），再交给 EnumText 按当前语言反查，最后回落默认值。
        private static RuntimeModePreset ParseRuntimeMode(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return RuntimeModePreset.Balanced;

            switch (text.Trim())
            {
                case "低占用":
                case "低佔用":
                case "Low":
                    return RuntimeModePreset.Low;
                case "高性能":
                case "高效能":
                case "High":
                    return RuntimeModePreset.High;
                case "均衡":
                case "Balanced":
                    return RuntimeModePreset.Balanced;
            }

            return EnumText.ParseRuntimeMode(text);
        }

        private static CompressionLevelPreset ParseCompressionLevel(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return CompressionLevelPreset.Normal;

            switch (text.Trim())
            {
                case "存储":
                case "存儲":
                case "Store":
                    return CompressionLevelPreset.Store;
                case "极限":
                case "極限":
                case "Ultra":
                    return CompressionLevelPreset.Ultra;
                case "标准":
                case "標準":
                case "Normal":
                    return CompressionLevelPreset.Normal;
            }

            return EnumText.ParseCompressionLevel(text);
        }
        #endregion

        #region 节点写回与废弃键清理
        private static void Set(Dictionary<string, string> values, string key, string value)
        {
            values[key] = value ?? string.Empty;
        }

        private static bool IsKnownKey(string key)
        {
            foreach (var candidate in _keys)
            {
                if (candidate.Name.Equals(key, StringComparison.OrdinalIgnoreCase)) return true;
            }

            return false;
        }

        private static bool HasMissingKeys(Dictionary<string, string> values)
        {
            foreach (var key in _keys)
            {
                if (!values.ContainsKey(key.Name)) return true;
            }

            return false;
        }

        // 这些键旧版本用过，继续留在文件里会被误认为仍然生效；
        // 写回前统一剔除，让配置文件只描述当前版本。
        private static readonly string[] _deprecatedKeys =
        {
            "ScreenWidth",
            "ScreenHeight",
            "SystemScale",
            "LocationX",
            "LocationY",
            "DisableVolume",
            "Portable",
            "ZipLevel",
            "MainText",
            "IsDeleteTemp"
        };

        private static Dictionary<string, string> RemoveDeprecated(Dictionary<string, string> values)
        {
            foreach (var key in _deprecatedKeys)
            {
                values.Remove(key);
            }

            return values;
        }
        #endregion

        #region 默认配置重建
        private static string DefaultValue(string key)
        {
            switch (key)
            {
                case "Language": return LanguageService.ResolveSystemLanguage();
                case "Volume": return "2000";
                case "Format": return "7z";
                case "Password": return string.Empty;
                case "Zstd": return bool.TrueString;
                case "GenerateSHA256": return bool.TrueString;
                case "AutoSave": return bool.TrueString;
                case "MaxLevelCompress": return bool.FalseString;
                case "IsTopMost": return bool.FalseString;
                case "IsPortable": return bool.FalseString;
                case "RuntimeMode": return RuntimeModePreset.Balanced.ToString();
                case "OutputPath": return string.Empty;
                case "IsSolidMode": return bool.FalseString;
                case "IsEncFileName": return bool.FalseString;
                case "CompressionLevel": return CompressionLevelPreset.Normal.ToString();
                case "UnzipPassword": return string.Empty;
                case "SevenZUsageCount": return "0";
                case "ErrorLevel": return "Warning";
                default: return string.Empty;
            }
        }

        // 重建用于文件缺失与文件损坏两种情况；损坏时必须覆盖写入，否则修不好的旧文件会一直被读到。
        private static bool CreateDefaultConfig(string configPath, bool forceOverwrite = false)
        {
            if (File.Exists(configPath) && !forceOverwrite) return true;

            var values = new Dictionary<string, string>(_keys.Length, StringComparer.OrdinalIgnoreCase);
            foreach (var key in _keys)
            {
                values[key.Name] = DefaultValue(key.Name);
            }

            string tempPath = configPath + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(configPath));

                JsonConfig.WriteFile(tempPath, BuildDocument(values));

                if (File.Exists(configPath))
                {
                    File.Replace(tempPath, configPath, null, true);
                }
                else
                {
                    File.Move(tempPath, configPath);
                }

                return true;
            }
            catch
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* 有意忽略：残留的临时文件删不掉不影响配置写入结果 */ }
                return false;
            }
        }
        #endregion
    }
}
