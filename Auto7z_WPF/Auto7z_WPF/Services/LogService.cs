using System;
using System.Globalization;
using System.IO;
using System.Reflection;

namespace Auto7z_WPF.Services
{
    /// <summary>
    /// 日志服务。日志写入 %LOCALAPPDATA%\Auto7z\Logs，
    /// 与配置、临时组件同属应用数据目录，避免桌面重定向（OneDrive）导致的写入失败。
    ///
    /// 分成两类文件：
    ///   普通日志 —— 记录日常过程（接收文件、自动模式识别等），完整保留。
    ///   问题日志 —— 记录警告与错误，只保留最近若干天。
    /// 两类分开存放，按级别决定写入哪个文件，
    /// 这样清理过期文件时不会连带删掉日常日志。
    /// </summary>
    public class LogService
    {
        private static readonly string _appName = Assembly.GetExecutingAssembly().GetName().Name;

        private static readonly object _writeLock = new object();

        private static readonly System.Text.Encoding Utf8NoBom = new System.Text.UTF8Encoding(false);

        /// <summary>问题日志保留天数，超过天数的在启动时删除。</summary>
        private const int IssueLogRetentionDays = 7;

        /// <summary>进程启动时确定的普通日志文件路径，之后不再随日期变化。</summary>
        private static string _normalLogFile;

        /// <summary>进程启动时确定的问题日志文件路径，之后不再随日期变化。</summary>
        private static string _issueLogFile;

        /// <summary>低于该级别的日志不写入。</summary>
        public static ErrorLevel MinLogLevel { get; set; } = ErrorLevel.Warning;

        /// <summary>日志目录。跟随 <see cref="AppPaths"/> 的当前模式。</summary>
        private static string LogDirectory => AppPaths.LogDirectory;

        /// <summary>
        /// 清掉已缓存的日志文件路径。
        ///
        /// 切换便携模式后数据目录会变，日志也要跟着换，
        /// 否则会继续往旧目录里写。
        /// </summary>
        public static void ResetLogFiles()
        {
            lock (_writeLock)
            {
                _normalLogFile = null;
                _issueLogFile = null;
            }
        }

        /// <summary>
        /// 普通日志文件路径（按天切分）。
        ///
        /// 进程启动时确定一次并固定下来：
        /// 若每次写入都按当前日期重新计算，跨零点运行的实例会把一次运行拆到两个文件里，
        /// 午夜前后的日志分散在不同日期的文件中。
        /// </summary>
        public static string LogFilePath
        {
            get
            {
                if (_normalLogFile == null)
                {
                    lock (_writeLock)
                    {
                        if (_normalLogFile == null)
                        {
                            _normalLogFile = BuildLogFilePath("Auto7z", DateTime.Now);
                        }
                    }
                }

                return _normalLogFile;
            }
        }

        /// <summary>问题日志文件路径（警告与错误写入这里）。</summary>
        public static string IssueLogFilePath
        {
            get
            {
                if (_issueLogFile == null)
                {
                    lock (_writeLock)
                    {
                        if (_issueLogFile == null)
                        {
                            _issueLogFile = BuildLogFilePath("Auto7z_Issue", DateTime.Now);
                        }
                    }
                }

                return _issueLogFile;
            }
        }

        public enum ErrorLevel
        {
            Info = 0,
            Warning = 1,
            Error = 2
        }

        public void WriteLog(string message, ErrorLevel level)
        {
            // Info 记普通日志，Warning 与 Error 记问题日志
            string path = level == ErrorLevel.Info ? LogFilePath : IssueLogFilePath;
            WriteLog(message, level, path);
        }

        private void WriteLog(string message, ErrorLevel level, string logPath)
        {
            if (string.IsNullOrWhiteSpace(message) || level < MinLogLevel) return;

            try
            {
                lock (_writeLock)
                {
                    Directory.CreateDirectory(LogDirectory);
                    // 显式使用 UTF-8：默认编码受系统区域设置影响，会让中文日志在其它工具里变成乱码
                    using (var writer = new StreamWriter(logPath, true, Utf8NoBom))
                    {
                        string nowTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                        writer.WriteLine($"{nowTime} [{level.ToString().ToUpperInvariant()}]：{message}");
                    }
                }
            }
            catch
            {
                // 日志失败不应影响主流程，这里不弹窗，避免错误风暴
            }
        }

        /// <summary>
        /// 启动时清理过期的问题日志。
        ///
        /// 普通日志完整保留，只有问题日志按天数清理，
        /// 避免长时间使用后日志目录无限增长。
        /// </summary>
        public static void CleanupOldLogs()
        {
            try
            {
                if (!Directory.Exists(LogDirectory)) return;

                DateTime threshold = DateTime.Now.Date.AddDays(-IssueLogRetentionDays);

                foreach (string file in Directory.GetFiles(LogDirectory, _appName + "_Issue_*.log"))
                {
                    // 文件名格式：<程序名>_Issue_<yyyy-MM-dd>.log
                    string name = Path.GetFileNameWithoutExtension(file);
                    int separator = name.LastIndexOf('_');
                    if (separator < 0 || separator >= name.Length - 1) continue;

                    string dateText = name.Substring(separator + 1);
                    DateTime logDate;
                    if (!DateTime.TryParseExact(dateText, "yyyy-MM-dd",
                            CultureInfo.InvariantCulture, DateTimeStyles.None, out logDate))
                    {
                        continue;
                    }

                    if (logDate.Date >= threshold) continue;

                    try
                    {
                        File.Delete(file);
                    }
                    catch
                    {
                        // 文件被占用时保留，等下次启动再清理
                    }
                }
            }
            catch
            {
                // 清理失败不影响启动
            }
        }

        private static string BuildLogFilePath(string prefix, DateTime date)
        {
            return Path.Combine(LogDirectory, $"{prefix}_{date:yyyy-MM-dd}.log");
        }
    }
}
