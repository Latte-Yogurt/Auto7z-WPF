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

        /// <summary>
        /// 产物目录里的**所有权标记**文件名（BUG-04）。
        ///
        /// 它证明「这个目录是 Auto7z 为某个 JobId 建出来的」，而不是靠
        /// 「目录名长得像」来猜。只在任务进行期间存在：
        /// 正常结束时会删掉，所以用户拿到的产物目录里看不到它；
        /// 崩溃后它会留下，而那种目录本来就是要被清理的残片。
        /// </summary>
        private const string OwnerMarkerFileName = ".auto7z-owner";

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

            // **先取写入闸，并把整段操作一起罩住**（BUG-01 / BUG-03）。
            //
            // 必须在这里拦，不能只拦 WriteStateAtomic —— 那个更靠下的位置会得到
            // 「锁建出来了、状态文件没写」的半截结果：下次启动时锁 + 缺失的 state
            // 会被当成「上次崩溃」，进而按空产物路径走一遍清理逻辑。
            // 与其制造一个假的崩溃现场，不如整个跳过：搬迁期间本来就不该有任务在跑。
            //
            // 早先这里是 `if (IsClosed) return;` 的布尔判断，判完到建锁之间搬迁可能
            // 已经开始 —— 于是同一个「锁建了、状态没写」的坏结果还是会出现。
            // 现在改成持锁：搬迁方要动数据目录，必须等这一整段做完。
            //
            // 取不到（搬迁正在独占）就整体放弃。
            if (!MigrationGate.TryEnterWrite()) return;

            try
            {
                using (var mutex = AcquireMutex())
                {
                    if (mutex == null) return; // 拿不到互斥体：放弃记录，残留清理降级为不可用，但不阻断任务。

                    // 先清掉上次崩溃残留的旧锁，再建新锁。
                    RecoverUnderLock();

                    if (!TryCreateLock(out string jobId, out string startedAt)) return;

                    // **在产物目录里写下所有权标记**（BUG-04）。
                    //
                    // 必须与锁同时建好：没有标记的目录，将来崩溃恢复时不允许删除。
                    // 写入失败不阻断任务（标记是清理用的兜底，不是主流程），
                    // 但那样该目录就不会被自动清理 —— 由 DeleteOutputDirectory 拒绝。
                    WriteOwnerMarker(outputDirectory, jobId);

                    // 本线程已在隔离区内，WriteState 内部再取闸只会加深度，不会自锁。
                    WriteState(StateActive, jobId, outputDirectory, startedAt);
                }
            }
            catch
            {
                // 状态记录失败不影响任务本身：残留清理是尽力而为的兜底，不是主流程的一部分。
            }
            finally
            {
                MigrationGate.ExitWrite();
            }
        }

        /// <summary>任务结束（成功/失败/取消）后调用：原子写 state=done，再删锁。</summary>
        public static void MarkTaskFinished()
        {
            // **拿不到写入闸就整体放弃，包括删锁。**（P1-1，RC 阻断项）
            //
            // 上一版在这里刻意「不对称」：拿不到闸也照删锁，理由是
            // 「收尾让磁盘状态更简洁」。那个推理漏掉了外部程序——**搬迁**。
            //
            // 真实交错（GPT6 报的就是这条，已复核成立）：
            //
            //   1. 任务 active，产物目录有 `.auto7z-owner`，job.lock 存在
            //   2. 用户勾选便携模式 → 搬迁持闸 → 正在复制数据目录
            //   3. 任务恰好结束 → MarkTaskFinished 取闸失败
            //   4. 旧行为：仍执行 TryDeleteLock() → **锁被删了**
            //   5. 搬迁把「已删锁」之后的时间点复制过去，或者反过来：
            //      锁已被复制走、随后才被删 → 新目录里留下 active + 有效锁
            //   6. 下次启动：active + 锁 + 旧 PID 已退出 ⇒ 按「上次崩溃」递归删除产物目录
            //
            // 第 6 步删的是**用户已经成功生成的压缩包**。这是条件性数据丢失，
            // 而它恰恰是为「状态更简洁」这个次要目标付出的代价 —— 不划算。
            //
            // 现在的规则很简单：**要么整段收尾完成，要么什么都不动。**
            // 拿不到闸说明搬迁正在进行，此时状态与锁都属于「正在被搬走的那棵树」，
            // 本进程不该再去改它。搬迁结束后由下一次启动的 RecoverUnderLock
            // 按锁 + active 收敛（它会看到 PID 已退出，走精确清理），
            // 那条路径有所有权标记校验，不会误删用户产物。
            if (!MigrationGate.TryEnterWrite())
            {
                // 不写日志：日志写入同样要取闸，这里取不到，写出去也是丢弃。
                return;
            }

            try
            {
                using (var mutex = AcquireMutex())
                {
                    if (mutex == null) return;

                    // 先读出本次任务的产物目录，再写 done 把它清掉。
                    // 本方法没有目录参数（调用点分散在压缩/解压/取消/失败各处），
                    // 目录事实只存在于状态文件里 —— 读完就没得再问了。
                    string outputDir = GetValue(LoadState(), JobOutputDirKey);

                    WriteState(StateDone, jobId: string.Empty, outputDirectory: string.Empty, startedAt: string.Empty);

                    // **正常结束时删掉所有权标记，让产物目录保持干净。**
                    //
                    // 标记只在「任务进行中」有意义：它的用途是让崩溃恢复能确认
                    // 「这个目录确实是我的」。任务正常收尾后目录归用户所有，
                    // 里面不该多出一个隐藏文件。
                    //
                    // 顺序是有意的：先写 done、再删标记。反过来（先删标记后写 done）
                    // 若中间崩溃，留下的是「active + 无标记」的目录 ——
                    // 恢复会因缺少归属证据而不敢删，凭空多出一份残片。
                    RemoveOwnerMarker(outputDir);

                    TryDeleteLock();
                }
            }
            catch
            {
                // 写失败留着旧状态：下次启动会按锁 + active 再走一遍清理，幂等收敛。
            }
            finally
            {
                MigrationGate.ExitWrite();
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

                    // **把 jobId 一起传下去**：删除前要靠它核对目录里的所有权标记。
                    // 只传路径的话，删除侧就只剩「名字格式」这一条判据 —— 那正是 BUG-04。
                    if (DeleteOutputDirectory(outputDir, jobId))
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
            // **搬迁期间不写任务状态**（BUG-01 / BUG-03）。
            //
            // TaskState.json 与 job.lock 都是被搬迁的对象。搬迁期间本来就不该有任务在跑
            // （模式切换是用户主动操作，此时没有压缩/解压在进行），
            // 所以丢掉一次状态写入不影响正确性；反过来，把状态写进一个
            // 「已复制、未删除」的窗口，才会留下两边不一致的残留。
            //
            // 用持锁而不是布尔判断：判完到落盘之间搬迁可能已经开始。
            // 调用方（MarkTaskStarted）通常已经持闸，那时这里只是加一层重入深度。
            //
            // 直接返回：调用方本就是尽力而为，它们内部的 catch 也表明
            // 「状态记录失败不影响任务本身」。
            if (!MigrationGate.TryEnterWrite()) return;

            try
            {
                string target = StateFilePath;
                string directory = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

                // 临时名带 PID：两个进程同时写状态时不会互相覆盖对方的半截文件。
                string tmp = target + ".tmp." + Process.GetCurrentProcess().Id;

                // 边界校验：状态文件只能落在数据目录之内。
                // JsonConfig.WriteFile 的 allowedRoot 是可选参数，**默认不做校验** ——
                // 也就是说漏传就等于没这道闸门。这里显式传进去，避免「加了边界校验，
                // 却有一个调用点悄悄漏掉」这种最难发现的情形（配置写盘与日志删除都已接入，
                // 状态文件是第三个写盘点，本轮补上）。
                JsonConfig.WriteFile(tmp, JsonConfig.Build(entries, isBoolean), AppPaths.DataDirectory);

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
            finally
            {
                MigrationGate.ExitWrite();
            }
        }

        private static void TryDeleteTmp(string target)
        {
            try
            {
                string prefix = target + ".tmp.";
                string directory = Path.GetDirectoryName(target);
                if (directory == null) return;

                // 边界校验：只删**实际**落在数据目录之内的残留。
                //
                // 这些文件来自 GetFiles，正常情况下必然在目录内 —— 但数据目录可以被
                // AUTO7Z_DATA_ROOT 改道，也可能是指向别处的重解析点。
                // 那时字符串前缀判断会通过，而 File.Delete 穿过的是一条链接。
                // 删除不可逆，这里用文件系统边界（字符串 + 逐级重解析点检测）。
                string allowedRoot = AppPaths.DataDirectory;

                foreach (string f in Directory.GetFiles(directory, Path.GetFileName(prefix) + "*"))
                {
                    if (!FileSystemHelper.IsWithinFilesystemBoundary(f, allowedRoot)) continue;

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

        #region 目录所有权标记

        /// <summary>标记文件的完整路径。目录不存在时也能算出来（纯路径拼接）。</summary>
        private static string OwnerMarkerPath(string outputDirectory)
        {
            return Path.Combine(outputDirectory, OwnerMarkerFileName);
        }

        /// <summary>
        /// 在产物目录里写下所有权标记：`JobId` 一行、创建时间一行。
        ///
        /// 失败不抛：标记是崩溃清理的兜底凭据，不是任务本身的一部分。
        /// 写不出来只是「这个目录将来不会被自动清理」，比让压缩任务失败轻得多。
        /// </summary>
        /// <param name="outputDirectory">产物目录。</param>
        /// <param name="jobId">本次任务标识，与 job.lock 第一行一致。</param>
        private static void WriteOwnerMarker(string outputDirectory, string jobId)
        {
            if (string.IsNullOrWhiteSpace(outputDirectory) || string.IsNullOrEmpty(jobId)) return;

            try
            {
                Directory.CreateDirectory(outputDirectory);

                // 只写自己的标记文件，不碰目录里已有的任何内容。
                // 内容保持极简：将来即使要人工核对，两条文本也比二进制好读。
                string content = jobId + "\n"
                    + DateTime.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture) + "\n";

                File.WriteAllText(OwnerMarkerPath(outputDirectory), content, _utf8NoBom);
            }
            catch
            {
                // 目录只读、被占用等：放弃写标记。
                // 后果是 DeleteOutputDirectory 会因「无归属证据」而拒绝删除 ——
                // 也就是宁可留一份残片，也不冒险删一个来历不明的目录。
            }
        }

        /// <summary>
        /// 删掉产物目录里的所有权标记。任务正常结束时调用，让交付目录保持干净。
        ///
        /// 只删这一个固定文件名，且删除前确认它确实在数据目录之外的那个产物目录里
        /// （由调用方传入的路径决定），不会做任何递归或通配删除。
        /// </summary>
        /// <param name="outputDirectory">产物目录，可为 null（此时什么也不做）。</param>
        private static void RemoveOwnerMarker(string outputDirectory)
        {
            if (string.IsNullOrWhiteSpace(outputDirectory)) return;

            try
            {
                string marker = OwnerMarkerPath(outputDirectory);

                // 用边界版（字符串包含 **且** 路径上没有重解析点），不是纯字符串的
                // IsUnderDirectory：产物目录在用户的输出目录下，本身可能就是一条联接，
                // 那时「字符串在目录内」为真、实际删的是外部磁盘上的同名文件。
                //
                // 删除不可逆，这里多花几次属性查询是值得的。
                if (!FileSystemHelper.IsWithinFilesystemBoundary(marker, outputDirectory)) return;

                if (File.Exists(marker)) File.Delete(marker);
            }
            catch
            {
                // 删不掉就留着：一个隐藏标记文件不影响用户使用，
                // 且下次启动的清理逻辑会连同目录一起处理。
            }
        }

        /// <summary>
        /// 核对目录的**归属**：它必须带着我们为 <paramref name="jobId"/> 写的标记。
        ///
        /// 这是 BUG-04 的核心。早先只检查「目录名符合 43 字符的随机名格式」，
        /// 而格式是可以被伪造的：状态文件一旦被改写成
        /// `D:\ImportantData\xxxx...xxx`（43 个合法字符），
        /// 仅凭名字就会被递归删除 —— 删的是用户的真实数据。
        ///
        /// 名字格式只能说明「长得像」，标记才能说明「是我建的」。
        /// </summary>
        /// <param name="outputDirectory">待核对的目录。</param>
        /// <param name="jobId">状态文件里记录的任务标识。</param>
        /// <returns>确认属于本任务的产物目录返回 true。</returns>
        private static bool HasOwnerMarker(string outputDirectory, string jobId)
        {
            if (string.IsNullOrWhiteSpace(outputDirectory) || string.IsNullOrEmpty(jobId)) return false;

            string marker = OwnerMarkerPath(outputDirectory);

            // 标记文件本身也不能是链接：否者「标记在读、目录在别处」这种构造
            // 会让归属判断失去意义。fail-closed。
            if (FileSystemHelper.ExistsAsReparsePoint(marker)) return false;

            try
            {
                if (!File.Exists(marker)) return false;

                string[] lines = File.ReadAllLines(marker, _utf8NoBom);
                if (lines == null || lines.Length == 0) return false;

                // 严格比对：标记里的 JobId 必须与状态文件、job.lock 记录的那个完全一致。
                return string.Equals((lines[0] ?? string.Empty).Trim(), jobId.Trim(),
                    StringComparison.Ordinal);
            }
            catch
            {
                // 读不出标记 = 无法证明归属 = 不允许删除。
                return false;
            }
        }

        #endregion

        #region 目录清理与日志

        // 返回 true 表示目录已删除或本就不存在（无残留），false 表示删除失败（有残留）。
        //
        // **删除的三重前置条件（BUG-04）：**
        //   1. 目录名符合随机安全名格式（原有守卫，保留）
        //   2. 目录里带有本次 JobId 的所有权标记（新增，真正的归属证明）
        //   3. 路径上没有任何重解析点（新增，防止沿着链接删到外部磁盘）
        //
        // 第 1 条单独存在时**不能**作为删除依据：格式可伪造。
        // 早先正是如此 —— 状态文件被改写成指向用户目录时，仅凭名字就递归删除了它。
        private static bool DeleteOutputDirectory(string outputDirectory, string jobId)
        {
            string trimmed = outputDirectory.Trim();
            if (string.IsNullOrWhiteSpace(trimmed)) return true;

            // 白名单校验：只删本程序生成的随机安全目录（43 字符、只含 [A-Za-z0-9_-]）。
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

            // **归属校验**：名字像不等于是我建的，必须看到自己写的标记。
            //
            // 缺失标记的目录一律保留 —— 那可能是一个恰好同名的用户目录，
            // 也可能是被篡改过的状态指向的别处。删错一次不可逆，留一份残片可以再清。
            if (!HasOwnerMarker(trimmed, jobId))
            {
                LogService_Write("残留目录缺少所有权标记，拒绝删除：" + trimmed, LogService.ErrorLevel.Warning);
                return false;
            }

            // **路径上不能有重解析点**：否则递归删除会穿过联接删到外部磁盘。
            // 这是 IsSafeToDeleteRecursively 之外的第二道（那道只管数据目录白名单，
            // 而产物目录在用户的输出目录下，不在白名单范围内）。
            if (FileSystemHelper.ExistsAsReparsePoint(trimmed)
                || FileSystemHelper.HasReparsePointAncestor(trimmed))
            {
                LogService_Write("残留目录路径上存在重解析点，拒绝删除：" + trimmed, LogService.ErrorLevel.Warning);
                return false;
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
                // 直接引用 SevenZipService 的定义，不在这里重算一次路径。
                //
                // 早先这里写的是 Path.Combine(AppPaths.ComponentsDirectory, "7z.exe") ——
                // 与 SevenZipService.SevenZipExePath 是同一个值，但「同一个事实写两遍」
                // 意味着将来组件位置变了要改两处；漏一处就会「启动用的是新路径、
                // 这里仍在比对旧路径」，结果是孤儿 7z 永远匹配不上、清理静默失效。
                string expectedExe = SevenZipService.SevenZipExePath;
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

                bool acquired;
                try
                {
                    acquired = mutex.WaitOne(immediate ? 0 : 1000);
                }
                catch (AbandonedMutexException)
                {
                    // 上一个持有者没 ReleaseMutex 就退出了（进程被杀、线程异常终止）。
                    // 关键：这种情况下本线程**已经拿到**互斥体，异常只是通知「上一个主人是横死的」。
                    // 早先这里被外层 catch 一并吞掉、返回 null，于是「清理残留」这条路径
                    // 恰好在上一次崩溃之后最需要它的时候静默失效 —— 锁和 active 状态会一直留在盘上，
                    // 下次启动仍能清（PID 已死），但当次调用完全没做事，且不留任何痕迹。
                    acquired = true;
                }

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
