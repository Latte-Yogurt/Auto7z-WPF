using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using Auto7z_WPF.Models;

namespace Auto7z_WPF.Services
{
    /// <summary>
    /// 配置读写服务，基于 XML 文件持久化应用程序设置。
    /// 保存采用“临时文件 + 替换”，避免写入过程中崩溃导致配置损坏。
    /// </summary>
    public class ConfigService
    {
        /// <summary>
        /// 配置文件路径。
        ///
        /// 每次都按 <see cref="AppPaths"/> 当前的数据目录重新取。
        /// 切换便携模式后配置会搬到新位置，缓存下来的路径就失效了。
        /// </summary>
        public string ConfigPath => AppPaths.ConfigFilePath;

        /// <summary>
        /// 确保配置文件存在且完整。
        ///
        /// 文件不存在时按默认配置新建；
        /// 文件存在但缺键时补齐缺的键，保留已有的值。
        /// </summary>
        public void EnsureConfigLegal()
        {
            try
            {
                if (!File.Exists(ConfigPath))
                {
                    CreateDefaultConfig(ConfigPath);
                    return;
                }

                var doc = XDocument.Load(ConfigPath);
                if (doc.Root == null) return;

                var missing = DefaultConfigElements()
                    .Where(e => doc.Root.Element(e.Name) == null)
                    .ToList();

                if (missing.Count == 0) return;

                foreach (var element in missing)
                {
                    doc.Root.Add(element);
                }

                SaveDocument(doc);
            }
            catch
            {
                // 处理失败忽略，后续读取会使用默认值
            }
        }

        /// <summary>
        /// 加载配置到 AppConfig 实例。
        /// </summary>
        public AppConfig Load()
        {
            EnsureConfigLegal();

            var config = new AppConfig();

            try
            {
                var doc = XDocument.Load(ConfigPath);
                config.Language = GetElementValue(doc, "Language", LanguageService.FallbackLanguage, LanguageService.GetSupportedLanguages());
                config.Volume = GetElementValue(doc, "Volume", "2000", null);
                config.Format = ParseFormat(GetElementValue(doc, "Format", "7z", null));
                config.Password = GetElementValue(doc, "Password", string.Empty, null);
                config.Zstd = ParseBool(doc, "Zstd", true);
                config.GenerateMd5 = ParseBool(doc, "GenerateMD5", true);
                config.AutoSave = ParseBool(doc, "AutoSave", true);
                config.MaxLevelCompress = ParseBool(doc, "MaxLevelCompress", false);
                config.IsTopMost = ParseBool(doc, "IsTopMost", false);
                config.IsPortable = ParseBool(doc, "IsPortable", false);
                config.RuntimeMode = ParseRuntimeMode(GetElementValue(doc, "RuntimeMode", "Balanced", null));
                config.OutputPath = GetElementValue(doc, "OutputPath", string.Empty, null);
                config.IsSolidMode = ParseBool(doc, "IsSolidMode", false);
                config.IsEncFileName = ParseBool(doc, "IsEncFileName", false);
                // 压缩等级优先读 CompressionLevel，缺失时回退到旧键 ZipLevel
                config.CompressionLevel = ParseCompressionLevel(ReadCompressionLevel(doc));
                config.UnzipPassword = GetElementValue(doc, "UnzipPassword", string.Empty, null);
                config.SevenZUsageCount = ParseLong(doc, "SevenZUsageCount", 0);
                config.ErrorLevel = ParseErrorLevel(GetElementValue(doc, "ErrorLevel", "Warning", null));
            }
            catch (XmlException)
            {
                CreateDefaultConfig(ConfigPath);
            }
            catch (Exception)
            {
                // 读取失败保持默认值
            }

            return config;
        }

        /// <summary>
        /// 将 AppConfig 保存到 XML 配置文件。
        /// </summary>
        public bool Save(AppConfig config)
        {
            if (config == null) return false;

            EnsureConfigLegal();

            try
            {
                var doc = LoadDocument();
                if (doc?.Root == null) return false;

                // 先剔除废弃键，避免旧配置里的无用键值一直留在文件里
                RemoveDeprecatedKeys(doc);

                UpdateElement(doc, "Language", config.Language);
                UpdateElement(doc, "Volume", config.Volume);
                UpdateElement(doc, "Format", ArchiveFormatMap.ToSwitchValue(config.Format));
                UpdateElement(doc, "Password", config.Password);
                UpdateElement(doc, "Zstd", config.Zstd.ToString());
                UpdateElement(doc, "GenerateMD5", config.GenerateMd5.ToString());
                UpdateElement(doc, "AutoSave", config.AutoSave.ToString());
                UpdateElement(doc, "MaxLevelCompress", config.MaxLevelCompress.ToString());
                UpdateElement(doc, "IsTopMost", config.IsTopMost.ToString());
                UpdateElement(doc, "IsPortable", config.IsPortable.ToString());
                UpdateElement(doc, "RuntimeMode", config.RuntimeMode.ToString());
                UpdateElement(doc, "OutputPath", config.OutputPath ?? string.Empty);
                UpdateElement(doc, "IsSolidMode", config.IsSolidMode.ToString());
                UpdateElement(doc, "IsEncFileName", config.IsEncFileName.ToString());
                UpdateElement(doc, "CompressionLevel", config.CompressionLevel.ToString());
                UpdateElement(doc, "UnzipPassword", config.UnzipPassword);
                UpdateElement(doc, "SevenZUsageCount", config.SevenZUsageCount.ToString());
                UpdateElement(doc, "ErrorLevel", config.ErrorLevel.ToString());

                return SaveDocument(doc);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// 单项更新配置键值（供运行时即时写入）。
        /// </summary>
        public bool Update(string key, string value)
        {
            if (string.IsNullOrWhiteSpace(key)) return false;

            EnsureConfigLegal();

            try
            {
                var doc = LoadDocument();
                if (doc?.Root == null) return false;

                RemoveDeprecatedKeys(doc);
                UpdateElement(doc, key, value);
                return SaveDocument(doc);
            }
            catch (Exception)
            {
                return false;
            }
        }

        #region 私有辅助

        private XDocument LoadDocument()
        {
            if (!File.Exists(ConfigPath))
            {
                CreateDefaultConfig(ConfigPath);
            }

            try
            {
                return XDocument.Load(ConfigPath);
            }
            catch (XmlException)
            {
                // 配置损坏：重建默认配置后再写入
                CreateDefaultConfig(ConfigPath, forceOverwrite: true);
                return XDocument.Load(ConfigPath);
            }
        }

        /// <summary>
        /// 原子写入：先写 .tmp，再替换目标文件，避免中途失败留下半截 XML。
        /// </summary>
        private bool SaveDocument(XDocument doc)
        {
            string tempPath = ConfigPath + ".tmp";
            try
            {
                doc.Save(tempPath);

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
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* 忽略 */ }
                return false;
            }
        }

        private static string GetElementValue(XDocument doc, string elementName, string defaultValue, string[] validValues)
        {
            var node = doc.Root?.Element(elementName);
            if (node == null || string.IsNullOrWhiteSpace(node.Value))
            {
                return defaultValue;
            }

            var value = node.Value.Trim();

            if (validValues != null && !Array.Exists(validValues, v => v.Equals(value, StringComparison.OrdinalIgnoreCase)))
            {
                return defaultValue;
            }

            return value;
        }
        #endregion

        #region 配置值解析


        private static bool ParseBool(XDocument doc, string elementName, bool defaultValue)
        {
            var node = doc.Root?.Element(elementName);
            if (node == null || string.IsNullOrWhiteSpace(node.Value))
            {
                return defaultValue;
            }

            if (bool.TryParse(node.Value.Trim(), out var result))
            {
                return result;
            }

            return defaultValue;
        }

        private static long ParseLong(XDocument doc, string elementName, long defaultValue)
        {
            var node = doc.Root?.Element(elementName);
            if (node == null || string.IsNullOrWhiteSpace(node.Value))
            {
                return defaultValue;
            }

            if (long.TryParse(node.Value.Trim(), out var result) && result >= 0)
            {
                return result;
            }

            return defaultValue;
        }

        private static string ReadCompressionLevel(XDocument doc)
        {
            // 新键优先：一旦保存过新版配置，旧键就不再参与解析，避免旧值反向覆盖新值
            var current = doc.Root?.Element("CompressionLevel");
            if (current != null && !string.IsNullOrWhiteSpace(current.Value))
            {
                return current.Value.Trim();
            }

            var legacy = doc.Root?.Element("ZipLevel");
            return legacy != null && !string.IsNullOrWhiteSpace(legacy.Value)
                ? legacy.Value.Trim()
                : CompressionLevelPreset.Normal.ToString();
        }

        /// <summary>
        /// 解析压缩格式。无法识别时按 7z 处理，并记一条警告，
        /// 免得配置里的格式值写错了却悄无声息。
        /// </summary>
        private static ArchiveFormat ParseFormat(string text)
        {
            ArchiveFormat? parsed = ArchiveFormatMap.Parse(text);
            if (parsed.HasValue) return parsed.Value;

            new LogService().WriteLog(
                EnumText.GetErrorText("Log_UnknownFormat", "配置中的压缩格式无法识别，已按 7z 处理：{0}", text ?? string.Empty),
                LogService.ErrorLevel.Warning);

            return ArchiveFormat.SevenZip;
        }

        /// <summary>
        /// 解析日志级别：Info / Warning / Error。
        /// 这是配置文件中的隐藏开关（界面上没有对应控件），用于调节日志详细程度。
        /// </summary>
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

        /// <summary>
        /// 运行模式：存枚举名（Low/Balanced/High），同时兼容各语言的显示文本。
        /// </summary>
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

        /// <summary>
        /// 压缩等级：存枚举名，同时兼容旧键 ZipLevel 与各语言的显示文本。
        /// </summary>
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

        #region 配置写回


        private static void UpdateElement(XDocument doc, string elementName, string value)
        {
            var element = doc.Root?.Element(elementName);
            if (element != null)
            {
                element.Value = value ?? string.Empty;
            }
            else
            {
                doc.Root?.Add(new XElement(elementName, value ?? string.Empty));
            }
        }

        /// <summary>
        /// 已废弃的配置键。这些键在早期版本中用于保存窗口位置与缩放，
        /// 现在窗口位置由系统管理、也不再记录缩放，因此读写时直接剔除，
        /// 避免配置文件里堆积无用键值。
        ///
        /// DisableVolume：现在用“分卷大小 = 0 表示不分卷”来表达，不再需要单独的开关键。
        /// Portable：便携模式未实现，配置项没有对应的行为。
        /// ZipLevel：旧版压缩等级的键名，现为 CompressionLevel，读取时已做兼容。
        /// MainText：旧版主提示文本的键名，现由界面绑定视图模型提供。
        /// </summary>
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

        /// <summary>
        /// 从配置文档中移除已废弃的键。
        /// </summary>
        private static void RemoveDeprecatedKeys(XDocument doc)
        {
            var root = doc.Root;
            if (root == null) return;

            foreach (var key in _deprecatedKeys)
            {
                var element = root.Element(key);
                element?.Remove();
            }
        }

        private static void CreateDefaultConfig(string configPath, bool forceOverwrite = false)
        {
            if (File.Exists(configPath) && !forceOverwrite)
            {
                return;
            }

            var defaultConfig = new XDocument(
                new XElement("Configuration", DefaultConfigElements()));

            string tempPath = configPath + ".tmp";
            try
            {
                defaultConfig.Save(tempPath);

                if (File.Exists(configPath))
                {
                    File.Replace(tempPath, configPath, null, true);
                }
                else
                {
                    File.Move(tempPath, configPath);
                }
            }
            catch
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* 忽略 */ }
            }
        }

        /// <summary>
        /// 默认配置里的各个键。新建配置与补齐缺失的键都用这一份。
        /// </summary>
        private static List<XElement> DefaultConfigElements()
        {
            return new List<XElement>
            {
                new XElement("Language", LanguageService.ResolveSystemLanguage()),
                new XElement("Volume", "2000"),
                new XElement("Format", "7z"),
                new XElement("Password", string.Empty),
                new XElement("Zstd", "True"),
                new XElement("GenerateMD5", "True"),
                new XElement("AutoSave", "True"),
                new XElement("MaxLevelCompress", "False"),
                new XElement("IsTopMost", "False"),
                new XElement("IsPortable", "False"),
                new XElement("RuntimeMode", RuntimeModePreset.Balanced.ToString()),
                new XElement("OutputPath", string.Empty),
                new XElement("IsSolidMode", "False"),
                new XElement("IsEncFileName", "False"),
                new XElement("CompressionLevel", CompressionLevelPreset.Normal.ToString()),
                new XElement("UnzipPassword", string.Empty),
                new XElement("SevenZUsageCount", "0"),
                new XElement("ErrorLevel", "Warning")
            };
        }

        #endregion
    }
}
