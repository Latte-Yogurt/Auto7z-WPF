namespace Auto7z_WPF.Services
{
    public static class AppState
    {
        private static int _currentStatus = (int)OperationStatus.Normal;

        public static OperationStatus CurrentStatus
        {
            // 用 Volatile/Interlocked 而不是普通字段：状态由任务线程写、界面线程读，
            // 普通读写既没有原子性保证，也可能一直读到缓存里的旧值。
            get { return (OperationStatus)System.Threading.Volatile.Read(ref _currentStatus); }
            set { System.Threading.Interlocked.Exchange(ref _currentStatus, (int)value); }
        }

        public static string BasePath = System.AppDomain.CurrentDomain.BaseDirectory;

        public static string CurrentLanguage = "zh-CN";

        public static long SevenZUsageCount = 0;

        public enum OperationStatus
        {
            Normal = 0,

            CompressFile = 1,

            ExtractFile = 2,

            Finish = 3,

            Error = 4
        }
    }
}
