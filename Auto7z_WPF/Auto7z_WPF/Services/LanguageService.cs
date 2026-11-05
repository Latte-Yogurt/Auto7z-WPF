using System;
using System.Linq;
using System.Reflection;
using System.Windows;

namespace Auto7z_WPF.Services
{
    /// <summary>
    /// 语言资源的加载与切换，支持的语言固定三种，未知语言一律回落到英文。
    /// </summary>
    public class LanguageService
    {
        #region 语言资源与清单
        // 用路径片段识别语言字典，切换时只移除这些，其他合并字典不受影响。
        private const string LanguagesFolder = "languages/";

        // 语言代号必须与 Resources/Languages 下的文件名一致，改这里要同步资源文件。
        private static readonly string[] _supportedLanguages = { "zh-CN", "zh-TW", "en-US" };

        public const string FallbackLanguage = "en-US";

        private readonly string _assemblyName;

        private Uri _appResourceBase;

        // pack:// URI 首次构造依赖 WPF 的 PackUriHelper，未启动 WPF 的宿主会抛 UriFormatException，
        // 因此延迟到真正需要时再建，失败就留空。
        private Uri AppResourceBase
        {
            get
            {
                if (_appResourceBase != null) return _appResourceBase;

                try
                {
                    _appResourceBase = new Uri($"pack://application:,,,/{_assemblyName};component/", UriKind.Absolute);
                }
                catch
                {
                    _appResourceBase = null;
                }

                return _appResourceBase;
            }
        }

        #endregion
        public event EventHandler LanguageChanged;

        public LanguageService()
        {
            _assemblyName = Assembly.GetExecutingAssembly().GetName().Name;
        }

        #region 系统语言与切换
        public static bool IsSupported(string languageCode)
        {
            return !string.IsNullOrWhiteSpace(languageCode)
                && Array.Exists(_supportedLanguages, l => l.Equals(languageCode.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        // 返回副本，避免调用方改到内部数组导致白名单失效。
        public static string[] GetSupportedLanguages()
        {
            return (string[])_supportedLanguages.Clone();
        }

        // 先精确匹配支持列表；中文再按 Hant、TW、HK 判断简繁，其余语言回落英文。
        public static string ResolveSystemLanguage()
        {
            string cultureName = System.Globalization.CultureInfo.CurrentUICulture.Name;
            if (string.IsNullOrWhiteSpace(cultureName)) return FallbackLanguage;

            foreach (string supported in _supportedLanguages)
            {
                if (supported.Equals(cultureName, StringComparison.OrdinalIgnoreCase)) return supported;
            }

            if (cultureName.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
            {
                bool isTraditional = cultureName.IndexOf("Hant", StringComparison.OrdinalIgnoreCase) >= 0
                    || cultureName.IndexOf("TW", StringComparison.OrdinalIgnoreCase) >= 0
                    || cultureName.IndexOf("HK", StringComparison.OrdinalIgnoreCase) >= 0;

                return isTraditional ? "zh-TW" : "zh-CN";
            }

            return FallbackLanguage;
        }

        // 先摘掉旧的语言字典再挂新的，否则同一个资源键会因加载顺序不同而随机生效。
        public void SwitchLanguage(string languageCode)
        {
            if (!IsSupported(languageCode))
            {
                languageCode = FallbackLanguage;
            }

            // 切换失败只记日志，界面保留旧语言继续运行。
            try
            {
                var newDict = LoadLanguageDictionary(languageCode);
                var merged = Application.Current.Resources.MergedDictionaries;

                foreach (var dict in merged.Where(IsLanguageDictionary).ToList())
                {
                    merged.Remove(dict);
                }

                merged.Add(newDict);

                // 缓存不重建的话，后台线程仍会读到上一个语言的文案。
                EnumText.RebuildTextCache(newDict);

                AppState.CurrentLanguage = languageCode;
            }
            catch (Exception ex)
            {
                new LogService().WriteLog(EnumText.GetErrorText("Log_LanguageSwitchFailed", "切换语言失败（{0}）：{1}", languageCode, ex.Message), LogService.ErrorLevel.Error);
            }

            LanguageChanged?.Invoke(this, EventArgs.Empty);
        }

        #endregion
        #region 语言字典加载
        // 先用相对 URI 让 WPF 从当前程序集解析；宿主未启动 WPF 时这一步会失败，
        // 才用 pack:// 绝对地址兜底。
        private ResourceDictionary LoadLanguageDictionary(string languageCode)
        {
            try
            {
                return new ResourceDictionary
                {
                    Source = new Uri($"Resources/Languages/{languageCode}.xaml", UriKind.Relative)
                };
            }
            catch
            {
                Uri baseUri = AppResourceBase;
                // 没有可用的程序集基址就说明两种方式都行不通，把原始异常抛给上层记录。
                if (baseUri == null) throw;

                return new ResourceDictionary
                {
                    Source = new Uri(baseUri, $"Resources/Languages/{languageCode}.xaml")
                };
            }
        }

        // 按路径片段判断而不是比较完整 URI，相对地址与 pack:// 两种来源都能识别。
        private static bool IsLanguageDictionary(ResourceDictionary dict)
        {
            var source = dict?.Source?.OriginalString;
            if (string.IsNullOrWhiteSpace(source)) return false;

            return source.Replace('\\', '/').ToLowerInvariant().Contains(LanguagesFolder);
        }
        #endregion
    }
}
