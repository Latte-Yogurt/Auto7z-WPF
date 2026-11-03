using System;
using System.IO;
using System.Reflection;

namespace Auto7z_WPF.Services
{
    /// <summary>
    /// 数据目录的唯一出处，便携模式与安装模式的差异都收敛在这里。
    /// </summary>
    public static class AppPaths
    {
        #region 目录与文件常量
        public const string DataFolderName = "Auto7z";

        private const string PortableRootFolderName = ".config";

        public const string ConfigFileName = "Auto7z.json";

        private static readonly object _syncRoot = new object();

        private static bool _portable;
        private static string _dataDirectory;
        #endregion

        #region 便携模式开关
        // 切换模式要连带重置日志文件句柄：日志路径跟着数据目录走，
        // 不重置的话后续日志还会继续写进旧模式那棵树。
        public static bool IsPortable
        {
            get { lock (_syncRoot) { return _portable; } }
            set
            {
                bool changed;
                lock (_syncRoot)
                {
                    changed = _portable != value;
                    _portable = value;
                    // 模式一变，缓存下来的数据目录就指向了另一棵树，必须作废重算。
                    _dataDirectory = null;
                }

                if (changed) LogService.ResetLogFiles();
            }
        }
        #endregion

        #region 目录构成
        /// <summary>数据目录，由根目录加上固定的数据目录名拼成。</summary>
        public static string DataDirectory
        {
            get
            {
                lock (_syncRoot)
                {
                    if (_dataDirectory == null)
                    {
                        // 根目录与数据目录分两层：便携模式切换时只换根，数据目录名始终是 Auto7z。
                        // 这样搬动时改的是上层根路径，不会在旧位置留下一个空的同名壳目录。
                        _dataDirectory = Path.Combine(GetRootDirectory(), DataFolderName);
                    }
                    return _dataDirectory;
                }
            }
        }

        public static string ConfigFilePath => Path.Combine(DataDirectory, ConfigFileName);

        public static string LogDirectory => Path.Combine(DataDirectory, "Logs");

        public static string ComponentsDirectory => Path.Combine(DataDirectory, "7-Zip");

        public static string GetRootDirectory()
        {
            // 根目录只决定「放在哪」，具体的数据目录名由 DataDirectory 叠加，
            // 两者分开才使切换便携模式等价于换一个根，不必搬动下层内容。
            return _portable ? GetPortableRootDirectory() : GetLocalRootDirectory();
        }

        public static string GetLocalRootDirectory()
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        }

        public static string GetPortableRootDirectory()
        {
            return Path.Combine(GetProgramDirectory(), PortableRootFolderName);
        }
        #endregion

        #region 程序目录与目录创建
        /// <summary>程序所在目录，便携模式据此定位 .config。</summary>
        public static string GetProgramDirectory()
        {
            // 单文件发布或影子复制时 GetEntryAssembly().Location 可能是空的，
            // 这时退回 AppDomain 的基目录，否则路径会算成当前工作目录。
            string exePath = Assembly.GetEntryAssembly()?.Location;
            string directory = string.IsNullOrEmpty(exePath)
                ? AppDomain.CurrentDomain.BaseDirectory
                : Path.GetDirectoryName(exePath);

            return string.IsNullOrEmpty(directory) ? AppDomain.CurrentDomain.BaseDirectory : directory;
        }

        /// <summary>确保数据目录存在，成功返回 true。</summary>
        public static bool EnsureDataDirectory()
        {
            try
            {
                Directory.CreateDirectory(DataDirectory);
                return true;
            }
            catch
            {
                return false;
            }
        }
        #endregion
    }
}
