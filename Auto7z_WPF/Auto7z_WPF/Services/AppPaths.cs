using System;
using System.IO;
using System.Reflection;

namespace Auto7z_WPF.Services
{
    /// <summary>
    /// 数据目录的定位。
    ///
    /// 两种存放方式：
    ///   普通模式 —— %LOCALAPPDATA%\Auto7z
    ///   便携模式 —— 程序目录\.config\Auto7z
    ///
    /// 两种模式下的目录内容一样：配置文件、7-Zip 组件、日志。
    /// 便携模式的好处是整个程序拷到 U 盘上直接用，不往系统目录里写东西。
    ///
    /// 两种模式都由「根目录」加「数据目录名」拼出来，
    /// 区别只在根目录在哪：普通模式是 %LOCALAPPDATA%，
    /// 便携模式是程序目录下的 .config。
    /// 目录搬动时根目录跟着一起处理，就不会有空壳目录留下。
    /// </summary>
    public static class AppPaths
    {
        /// <summary>数据目录名。两种模式下都叫这个。</summary>
        public const string DataFolderName = "Auto7z";

        /// <summary>便携模式下的根目录名。</summary>
        private const string PortableRootFolderName = ".config";

        /// <summary>配置文件名。</summary>
        public const string ConfigFileName = "Auto7z.xml";

        private static readonly object _syncRoot = new object();

        private static bool _portable;
        private static string _dataDirectory;

        /// <summary>
        /// 是否为便携模式。
        /// 切换时数据目录会跟着变，已打开的日志文件也要换到新位置。
        /// </summary>
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
                    _dataDirectory = null;
                }

                if (changed) LogService.ResetLogFiles();
            }
        }

        /// <summary>
        /// 数据目录，配置、组件、日志都在这里面。
        /// </summary>
        public static string DataDirectory
        {
            get
            {
                lock (_syncRoot)
                {
                    if (_dataDirectory == null)
                    {
                        _dataDirectory = Path.Combine(GetRootDirectory(), DataFolderName);
                    }
                    return _dataDirectory;
                }
            }
        }

        /// <summary>配置文件完整路径。</summary>
        public static string ConfigFilePath => Path.Combine(DataDirectory, ConfigFileName);

        /// <summary>日志目录。</summary>
        public static string LogDirectory => Path.Combine(DataDirectory, "Logs");

        /// <summary>7-Zip 组件目录。</summary>
        public static string ComponentsDirectory => Path.Combine(DataDirectory, "7-Zip");

        /// <summary>
        /// 根目录，数据目录挂在它下面。
        ///
        /// 普通模式是 %LOCALAPPDATA%，便携模式是程序目录下的 .config。
        /// </summary>
        public static string GetRootDirectory()
        {
            return _portable ? GetPortableRootDirectory() : GetLocalRootDirectory();
        }

        /// <summary>普通模式的根目录：%LOCALAPPDATA%。</summary>
        public static string GetLocalRootDirectory()
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        }

        /// <summary>便携模式的根目录：程序所在目录下的 .config。</summary>
        public static string GetPortableRootDirectory()
        {
            return Path.Combine(GetProgramDirectory(), PortableRootFolderName);
        }

        /// <summary>程序所在目录。</summary>
        public static string GetProgramDirectory()
        {
            string exePath = Assembly.GetEntryAssembly()?.Location;
            string directory = string.IsNullOrEmpty(exePath)
                ? AppDomain.CurrentDomain.BaseDirectory
                : Path.GetDirectoryName(exePath);

            return string.IsNullOrEmpty(directory) ? AppDomain.CurrentDomain.BaseDirectory : directory;
        }

        /// <summary>数据目录，建不出来返回 false。</summary>
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
    }
}
