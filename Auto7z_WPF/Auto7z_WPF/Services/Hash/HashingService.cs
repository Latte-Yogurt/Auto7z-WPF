using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace Auto7z_WPF.Services
{
    /// <summary>
    /// 校验文件的读写。算法由 <see cref="HashAlgorithms"/> 决定，值一律是小写十六进制，
    /// 行格式是 sha256sum 通用的「值 + 空格 + * + 文件名」，不带路径。
    /// </summary>
    public static class ChecksumWriter
    {
        private static readonly char[] _hexLower = "0123456789abcdef".ToCharArray();

        /// <summary>字节数组转小写十六进制串。</summary>
        public static string ToHex(byte[] bytes)
        {
            if (bytes == null) return string.Empty;

            var chars = new char[bytes.Length * 2];
            for (int i = 0; i < bytes.Length; i++)
            {
                byte b = bytes[i];
                chars[i * 2] = _hexLower[b >> 4];
                chars[i * 2 + 1] = _hexLower[b & 0x0F];
            }
            return new string(chars);
        }

        /// <summary>
        /// 生成一行标准格式的校验文本。文件名本身以 * 开头时前面加下划线，
        /// 避免和 sha256sum 的引导符混淆。
        /// </summary>
        public static string FormatLine(string hex, string fileName)
        {
            string name = fileName ?? string.Empty;
            if (name.StartsWith("*", StringComparison.Ordinal)) name = "_" + name;

            return string.Format("{0} *{1}", hex, name);
        }

        /// <summary>
        /// 把一批行写出。默认 UTF-8 无 BOM：GBK 遇到非中文字符集的文件名会产生乱码/丢字，
        /// 而 BOM 会让不少校验解析软件把首行哈希当成无效内容。
        /// 覆盖前先清掉隐藏与只读属性，否则 FileMode.Create 会失败。
        /// </summary>
        public static void Write(string path, IEnumerable<string> lines, Encoding encoding = null)
        {
            if (string.IsNullOrWhiteSpace(path) || lines == null) return;

            if (File.Exists(path))
            {
                try
                {
                    FileAttributes attributes = File.GetAttributes(path);
                    if ((attributes & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
                    {
                        File.SetAttributes(path, FileAttributes.Normal);
                    }
                }
                catch
                {
                    // 改不动属性就照样尝试写入，真正的失败会在下面写文件时暴露。
                }
            }

            // 原子写入：先写同目录临时文件，完整落盘后再替换目标。
            //
            // 直接 new StreamWriter(path, false, ...) 会先把目标截断再写：一旦中途抛异常
            // （磁盘满、进程被结束、句柄被占用），旧的校验清单已经被毁，新的又不完整 ——
            // 两头都没有可用清单。这里沿用 TaskStateService 已有的那套原子写法。
            string directory = Path.GetDirectoryName(Path.GetFullPath(path));
            string tempPath = Path.Combine(directory,
                Path.GetFileName(path) + ".tmp" + Guid.NewGuid().ToString("N").Substring(0, 8));

            try
            {
                using (var writer = new StreamWriter(tempPath, false, encoding ?? new UTF8Encoding(false)))
                {
                    foreach (string line in lines)
                    {
                        writer.WriteLine(line);
                    }

                    // 显式落盘：不 Flush 的话数据可能还在缓冲里，替换过去的会是个空文件。
                    // Flush(true) 要求 FileStream 才能把 OS 缓冲也刷下去，否则只停在 .NET 缓冲这一层。
                    writer.Flush();
                    var fileStream = writer.BaseStream as FileStream;
                    if (fileStream != null) fileStream.Flush(true);
                }

                if (File.Exists(path))
                {
                    // 目标是只读时 File.Replace 会失败，先去掉只读位再替换。
                    File.SetAttributes(tempPath, File.GetAttributes(path));
                    File.Replace(tempPath, path, null);
                }
                else
                {
                    File.Move(tempPath, path);
                }
            }
            finally
            {
                // 替换成功后临时文件已不存在；失败时把它清掉，不留下半截文件。
                try
                {
                    if (File.Exists(tempPath)) File.Delete(tempPath);
                }
                catch
                {
                    // 临时文件删不掉不影响已经成功写入的结果。
                }
            }
        }
    }

    /// <summary>
    /// 一批文件的校验计算结果。
    ///
    /// 存在的理由：只返回 string[] 时，调用方无法区分「这批只有 4 个文件」与
    /// 「这批有 5 个文件但 1 个读失败被跳过」。前者是正常的，后者意味着写出的校验清单不完整，
    /// 而用户会拿这份清单去校验一整套压缩包 —— 缺一项就等于永远校验不过，且原因不可见。
    /// </summary>
    public class HashBatchResult
    {
        public HashBatchResult(string[] lines, IList<string> failedFiles)
            : this(lines, failedFiles, null)
        {
        }

        public HashBatchResult(string[] lines, IList<string> failedFiles, IList<string> changedDuringReadFiles)
        {
            Lines = lines ?? new string[0];
            FailedFiles = failedFiles ?? new List<string>();
            ChangedDuringReadFiles = changedDuringReadFiles ?? new List<string>();
        }

        /// <summary>成功算出的行（格式 "hex *filename"），可直接写进校验清单。</summary>
        public string[] Lines { get; private set; }

        /// <summary>读取失败、因而没有出现在 <see cref="Lines"/> 里的文件绝对路径。</summary>
        public IList<string> FailedFiles { get; private set; }

        /// <summary>
        /// 读到了、算出值了，但**读取期间文件被改动过**的文件绝对路径。
        ///
        /// 这些行在 <see cref="Lines"/> 里，只是那个摘要对应的是「读取过程中的某一瞬间」，
        /// 未必等于磁盘上此刻的内容。以前这种文件与正常文件完全无法区分：
        /// `FileShare.ReadWrite` 允许别的进程同时写，摘要照样算得出来，于是清单可能永远校验不过，
        /// 而清单本身看起来完美无缺。
        ///
        /// 判定方式是读取前后各取一次长度与最后写入时间：两者都没变才算「读的是一份不动的文件」。
        /// 这是一条**新的诚实契约**，不改变原有任何行为 —— 行照样写出去（总比没有强），
        /// 只是把「这一行可能已经过时」这件事摆到调用方眼前。
        /// </summary>
        public IList<string> ChangedDuringReadFiles { get; private set; }

        /// <summary>
        /// 是否每个输入文件都算出了值**且**在读取期间没有变动。
        /// 为 false 时 <see cref="Lines"/> 要么不覆盖全部文件，要么其中某些行可能已经过时。
        /// </summary>
        public bool IsComplete { get { return FailedFiles.Count == 0 && ChangedDuringReadFiles.Count == 0; } }

        /// <summary>输入文件总数（成功 + 失败）。</summary>
        public int TotalFiles { get { return Lines.Length + FailedFiles.Count; } }

        /// <summary>
        /// 上一轮遗留的同名校验清单最后怎么了。
        ///
        /// 只有「一个文件都没算出值」时才有意义：那种情况下没有新清单可写，
        /// 磁盘上若还留着上一轮的清单，它会被当成本次产物的校验文件。
        /// 调用方据此决定提示「已清除」还是「删不掉，请手动检查」——
        /// 两种情况对用户的意义完全不同，不能合并成一句「清单没生成」。
        ///
        /// 注意：<see cref="StaleManifestOutcome.NotPresent"/> 表示**已确认**没有旧清单，
        /// 不是「查不出来」。查不出来会报 <see cref="StaleManifestOutcome.RemovalFailed"/>。
        /// </summary>
        public StaleManifestOutcome StaleManifest { get; internal set; }

        public static HashBatchResult Empty()
        {
            return new HashBatchResult(new string[0], new List<string>());
        }
    }

    /// <summary>旧校验清单的清理结果，见 <see cref="HashBatchResult.StaleManifest"/>。</summary>
    public enum StaleManifestOutcome
    {
        /// <summary>确认本来就没有旧清单，无事可做。</summary>
        NotPresent,

        /// <summary>确认旧清单存在且已删除。</summary>
        Removed,

        /// <summary>
        /// 没能确认旧清单已被清除：它确实还在（删不掉），或者连「在不在」都无法判断。
        ///
        /// 两种情况的用户动作是同一个 —— 去那个路径手动看一眼 —— 所以合并为一个状态。
        /// 但**绝不能**把它报成 <see cref="NotPresent"/>：那等于宣称磁盘是干净的，
        /// 而实际可能正躺着一份描述着别的内容的清单。
        /// </summary>
        RemovalFailed
    }

    /// <summary>
    /// 多算法校验值计算服务。这是本项目所有校验计算的唯一入口：
    /// 压缩完成后的校验文件、测试探针、界面进度都走这里。
    ///
    /// 设计取舍：
    /// **按算法定缓冲区**：大文件顺序扫描 1MB 明显快于小缓冲，但小文件占多数时 1MB 是浪费，
    /// 平均不到 1MB 时换 64KB —— 实测 5000 个 4KB 文件快约 18%。
    /// **进度单调**：基于「已处理字节 / 总字节」，一次任务内单调递增，不再每个文件都从 0 跳到 100。
    /// **坏文件跳过**：批量计算不会因为一个坏文件全废，失败的文件按已知体积补齐进度。
    /// **哈希实例复用**：每个文件省掉一次内核重建，实测与逐文件新建结果完全一致。
    /// </summary>
    public class HashingService : IDisposable
    {
        // 1MB 缓冲区。这个尺寸会落到大对象堆上，但顺序扫描大文件时它明显快于小缓冲，取舍是故意的。
        private const int BufferSize = 1024 * 1024;

        // 小文件占多数时 1MB 缓冲反而是浪费：每个文件都要从 1MB 的堆块里读几 KB，
        // 而真正的成本落在逐文件的元数据往返上。实测 5000 个 4KB 文件，64KB 缓冲比 1MB 快约 18%。
        private const int SmallFileBufferSize = 64 * 1024;

        // 小于这个尺寸的文件按「小文件」处理，超过就换回 1MB 的顺序扫描缓冲。
        private const long SmallFileThreshold = 1024 * 1024;

        private const int UiRefreshMs = 100;           // 进度回调节流间隔

        /// <summary>
        /// 计算一批文件，并回报哪些文件没能算出值。
        ///
        /// 为什么需要这个重载：原先只有 <see cref="Calculate"/>，单个文件读取失败时被静默跳过，
        /// 调用方只拿到「成功的那几行」。于是「5 个分卷里有 1 个读不出来」会生成一份**只有 4 行**的
        /// 校验清单，而调用方既没异常也没失败状态 —— 用户以为清单覆盖了全套，实际缺一项。
        /// 哈希算错是静默的，漏项同样是静默的，所以必须把失败明细摆到调用方眼前。
        /// </summary>
        public HashBatchResult CalculateDetailed(string[] paths, HashAlgorithmInfo algorithm,
            Action<int> progress, CancellationToken cancellationToken)
        {
            if (paths == null || paths.Length == 0) return HashBatchResult.Empty();

            HashAlgorithmInfo target = algorithm ?? HashAlgorithms.Default;

            // 先把这批路径展开成「文件列表 + 每文件大小」，展开时一并算出总大小。
            //
            // 这里不再对每个文件各做一次 File.Exists 与 FileInfo.Length：两者都是元数据往返，
            // 每次都要向文件系统重问一遍同一个文件的属性。实测 5000 个 4KB 文件时，这两项
            // 合计占掉单次计算约 45% 的时间，而这些文件本来就在同一次目录枚举里列出来过。
            // 目录分支改用 FileSystemHelper 的原生遍历，一次拿回路径与大小（同样工作量约 1/5 的耗时）；
            // 打不开的文件在下面的读取阶段被单独 catch，效果与逐条探测等价。
            var files = new List<string>();
            var fileSizes = new List<long>();
            // 枚举阶段就没能读到的子目录/路径：它们里面的文件不会进入本次计算，
            // 因此清单必然不完整，最终一并计入 FailedFiles 让调用方看见。
            var unreadablePaths = new List<string>();
            foreach (var path in paths)
            {
                if (string.IsNullOrWhiteSpace(path)) continue;
                string fullPath = path;
                try
                {
                    fullPath = Path.GetFullPath(path);

                    // 单条输入可能是文件也可能是目录，必须分清，所以这里各探一次。
                    if (File.Exists(fullPath))
                    {
                        files.Add(fullPath);
                        try
                        {
                            fileSizes.Add(new FileInfo(fullPath).Length);
                        }
                        catch
                        {
                            fileSizes.Add(0);
                        }
                    }
                    else if (Directory.Exists(fullPath))
                    {
                        FileSystemHelper.EnumerateFilesWithSizesDetailed(fullPath, out List<string> childFiles,
                            out List<long> childSizes, out List<string> skippedDirs);
                        files.AddRange(childFiles);
                        fileSizes.AddRange(childSizes);

                        // 读不进去的子目录：这里进去的文件不会出现在清单里，必须让调用方知道。
                        foreach (string skipped in skippedDirs)
                        {
                            unreadablePaths.Add(skipped);
                        }
                    }
                    else
                    {
                        // 既不是文件也不是目录：路径在枚举与读取之间被删掉了，或本来就不存在。
                        // 这里**必须**记账 —— 早先这个分支是空的，该路径被完全丢弃，
                        // 于是 TotalFiles 少算一个、IsComplete 仍然是 true，
                        // 而「少一项的清单」正是本次要消灭的那种静默不完整。
                        unreadablePaths.Add(fullPath);
                    }
                }
                catch
                {
                    // 整个输入路径都无法访问：同样要记账，否则清单会看起来完整却少了整片内容。
                    unreadablePaths.Add(fullPath);
                }
            }

            long totalSize = 0;
            for (int i = 0; i < fileSizes.Count; i++) totalSize += fileSizes[i];

            var results = new List<string>(files.Count);
            // 记录每个没算出来的文件：调用方据此判断清单是否完整。
            var failed = new List<string>();
            // 算出来了、但读取期间文件被改动过：摘要对应的是读取途中的某个瞬间，未必等于磁盘现状。
            var changed = new List<string>();
            long processedSize = 0;
            DateTime lastUi = DateTime.MinValue;
            int lastPercent = -1;

            // 缓冲区按这批文件的大小分布挑一次，不再每个文件都重建。
            byte[] buffer = new byte[ChooseBufferSize(fileSizes, totalSize)];

            // 内核实例全程复用：构造成本可以忽略，但复用后每个文件都省掉一次提供程序的重新打开。
            //
            // 释放用 try/finally + 运行时判断 IDisposable，而不是给 IHashCore 加接口约束：
            //   ① 现有实现（纯托管算法）都不持有非托管资源，强制实现 IDisposable 是空负担；
            //   ② 但将来若换成调用系统 CNG 提供程序的实现，它一定会有原生句柄 ——
            //      那时泄漏只在长时间批量计算时才显形，极难归因。
            // 这里按「谁实现了就释放谁」处理，两头都不吃亏，且不改变接口契约。
            IHashCore core = target.CreateCore();
            try
            {
                core.Initialize();

                for (int i = 0; i < files.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    string filePath = files[i];
                    try
                    {
                        // 每个文件开始前复位摘要状态，复用实例不会把上一个文件的残余带进来。
                        core.Initialize();

                        // 读前/读后各取一次「长度 + 最后写入时间」。FileShare.ReadWrite 允许别的进程
                        // 同时写这个文件，摘要照样算得出来 —— 但那样算的是「读取过程中的某一瞬间」，
                        // 清单可能永远校验不过，而清单本身看起来毫无问题。两次指纹一致才算读的是一份不动的文件。
                        //
                        // 取不到指纹不算失败（读得到内容就说明能算），只是无法作此判断，按「未变动」处理：
                        // 这里的目标是把「可判定且确实变了」这种情况暴露出来，不是给每次读取加一道否决权。
                        bool fingerprintBefore = TryReadFingerprint(filePath, out long lengthBefore, out DateTime writeBefore);

                        using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.SequentialScan))
                        {
                            int bytesRead;
                            while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0)
                            {
                                cancellationToken.ThrowIfCancellationRequested();

                                core.Append(buffer, 0, bytesRead);
                                processedSize += bytesRead;

                                var now = DateTime.UtcNow;
                                if ((now - lastUi).TotalMilliseconds >= UiRefreshMs)
                                {
                                    lastUi = now;
                                    int percent = GetPercent(processedSize, totalSize);
                                    if (percent != lastPercent)
                                    {
                                        lastPercent = percent;
                                        progress?.Invoke(percent);
                                    }
                                }
                            }
                        }

                        results.Add(ChecksumWriter.FormatLine(
                            ChecksumWriter.ToHex(core.Finish()), Path.GetFileName(filePath)));

                        if (fingerprintBefore
                            && TryReadFingerprint(filePath, out long lengthAfter, out DateTime writeAfter)
                            && (lengthAfter != lengthBefore || writeAfter != writeBefore))
                        {
                            // 行照样写出去（一份可能过时的清单也比没有强），但必须登记，
                            // 让调用方能把「这一行对应的是读取途中那一瞬间」告诉用户。
                            changed.Add(filePath);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch
                    {
                        // 单个文件失败（被占用/无权限）时用已完成的大小补齐进度，不打断整体流程；
                        // 但必须把它记下来 —— 否则调用方无从知道清单少了哪一项。
                        //
                        // 越界防护不是多余的：files 与 fileSizes 目前由三条路径成对填充
                        // （单文件、目录枚举、以及 catch 里的补 0），理论上等长。
                        // 但这层不变量没有任何编译期约束，一旦将来新增一条只往 files 里加、
                        // 忘了配 fileSizes 的路径，这里就会抛 IndexOutOfRangeException ——
                        // 而它发生在「处理失败文件」的 catch 里，会把一次可恢复的读取失败
                        // 升级成整批计算崩溃，正是本方法最该避免的事。
                        if (i < fileSizes.Count) processedSize += fileSizes[i];
                        failed.Add(filePath);
                    }

                    int current = GetPercent(processedSize, totalSize);
                    if (current != lastPercent)
                    {
                        lastPercent = current;
                        progress?.Invoke(current);
                    }
                }
            }
            finally
            {
                // 只有实现了 IDisposable 的内核才释放（见上面的说明）。
                var disposable = core as IDisposable;
                if (disposable != null) disposable.Dispose();
            }

            progress?.Invoke(100);
            // 枚举阶段漏掉的路径与读取阶段失败的文件，对调用方是同一件事：清单没覆盖全。
            foreach (string missing in unreadablePaths)
            {
                failed.Add(missing);
            }

            return new HashBatchResult(results.ToArray(), failed, changed);
        }

        /// <summary>
        /// 取文件的「长度 + 最后写入时间」作为一致性指纹。取不到时返回 false，
        /// 由调用方按「无法判定」处理（不是失败）。
        ///
        /// 用 FileInfo 而不是再开一次文件流：这里只需要元数据，一次属性查询与一次文件打开的成本
        /// 差了一个数量级，而它要在每个文件上执行两次。
        /// </summary>
        private static bool TryReadFingerprint(string filePath, out long length, out DateTime lastWriteUtc)
        {
            length = -1;
            lastWriteUtc = DateTime.MinValue;

            try
            {
                var info = new FileInfo(filePath);
                if (!info.Exists) return false;

                length = info.Length;
                lastWriteUtc = info.LastWriteTimeUtc;
                return true;
            }
            catch
            {
                // 文件在两次探测之间被删掉、目录不可达、权限变化 —— 都归为「无法判定」。
                return false;
            }
        }

        /// <summary>
        /// 按这批文件的大小分布挑缓冲区：平均不到 1MB 时用小缓冲，否则用 1MB 的顺序扫描缓冲。
        /// </summary>
        private static int ChooseBufferSize(List<long> fileSizes, long totalSize)
        {
            if (fileSizes.Count == 0 || totalSize <= 0) return BufferSize;

            long average = totalSize / fileSizes.Count;
            return average < SmallFileThreshold ? SmallFileBufferSize : BufferSize;
        }

        private static int GetPercent(long processed, long total)
        {
            if (total <= 0) return 100;
            int percent = (int)(processed * 100 / total);
            if (percent < 0) return 0;
            return percent > 100 ? 100 : percent;
        }

        /// <summary>
        /// 计算一套产物的校验值并写成**一份**清单。
        ///
        /// 这是校验文件的正确形态：一个归档（尤其分卷）是多文件组成的产物，
        /// 校验清单必须把这些文件全部列在一份文件里，每行一条。
        /// 早先的实现对每个分卷各调一次，产出 7 个只含一行的清单 ——
        /// 那样谁也没法用它校验一整套包。
        /// </summary>
        /// <param name="producedFiles">本次产出的全部文件，按顺序列入清单。</param>
        /// <param name="checksumFilePath">要写出的清单路径。</param>
        /// <param name="algorithm">要用的算法；为空时退回默认算法。</param>
        /// <param name="progress">进度回调，可为 null。</param>
        /// <param name="cancellationToken">取消标记。</param>
        public HashBatchResult WriteChecksumManifest(IReadOnlyList<string> producedFiles, string checksumFilePath,
            HashAlgorithmInfo algorithm, Action<int> progress, CancellationToken cancellationToken)
        {
            if (producedFiles == null || producedFiles.Count == 0) return HashBatchResult.Empty();
            if (string.IsNullOrWhiteSpace(checksumFilePath)) return HashBatchResult.Empty();

            var paths = new List<string>(producedFiles.Count);
            foreach (string file in producedFiles)
            {
                if (!string.IsNullOrWhiteSpace(file)) paths.Add(file);
            }
            if (paths.Count == 0) return HashBatchResult.Empty();

            HashBatchResult batch = CalculateDetailed(paths.ToArray(), algorithm, progress, cancellationToken);

            // 只写一份，且把全部行一次写进去；单文件时与旧行为完全一致。
            //
            // 注意：即使 batch 不完整也照样写出已算出的那些行 —— 一份残缺但有内容的清单，
            // 比完全没有清单更有用（至少能校验其中几项）。但返回值会带上 FailedFiles，
            // 由调用方决定是提示用户还是把任务判为未完全成功，绝不能静默当成完整清单。
            if (batch.Lines != null && batch.Lines.Length > 0)
            {
                ChecksumWriter.Write(checksumFilePath, batch.Lines);
                return batch;
            }

            // 一行都没算出来：本次产物是存在的（调用方传了 producedFiles），
            // 但一个文件都读不出来。此时**绝不能把旧清单留在原地** ——
            // 上一轮为同名归档生成的 .sha256 还躺在那儿，用户拿它去校验本次的包，
            // 结果必然对不上，而界面上看起来「校验文件是有的」。
            // 删掉才是诚实的：没有清单，比有一份冒充本次产物的旧清单要好。
            batch.StaleManifest = DeleteStaleManifest(checksumFilePath);
            return batch;
        }

        /// <summary>
        /// 清掉上一轮遗留的校验清单，并如实回报结果。
        /// 删不掉不是致命错误，但必须让调用方知道磁盘上还留着一份与本次产物无关的清单。
        ///
        /// 这里**不用 <see cref="File.Exists"/> 判断「是否存在」**。它只回答「是否存在一个可读的文件」，
        /// 而不是「这个路径上有没有东西」：路径被同名目录占着、路径过长、盘符不可达、
        /// 权限不足时它一律返回 false。照它的答案报 NotPresent，就等于在可能仍有旧清单的情况下
        /// 宣称磁盘是干净的 —— 而这正是本方法存在的理由（不让旧清单冒充本次产物）。
        /// 改用 <see cref="File.GetAttributes(string)"/>：它成功即证明「确实存在」，
        /// 抛 FileNotFoundException / DirectoryNotFoundException 才是「确实不存在」，
        /// 其余异常都是「无法确认」，一律按 RemovalFailed 上报。
        /// </summary>
        private static StaleManifestOutcome DeleteStaleManifest(string checksumFilePath)
        {
            FileAttributes attributes;

            // 第一步：确认这个路径上到底有没有东西，把「不存在」与「无法确认」分开。
            try
            {
                attributes = File.GetAttributes(checksumFilePath);
            }
            catch (FileNotFoundException)
            {
                return StaleManifestOutcome.NotPresent;
            }
            catch (DirectoryNotFoundException)
            {
                // 连父目录都不存在，这个路径上不可能有文件。
                return StaleManifestOutcome.NotPresent;
            }
            catch (Exception ex)
            {
                // 路径无效、盘符不可达、权限不足……都属「无法确认」。
                // 不能当成「不存在」：真有一份旧清单在那里的话，用户会拿它去校验新产物。
                ReportStaleManifestUncertain(checksumFilePath, ex);
                return StaleManifestOutcome.RemovalFailed;
            }

            // 第二步：把占用这个路径的东西删掉。
            //
            // 顺序很重要：**先判断是不是目录，再处理只读属性**。
            // 反过来的话，一个带只读属性的同名目录会被先摘掉只读位、再被判定为「目录不动」——
            // 目录虽然保住了，属性却被我们改了。那不是我们的东西，一个字节都不该动。
            try
            {
                if ((attributes & FileAttributes.Directory) == FileAttributes.Directory)
                {
                    // 本该是校验清单的位置上是一个目录。不替用户删目录、也不替用户改属性 ——
                    // 里面的内容与本程序无关，删了就是数据丢失，改属性同样是越界。
                    ReportStaleManifestUncertain(checksumFilePath,
                        new IOException("同名路径是一个目录，未自动删除"));
                    return StaleManifestOutcome.RemovalFailed;
                }

                if ((attributes & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
                {
                    // 到这里已确认是文件，摘掉只读位是删除的前置动作，属于分内之事。
                    // 改不动属性就照样尝试删除，真正的失败会在下面暴露。
                    try
                    {
                        File.SetAttributes(checksumFilePath, FileAttributes.Normal);
                    }
                    catch
                    {
                        // 摘不掉只读位不单独上报：下面的删除会给出真正的失败原因，
                        // 此处再报一次只会让同一个问题出现两条日志。
                    }
                }

                File.Delete(checksumFilePath);
            }
            catch (Exception ex)
            {
                ReportStaleManifestUncertain(checksumFilePath, ex);
                return StaleManifestOutcome.RemovalFailed;
            }

            // 第三步：删除后再确认一次。仍然拿不到「不存在」的明确答案，就不能报 Removed。
            try
            {
                File.GetAttributes(checksumFilePath);
                // 还能读到属性 —— 文件还在，删除没有生效。
                return StaleManifestOutcome.RemovalFailed;
            }
            catch (FileNotFoundException)
            {
                return StaleManifestOutcome.Removed;
            }
            catch (DirectoryNotFoundException)
            {
                return StaleManifestOutcome.Removed;
            }
            catch (Exception ex)
            {
                ReportStaleManifestUncertain(checksumFilePath, ex);
                return StaleManifestOutcome.RemovalFailed;
            }
        }

        /// <summary>
        /// 记录一次「无法确认旧清单是否已被清除」的事件。此时磁盘上可能仍存在一份
        /// 与本次产物无关的清单，必须留痕，否则现场无从回溯。
        /// </summary>
        private static void ReportStaleManifestUncertain(string checksumFilePath, Exception ex)
        {
            try
            {
                System.Diagnostics.Trace.TraceWarning(
                    "无法确认上一轮的校验清单是否已清除，它可能与本次产物无关，请手动检查："
                    + checksumFilePath + " —— " + ex.GetType().Name + ": " + ex.Message);
            }
            catch
            {
                // 记录日志本身失败不应影响调用方拿到结果。
            }
        }

        public void Dispose()
        {
            // 本类不持有需要释放的非托管资源，保留 Dispose 以支持 using 语法。
            GC.SuppressFinalize(this);
        }
    }
}
