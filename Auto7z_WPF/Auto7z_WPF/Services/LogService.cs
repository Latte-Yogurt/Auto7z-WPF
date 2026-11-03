using System;
using System.Globalization;
using System.IO;
using System.Reflection;

namespace Auto7z_WPF.Services
{
    /// <summary>
    /// 按天分文件的文本日志：Info 进常规日志，Warning 及以上进问题日志，反馈时只需提供后者。
    /// </summary>
    public class LogService
    {
        #region 日志文件路径
        // 问题日志的文件名前缀，清理时按同一前缀匹配，避免误删其他日志。
        private static readonly string _appName = "Auto7z";

        // 多个线程可能同时写同一个文件，路径计算与写入都放在这把锁里串行化。
        private static readonly object _writeLock = new object();

        // 不写 BOM，外部脚本按行合并日志时不会在文件中间混入 BOM 字节。
        private static readonly System.Text.Encoding _utf8NoBom = new System.Text.UTF8Encoding(false);

        // 问题日志保留一周，覆盖用户反馈周期，又不至于无限增长。
        private const int IssueLogRetentionDays = 7;

        // 路径按当天日期生成后缓存；进程跨天运行仍写同一文件，避免一次会话的日志被拆成两份。
        private static string _normalLogFile;

        private static string _issueLogFile;

        // 默认只记 Warning 及以上，需要更细的排查信息时由用户显式调低阈值。
        public static ErrorLevel MinLogLevel { get; set; } = ErrorLevel.Warning;

        private static string LogDirectory => AppPaths.LogDirectory;

        // 日志目录切换后必须清空缓存，否则仍会写回旧路径。
        public static void ResetLogFiles()
        {
            lock (_writeLock)
            {
                _normalLogFile = null;
                _issueLogFile = null;
            }
        }

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

        #endregion
        #region 日志级别与写入
        // 数值大小用于级别过滤，新增级别必须插在合适的位置，否则过滤结果会错。
        public enum ErrorLevel
        {
            Info = 0,
            Warning = 1,
            Error = 2
        }

        // Info 之外的级别都进问题日志，用户反馈时只需要提供 Issue 文件。
        public void WriteLog(string message, ErrorLevel level)
        {
            string path = level == ErrorLevel.Info ? LogFilePath : IssueLogFilePath;
            WriteLog(message, level, path);
        }

        private void WriteLog(string message, ErrorLevel level, string logPath)
        {
            // 低于阈值的消息直接丢弃，避免高频 Info 淹没真正需要排查的问题。
            if (string.IsNullOrWhiteSpace(message) || level < MinLogLevel) return;

            try
            {
                lock (_writeLock)
                {
                    // 目录可能被用户或清理工具删掉，每次写入前确保存在。
                    Directory.CreateDirectory(LogDirectory);
                    // 每次写入都重新打开再关闭，进程被强杀时已写内容仍会留在磁盘上。
                    using (var writer = new StreamWriter(logPath, true, _utf8NoBom))
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

        #endregion
        #region 日志清理与命名
        // 只清理问题日志且按文件名里的日期判断，常规日志留给用户自行处置；日期解析不出的文件一律不动。
        public static void CleanupOldLogs()
        {
            try
            {
                if (!Directory.Exists(LogDirectory)) return;

                DateTime threshold = DateTime.Now.Date.AddDays(-IssueLogRetentionDays);

                foreach (string file in Directory.GetFiles(LogDirectory, _appName + "_Issue_*.log"))
                {
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
        #endregion
    }
}
