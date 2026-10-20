using System;
using System.Linq;
using System.Reflection;
using System.Windows;

namespace Auto7z_WPF.Services
{
    /// <summary>
    /// 多语言资源切换服务。
    /// 负责在运行时替换 Application.Current.Resources.MergedDictionaries 中的语言资源字典，
    /// 并在切换完成后触发 <see cref="LanguageChanged"/>，让界面文本（主提示语等）同步刷新。
    /// </summary>
    public class LanguageService
    {
        private const string LanguagesFolder = "languages/";

        /// <summary>界面提供的语言。顺序即界面里的排列顺序。</summary>
        private static readonly string[] SupportedLanguages = { "zh-CN", "zh-TW", "en-US" };

        /// <summary>找不到匹配语言时的兜底语言。</summary>
        public const string FallbackLanguage = "en-US";

        private readonly string _assemblyName;
        private readonly Uri _appResourceBase;

        /// <summary>语言切换完成后触发。</summary>
        public event EventHandler LanguageChanged;

        public LanguageService()
        {
            _assemblyName = Assembly.GetExecutingAssembly().GetName().Name;
            _appResourceBase = new Uri($"pack://application:,,,/{_assemblyName};component/", UriKind.Absolute);
        }

        /// <summary>
        /// 语言代码是否受支持。
        /// </summary>
        public static bool IsSupported(string languageCode)
        {
            return !string.IsNullOrWhiteSpace(languageCode)
                && Array.Exists(SupportedLanguages, l => l.Equals(languageCode.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// 受支持的语言代码列表。
        /// </summary>
        public static string[] GetSupportedLanguages()
        {
            return (string[])SupportedLanguages.Clone();
        }

        /// <summary>
        /// 按系统界面语言挑选一个受支持的语言。
        ///
        /// 系统的语言代码不一定是词典里的名字，比如简体中文在有些系统上是 zh-Hans、
        /// zh-Hans-CN，繁体中文是 zh-Hant、zh-Hant-TW。
        /// 这里先按完整名称匹配，匹配不上再按语言前缀归类，
        /// 中文一律归到繁体，其余一律归到英文。
        /// </summary>
        /// <returns>受支持的语言代码。</returns>
        public static string ResolveSystemLanguage()
        {
            string cultureName = System.Globalization.CultureInfo.CurrentUICulture.Name;
            if (string.IsNullOrWhiteSpace(cultureName)) return FallbackLanguage;

            foreach (string supported in SupportedLanguages)
            {
                if (supported.Equals(cultureName, StringComparison.OrdinalIgnoreCase)) return supported;
            }

            // 按前缀判断语种
            if (cultureName.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
            {
                bool isTraditional = cultureName.IndexOf("Hant", StringComparison.OrdinalIgnoreCase) >= 0
                    || cultureName.IndexOf("TW", StringComparison.OrdinalIgnoreCase) >= 0
                    || cultureName.IndexOf("HK", StringComparison.OrdinalIgnoreCase) >= 0;

                return isTraditional ? "zh-TW" : "zh-CN";
            }

            return FallbackLanguage;
        }

        /// <summary>
        /// 按当前语言代码重新加载语言资源字典。
        /// </summary>
        public void SwitchLanguage(string languageCode)
        {
            if (!IsSupported(languageCode))
            {
                languageCode = FallbackLanguage;
            }

            try
            {
                var newDict = LoadLanguageDictionary(languageCode);
                var merged = Application.Current.Resources.MergedDictionaries;

                // 移除所有语言资源字典，只保留本次加载的这一个。
                // 只替换“第一个匹配项”会留下重复字典：App.xaml 里的条目从未被替换过，
                // 于是运行时加载的字典排在它后面，查找资源时旧语言仍然生效。
                foreach (var dict in merged.Where(IsLanguageDictionary).ToList())
                {
                    merged.Remove(dict);
                }

                merged.Add(newDict);
                AppState.CurrentLanguage = languageCode;
            }
            catch (Exception ex)
            {
                new LogService().WriteLog(EnumText.GetErrorText("Log_LanguageSwitchFailed", "切换语言失败（{0}）：{1}", languageCode, ex.Message), LogService.ErrorLevel.Error);
            }

            // 无论资源是否切换成功，都通知界面按当前语言刷新文本
            LanguageChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// 加载指定语言的资源字典。优先使用相对 URI，避免 App.xaml 中
        /// 相对 URI 与代码中绝对 pack URI 混用时需要维护两套路径写法。
        /// </summary>
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
                // 备用：显式 pack URI
                return new ResourceDictionary
                {
                    Source = new Uri(_appResourceBase, $"Resources/Languages/{languageCode}.xaml")
                };
            }
        }

        /// <summary>
        /// 判断资源字典是否为语言资源字典。同时识别源程序集里的相对路径
        /// 与 pack URI 两种写法（后者需要忽略大小写与 ;component/ 前缀差异）。
        /// </summary>
        private static bool IsLanguageDictionary(ResourceDictionary dict)
        {
            var source = dict?.Source?.OriginalString;
            if (string.IsNullOrWhiteSpace(source)) return false;

            return source.Replace('\\', '/').ToLowerInvariant().Contains(LanguagesFolder);
        }
    }
}
