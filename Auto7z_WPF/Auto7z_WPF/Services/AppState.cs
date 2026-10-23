namespace Auto7z_WPF.Services
{
    /// <summary>
    /// 跨线程可见的应用程序运行状态。
    /// 只存放与界面无关的全局状态；界面文本一律走语言资源字典。
    /// </summary>
    public static class AppState
    {
        private static int _currentStatus = (int)OperationStatus.Normal;

        /// <summary>当前任务状态（跨线程可见）。</summary>
        public static OperationStatus CurrentStatus
        {
            get { return (OperationStatus)System.Threading.Volatile.Read(ref _currentStatus); }
            set { System.Threading.Interlocked.Exchange(ref _currentStatus, (int)value); }
        }

        /// <summary>程序目录。</summary>
        public static string BasePath = System.AppDomain.CurrentDomain.BaseDirectory;

        /// <summary>当前语言（语言代码，如 zh-CN）。</summary>
        public static string CurrentLanguage = "zh-CN";

        /// <summary>压缩/解压执行次数。</summary>
        public static long SevenZUsageCount = 0;

        /// <summary>
        /// 应用程序工作状态枚举。
        /// </summary>
        public enum OperationStatus
        {
            /// <summary>待机中。</summary>
            Normal = 0,

            /// <summary>压缩中。</summary>
            CompressFile = 1,

            /// <summary>解压中。</summary>
            ExtractFile = 2,

            /// <summary>完成。</summary>
            Finish = 3,

            /// <summary>错误。</summary>
            Error = 4
        }
    }
}
