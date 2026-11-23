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
        // 两个字段只在这把锁内改写（ResetLogFiles 与两个 getter），getter 的返回值也在锁内取，
        // 否则会出现「读到 null」的窗口，见 LogFilePath 的注释。
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
                // 锁外快路径先取一次本地副本：直接读字段再在锁内二次判断，
                // 最后的 return 必须也在锁内返回 —— 否则「刚重建好、还没 return」
                // 这段时间里被 ResetLogFiles() 置空，getter 就会返回 null。
                // 那会让调用方把 null 当路径去开文件流，抛异常后被日志层的 catch 吞掉，
                // 表现为「切换便携模式的那一瞬间有几条日志凭空消失」，且毫无痕迹。
                string cached = _normalLogFile;
                if (cached != null) return cached;

                lock (_writeLock)
                {
                    if (_normalLogFile == null)
                    {
                        _normalLogFile = BuildLogFilePath("Auto7z", DateTime.Now);
                    }

                    // 在锁内返回，保证拿到的值此刻不为 null。
                    return _normalLogFile;
                }
            }
        }

        public static string IssueLogFilePath
        {
            get
            {
                string cached = _issueLogFile;
                if (cached != null) return cached;

                lock (_writeLock)
                {
                    if (_issueLogFile == null)
                    {
                        _issueLogFile = BuildLogFilePath("Auto7z_Issue", DateTime.Now);
                    }

                    return _issueLogFile;
                }
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

            // **先取写入闸，再写文件**（BUG-01）。
            //
            // 日志文件正是被搬迁的那些文件之一：写进去的行如果落在「已复制、未删除」
            // 这个窗口里，复制时没读到、删除后也没了 —— 那行日志**永久消失**。
            //
            // 早先这里判的是 `MigrationGate.IsClosed` 布尔量，判完到真正打开文件之间
            // 搬迁可能已经开始 —— 检查再多次也只是把窗口变窄，不是消除。
            // 现在改成真的持锁：搬迁方必须等这次写入关掉文件才能开始复制。
            //
            // 取不到（搬迁正在独占）就放弃本次写入：日志是尽力而为的，
            // 丢一行远好过留下一份「看起来完整、其实缺了一段」的文件。
            if (!MigrationGate.TryEnterWrite()) return;

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
            finally
            {
                // 顺序与上面相反：先写完文件、再放开搬迁闸。
                MigrationGate.ExitWrite();
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

                    // 边界校验：只能删「确认在日志目录之内、且路径上没有重解析点」的文件。
                    //
                    // 为什么不是普通的字符串包含判断：`GetFullPath` 不会展开联接。
                    // 日志目录若是指向 D:\ExternalData 的 Junction，一条 Logs\old.log
                    // 在字符串上确实在日志目录里，但 File.Delete 会穿过联接到外部磁盘。
                    // 这条检查因此必须是「文件系统边界」而不是「字符串前缀」。
                    //
                    // 它同时挡住了另一类风险：数据目录被改道到带父级跳转的路径时
                    // （AUTO7Z_DATA_ROOT 指到别处），前缀判断也可能被绕过。
                    // 删除不可逆，宁可在这一层多问一句（代价是几次 GetFileAttributes）。
                    if (!FileSystemHelper.IsWithinFilesystemBoundary(file, LogDirectory)) continue;

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
