using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Auto7z_WPF.Services
{
    /// <summary>
    /// 路径与目录的容错工具，所有对外方法都以返回值表达失败，不向调用方抛异常。
    /// </summary>
    public static class FileSystemHelper
    {
        // 自增序号补足时间戳精度，保证同一 tick 内生成的名称也不重复。
        private static long _folderNameCounter;

        #region 原生目录遍历声明
        // 与 Win32 的 WIN32_FIND_DATAW 逐字段对应，字段顺序不能改；
        // 只用到属性、大小与文件名，其余字段保留占位以维持正确的偏移。
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WIN32_FIND_DATAW
        {
            public uint dwFileAttributes;
            public uint ftCreationTimeLow;
            public uint ftCreationTimeHigh;
            public uint ftLastAccessTimeLow;
            public uint ftLastAccessTimeHigh;
            public uint ftLastWriteTimeLow;
            public uint ftLastWriteTimeHigh;
            public uint nFileSizeHigh;
            public uint nFileSizeLow;
            public uint dwReserved0;
            public uint dwReserved1;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string cFileName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)]
            public string cAlternateFileName;
        }

        private const uint FILE_ATTRIBUTE_DIRECTORY = 0x00000010;
        private const uint FILE_ATTRIBUTE_REPARSE_POINT = 0x00000400;

        // FindNextFile 正常遍历到末尾时 GetLastError 就是这个值。
        // 任何别的值都说明枚举是被错误中断的（I/O 故障、目录在遍历途中被删），
        // 此时剩余条目根本没被读出来 —— 不能当成「遍历正常结束」。
        private const int ERROR_NO_MORE_FILES = 18;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr FindFirstFileW(string fileName, out WIN32_FIND_DATAW findFileData);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool FindNextFileW(IntPtr findHandle, out WIN32_FIND_DATAW findFileData);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool FindClose(IntPtr findHandle);
        #endregion

        #region 路径规范化与校验
        // 拖拽来源的路径常带引号和多余空白，先清洗再交给 Path 解析。
        public static string NormalizeDirectoryPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return string.Empty;

            string value = path.Trim().Trim('"').Trim();
            if (value.Length == 0) return string.Empty;

            try
            {
                // 解析成绝对路径，后续比较与拼接不受当前工作目录影响。
                return Path.GetFullPath(value);
            }
            catch
            {
                return value;
            }
        }

        // 输出路径可以是已存在的文件或目录；两者都不存在就判为无效，不替用户创建目录。
        public static bool TryValidateOutputPath(string outputPath, out string outDirectory)
        {
            outDirectory = null;

            // 未填输出路径视为合法，由调用方回落到默认目录。
            if (string.IsNullOrWhiteSpace(outputPath)) return true;

            string candidate = NormalizeDirectoryPath(outputPath);
            if (string.IsNullOrWhiteSpace(candidate)) return true;

            try
            {
                if (File.Exists(candidate))
                {
                    string dir = Path.GetDirectoryName(candidate);
                    if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
                    outDirectory = dir;
                    return true;
                }

                if (Directory.Exists(candidate))
                {
                    outDirectory = candidate;
                    return true;
                }

                return false;
            }
            catch
            {
                return false;
            }
        }

        public static bool IsExistingFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            try
            {
                return File.Exists(path);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 判断 <paramref name="path"/> 是否落在 <paramref name="directory"/> 之内（含该目录本身）。
        ///
        /// 这是写盘路径的边界校验：目前所有写盘目标都由程序自己的目录推导，
        /// 但一旦输出目录变成可配置（或配置键被手工改成 "../.."），
        /// 一次写盘就可能落到数据目录之外 —— 配置覆盖、日志删除都会变成任意路径写入。
        /// 这里先把这条边界的判据固定下来，调用方一律先过它再落笔。
        ///
        /// 三个必须注意的点：
        /// 1. 双方都要先 GetFullPath，否则 "C:\a\b\..\c" 与 "C:\a\c" 会被当成不同路径；
        /// 2. 前缀比较必须补一个分隔符，否则 "C:\data" 会把 "C:\database" 认作子目录；
        /// 3. 比较用 OrdinalIgnoreCase：Windows 路径不区分大小写。
        /// </summary>
        public static bool IsUnderDirectory(string path, string directory)
        {
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(directory)) return false;

            try
            {
                string fullPath = Path.GetFullPath(path);
                string fullRoot = Path.GetFullPath(directory);

                // 目录本身也算「在其内」：GetFullPath 会把尾部反斜杠去掉，
                // 不单独判等的话，补完分隔符的前缀永远比 "C:\a\b" 多一个字符而匹配不上。
                // 先用 TrimEnd 统一两侧形态，再补一个分隔符做前缀比较。
                string trimmedRoot = fullRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (string.Equals(fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                        trimmedRoot, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                // 前缀比较必须带分隔符，否则 "C:\data" 会把 "C:\database" 认作子目录。
                // 根路径（"C:\"）TrimEnd 之后只剩 "C:"，补回分隔符正好是 "C:\"。
                string prefix = trimmedRoot + Path.DirectorySeparatorChar;
                return fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                // 路径非法（含通配符、超长、盘符不存在）时按「不在其内」处理：
                // 边界校验只能往保守的方向失败，放行等于让它形同不存在。
                return false;
            }
        }

        #endregion
        #region 目录探测与写入探针
        // 用随机文件名做写入探针：固定名会覆盖并截断用户目录里的同名文件，随机名写完立即删除。
        public static bool IsDirectoryWritable(string directoryPath)
        {
            if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath))
                return false;

            string probePath = Path.Combine(directoryPath, "Auto7z_" + Path.GetRandomFileName() + ".tmp");
            try
            {
                using (var stream = new FileStream(probePath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    stream.WriteByte(0);
                }
                File.Delete(probePath);
                return true;
            }
            catch
            {
                return false;
            }
        }

        #endregion
        #region 目录体积统计
        // 目录按文件逐个累加；无法获取时返回 -1 而不是 0，调用方才能区分空目录与读取失败。
        public static long GetSizeInBytes(string path)
        {
            return GetSizeInBytesDetailed(path, out _);
        }

        /// <summary>
        /// 体积统计，并回报有哪些子树没能读进去。
        ///
        /// 为什么需要这个重载：早先读不到的子树是被静默 `continue` 掉的，调用方拿到的数字
        /// 看起来像个完整答案，其实少了若干子树。这个值本身只用于判断要不要分卷，
        /// 因此**读失败不该中断压缩**（这条策略不变，与哈希清单的缺口不是同一类问题）；
        /// 但「少读了一部分」这件事必须能被看见 —— 用户抱怨「明明应该分卷却没分」时，
        /// 日志里要有线索可查，而不是只能靠猜。
        /// </summary>
        /// <param name="path">文件或目录路径。</param>
        /// <param name="skippedDirectoryCount">因打不开或枚举被中断而整棵跳过的子树数量。</param>
        public static long GetSizeInBytesDetailed(string path, out int skippedDirectoryCount)
        {
            skippedDirectoryCount = 0;

            if (string.IsNullOrWhiteSpace(path)) return -1;
            try
            {
                var attributes = File.GetAttributes(path);

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    return GetDirectorySizeNative(path, out skippedDirectoryCount);
                }

                return new FileInfo(path).Length;
            }
            catch
            {
                return -1;
            }
        }

        // 用 FindFirstFile/FindNextFile 原生遍历。
        // DirectoryInfo.EnumerateFiles 每个条目都要构造一个 FileInfo 并单独取属性，
        // 本机实测 5000 文件目录约 4.9ms，原生遍历约 2.6ms —— 少的那一半全是托管对象的分配与元数据往返。
        // 目录体积只用于分卷判断，容错策略是读不到的子树跳过，不因此让整次统计失败。
        //
        // 但「跳过」与「读完」必须能区分：FindNextFile 返回 false 在正常结束（ERROR_NO_MORE_FILES）
        // 与枚举被错误中断时含义完全不同，早先两者都被当成结束，后半段条目凭空消失而调用方毫不知情。
        // 这里与 EnumerateFilesWithSizesDetailed 用同一套判据，把没能读全的子树记进计数。
        private static long GetDirectorySizeNative(string rootPath, out int skippedDirectoryCount)
        {
            long total = 0;
            int skipped = 0;

            // 显式栈而不是递归：目录层级很深时递归会吃掉线程栈，而这里跑在任务线程上，栈大小不可控。
            var pending = new Stack<string>();
            pending.Push(rootPath);

            while (pending.Count > 0)
            {
                string directory = pending.Pop();

                WIN32_FIND_DATAW data;
                IntPtr handle = FindFirstFileW(Path.Combine(directory, "*"), out data);
                // 打开失败（权限不足、目录已被删）就跳过这一棵子树，与托管实现的行为一致；
                // 但这棵子树确实没读，必须计数。
                if (handle == new IntPtr(-1))
                {
                    skipped++;
                    continue;
                }

                try
                {
                    // 不能写成 do/while：循环体里用 continue 处理 "."/".." 会跳过推进那一步，
                    // 枚举会卡在第一条原地空转。推进放在循环体末尾，continue 之前。
                    while (true)
                    {
                        string name = data.cFileName;
                        if (name != "." && name != "..")
                        {
                            if ((data.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0)
                            {
                                // 重解析点（符号链接/联接）不跟进去：跟进去可能成环，也可能跨到别的卷上。
                                // 这是有意的设计，不算漏读，不进计数。
                                if ((data.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) == 0)
                                {
                                    pending.Push(Path.Combine(directory, name));
                                }
                            }
                            else
                            {
                                total += ((long)data.nFileSizeHigh << 32) | data.nFileSizeLow;
                            }
                        }

                        if (FindNextFileW(handle, out data)) continue;

                        // 正常遍历结束是 ERROR_NO_MORE_FILES；其余错误码意味着这棵子树只读了一半。
                        if (Marshal.GetLastWin32Error() != ERROR_NO_MORE_FILES) skipped++;
                        break;
                    }
                }
                finally
                {
                    FindClose(handle);
                }
            }

            skippedDirectoryCount = skipped;
            return total;
        }
        #endregion

        #region 文件枚举（带漏读记账）
        /// <summary>
        /// 枚举目录下的全部文件与大小，并回报有哪些子目录没能读进去。
        ///
        /// 存在的理由：打不开的子目录（权限不足、枚举途中被删）早先是被静默 `continue` 掉的，
        /// 调用方无从区分「这个目录确实只有这些文件」与「有一部分子目录根本没读到」。
        /// 对哈希清单来说这是致命的 —— 漏掉的文件不会出现在清单里，而清单看起来是完整的；
        /// 对体积统计来说，算少了会让分卷判断偏保守或偏激进。
        ///
        /// 三条漏项路径都必须记进 skippedDirectories：目录打不开、枚举中途出错、根目录在调用前被删。
        /// 只覆盖第一条是不够的 —— 后两条同样会交出一份「看起来完整」的清单。
        /// </summary>
        public static void EnumerateFilesWithSizesDetailed(string rootPath, out List<string> files,
            out List<long> sizes, out List<string> skippedDirectories)
        {
            files = new List<string>();
            sizes = new List<long>();
            skippedDirectories = new List<string>();

            if (string.IsNullOrWhiteSpace(rootPath)) return;

            // 根目录在调用方做 Directory.Exists 检查之后、真正枚举之前被删掉，
            // 早先这里直接 return：调用方拿到的是「空列表 + 零个跳过目录」，
            // 于是把「什么都没读到」当成「这个目录确实没有文件」。必须记账。
            if (!Directory.Exists(rootPath))
            {
                skippedDirectories.Add(rootPath);
                return;
            }

            var pending = new Stack<string>();
            pending.Push(rootPath);

            while (pending.Count > 0)
            {
                string directory = pending.Pop();

                WIN32_FIND_DATAW data;
                IntPtr handle = FindFirstFileW(Path.Combine(directory, "*"), out data);
                if (handle == new IntPtr(-1))
                {
                    // 记下来而不是默默跳过：这是「清单可能不完整」的唯一线索。
                    skippedDirectories.Add(directory);
                    continue;
                }

                try
                {
                    while (true)
                    {
                        string name = data.cFileName;
                        // 注意：这里不能 continue —— 循环体末尾才是推进枚举的那一步，
                        // 提前 continue 会让 "." / ".." 卡在原地空转。
                        if (name != "." && name != "..")
                        {
                            string fullPath = Path.Combine(directory, name);

                            if ((data.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0)
                            {
                                // 重解析点（符号链接/联接）不跟进去：可能成环，也可能跨到别的卷上。
                                // 这是**有意的设计**，不算「漏读」，所以不进 skippedDirectories。
                                if ((data.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) == 0)
                                {
                                    pending.Push(fullPath);
                                }
                            }
                            else
                            {
                                files.Add(fullPath);
                                sizes.Add(((long)data.nFileSizeHigh << 32) | data.nFileSizeLow);
                            }
                        }

                        if (FindNextFileW(handle, out data)) continue;

                        // FindNextFile 返回 false 有两种完全不同的含义：
                        // 正常遍历结束（ERROR_NO_MORE_FILES），以及枚举被错误中断（I/O 故障、目录中途被删）。
                        // 早先两者被一概当成「正常结束」，后半段条目就这么静默消失了 ——
                        // 于是调用方拿到一份看起来完整的清单，而它其实缺了一截。
                        int nextError = Marshal.GetLastWin32Error();
                        if (nextError != ERROR_NO_MORE_FILES)
                        {
                            // 已读到的条目保留（有总比没有强），但本目录记为不完整，
                            // 让调用方知道这份结果不能当作全集。
                            skippedDirectories.Add(directory);
                        }
                        break;
                    }
                }
                finally
                {
                    FindClose(handle);
                }
            }
        }

        #endregion
        #region 目录清理
        // 清理临时目录失败不应中断压缩与解压主流程，残留文件留待下次清理。
        public static void DeleteDirectoryQuietly(string directoryPath)
        {
            if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath)) return;

            try
            {
                Directory.Delete(directoryPath, true);
            }
            catch
            {
                // 被占用或权限不足时保留，交由下次启动重试
            }
        }

        #endregion
        #region 名称生成
        // 时间戳加序号再哈希，得到定长且不含路径分隔符的名字，可直接用作目录名。
        public static string GenerateSafeFolderName()
        {
            string seed = DateTime.UtcNow.Ticks.ToString();

            long seq = Interlocked.Increment(ref _folderNameCounter);
            seed += "#" + seq.ToString(CultureInfo.InvariantCulture);

            byte[] bytes = Encoding.UTF8.GetBytes(seed);
            using (var sha256 = SHA256.Create())
            {
                string base64 = Convert.ToBase64String(sha256.ComputeHash(bytes));
                return base64.Replace('/', '_').Replace('+', '-').TrimEnd('=');
            }
        }

        // 判断一个目录名是否由 GenerateSafeFolderName 生成：
        // 产物是 32 字节 SHA256 的 base64（44 字符），替换 / → _、+ → -、去掉末尾的 =，
        // 因此定长 43、只含 [A-Za-z0-9_-]，且原始 base64 允许的字符集被这个替换映射约束。
        // 用这个名字做删除前的白名单校验：崩溃恢复只能删本程序自己建的随机目录，
        // 绝不能碰用户目录里恰好同名的东西（概率极小，但校验成本几乎为零）。
        public static bool IsGeneratedFolderName(string name)
        {
            if (string.IsNullOrEmpty(name) || name.Length != 43) return false;

            for (int i = 0; i < name.Length; i++)
            {
                char ch = name[i];
                bool valid = (ch >= 'A' && ch <= 'Z')
                    || (ch >= 'a' && ch <= 'z')
                    || (ch >= '0' && ch <= '9')
                    || ch == '_'
                    || ch == '-';
                if (!valid) return false;
            }

            return true;
        }

        // 重名时在文件名后追加递增序号，与资源管理器的命名习惯一致。
        public static string MakeUniqueFilePath(string directory, string fileName)
        {
            string candidate = Path.Combine(directory, fileName);
            if (!File.Exists(candidate)) return candidate;

            string name = Path.GetFileNameWithoutExtension(fileName);
            string extension = Path.GetExtension(fileName);
            int index = 2;
            while (File.Exists(candidate))
            {
                candidate = Path.Combine(directory, $"{name} ({index}){extension}");
                index++;
            }
            return candidate;
        }
        #endregion
    }
}
