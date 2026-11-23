using System;
using System.IO;
using System.Linq;

namespace Auto7z_WPF.Services
{
    /// <summary>
    /// 便携模式下的数据目录搬迁：把整棵数据目录从一个根移到另一个根。
    ///
    /// 为什么单独成类（从 Auto7zViewModel 提取）：
    /// 这块逻辑原本是 ViewModel 里 182 行的「便携数据目录搬迁」region，六个方法**全是 static**、
    /// 不碰任何实例状态、只依赖 AppPaths 与文件系统。它属于「文件系统搬迁」这个独立职责，
    /// 与「界面状态机」没有内聚关系 —— 放在 ViewModel 里只是因为它长在那里。
    ///
    /// 提取后 ViewModel 少 182 行，而这块逻辑可以单独推理与测试（它的核心风险是
    /// 「删到哪里」而不是「界面怎么显示」，两者的关注点完全不同）。
    ///
    /// **注意**：这里只做搬迁，不决定「该不该搬」。模式切换的判定留在 ViewModel，
    /// 因为那涉及 _isPortable 字段与日志/外壳注册等副作用。
    /// </summary>
    public static class DataDirectoryMover
    {
        /// <summary>普通模式 → 便携模式：把数据目录搬到程序目录旁的 .config 下。</summary>
        /// <returns>搬移是否成功。</returns>
        public static bool MoveToPortable()
        {
            return MoveDataDirectory(
                Path.Combine(AppPaths.GetLocalRootDirectory(), AppPaths.DataFolderName),
                Path.Combine(AppPaths.GetPortableRootDirectory(), AppPaths.DataFolderName));
        }

        /// <summary>便携模式 → 普通模式：把数据目录搬回 %LOCALAPPDATA%，并清掉空的 .config。</summary>
        /// <returns>搬移是否成功。</returns>
        public static bool MoveToLocal()
        {
            string portableRoot = AppPaths.GetPortableRootDirectory();
            bool moved = MoveDataDirectory(
                Path.Combine(portableRoot, AppPaths.DataFolderName),
                Path.Combine(AppPaths.GetLocalRootDirectory(), AppPaths.DataFolderName));

            // 便携判定看的是 .config 目录本身（见 AppPaths.DetectPortableFromDisk），
            // 所以取消便携必须真的把它删掉，否则下次启动又会被判成便携、把数据搬回来。
            if (moved) RemoveEmptyDirectory(portableRoot);

            return moved;
        }

        /// <summary>
        /// 删掉一个空目录。
        ///
        /// 便携判定看的是 .config 目录本身，所以「取消便携」必须真的把这个目录删掉。
        /// 目录里还有别的东西时不动它 —— 那些不是本程序的，删掉就是越权。
        /// 这种情况下取消便携不彻底，日志里会留一条警告。
        /// </summary>
        public static void RemoveEmptyDirectory(string path)
        {
            try
            {
                if (!Directory.Exists(path)) return;
                if (Directory.EnumerateFileSystemEntries(path).Any()) return;

                Directory.Delete(path);
            }
            catch
            {
                // 删不掉就留着，一个空目录不影响后续流程。
            }
        }

        /// <summary>
        /// 判断某个目录是否位于「本程序的数据目录」之内，只有在其内才允许递归删除。
        ///
        /// 这是不可逆操作的唯一闸门。允许的范围严格限定为：
        /// <c>{便携根}/Auto7z</c> 与 <c>{本地根}/Auto7z</c> 及其子路径。
        /// </summary>
        /// <param name="directoryPath">待校验的目录。</param>
        /// <returns>允许递归删除返回 true。</returns>
        public static bool IsSafeToDeleteRecursively(string directoryPath)
        {
            if (string.IsNullOrWhiteSpace(directoryPath)) return false;

            try
            {
                string full = Path.GetFullPath(directoryPath);
                string[] roots =
                {
                    Path.GetFullPath(AppPaths.GetPortableRootDirectory()),
                    Path.GetFullPath(AppPaths.GetLocalRootDirectory())
                };

                foreach (string root in roots)
                {
                    string dataDirectory = Path.GetFullPath(Path.Combine(root, AppPaths.DataFolderName));

                    if (string.Equals(full, dataDirectory, StringComparison.OrdinalIgnoreCase))
                    {
                        // **数据根目录自身也要查重解析点。**
                        //
                        // 早先这里直接返回「名字对不对」，跳过了链接检查 ——
                        // 于是一个指向 D:\ImportantData 的 Junction 只要叫 Auto7z、
                        // 位置在 .config 下，就能通过全部校验，然后被递归删除。
                        // 上一轮加的重解析点防护只在「子目录分支」里，根目录这一支漏了。
                        //
                        // 这不是补一个 if 就完事：还要检查**每一个现存祖先目录**，
                        // 因为 .config 本身也可能是链接。见下面 HasReparsePointAncestor。
                        if (!string.Equals(Path.GetFileName(full), AppPaths.DataFolderName,
                                StringComparison.Ordinal))
                        {
                            return false;
                        }

                        return !HasReparsePointAncestor(full);
                    }

                    if (full.StartsWith(dataDirectory + Path.DirectorySeparatorChar,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        // 字符串上在数据目录内 —— 但还要确认**真的**在。
                        //
                        // 如果这条路径的最后一段（或中间某段）是重解析点，
                        // 它实际指向的可能是另一个磁盘。对这种路径做递归删除，
                        // 删掉的是外部数据。第二道防线：这里再挡一次。
                        return !HasReparsePointAncestor(full);
                    }
                }

                return false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 把 <paramref name="sourceDirectory"/> 的内容整体搬到 <paramref name="targetDirectory"/>，
        /// 成功后再删掉源目录。
        ///
        /// **职责划分（这是本方法存在的核心理由）**：
        /// 「搬运」与「删除」必须分开，而且删除只发生在最外层一次。
        ///
        /// 早先的写法是：递归的合并方法每进一个子目录就先搬文件、**再自己把那个子目录删掉**，
        /// 然后外层又对**已经被清空的父目录**做一次安全校验加一次整体删除。
        /// 后果有两个，都是真缺陷：
        ///   ① 安全守卫守的是一个空路径 —— 它从未保护过任何一次真实删除，
        ///      真正危险的那些删除（递归里的）完全绕过了它；
        ///   ② 中途失败时，已删掉一半的子目录救不回来，源目录却还在，
        ///      留下「两边都不完整」的中间态。
        ///
        /// 现在：递归只搬不删；全部搬完后，对源根做一次安全校验，通过才整体删除。
        /// </summary>
        /// <param name="sourceDirectory">源目录。</param>
        /// <param name="targetDirectory">目标目录。</param>
        /// <returns>搬迁并清理是否成功。</returns>
        public static bool MoveDataDirectory(string sourceDirectory, string targetDirectory)
        {
            try
            {
                // **不能用 `Directory.Exists` 的布尔值同时表达「不存在」与「无法访问」。**
                //
                // `Directory.Exists` 在权限不足、路径非法、磁盘不可用等情况下
                // 一律返回 false。早先直接
                //     if (!Directory.Exists(source)) { CreateDirectory(target); return true; }
                // 于是「源目录存在但当前进程读不了」会被当成「源目录不存在」——
                // 目标目录被建出来、原有配置没迁、调用方收到**成功**，
                // 程序随即切到新数据目录，用户看到的是一套默认配置。
                // 这是典型的「错误的成功」，比报错难查得多。
                //
                // 现在用三态探测区分。
                DirectoryProbe sourceState = ProbeDirectory(sourceDirectory);

                // **源与目标都必须先过前置校验，一个都不能少**（BUG-02）。
                //
                // 早先只校验了源目录，目标目录直接 `Directory.CreateDirectory` 就用。
                // 后果：程序目录旁的 `.config` 若是指向 D:\ExternalData 的 Junction，
                // 「普通模式 → 便携模式」就会一路把配置、日志、组件写进那个外部目录，
                // 用户完全没有这个预期；目标里若已有同名文件，File.Replace 还会替换它。
                //
                // 现在两侧对称校验。目标校验必须在 CreateDirectory **之前** ——
                // 创建动作本身就会落到链接指向的位置，事后再查已经晚了。
                if (!ValidateMigrationPaths(sourceDirectory, targetDirectory, sourceState))
                    return false;

                if (sourceState == DirectoryProbe.Missing)
                {
                    // 源不存在：只需要把目标建出来（此时已确认目标路径安全）。
                    Directory.CreateDirectory(targetDirectory);
                    return true;
                }

                if (sourceState == DirectoryProbe.Unreadable)
                {
                    // 存在但读不了：必须报失败，绝不能假装成功。
                    return false;
                }

                if (sourceState == DirectoryProbe.Unreachable)
                {
                    // 路径非法 / 磁盘不可用：同样报失败。
                    return false;
                }

                // **从这里开始进入写入隔离**（BUG-01 / BUG-03）。
                //
                // 下面每一步都在动被日志、配置、任务状态同时写着的那些文件。
                // 闸门一关，三个写入方都放弃本次写入；闸门打开后由调用方补写一次。
                // 用 using 而不是手动 try/finally：中途任何异常都必须开闸，
                // 否则此后所有日志与配置写入会永久静默 —— 而程序看起来「一切正常」，
                // 只是什么都不记录了，这类故障极难排查。
                //
                // **闸门可以重入**：调用方（SetPortableMode）为了把「模式切换」这一步
                // 也纳入同一个事务，会先自己持一次闸再调用本方法；本方法这一层只加深度，
                // 真正加解锁发生在最外层。见 MigrationGate 的注释。
                using (MigrationGate.SuspendWrites())
                {
                    // **先安全创建再锁定目标目录**（BUG-05）。
                    //
                    // 早先是 `TryAcquire(target)`：目标不存在时它返回 null 且不报错，
                    // 调用方理解为「没什么可锁的」继续往下走。而首次切便携时
                    // 目标目录**通常还不存在** —— 于是最常见的那条路径上，
                    // 整个搬迁过程**从来没有锁**，T3 那个「别人在这里建联接」的窗口
                    // 完全敞开。
                    //
                    // AcquireOrCreate 把创建与锁定并成一步，并按句柄复核身份：
                    // 若建出来（或本来就存在）的是个联接，直接拒绝，绝不沿着它写。
                    //
                    // 拿不到锁就**放弃搬迁**：说明目录正被别的程序以不兼容的方式打开，
                    // 或路径上是个链接，此时路径的稳定性无从保证，
                    // 继续搬正是 TOCTOU 想利用的状态。
                    //
                    // **源目录不在这里锁定 —— 这是修出来的第二版。**
                    // 第一版把源目录也锁了，看似更严谨，实际是个自锁：
                    // 句柄带着 DELETE 访问权且不共享，而流程最后一步恰好是
                    // 对源目录做整体删除 —— 我们把自己的路堵死了，搬迁永远返回失败。
                    // 便携模式回归测试立刻暴露了它（连普通模式都切不过去）。
                    using (MigrationDirectoryLock dirLock =
                        MigrationDirectoryLock.AcquireOrCreate(targetDirectory, out string targetLockError))
                    {
                        if (targetLockError != null) return false;

                        // 源目录侧的保护由「它自己的上一级」提供 —— 见下面这段的说明。
                        // 目标目录已由上一步建好并锁定，这里不必再建。
                        CopyThenDeleteSource(sourceDirectory, targetDirectory);

                        // **删源之前复核目标目录还是取得锁时那一个**（BUG-05）。
                        //
                        // 复制可能持续数秒。目录若在这期间被删掉重建（路径字符串一样、
                        // 对象已经换了），我们写进去的东西就落在了另一处。
                        // 卷序列号 + 文件索引是 NTFS 给对象的唯一标识，变了就说明不是同一个。
                        //
                        // 此时代码还没删源文件，所以复核失败是**安全的失败**：
                        // 源数据完好，只是这次没搬成。目标目录已不可信，
                        // 不在它上面继续任何动作（尤其不能删源）。
                        if (!dirLock.VerifyUnchanged(out string identityError)) return false;

                        // 递归只搬不删，走到这里源目录应当已经是空的；
                        // 仍有残留说明有文件没搬走（被占用等），此时不删，交由调用方报失败。
                        if (Directory.EnumerateFileSystemEntries(sourceDirectory).Any()) return false;

                        // **删除之前才复查源目录，并且用「不递归」删除。**
                        //
                        // 第一版在这里加了一把源目录的锁 —— 那是个自锁：
                        // 句柄带着 DELETE 访问权且不共享，而紧接着的动作就是
                        // `Directory.Delete(sourceDirectory)`，等于把自己的路堵死，
                        // 搬迁永远返回失败。便携模式回归测试立刻把它暴露了出来
                        // （连普通模式都切不过去，因为搬迁从未成功返回）。
                        //
                        // 第二版改成：不在删除前后持有该目录的句柄，而是
                        //   1. 删除前最后一刻复查它不是重解析点；
                        //   2. **递归参数传 false**。
                        //
                        // 第 2 条是真正的安全落点：`recursive: true` 会在
                        // 「判断为空」之后、真正删除之前又新冒出一个子目录时
                        // 把那个子目录也一起删掉；而 `false` 只删空目录，
                        // 出现意外内容时直接抛 IOException，由外层捕获报失败。
                        // 加上前置的空检查，「空判断→删除」之间能做的破坏被压到最小。
                        //
                        // 这里仍然存在一个无法用路径 API 消除的窗口（TOCTOU 的本质），
                        // 如实记在这里而不是假装它不存在：
                        // 若目录在复查之后、删除之前被换成 Junction，
                        // `Directory.Delete(path, false)` 作用在联接上时删除的是**联接本身**，
                        // 不会递归进入它指向的目标 —— .NET 对重解析点有这个保护，
                        // 这也是选择「不递归」而不是「递归」的另一个理由。
                        if (ExistsAsReparsePoint(sourceDirectory)) return false;

                        Directory.Delete(sourceDirectory, false);
                    }
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 搬迁开始前的**双侧**前置校验：源与目标都必须落在预期位置、都不含重解析点、
        /// 且两者不能是同一处、不能互相包含。
        ///
        /// 这是 BUG-02 的修复落点。校验失败一律返回 false，让调用方报「切换失败」——
        /// 宁可让用户看到一次失败，也不能在用户不知情时把数据写到别处。
        ///
        /// 检查项（顺序按「越便宜越先」排）：
        ///   1. 双侧路径都必须是合法绝对路径
        ///   2. 目标不能在源内部、源也不能在目标内部（互相包含会边搬边改自己）
        ///   3. 源必须是「可递归删除」的那个数据目录（原有守卫，保留）
        ///   4. 目标必须落在程序认可的根之下（防止配置改写把目标指到任意路径）
        ///   5. 双侧的每一个现存祖先都不能是重解析点
        ///
        /// 第 4 项单独存在的理由：`MoveDataDirectory` 是 public 方法，
        /// 将来若有人绕过 `MoveToPortable` / `MoveToLocal` 直接调用它，
        /// 目标就完全由调用方决定。守卫必须在这里，不能只靠上游的约定。
        /// </summary>
        /// <param name="sourceDirectory">源目录。</param>
        /// <param name="targetDirectory">目标目录。</param>
        /// <param name="sourceState">已探测出的源状态（Missing 时跳过源侧的删除守卫）。</param>
        /// <returns>允许开始搬迁返回 true。</returns>
        private static bool ValidateMigrationPaths(string sourceDirectory, string targetDirectory,
            DirectoryProbe sourceState)
        {
            string fullSource;
            string fullTarget;
            try
            {
                fullSource = Path.GetFullPath(sourceDirectory);
                fullTarget = Path.GetFullPath(targetDirectory);
            }
            catch
            {
                // 路径非法（含通配符、超长、盘符不存在）：不能开始。
                return false;
            }

            // --- 2. 不能互相包含 ---
            // 相同也在这里被挡下（IsPathInside 语义为「不含相等」，故需显式判等）。
            if (string.Equals(TrimTrailingSeparators(fullSource), TrimTrailingSeparators(fullTarget),
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (IsPathInside(fullTarget, fullSource) || IsPathInside(fullSource, fullTarget))
            {
                // 目标在源内部：边搬边把自己刚写出来的文件再搬一遍，永不收敛。
                // 源在目标内部：搬完之后对源做整体删除，会把刚建好的目标一起删掉。
                return false;
            }

            // --- 3. 源侧：原有守卫（数据目录白名单 + 重解析点） ---
            // 源不存在时不构成威胁（不会有删除），跳过。
            if (sourceState != DirectoryProbe.Missing && !IsSafeToDeleteRecursively(fullSource))
            {
                return false;
            }

            // --- 4. 目标侧：必须落在程序认可的根之下 ---
            if (!IsUnderKnownRoot(fullTarget))
            {
                return false;
            }

            // --- 5. 双侧逐级重解析点检查 ---
            // 源已经由 IsSafeToDeleteRecursively 查过，这里只需补目标侧；
            // 但源侧在 Missing 分支上没有被查过（那时它还不存在），
            // 而「建出来的源路径上方有链接」同样会出事，所以两侧都显式再查一次。
            // 代价是几十次属性查询，搬迁本来就慢，这点开销可以忽略。
            if (HasReparsePointAncestor(fullSource)) return false;
            if (HasReparsePointAncestor(fullTarget)) return false;

            return true;
        }

        /// <summary>
        /// 目标是否落在程序认可的根目录之下：{便携根} 或 {本地根}。
        ///
        /// 判据与 <see cref="IsSafeToDeleteRecursively"/> 同源，但**语义不同**：
        /// 那个是「允许递归删除哪里」，这个是「允许往哪里写」。
        /// 两者眼下的范围一致，但将来若要放宽写入（比如允许用户指定数据目录）
        /// 而收紧删除，就该是两套判据 —— 现在就分成两个方法，免得将来改一处忘一处。
        ///
        /// 允许目标等于某个根：`MoveToPortable` 的目标是 `{便携根}/Auto7z`，
        /// 而便携根本身可能就是程序目录下的 `.config`，其下再没有别的层级。
        /// </summary>
        /// <param name="fullTarget">已规范化的绝对路径。</param>
        /// <returns>落在认可范围内返回 true。</returns>
        private static bool IsUnderKnownRoot(string fullTarget)
        {
            string[] roots;
            try
            {
                roots = new[]
                {
                    Path.GetFullPath(AppPaths.GetPortableRootDirectory()),
                    Path.GetFullPath(AppPaths.GetLocalRootDirectory())
                };
            }
            catch
            {
                return false;
            }

            foreach (string root in roots)
            {
                if (string.IsNullOrEmpty(root)) continue;

                // 等于根本身也算（TrimTrailingSeparators 处理盘符根 "C:\"）。
                if (string.Equals(TrimTrailingSeparators(fullTarget), TrimTrailingSeparators(root),
                        StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                if (IsPathInside(fullTarget, root)) return true;
            }

            return false;
        }

        /// <summary>
        /// <paramref name="candidate"/> 是否位于 <paramref name="root"/> **内部**（不含相等）。
        ///
        /// 与搬迁类里早先各处手写的 `StartsWith(root + 分隔符)` 相比，
        /// 它多处理了「盘符根」这一种：`Path.GetFullPath("C:\\")` 返回的字符串
        /// **已经**以分隔符结尾，再拼一个就变成 `C:\\`，而 `C:\Auto7zOutput`
        /// 并不以 `C:\\` 开头 —— 判定为「不在源内」，自包含检查整个被绕过。
        /// 实测确认：源 C:\、候选 C:\Auto7zOutput、前缀 C:\\ → StartsWith=False。
        ///
        /// 调用方须先各自 `Path.GetFullPath`。
        /// </summary>
        /// <param name="candidate">待判断的路径。</param>
        /// <param name="root">容器路径。</param>
        /// <returns>candidate 在 root 内部时返回 true。</returns>
        private static bool IsPathInside(string candidate, string root)
        {
            if (string.IsNullOrEmpty(candidate) || string.IsNullOrEmpty(root)) return false;

            string c = TrimTrailingSeparators(candidate);
            string r = TrimTrailingSeparators(root);

            // 相等不算「在内部」。
            if (string.Equals(c, r, StringComparison.OrdinalIgnoreCase)) return false;

            // 把 r 的末尾分隔符整成恰好一个，再比前缀。
            string prefix = r.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                ? r
                : r + Path.DirectorySeparatorChar;

            return c.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 去掉路径末尾的分隔符，但**保留根目录自身的那个**。
        ///
        /// `C:\`         → `C:\`    （盘符根不能变成 `C:`，那是「当前目录在 C 盘」的意思）
        /// `C:\a\`       → `C:\a`
        /// `\\srv\share\` → `\\srv\share`
        /// </summary>
        /// <param name="path">已规范化的绝对路径。</param>
        /// <returns>去掉末尾多余分隔符的路径。</returns>
        private static string TrimTrailingSeparators(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;

            string trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (trimmed.Length == 0) return path;

            // `C:` 这种形态要补回分隔符，否则语义从「C 盘根」变成「C 盘当前目录」。
            if (trimmed.Length == 2 && trimmed[1] == ':') return trimmed + Path.DirectorySeparatorChar;

            return trimmed;
        }

        /// <summary>源目录探测结果。三态，不能用 bool 代替。</summary>
        private enum DirectoryProbe
        {
            /// <summary>确认存在且可访问。</summary>
            Present = 0,

            /// <summary>确认不存在。</summary>
            Missing = 1,

            /// <summary>存在但当前进程无法读取（权限不足等）。</summary>
            Unreadable = 2,

            /// <summary>路径非法、磁盘不可用、或其它无法判断的情形。</summary>
            Unreachable = 3,
        }

        /// <summary>
        /// 判断一个目录的三种状态。**不依赖 `Directory.Exists` 的布尔值。**
        ///
        /// 做法：用 `File.GetAttributes` 取属性。
        ///   - 成功              → 存在（再看是不是目录）
        ///   - FileNotFound      → 确认不存在
        ///   - DirectoryNotFound → 父目录不存在 → 确认不存在
        ///   - Unauthorized      → 存在但读不了（**不能当不存在**）
        ///   - 其它（IO、参数）  → 无法判断，按不可达处理（**不能当不存在**）
        /// </summary>
        /// <param name="path">待探测的目录路径。</param>
        /// <returns>三态结果。</returns>
        private static DirectoryProbe ProbeDirectory(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return DirectoryProbe.Unreachable;

            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(path);
            }
            catch (FileNotFoundException)
            {
                return DirectoryProbe.Missing;
            }
            catch (DirectoryNotFoundException)
            {
                return DirectoryProbe.Missing;
            }
            catch (UnauthorizedAccessException)
            {
                // 存在但读不了 —— 这正是 BUG-02 的核心场景。
                return DirectoryProbe.Unreadable;
            }
            catch
            {
                return DirectoryProbe.Unreachable;
            }

            // 取到属性但不是目录：调用方期待的是目录，按不可达处理。
            if ((attributes & FileAttributes.Directory) == 0) return DirectoryProbe.Unreachable;

            // 目录属性拿到了还不够 —— 能列目录才算可访问。
            try
            {
                using (var e = Directory.EnumerateFileSystemEntries(path).GetEnumerator())
                {
                    e.MoveNext();   // 只取第一项，不遍历全部
                }
            }
            catch (UnauthorizedAccessException)
            {
                return DirectoryProbe.Unreadable;
            }
            catch
            {
                return DirectoryProbe.Unreachable;
            }

            return DirectoryProbe.Present;
        }

        /// <summary>
        /// 搬迁一棵目录树：**先完整复制，再清理源**。
        ///
        /// ## 为什么必须分成两个阶段（P1-2）
        ///
        /// 早先这里是「每复制完一个文件就立刻删掉源文件」。看起来省空间，
        /// 实际把**单文件的原子性**错当成了**整棵目录的事务性**：
        ///
        ///     复制 a.txt → 删 a.txt → 复制 b.txt 时失败
        ///     ⇒ 源目录缺 a.txt，目标目录可能缺 b.txt，两边都不完整
        ///
        /// 更隐蔽的后果在便携模式：目标目录建出来时顺带创建了 `.config`，
        /// 而 `AppPaths.DetectPortableFromDisk()` 把「`.config` 是否存在」
        /// 当作便携的唯一判据。于是中途失败后：
        ///
        ///     · 当前进程仍认为自己处于普通模式
        ///     · 一部分数据已经在 `.config/Auto7z`，另一部分还在原处
        ///     · 用户重启 → 因为 `.config` 存在 → 进入便携模式 → 读到不完整的数据
        ///
        /// 现在改成两段：
        ///   1. `CopyTree` —— 只复制，源目录**一个字节都不动**
        ///   2. `DeleteSourceTree` —— 全部复制成功后，才逐文件比对长度并清理源
        ///
        /// 第 1 步失败时抛异常，第 2 步根本不会执行，源目录保持完整。
        /// 调用方（`MoveDataDirectory`）拿到的就是「没搬成，但数据都在」。
        ///
        /// `DeleteSourceTree` 内部仍然只删「确实位于源根之下、且长度与目标一致」的文件，
        /// 并且**单层**删除空目录（不是 `recursive: true`）：
        /// 递归删除会在「判断为空」之后、真正删除之前把新冒出来的内容一起删掉。
        ///
        /// **遇到重解析点（Junction / SymbolicLink / MountPoint）时不跟随**（2026-11-23 修复）：
        ///
        /// `Directory.GetDirectories` / `GetFiles` **会穿过 Junction 进入外部目录**。
        /// 实测：在 `src\sub\link` 建一个指向 `outside` 的 Junction 后，
        /// `Directory.GetFiles("src\sub\link")` 能读到 outside 里的文件。
        ///
        /// 如果照常递归，`IsSafeToDeleteRecursively` 那条「路径字符串在数据目录内」的
        /// 检查会通过（字符串确实在内部），但实际操作的是**外部磁盘上的真实文件** ——
        /// 可能把用户 `D:\ImportantData` 里的东西搬进数据目录，或者反过来。
        ///
        /// 现在的做法：目录项带 `FileAttributes.ReparsePoint` 就**整体跳过**
        /// （既不搬它本身，也不进它内部）。跳过的目录留在源目录里，
        /// 外层「是否已空」检查会因此返回 false —— 调用方能看到失败，而不是静默误搬。
        /// </summary>
        private static void CopyThenDeleteSource(string sourceDirectory, string targetDirectory)
        {
            // 第 1 步：**完整复制**。源目录一个字节都不动 —— 中途任何失败都不会
            // 留下「源已残缺、目标也残缺」的中间态（P1-2）。
            CopyTree(sourceDirectory, targetDirectory);

            // 第 2 步：复制全部成功之后，才清理源。
            // 这一步只在上面正常返回时执行；抛异常就直接跳过，源目录保持完整。
            DeleteSourceTree(sourceDirectory, sourceDirectory, targetDirectory);
        }

        /// <summary>
        /// 递归**只复制、不删除**：把 <paramref name="sourceDirectory"/> 的内容复制到
        /// <paramref name="targetDirectory"/>，源目录保持完整。
        ///
        /// 这个「不删」是 P1-2 的核心。早先这里是 `CopyThenDeleteSource`：
        /// 每复制完一个文件就立刻删掉源文件，于是中途任何一个文件失败
        /// （被占用、权限、磁盘满），**前面已经删掉的源文件就回不来了** ——
        /// 源目录残缺、目标目录也残缺，两边都不完整。
        ///
        /// 现在拆成两步：
        ///   1. `CopyTree` 只复制（本方法），源目录**一个字节都不动**
        ///   2. 全部复制成功、外层确认无误后，才由 `DeleteEmptiedSourceTree` 清理源
        ///
        /// 于是任何一步失败都退回到「源完整、目标是个可以再来一次的暂存副本」，
        /// 而不是「两边都缺一块」。
        ///
        /// **遇到重解析点（Junction / SymbolicLink / MountPoint）时不跟随**（2026-11-23 修复）：
        ///
        /// `Directory.GetDirectories` / `GetFiles` **会穿过 Junction 进入外部目录**。
        /// 实测：在 `src\sub\link` 建一个指向 `outside` 的 Junction 后，
        /// `Directory.GetFiles("src\sub\link")` 能读到 outside 里的文件。
        ///
        /// 如果照常递归，`IsSafeToDeleteRecursively` 那条「路径字符串在数据目录内」的
        /// 检查会通过（字符串确实在内部），但实际操作的是**外部磁盘上的真实文件** ——
        /// 可能把用户 `D:\ImportantData` 里的东西搬进数据目录，或者反过来。
        ///
        /// 现在的做法：目录项带 `FileAttributes.ReparsePoint` 就**整体跳过**
        /// （既不搬它本身，也不进它内部）。跳过的目录留在源目录里，
        /// 外层「是否已空」检查会因此返回 false —— 调用方能看到失败，而不是静默误搬。
        /// </summary>
        /// <param name="sourceDirectory">源目录（只读，不修改）。</param>
        /// <param name="targetDirectory">目标目录（已存在且已锁定）。</param>
        private static void CopyTree(string sourceDirectory, string targetDirectory)
        {
            Directory.CreateDirectory(targetDirectory);

            // **每次进入一层都复查这一层不是重解析点**（BUG-06）。
            //
            // 上一层的循环已经查过「这个子目录」了，但那是在**调用之前**。
            // 从那次检查到这里真正开始遍历，中间有窗口；TOCTOU 就住在里面。
            // 这一句把检查挪到「动手的那一刻」，窗口缩到最小。
            //
            // 注意仍然无法做到零窗口（路径式 API 的固有限制），
            // 叠加外层 MigrationDirectoryLock 的目录独占后，
            // 「把已检查过的目录换成链接」这条主要路径已被堵住。
            if (IsReparsePoint(sourceDirectory)) return;

            foreach (string file in Directory.GetFiles(sourceDirectory))
            {
                // 文件本身也可能是符号链接：搬它会把「链接」变成「内容」，
                // 或者把外部文件搬进来。一律跳过，留给调用方处置。
                if (IsReparsePoint(file)) continue;

                CopyOneFile(file, Path.Combine(targetDirectory, Path.GetFileName(file)));
            }

            foreach (string directory in Directory.GetDirectories(sourceDirectory))
            {
                // **Junction / SymbolicLink / MountPoint 一律不跟随、不搬运。**
                // 见方法注释里的实测说明：跟随会把外部目录的内容搬进来或搬走。
                //
                // 用 fail-closed 的 ExistsAsReparsePoint 而不是 fail-open 的 IsReparsePoint：
                // 这里最终要删除整棵子树，判断不了就必须当链接处理。
                if (ExistsAsReparsePoint(directory)) continue;

                CopyTree(directory, Path.Combine(targetDirectory, Path.GetFileName(directory)));
            }
        }

        /// <summary>
        /// 清理**已经全部复制成功**的源目录树：先删文件，再自底向上删空目录。
        ///
        /// 只在 `CopyTree` 完整走完、且外层确认目标可用之后才调用。
        ///
        /// **只删「确认与目标一致」的源文件。** 删每个文件之前比对一下它的长度与
        /// 目标副本；对不上就保留（说明复制之后源又被改过），让外层「是否已空」
        /// 检查报失败。这样即便 P1-3 那道句柄保护在某个文件系统上失效，
        /// 也只会留下一个「多出来但没丢数据」的源文件，而不是静默丢掉一次写入。
        ///
        /// 目录用 `Directory.Delete(path, false)` 单层删除：
        /// 递归删除会在「判断为空」之后、真正删除之前把新冒出来的内容一起删掉，
        /// 而这里每一层单独判断、单独删除，意外内容只会让它失败，不会让它被删。
        /// </summary>
        /// <param name="currentDirectory">当前要清理的目录。</param>
        /// <param name="sourceRoot">本次搬迁的源根（用于边界校验）。</param>
        /// <param name="targetRoot">对应的目标根（用于比对文件长度）。</param>
        private static void DeleteSourceTree(string currentDirectory, string sourceRoot, string targetRoot)
        {
            string fullRoot;
            string fullCurrent;
            try
            {
                fullRoot = Path.GetFullPath(sourceRoot);
                fullCurrent = Path.GetFullPath(currentDirectory);
            }
            catch
            {
                return;
            }

            // 边界校验：只清理确实位于本次搬迁源目录之下的那一层。
            bool underRoot = string.Equals(fullCurrent, fullRoot, StringComparison.OrdinalIgnoreCase)
                || IsPathInside(fullCurrent, fullRoot);
            if (!underRoot) return;

            if (!Directory.Exists(currentDirectory)) return;

            // 对应到目标侧的同一个子目录：源根 → 目标根，保持相对结构。
            string relative;
            try
            {
                relative = fullCurrent.Substring(fullRoot.Length)
                    .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch
            {
                return;
            }

            string targetCurrent = string.IsNullOrEmpty(relative)
                ? targetRoot
                : Path.Combine(targetRoot, relative);

            // 先删这一层的文件（已确认复制到目标），再递归清理子目录。
            string[] files;
            try
            {
                files = Directory.GetFiles(currentDirectory);
            }
            catch
            {
                return;
            }

            foreach (string file in files)
            {
                // 链接不处理：它的「内容」不归本次搬迁管，删它等于删一个指针。
                if (ExistsAsReparsePoint(file)) continue;

                string fileName = Path.GetFileName(file);
                string targetFile = Path.Combine(targetCurrent, fileName);

                if (!IsSafeToRemoveSourceFile(file, targetFile)) continue;

                try
                {
                    File.Delete(file);
                }
                catch (IOException)
                {
                    // 被占用：保留，外层「是否已空」检查会报失败。
                }
                catch (UnauthorizedAccessException)
                {
                    // 无权限：同上。
                }
            }

            string[] children;
            try
            {
                children = Directory.GetDirectories(currentDirectory);
            }
            catch
            {
                return;
            }

            foreach (string child in children)
            {
                // 链接不跟随、也不删：它可能指向外部目录，
                // 删一个空链接同样是越界操作，且它里面的内容不归本次搬迁管。
                if (ExistsAsReparsePoint(child)) continue;

                DeleteSourceTree(child, sourceRoot, targetRoot);
            }

            // **删除前再查一次不是链接**：上面的递归到此处的窗口里，这一层可能刚被换掉。
            if (ExistsAsReparsePoint(currentDirectory)) return;

            // 仍有内容（被占用、跳过链接、并发写入）：保留该目录，
            // 让外层「是否已空」检查报失败，而不是把内容一起删掉。
            try
            {
                if (Directory.EnumerateFileSystemEntries(currentDirectory).Any()) return;
            }
            catch
            {
                return;
            }

            // **源根目录本身不由这里删除。**
            //
            // 调用方（`MoveDataDirectory`）在拿回控制权后还要做三件事：
            //   ① 复核目标目录身份（VerifyUnchanged）
            //   ② 检查源目录已经为空
            //   ③ 复查源目录不是重解析点，然后 `Directory.Delete(sourceDirectory, false)`
            //
            // 若这里就把根删掉，第 ② 步会看到「目录不存在」，紧接着的
            // `Directory.EnumerateFileSystemEntries` 直接抛 DirectoryNotFoundException，
            // 被外层 catch 吞成 `return false` —— 搬迁明明成功了却报失败，
            // `.config` 留在原处，下次启动又被判成便携。
            //
            // 实测踩到：便携模式探针报「切回普通模式后便携目录仍在磁盘上」，
            // 而目标侧文件已经完整复制过去了。根因就是这里多删了一层。
            if (string.Equals(fullCurrent, fullRoot, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            try
            {
                Directory.Delete(currentDirectory, false);
            }
            catch (IOException)
            {
                // 还有东西占着：保留该目录，由外层报失败。
            }
            catch (UnauthorizedAccessException)
            {
                // 无权限：同上。
            }
        }

        /// <summary>
        /// 判断一个源文件是否可以安全删除：目标副本必须存在、且长度与源一致。
        ///
        /// 长度一致不能证明内容一致，但它能挡住「复制之后源又被改过」这一类
        /// —— 长度一改就不删，数据留在源里，不会凭空消失。
        /// 长度相同的静默改写无法用这种方式发现，那需要逐份哈希，
        /// 对「搬自己家的配置与日志」而言过重；这一层的目标是**不丢数据**，
        /// 不是提供密码学完整性。这一点如实写在这里。
        /// </summary>
        /// <param name="sourceFile">源文件。</param>
        /// <param name="targetFile">目标副本。</param>
        /// <returns>确认可以删除源文件返回 true。</returns>
        private static bool IsSafeToRemoveSourceFile(string sourceFile, string targetFile)
        {
            try
            {
                if (!File.Exists(targetFile)) return false;

                var sourceInfo = new FileInfo(sourceFile);
                var targetInfo = new FileInfo(targetFile);

                return sourceInfo.Length == targetInfo.Length;
            }
            catch
            {
                // 读不到信息就无法确认，保留源文件 —— 宁可多留一个文件，
                // 也不能删掉一份可能没被复制过去的数据。
                return false;
            }
        }

        /// <summary>
        /// 只复制一个文件，**不删除源文件**。
        ///
        /// 这是搬迁的复制阶段唯一真正写目标的地方（`CopyTree` → `CopyOneFile`）。
        /// 源文件的清理属于提交阶段（`DeleteSourceTree`）—— 见 P1-2。
        ///
        /// **为什么源文件句柄要持有到提交完成（P1-3）**
        ///
        /// 上一版把共享模式改成 `FileShare.Read` 挡住了「复制期间被写」，
        /// 但句柄在复制结束的 `}` 处就关了，而真正的删源发生在后面的提交阶段 ——
        /// 中间这个窗口里，外部程序可以重新打开源文件写入：
        ///
        ///     复制完成 → 源句柄关闭 → 外部程序写入新内容
        ///              → 我们提交目标（不含新内容）→ 提交阶段删掉源文件
        ///
        /// 新写入的内容既不在目标里、也没留在源里，**直接消失**。
        /// 只改共享模式挡不住这一段，因为窗口在句柄之外。
        ///
        /// 现在两处一起改：
        ///   · 共享模式含 `FileShare.Delete`（**仍不含 Write**）——
        ///     写入者要打开必须拿到 FILE_SHARE_WRITE，我们不给；
        ///   · 复制与原子替换都在 using 内部完成，句柄到提交后才释放。
        ///
        /// 提交阶段删源时还会再比对一次源与目标的长度（见
        /// <see cref="IsSafeToRemoveSourceFile"/>），对不上就不删。
        /// 两道加起来的目标是「不丢数据」，不是「内容的密码学一致」。
        /// </summary>
        /// <param name="source">源文件路径。</param>
        /// <param name="target">目标文件路径。</param>
        private static void CopyOneFile(string source, string target)
        {
            string temp = BuildUniqueTempPath(target);

            try
            {
                // **源文件句柄持有到目标提交完成为止**（P1-3）。
                //
                // 注意 `File.Replace` / `File.Move` 必须在**源句柄的 using 内部**。
                // 写在外面（像第一版那样）等于：复制完 → 关源句柄 → 提交目标，
                // 中间那段窗口里外部程序可以重新打开源文件写入，
                // 而我们提交出去的是「写入之前」的内容 —— P1-3 正是这个形态。
                //
                // 检查器 A'3 就是按住这一点：提交位置必须早于 using 闭合位置。
                using (var src = new FileStream(source, FileMode.Open, FileAccess.Read,
                           FileShare.Read | FileShare.Delete))
                {
                    using (var dst = new FileStream(temp, FileMode.CreateNew, FileAccess.Write,
                               FileShare.None))
                    {
                        src.CopyTo(dst);
                        dst.Flush(true);

                        if (dst.Length != src.Length)
                        {
                            throw new IOException(
                                "复制长度不一致：" + source + "（源 " + src.Length
                                + " 字节，目标 " + dst.Length + " 字节）");
                        }
                    }

                    // 提交：仍在源句柄的写保护之下。
                    if (File.Exists(target))
                    {
                        File.Replace(temp, target, null);
                    }
                    else
                    {
                        File.Move(temp, target);
                    }
                }
            }
            catch
            {
                TryDeleteQuietly(temp);
                throw;
            }
        }

        /// <summary>
        /// 路径是否是一个**重解析点**（Junction / SymbolicLink / MountPoint 等）。
        ///
        /// 为什么必须挡：`Directory.GetFiles` / `GetDirectories` **会穿过重解析点**
        /// 进入它指向的真实目录。于是一条「路径字符串上看在数据目录内」的检查会通过，
        /// 而实际读写的是外部磁盘 —— 搬迁可能把 `D:\ImportantData` 的内容搬进来，
        /// 也可能把数据目录里的东西搬到外部去。
        ///
        /// 实测（2026-11-23）：在 `src\sub\link` 建 Junction 指向 `outside` 后，
        /// `Directory.GetFiles("src\sub\link")` 能读到 outside 里的文件。
        ///
        /// **取不到属性时返回 false（不是重解析点）**：
        /// 搬运遍历里遇到的路径必然存在（来自 `GetFiles` / `GetDirectories`），
        /// 取不到属性只会是「刚好在这一瞬间被删了」—— 那种情况后续的复制会失败并上报，
        /// 不需要在这里拦。反过来（返回 true）会把正常路径当成链接跳过。
        ///
        /// 需要「存在且是重解析点」语义的地方用 <see cref="ExistsAsReparsePoint"/>。
        /// </summary>
        /// <param name="path">待检查的路径（文件或目录）。</param>
        /// <returns>是重解析点返回 true。</returns>
        private static bool IsReparsePoint(string path)
        {
            try
            {
                FileAttributes attributes = File.GetAttributes(path);
                return (attributes & FileAttributes.ReparsePoint) != 0;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 路径存在、且是重解析点。
        ///
        /// 用于删除守卫：路径**不存在**时不构成安全威胁（删不存在的东西什么也不会发生），
        /// 因此要显式区分「不存在」与「存在但是链接」。
        /// 早先守卫里直接用了 <see cref="IsReparsePoint"/>，
        /// 而它当时「取不到属性即返回 true」，把「待创建的子目录」也判成了不安全 ——
        /// guardcheck 报「数据目录的子目录 / 深层子目录 实际 False、预期 True」。
        /// </summary>
        /// <param name="path">待检查的路径。</param>
        /// <returns>存在且是重解析点时返回 true。</returns>
        /// <summary>
        /// 路径是否「存在且是重解析点」，**无法确认时按不安全处理**。
        ///
        /// 三种情况的处置（这是 BUG-03 的修复）：
        ///   1. 路径确实不存在      → false（删不存在的东西什么都不会发生）
        ///   2. 存在且确认是链接    → true
        ///   3. **无法确认**（无权限读属性、IO 异常等） → true（拒绝）
        ///
        /// 早先是 `catch { return false; }` —— 把「无法确认是不是链接」
        /// 当成「确认不是链接」。这个函数参与的是**递归删除**的安全判断，
        /// fail-open 方向是错的：宁可拒绝一次合法删除，也不能放过一次误删。
        ///
        /// 注意与 <see cref="IsReparsePoint"/> 的区别：那个用在高频遍历里，
        /// 路径必然存在，取不到属性就是「刚好被删了」，返回 false 更合适 ——
        /// 用 fail-closed 会把正常路径大量误判成链接。
        /// 两个函数语义不同，**不要合并**。
        /// </summary>
        /// <param name="path">待检查的路径。</param>
        /// <returns>存在且是重解析点（或无法确认）时返回 true。</returns>
        private static bool ExistsAsReparsePoint(string path)
        {
            try
            {
                FileAttributes attributes = File.GetAttributes(path);
                return (attributes & FileAttributes.ReparsePoint) != 0;
            }
            catch (FileNotFoundException)
            {
                // 确认不存在：不是链接，删除它什么也不会发生。
                return false;
            }
            catch (DirectoryNotFoundException)
            {
                // 父目录都不存在：同上。
                return false;
            }
            catch
            {
                // UnauthorizedAccessException / IOException / 其它：
                // **无法确认**，按不安全处理。宁可拒绝。
                return true;
            }
        }

        /// <summary>
        /// 从 <paramref name="path"/> 向上遍历**每一个现存祖先**，其中是否有重解析点。
        ///
        /// 为什么不能只查路径本身（BUG-01 的修复）：
        /// 目录结构可能是
        ///     程序目录\.config  ->  Junction 指向别处
        ///         \Auto7z
        /// 此时 `Auto7z` 自身**不是**链接，但它的父目录 `.config` 是 ——
        /// 于是 `Auto7z` 的实际位置在另一个磁盘上。只查最后一段会漏掉这种情况。
        ///
        /// **不存在的那几层要跳过，不能停。**（2026-11-23 修复）
        ///
        /// 早先这里写的是「遇到第一个不存在的层级就 return false」，
        /// 理由「再往上都是本来就不存在的路径段，删除不会碰到它们」——
        /// 对**删除**成立，对**写入**不成立：
        ///
        ///     D:\holder  ← Junction 指向 E:\real
        ///        \Auto7z
        ///           \Auto7z.json   ← 尚不存在，正要写入
        ///
        /// 从 `Auto7z.json` 往上第一层就不存在 → 立刻 return false，
        /// 根本没走到 `holder`。而新建文件的数据落在哪，恰恰由最近的现存祖先决定。
        /// 实测确认：修复前该路径返回 False（放行），修复后返回 True（拒绝）。
        ///
        /// 任何一层「无法确认」都返回 true（拒绝删除与写入）。
        /// </summary>
        /// <param name="path">完整的绝对路径。</param>
        /// <returns>某个现存祖先（含自身）是重解析点，或无法确认时返回 true。</returns>
        private static bool HasReparsePointAncestor(string path)
        {
            string current;
            try
            {
                current = Path.GetFullPath(path);
            }
            catch
            {
                return true;
            }

            // 逐级向上，最多走到盘符根（Path.GetDirectoryName 返回 null 时停）。
            while (!string.IsNullOrEmpty(current))
            {
                // 每一层都问一次。ExistsAsReparsePoint 的三态语义已经覆盖
                // 「不存在→false」与「无法确认→true」，不需要再额外判存在性 ——
                // 早先那句额外的检查正是缺陷的来源。
                if (ExistsAsReparsePoint(current)) return true;

                string parent;
                try
                {
                    parent = Path.GetDirectoryName(current);
                }
                catch
                {
                    return true;
                }

                // 到根了（C:\ 的 GetDirectoryName 返回 null）。
                if (string.IsNullOrEmpty(parent) || string.Equals(parent, current,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                current = parent;
            }

            return false;
        }

        /// <summary>
        /// 为目标文件生成一个**唯一**的临时路径：目标目录下的 `.moving-<guid>.tmp`。
        ///
        /// 两个约束同时满足：
        ///   · **足够短且与原文件名无关**（BUG-06）—— 原文件名哪怕贴着 255 字符上限，
        ///     临时名也还是 44 个字符，不会因为拼接而超限；
        ///   · **与目标同目录** —— `File.Replace` 要求同卷才能原子，跨卷会退化成
        ///     「删除 + 复制」，那正是我们要避免的窗口。
        ///
        /// 名字里带 GUID 之后不可能与用户的文件撞名，配合 `FileMode.CreateNew`，
        /// 即便真的撞上也会抛异常而不是覆盖 —— fail-closed。
        /// </summary>
        /// <param name="target">目标文件路径。</param>
        /// <returns>唯一临时路径。</returns>
        private static string BuildUniqueTempPath(string target)
        {
            string directory = Path.GetDirectoryName(target);
            if (string.IsNullOrEmpty(directory)) directory = ".";

            return Path.Combine(directory, MovingTempPrefix + Guid.NewGuid().ToString("N") + MovingTempSuffix);
        }

        /// <summary>
        /// 搬迁临时文件的名字前缀。与 GUID、后缀组合后长度为固定值，
        /// **不含原文件名** —— 这是 BUG-06 的落点：原文件名合法不代表加后缀后仍合法。
        /// </summary>
        private const string MovingTempPrefix = ".moving-";

        /// <summary>搬迁临时文件的名字后缀。</summary>
        private const string MovingTempSuffix = ".tmp";

        /// <summary>删文件，失败不抛。用于清理临时文件与「已安全落地」的源文件。</summary>
        private static void TryDeleteQuietly(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (IOException)
            {
                // 被占用：留着即可。目标侧已有完整副本，不影响数据完整性。
            }
            catch (UnauthorizedAccessException)
            {
                // 无写权限：同上。目标侧已有完整副本，
                // 这个残留的源文件/临时文件不影响数据完整性，由外层报搬迁失败。
            }
        }
    }
}
