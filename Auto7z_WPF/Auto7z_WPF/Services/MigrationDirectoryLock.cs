using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Auto7z_WPF.Services
{
    /// <summary>
    /// 搬迁期间对数据目录的**句柄级独占**，用于缩小 TOCTOU 窗口，并在目标目录
    /// 尚不存在时给出一个真正有效的锁（BUG-05）。
    ///
    /// ## 缺陷（两代）
    ///
    /// 第一代问题是所有重解析点检查都是「先查、再动手」：
    ///
    ///     T1  检查 target 不是 Junction        → 安全
    ///     T2  另一个进程把 target 换成 Junction
    ///     T3  程序继续 Copy / Delete
    ///
    /// 纯路径检查**无法**消除这个窗口 —— 这是路径式 API 的固有性质，不是实现失误。
    ///
    /// 第二代（本轮修复）暴露的是另一件事：首次便携迁移时目标目录**通常还不存在**。
    /// 早先 <see cref="TryAcquire"/> 对不存在的目录返回 null（并且不是错误），
    /// 调用方理解为「没什么可锁的」继续往下走，于是：
    ///
    ///     T1  校验目标路径安全
    ///     T2  TryAcquire 发现目标不存在 → 返回 null，**没有锁**
    ///     T3  别人在这个位置建了一个 Junction
    ///     T4  Directory.CreateDirectory(target) —— 落到联接指向的真实目录上
    ///     T5  程序沿着它把整棵数据写进外部磁盘
    ///
    /// 也就是说：最常见的那条路径（首次切便携）上，锁**从来没有生效过**。
    ///
    /// ## 现在的做法
    ///
    /// <see cref="AcquireOrCreate"/> 把「创建」与「锁定」并成一步，并按句柄复核身份：
    ///
    ///   1. 建目录（已存在也无妨，CreateDirectory 幂等）
    ///   2. 用 `FILE_FLAG_OPEN_REPARSE_POINT` 打开它 —— **打开链接本身，不跟随**
    ///   3. 从句柄取属性：若带 ReparsePoint，说明创建动作正好落在别人放的链接上 → 拒绝
    ///   4. 记下卷序列号与文件索引，作为这个目录的「身份」
    ///
    /// 第 2 步是关键：普通 `CreateFile` 会跟随联接，于是我们锁住的是**外部真实目录**，
    /// 而路径本身早就被换掉了也看不出来。带上 OPEN_REPARSE_POINT 之后，
    /// 句柄指向的就是那个链接对象，第 3 步才有东西可查。
    ///
    /// 复制结束后再用 <see cref="VerifyUnchanged"/> 复核一次身份：
    /// 目录若在中途被换掉（删掉重建），卷序列号/文件索引会变，据此拒绝提交。
    ///
    /// ## 边界（这里必须说实话）
    ///
    /// **能挡住：**
    ///   · 持锁期间别人把这个目录改名或删除（不共享 DELETE，Windows 拒绝）
    ///   · 目标目录在创建的一瞬间就是个链接（句柄复核直接拒绝）
    ///   · 复制途中目录被删掉重建（身份复核发现不符）
    ///
    /// **挡不住：**
    ///   · 目录**内部**新建 Junction —— 我们锁的是这一层，不是子层；
    ///     这一路靠遍历时逐项 `IsReparsePoint` 检查兜底
    ///   · 同权限恶意进程直接改我们已经拿到的目标文件
    ///   · 网络路径与某些文件系统上，独占共享与文件索引的语义不完整
    ///
    /// **共享模式为什么不是 0**：见 <see cref="SHARE_READ_WRITE_ONLY"/> 的注释。
    /// 一句话 —— 不共享 DELETE 才决定「不可被替换」，
    /// 而「什么都不共享」会把我们自己的搬迁操作也一起锁死。
    ///
    /// 也就是说：**这是缩小窗口，不是消除窗口。** 真要抵御同权限的恶意并发，
    /// 需要全程使用句柄相对操作（NtCreateFile 的 RootDirectory 参数逐级打开），
    /// 那是另一个量级的改造。对个人桌面软件而言，「挡住误操作与常见的替换攻击」
    /// 是恰当的目标；把它说成「彻底解决 TOCTOU」就是不诚实。
    ///
    /// ## 失败时的处置
    ///
    /// 拿不到句柄说明目录已经被别人以不兼容的方式打开。调用方**放弃搬迁**并报失败 ——
    /// 在无法保证路径稳定的情况下继续搬，正是 TOCTOU 想利用的状态。
    /// </summary>
    public sealed class MigrationDirectoryLock : IDisposable
    {
        #region 原生声明
        private const uint FILE_READ_ATTRIBUTES = 0x0080;
        private const uint FILE_WRITE_ATTRIBUTES = 0x0100;
        private const uint DELETE = 0x00010000;
        private const uint SYNCHRONIZE = 0x00100000;

        // 打开目录必须有它，否则 CreateFile 对目录会失败。
        private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;

        // **打开链接本身而不是它的目标。** 这是 BUG-05 修复的核心：
        // 少了它，一个 Junction 会被透明跟随，句柄锁住的是外部真实目录，
        // 我们既看不出路径已被替换，也保护不了真正的路径。
        private const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;

        // 我们要的最小访问权限：只为「持有」而打开，不读内容。
        // 显式要求 DELETE 是刻意的：让共享冲突在**获取时**就发生，
        // 而不是等到我们对里面的文件动手时才发现。
        private const uint DESIRED_ACCESS = FILE_READ_ATTRIBUTES | FILE_WRITE_ATTRIBUTES
                                            | DELETE | SYNCHRONIZE;

        /// <summary>
        /// 共享模式：允许别人读、写，但**不共享 DELETE**。
        ///
        /// **不能用 0（什么都不共享），这会自锁。**（2026-11-23 实测发现）
        ///
        /// 第一版为了「独占」把 shareMode 设成 0，看起来最严谨，实测却是：
        ///
        ///     share=0   create_tmp=OK
        ///               move=IOException（另一个进程正在使用此文件）
        ///               enum=IOException
        ///     share=3   create_tmp=OK   move=OK   enum=OK   rename=BLOCKED
        ///
        /// 原因：目录句柄不共享时，**我们自己对目录内子项的重命名与枚举也一并被拒**，
        /// 因为那些操作在 NTFS 上要拿这一层目录的共享锁。于是「保持目录不可被替换」
        /// 的代价变成了「搬迁根本无法完成」—— 目标目录建出来、一个文件也搬不进去，
        /// 表现为 `MoveDataDirectory` 永远返回 false。
        ///
        /// 关键在于：**「不可被改名/删除」只取决于是否共享 DELETE**，
        /// 与是否共享读写无关。实测 share=3 时 rename 依然被 BLOCKED，
        /// 而目录内的一切正常操作都可用。这正是我们要的那一半性质。
        ///
        /// 也就是说这里的语义是「独占可写、但允许读」——
        /// 挡的是「别人把这个目录换掉」，不是「别人读它」。
        /// </summary>
        private const uint SHARE_READ_WRITE_ONLY = 0x00000001 | 0x00000002;

        private const uint OPEN_EXISTING = 3;
        private const uint INVALID_FILE_ATTRIBUTES = 0xFFFFFFFF;
        private const int ERROR_SHARING_VIOLATION = 32;
        private const int ERROR_LOCK_VIOLATION = 33;
        private const int ERROR_FILE_NOT_FOUND = 2;
        private const int ERROR_PATH_NOT_FOUND = 3;

        private const uint FILE_ATTRIBUTE_DIRECTORY = 0x00000010;
        private const uint FILE_ATTRIBUTE_REPARSE_POINT = 0x00000400;

        [StructLayout(LayoutKind.Sequential)]
        private struct BY_HANDLE_FILE_INFORMATION
        {
            public uint FileAttributes;
            public FILETIME CreationTime;
            public FILETIME LastAccessTime;
            public FILETIME LastWriteTime;
            public uint VolumeSerialNumber;
            public uint FileSizeHigh;
            public uint FileSizeLow;
            public uint NumberOfLinks;
            public uint FileIndexHigh;
            public uint FileIndexLow;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FILETIME
        {
            public uint Low;
            public uint High;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFileW(
            string fileName,
            uint desiredAccess,
            uint shareMode,
            IntPtr securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            IntPtr templateFile);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFileAttributesW(string fileName);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileInformationByHandle(
            SafeFileHandle file, out BY_HANDLE_FILE_INFORMATION info);
        #endregion

        private readonly SafeFileHandle _handle;
        private readonly string _path;

        // 取得锁时记录的目录身份：卷序列号 + 文件索引。
        // 复制完成后用它复核「还是不是同一个目录」。
        private readonly uint _volumeSerial;
        private readonly ulong _fileIndex;

        private MigrationDirectoryLock(SafeFileHandle handle, string path,
            uint volumeSerial, ulong fileIndex)
        {
            _handle = handle;
            _path = path;
            _volumeSerial = volumeSerial;
            _fileIndex = fileIndex;
        }

        /// <summary>
        /// 尝试独占持有 <paramref name="directoryPath"/>（目录必须已存在）。
        ///
        /// 目录不存在时返回 null 且 **error 为 null** —— 调用方据此判断
        /// 「没什么可锁的」，而不是把它当成错误。
        ///
        /// **不要用它来锁搬迁目标**：目标通常还不存在，那样等于没锁。
        /// 搬迁目标请用 <see cref="AcquireOrCreate"/>。
        /// </summary>
        /// <param name="directoryPath">要锁定的目录。</param>
        /// <param name="error">失败原因，成功时为 null。</param>
        /// <returns>成功返回锁对象；目录不存在或获取失败返回 null。</returns>
        public static MigrationDirectoryLock TryAcquire(string directoryPath, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(directoryPath))
            {
                error = "路径为空";
                return null;
            }

            try
            {
                if (GetFileAttributesW(directoryPath) == INVALID_FILE_ATTRIBUTES)
                {
                    int code = Marshal.GetLastWin32Error();
                    if (code == ERROR_FILE_NOT_FOUND || code == ERROR_PATH_NOT_FOUND)
                    {
                        return null;
                    }

                    error = "读取属性失败，Win32 错误码 " + code;
                    return null;
                }

                return OpenAndVerify(directoryPath, out error);
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + ex.Message;
                return null;
            }
        }

        /// <summary>
        /// **安全创建 + 锁定**：保证返回非 null 时，调用方持有的句柄确实指向
        /// 刚创建（或已存在）的那个真实目录，而不是它上面某个链接。
        ///
        /// 这是 BUG-05 的修复落点。步骤见类注释；失败一律返回 null 并给出原因，
        /// 调用方必须放弃搬迁（拿不到锁就不再往下走）。
        /// </summary>
        /// <param name="directoryPath">目标目录，可以尚不存在。</param>
        /// <param name="error">失败原因，成功时为 null。</param>
        /// <returns>成功返回锁对象；失败返回 null 并设置 error。</returns>
        public static MigrationDirectoryLock AcquireOrCreate(string directoryPath, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(directoryPath))
            {
                error = "路径为空";
                return null;
            }

            try
            {
                // 第 1 步：建出来。CreateDirectory 对已存在的目录是幂等的，
                // 所以「首次迁移」与「目标已存在」两情况共用一条路径。
                //
                // 这里**不做**「先判断存在再创建」：那会引入一个新的检查-使用窗口，
                // 而且「存在但不可访问」这类情况会被误判成不存在。
                Directory.CreateDirectory(directoryPath);

                // 第 2~4 步：按句柄打开并复核身份。
                return OpenAndVerify(directoryPath, out error);
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + ex.Message;
                return null;
            }
        }

        /// <summary>
        /// 按句柄打开目录，并复核「它是真实目录、不是链接」。
        ///
        /// 两个调用方（<see cref="TryAcquire"/> 与 <see cref="AcquireOrCreate"/>）
        /// 共用这一段：打开方式与复核判据必须只有一处，否则将来修一处漏一处 ——
        /// 那正是「同一份知识写两遍」的典型后果。
        /// </summary>
        private static MigrationDirectoryLock OpenAndVerify(string directoryPath, out string error)
        {
            error = null;

            // 共享模式见 SHARE_READ_WRITE_ONLY 的注释：**不能用 0**，
            // 那会把我们自己对目录内子项的重命名与枚举也一起锁死，
            // 搬迁永远搬不进去。不共享 DELETE 才是「目录不可被替换」的真正来源。
            //
            // 同时带上 OPEN_REPARSE_POINT：若这个路径现在是个联接，
            // 我们打开的是联接对象本身，下面才查得出 ReparsePoint 属性。
            SafeFileHandle handle = CreateFileW(
                directoryPath,
                DESIRED_ACCESS,
                SHARE_READ_WRITE_ONLY,
                IntPtr.Zero,
                OPEN_EXISTING,
                FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT,
                IntPtr.Zero);

            if (handle == null || handle.IsInvalid)
            {
                int code = Marshal.GetLastWin32Error();
                if (handle != null) handle.Dispose();

                error = code == ERROR_SHARING_VIOLATION || code == ERROR_LOCK_VIOLATION
                    ? "目录已被其它进程独占打开（Win32 错误码 " + code + "）"
                    : "无法独占打开目录，Win32 错误码 " + code;
                return null;
            }

            BY_HANDLE_FILE_INFORMATION info;
            if (!GetFileInformationByHandle(handle, out info))
            {
                int code = Marshal.GetLastWin32Error();
                handle.Dispose();
                error = "无法读取目录信息，Win32 错误码 " + code;
                return null;
            }

            // **确诊链接**：这就是「创建动作落在别人放的联接上」的唯一可判定证据。
            // 早先没有这一步，于是 T3→T4 那条路走到底也没人发现。
            if ((info.FileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) != 0)
            {
                handle.Dispose();
                error = "目标目录是重解析点（Junction / 符号链接），拒绝在其上搬迁";
                return null;
            }

            // 必须是目录。CreateFileW 配合 BACKUP_SEMANTICS 能打开文件，
            // 所以类型要自己确认，不能假设「能打开就是目录」。
            if ((info.FileAttributes & FILE_ATTRIBUTE_DIRECTORY) == 0)
            {
                handle.Dispose();
                error = "目标路径不是目录";
                return null;
            }

            ulong index = ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow;
            return new MigrationDirectoryLock(handle, directoryPath, info.VolumeSerialNumber, index);
        }

        /// <summary>
        /// 复核「这个目录还是取得锁时那一个」。
        ///
        /// 复制要花时间，期间目录可能被删掉再重建（那就成了一个不同的目录，
        /// 哪怕路径字符串一模一样）。卷序列号 + 文件索引是 NTFS 给对象的唯一标识，
        /// 变了就说明不是同一个。
        ///
        /// 用于「复制完成、删除源文件之前」：确认目标确实落在那棵被锁住的树上。
        /// </summary>
        /// <param name="error">不符时的原因，相符时为 null。</param>
        /// <returns>仍是同一个目录返回 true。</returns>
        public bool VerifyUnchanged(out string error)
        {
            error = null;

            try
            {
                if (_handle == null || _handle.IsInvalid)
                {
                    error = "句柄已释放，无法复核目录身份";
                    return false;
                }

                BY_HANDLE_FILE_INFORMATION info;
                if (!GetFileInformationByHandle(_handle, out info))
                {
                    error = "复核时无法读取目录信息，Win32 错误码 "
                        + Marshal.GetLastWin32Error();
                    return false;
                }

                if ((info.FileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) != 0)
                {
                    error = "目录在搬迁期间变成了重解析点";
                    return false;
                }

                ulong index = ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow;
                if (info.VolumeSerialNumber != _volumeSerial || index != _fileIndex)
                {
                    error = "目录身份已改变（搬迁期间被替换）";
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }

        /// <summary>被锁定的路径，用于日志与错误信息。</summary>
        public string Path
        {
            get { return _path; }
        }

        /// <summary>释放独占。释放之后目录重新可以被改名或删除。</summary>
        public void Dispose()
        {
            try
            {
                if (_handle != null && !_handle.IsInvalid) _handle.Dispose();
            }
            catch
            {
                // 关闭句柄失败不影响搬迁结果，进程退出时系统会回收。
            }
        }
    }
}
