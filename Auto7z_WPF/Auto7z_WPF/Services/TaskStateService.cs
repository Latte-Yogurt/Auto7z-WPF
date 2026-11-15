using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace Auto7z_WPF.Services
{
    /// <summary>
    /// 任务进行中的状态持久化，用于崩溃后清理残留产物（分卷残片、半截归档）。
    ///
    /// 设计要点（job.lock 为真相源 + JSON 状态机 + 原子写 + 交叉验证）：
    ///
    /// 1. 状态文件 TaskState.json 用扁平键存一个 job 对象（JsonConfig 只支持顶层扁平对象，
    ///    故不嵌套），键为 JobId / JobState / JobOutputDir / JobStartedAt；
    /// 2. job.lock 独占创建（CREATE_NEW），内容为 jobId + PID + startedAt，
    ///    与 JSON 的 JobId 交叉验证，防止「JSON 是旧任务的、锁是新任务的」张冠李戴；
    /// 3. 所有写 JSON 都走原子路径：写同目录 .tmp → fsync 落盘 → File.Replace 到目标并备份 .bak；
    /// 4. 读 JSON 失败时回退 .bak；两者都坏则走保守清理（不删任何文件）；
    /// 5. 跨进程用命名互斥体串行化「开始任务 / 结束任务 / 启动恢复」三段临界区；
    /// 6. 恢复时检查锁文件里的 PID 是否还活着：活着说明任务正在进行，跳过清理，
    ///    否则第二个实例启动时会把第一个实例进行中的任务目录误删。
    ///
    /// 状态机：active → (崩溃后) cleaning → done。done 之后只删锁、不动文件。
    /// </summary>
    public static class TaskStateService
    {
        private const string StateFileName = "TaskState.json";
        private const string StateBackupFileName = "TaskState.json.bak";
        private const string LockFileName = "job.lock";

        private const string JobIdKey = "JobId";
        private const string JobStateKey = "JobState";
        private const string JobOutputDirKey = "JobOutputDir";
        private const string JobStartedAtKey = "JobStartedAt";

        private const string StateActive = "active";
        private const string StateCleaning = "cleaning";
        private const string StateDone = "done";

        // 跨进程命名互斥体：串行化「开始/结束/恢复」三段，防止两个进程同时读写状态文件。
        private const string MutexName = @"Local\Auto7z_TaskState";

        private static readonly UTF8Encoding _utf8NoBom = new UTF8Encoding(false);

        private static string StateFilePath => Path.Combine(AppPaths.DataDirectory, StateFileName);
        private static string BackupFilePath => Path.Combine(AppPaths.DataDirectory, StateBackupFileName);
        private static string LockFilePath => Path.Combine(AppPaths.DataDirectory, LockFileName);

        #region 对外接口

        /// <summary>任务真正开始前写入：独占建锁 + 原子写 state=active。</summary>
        /// <param name="outputDirectory">本任务的产物目录（随机安全目录），异常退出后要清理的就是它。</param>
        public static void MarkTaskStarted(string outputDirectory)
        {
            if (string.IsNullOrWhiteSpace(outputDirectory)) return;

            try
            {
                using (var mutex = AcquireMutex())
                {
                    if (mutex == null) return; // 拿不到互斥体：放弃记录，残留清理降级为不可用，但不阻断任务。

                    // 先清掉上次崩溃残留的旧锁，再建新锁。
                    RecoverUnderLock();

                    if (!TryCreateLock(out string jobId, out string startedAt)) return;

                    WriteState(StateActive, jobId, outputDirectory, startedAt);
                }
            }
            catch
            {
                // 状态记录失败不影响任务本身：残留清理是尽力而为的兜底，不是主流程的一部分。
            }
        }

        /// <summary>任务结束（成功/失败/取消）后调用：原子写 state=done，再删锁。</summary>
        public static void MarkTaskFinished()
        {
            try
            {
                using (var mutex = AcquireMutex())
                {
                    if (mutex == null) return;

                    // 把 state 落成 done（清空产物路径），随后删锁。done 之后的文件是完整产物，绝不再动。
                    WriteState(StateDone, jobId: string.Empty, outputDirectory: string.Empty, startedAt: string.Empty);
                    TryDeleteLock();
                }
            }
            catch
            {
                // 写失败留着旧状态：下次启动会按锁 + active 再走一遍清理，幂等收敛。
            }
        }

        /// <summary>
        /// 启动时调用：检查上次是否有未完成任务，有则清理残留产物目录。
        /// 先拿互斥体，拿不到说明有实例在跑，直接跳过恢复。
        /// </summary>
        public static void CleanupStaleTask()
        {
            try
            {
                using (var mutex = AcquireMutex(immediate: true))
                {
                    if (mutex == null) return; // 有实例在跑，跳过恢复，避免误删进行中任务。

                    RecoverUnderLock();
                }
            }
            catch
            {
                // 清理失败不阻断启动：残留目录只是占点空间，不该让程序起不来。
            }
        }

        #endregion

        #region 恢复核心（调用方必须已持有互斥体）

        /// <summary>恢复流程的完整实现，按「锁文件是否存在 → JSON 是否可信 → 状态」逐级判定。</summary>
        private static void RecoverUnderLock()
        {
            bool lockExists = File.Exists(LockFilePath);
            Dictionary<string, string> state = LoadState();

            string jobId = GetValue(state, JobIdKey);
            string jobState = GetValue(state, JobStateKey);
            string outputDir = GetValue(state, JobOutputDirKey);

            // —— 锁不存在 ——
            if (!lockExists)
            {
                if (jobState == StateActive || jobState == StateCleaning)
                {
                    // 有 active/cleaning 状态却没有锁：锁被人为删了或异常丢失。
                    // 拿不准就不删任何文件，只清空 job 并记一条告警。
                    LogService_Write("任务状态异常：有未完成任务标记但锁文件缺失，已清空状态、保留磁盘文件", LogService.ErrorLevel.Warning);
                    ClearJob();
                    return;
                }

                // job 为空或 done：无残留。清掉可能残留的旧状态文件，保持磁盘干净。
                if (state != null && jobId.Length > 0) ClearJob();
                return;
            }

            // —— 锁存在 ——
            string lockJobId = ReadLockJobId();
            int lockPid = ReadLockPid();

            // 关键安全检查：锁里的 PID 还活着 = 任务正在进行，跳过清理。
            // 没有这一步，第二个实例启动时会误删第一个实例正在写的任务目录。
            if (IsProcessAlive(lockPid))
            {
                return;
            }

            // JSON 损坏且 .bak 也坏：无法定位产物目录，保守清理（不删文件）。
            if (state == null)
            {
                ConservativeCleanup("状态文件与备份均损坏");
                return;
            }

            // jobId 缺失或与锁内容不一致：交叉验证失败，保守清理（不删文件）。
            if (string.IsNullOrEmpty(jobId) || !string.Equals(jobId, lockJobId, StringComparison.Ordinal))
            {
                ConservativeCleanup("任务标识与锁文件不一致");
                return;
            }

            // state=done：产物已完整，只删锁，不动文件。
            if (jobState == StateDone)
            {
                TryDeleteLock();
                return;
            }

            // state=active/cleaning 且产物目录有值：精确清理。
            if (jobState == StateActive || jobState == StateCleaning)
            {
                if (!string.IsNullOrWhiteSpace(outputDir))
                {
                    // 先标记进入清理阶段：清理中途再崩溃，下次仍能重扫（幂等）。
                    WriteState(StateCleaning, jobId, outputDir, GetValue(state, JobStartedAtKey));
                    if (DeleteOutputDirectory(outputDir))
                    {
                        // 删干净才写 done、删锁；删不掉保留锁，下次启动继续。
                        WriteState(StateDone, jobId: string.Empty, outputDirectory: string.Empty, startedAt: string.Empty);
                        TryDeleteLock();
                    }
                    else
                    {
                        // 目录仍在（被占用等）：保留 cleaning 状态与锁，本次不写 done。
                        LogService_Write("残留目录未能删除（可能仍被占用），保留锁待下次启动重试", LogService.ErrorLevel.Warning);
                    }
                    return;
                }

                // 产物目录为空/缺失：无法定位，保守清理。
                ConservativeCleanup("未记录产物目录");
                return;
            }

            // 未知状态：保守清理。
            ConservativeCleanup("未知任务状态：" + (jobState ?? "(空)"));
        }

        /// <summary>
        /// 保守清理：只删锁、清状态，绝不删任何用户文件。
        /// 现有产物是「随机 43 字符目录 + 未知父目录」，JSON 损坏时连父目录都不知道，
        /// 按命名模式扫描会误删其他正常任务的目录，因此保守清理不碰磁盘文件。
        /// </summary>
        private static void ConservativeCleanup(string reason)
        {
            LogService_Write("任务残留无法精确清理（" + reason + "），已保留磁盘文件、仅清空状态", LogService.ErrorLevel.Warning);
            ClearJob();
            TryDeleteLock();
        }

        #endregion

        #region 状态读写

        private static Dictionary<string, string> LoadState()
        {
            var state = TryParse(StateFilePath);
            if (state != null) return state;

            // 主文件损坏：回退 .bak。
            var backup = TryParse(BackupFilePath);
            if (backup != null) return backup;

            return null;
        }

        private static Dictionary<string, string> TryParse(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                string text = File.ReadAllText(path, _utf8NoBom);
                return JsonConfig.Parse(text);
            }
            catch
            {
                return null;
            }
        }

        private static string GetValue(Dictionary<string, string> state, string key)
        {
            if (state == null) return null;
            return state.TryGetValue(key, out string value) ? value : null;
        }

        private static void WriteState(string jobState, string jobId, string outputDirectory, string startedAt)
        {
            var entries = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>(JobIdKey, jobId ?? string.Empty),
                new KeyValuePair<string, string>(JobStateKey, jobState ?? string.Empty),
                new KeyValuePair<string, string>(JobOutputDirKey, outputDirectory ?? string.Empty),
                new KeyValuePair<string, string>(JobStartedAtKey, startedAt ?? string.Empty)
            };
            var isBoolean = new List<bool> { false, false, false, false };

            WriteStateAtomic(entries, isBoolean);
        }

        private static void ClearJob()
        {
            // 清空 = 写一份「全空」的合法状态；读侧据此判断「无 job」。
            WriteState(jobState: string.Empty, jobId: string.Empty,
                outputDirectory: string.Empty, startedAt: string.Empty);
        }

        // 原子写：同目录 .tmp → fsync 落盘 → File.Replace 到目标并备份 .bak。
        private static void WriteStateAtomic(List<KeyValuePair<string, string>> entries, List<bool> isBoolean)
        {
            string target = StateFilePath;
            string directory = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            string tmp = target + ".tmp." + Process.GetCurrentProcess().Id;

            JsonConfig.WriteFile(tmp, JsonConfig.Build(entries, isBoolean));

            // fsync：确保数据真正落盘，再原子替换，避免替换后只剩半截文件。
            try
            {
                using (var fs = new FileStream(tmp, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    fs.Flush(true);
                }
            }
            catch
            {
                // 某些文件系统不支持 Flush(true)，忽略：仍走下面的替换，最坏情况是 .bak 回退兜底。
            }

            if (File.Exists(target))
            {
                File.Replace(tmp, target, BackupFilePath, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(tmp, target);
            }

            TryDeleteTmp(target);
        }

        private static void TryDeleteTmp(string target)
        {
            try
            {
                string prefix = target + ".tmp.";
                string directory = Path.GetDirectoryName(target);
                if (directory == null) return;
                foreach (string f in Directory.GetFiles(directory, Path.GetFileName(prefix) + "*"))
                {
                    try { File.Delete(f); } catch { /* 残留的 .tmp 不影响下一次写入，下次会换新 PID 名 */ }
                }
            }
            catch
            {
                // 清理 .tmp 失败不影响主流程。
            }
        }

        #endregion

        #region 锁文件

        // 独占创建锁文件；返回 true 表示本进程成为唯一任务持有者。
        private static bool TryCreateLock(out string jobId, out string startedAt)
        {
            jobId = Guid.NewGuid().ToString("N");
            startedAt = DateTime.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture);

            try
            {
                using (var fs = new FileStream(LockFilePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(fs, _utf8NoBom))
                {
                    writer.WriteLine(jobId);
                    writer.WriteLine(Process.GetCurrentProcess().Id);
                    writer.WriteLine(startedAt);
                    writer.Flush();
                    fs.Flush(true);
                }
                return true;
            }
            catch (IOException)
            {
                // CREATE_NEW 失败 = 锁已存在。正常流程下 RecoverUnderLock 已先清理过旧锁，
                // 走到这里说明清理没删掉（被占用等），放弃本次记录。
                return false;
            }
            catch
            {
                return false;
            }
        }

        private static string ReadLockJobId()
        {
            try
            {
                var lines = File.ReadAllLines(LockFilePath, _utf8NoBom);
                return lines != null && lines.Length > 0 ? (lines[0] ?? string.Empty).Trim() : string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static int ReadLockPid()
        {
            try
            {
                var lines = File.ReadAllLines(LockFilePath, _utf8NoBom);
                if (lines != null && lines.Length > 1 && int.TryParse((lines[1] ?? string.Empty).Trim(), out int pid))
                    return pid;
            }
            catch
            {
                // 读不到 PID 按 0 处理，跳过存活检查。
            }
            return 0;
        }

        private static bool IsProcessAlive(int pid)
        {
            if (pid <= 0) return false;
            try
            {
                using (var process = Process.GetProcessById(pid))
                {
                    return !process.HasExited;
                }
            }
            catch
            {
                // 进程不存在或已退出：都视为「已死」，可以清理。
                return false;
            }
        }

        private static void TryDeleteLock()
        {
            try
            {
                if (File.Exists(LockFilePath)) File.Delete(LockFilePath);
            }
            catch
            {
                // 删不掉留着：下次启动再试，锁文件存在是幂等收敛的信号。
            }
        }

        #endregion

        #region 目录清理与日志

        // 返回 true 表示目录已删除或本就不存在（无残留），false 表示删除失败（有残留）。
        private static bool DeleteOutputDirectory(string outputDirectory)
        {
            string trimmed = outputDirectory.Trim();
            if (string.IsNullOrWhiteSpace(trimmed)) return true;

            // 白名单校验：只删本程序生成的随机安全目录（43 字符、只含 [A-Za-z0-9_-]），
            // 防止状态文件被篡改后指向用户目录。
            string name = Path.GetFileName(trimmed.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (!FileSystemHelper.IsGeneratedFolderName(name))
            {
                LogService_Write("残留目录名校验失败，拒绝删除：" + trimmed, LogService.ErrorLevel.Warning);
                return false;
            }

            if (!Directory.Exists(trimmed))
            {
                // 目录本就不存在：无残留，算成功。
                return true;
            }

            // 孤儿 7z 进程可能仍占用目录里的分卷文件：先杀干净再删，否则删除会因占用失败。
            // 这些 7z 进程的父进程（Auto7z.exe）已被强杀，它们只会继续写半截产物，留着有害无益。
            KillOrphanSevenZip();

            // 删除失败重试几次：7z 进程被杀后文件句柄释放需要一点时间。
            for (int attempt = 0; attempt < 5; attempt++)
            {
                FileSystemHelper.DeleteDirectoryQuietly(trimmed);
                if (!Directory.Exists(trimmed))
                {
                    return true;
                }
                System.Threading.Thread.Sleep(200);
            }

            return !Directory.Exists(trimmed);
        }

        // 杀掉残留的 7z 子进程：父进程被强杀后，7z.exe 成为孤儿进程继续写分卷。
        // 只杀本程序相关的 7z（组件目录里的 7z.exe），不碰用户系统里的其他 7-Zip。
        private static void KillOrphanSevenZip()
        {
            try
            {
                string expectedExe = Path.Combine(AppPaths.ComponentsDirectory, "7z.exe");
                foreach (var process in Process.GetProcessesByName("7z"))
                {
                    try
                    {
                        if (string.Equals(process.MainModule.FileName, expectedExe, StringComparison.OrdinalIgnoreCase))
                        {
                            process.Kill();
                            process.WaitForExit(2000);
                        }
                    }
                    catch
                    {
                        // 进程可能已自行退出，或无权读取模块信息；继续下一个。
                    }
                    finally
                    {
                        try { process.Dispose(); } catch { /* 释放失败无碍 */ }
                    }
                }
            }
            catch
            {
                // 枚举进程失败不影响后续删除重试。
            }
        }

        private static void LogService_Write(string message, LogService.ErrorLevel level)
        {
            try
            {
                new LogService().WriteLog(message, level);
            }
            catch
            {
                // 日志失败不影响恢复流程。
            }
        }

        #endregion

        #region 互斥体

        private static Mutex AcquireMutex(bool immediate = false)
        {
            Mutex mutex = null;
            try
            {
                mutex = new Mutex(false, MutexName);
                bool acquired = mutex.WaitOne(immediate ? 0 : 1000);
                if (!acquired)
                {
                    mutex.Dispose();
                    return null;
                }
                return mutex;
            }
            catch
            {
                if (mutex != null)
                {
                    try { mutex.Dispose(); } catch { /* 释放失败无碍 */ }
                }
                return null;
            }
        }

        #endregion
    }
}
