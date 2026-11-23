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

        /// <summary>
        /// 数据根目录的改道变量。设成一个绝对路径时，普通模式的数据目录挂到它下面，
        /// 而不是 %LOCALAPPDATA%。
        ///
        /// 这条改道是给测试工具用的：测试驱动的是真实的程序，程序会往数据目录写配置、
        /// 日志、TaskState 与释放出来的 7-Zip 组件。默认落在 %LOCALAPPDATA%\Auto7z 时，
        /// 跑一轮测试就把用户自己的配置改了、日志刷了、组件覆盖了 —— 用户下次打开程序
        /// 看到的是测试留下的状态。有了这条改道，测试把所有东西都写进 %TEMP% 下的
        /// 受控目录，用户真实的那份目录一个字节都不会被碰。
        ///
        /// 注意：不能靠改 LOCALAPPDATA 环境变量达到同样效果 ——
        /// Environment.GetFolderPath(SpecialFolder.LocalApplicationData) 走的是
        /// SHGetFolderPath，读的是用户配置文件里的已知文件夹，与环境变量无关（实测确认）。
        /// 所以只能由程序自己提供一个明确的改道点。
        /// </summary>
        public const string DataRootVariable = "AUTO7Z_DATA_ROOT";

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
        // **本属性只改状态，不触发任何副作用。**
        // 早先这里有 `if (changed) LogService.ResetLogFiles();`，而 LogService.LogDirectory
        // 又读 AppPaths.LogDirectory —— 两个静态类形成 AppPaths → LogService → AppPaths 的
        // 循环依赖，初始化顺序变得敏感。日志句柄的重置改由调用方在改完模式后显式调用
        // （见 Auto7zViewModel.SetPortableMode），AppPaths 回到「纯路径计算」这一职责。
        public static bool IsPortable
        {
            get { lock (_syncRoot) { return _portable; } }
            set
            {
                lock (_syncRoot)
                {
                    _portable = value;
                    // 模式一变，缓存下来的数据目录就指向了另一棵树，必须作废重算。
                    _dataDirectory = null;
                }
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
            // 改道优先：测试工具把它指到 %TEMP% 下的受控目录，
            // 于是配置、日志、TaskState 与 7-Zip 组件全都写进临时目录，
            // 用户真实的 %LOCALAPPDATA%\Auto7z 一次都不会被碰。
            string redirect = GetDataRootOverride();
            if (redirect != null) return redirect;

            return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        }

        /// <summary>
        /// 读改道变量。只认绝对路径：相对路径的含义取决于当前工作目录，
        /// 而工作目录可能在程序运行期间被改掉，那会让同一台机器上的两次运行
        /// 写进两个不同的地方 —— 这种不确定性比"忽略改道"更危险。
        /// 非法或空的改道一律当作未设置。
        /// </summary>
        private static string GetDataRootOverride()
        {
            string value;
            try
            {
                value = Environment.GetEnvironmentVariable(DataRootVariable);
            }
            catch
            {
                // 环境变量读不出来时按未设置处理：普通模式才是没有副作用的那一侧。
                return null;
            }

            if (string.IsNullOrWhiteSpace(value)) return null;

            try
            {
                string full = Path.GetFullPath(value.Trim());
                return Path.IsPathRooted(full) ? full : null;
            }
            catch
            {
                return null;
            }
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
