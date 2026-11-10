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
        // 便携与否只由「程序目录旁的数据目录存不存在」决定，配置里不再存这个键。
        //
        // 为什么不再用 IsPortable 键：配置键只是某一次的记录，而目录是磁盘上的事实。
        // 两者一旦不一致就会互相矛盾 —— 例如配置写着 IsPortable=false、而 .config 里
        // 确有一份配置，程序该听谁的？听配置就会把数据目录指向一个空位置、把真正
        // 在用的那份配置晾在一边（实测踩过：两处都有配置时产品行为完全不可预测）。
        // 改成「文件在哪就是哪」之后，同一时刻只可能有一个数据目录，矛盾无从产生。
        // 这是 Keypirinha / Handy / VS Code 一类便携程序的通行做法：
        // 启动时看程序目录旁有没有数据目录（或 portable 标记），有才跑便携。
        //
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

        /// <summary>
        /// 按磁盘事实探测是否处于便携模式：程序目录旁存在 .config 目录即为便携。
        ///
        /// 判据是 .config 目录本身，而不是它下面的 Auto7z 数据目录：程序没运行时
        /// 不会有人替它建 Auto7z 子目录，「把程序拷到移动盘、旁边留个空的 .config」
        /// 是最自然的便携打包方式。只认 Auto7z 子目录的话，这份空 .config 会被无视，
        /// 程序照常读 %LOCALAPPDATA%，用户以为拷走的东西其实没跟着走。
        ///
        /// 代价是「取消便携」必须真的把 .config 删掉，否则下次启动又会被判成便携 ——
        /// 这一步由 ViewModel 的 RemoveEmptyDirectory 负责，两者是一对契约。
        /// </summary>
        public static bool DetectPortableFromDisk()
        {
            try
            {
                return Directory.Exists(GetPortableRootDirectory());
            }
            catch
            {
                // 探测本身失败时按「不是便携」处理：普通模式才是不会有副作用的那一侧。
                return false;
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
