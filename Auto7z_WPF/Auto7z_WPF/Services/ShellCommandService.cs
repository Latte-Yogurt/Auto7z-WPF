using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text;
using Auto7z_WPF.Models;

namespace Auto7z_WPF.Services
{
    /// <summary>
    /// 命令行解析入口，并负责把外壳扩展经共享内存传来的路径读出来。
    /// </summary>
    public static class ShellCommandService
    {
        #region 开关定义
        /// <summary>共享内存参数的起始标记，只可能由外壳扩展产生，出现即代表本次是右键菜单调用。</summary>
        private const char MapPrefix = '#';

        public const string SwitchAuto = "-a";
        public const string SwitchZip = "-z";
        public const string SwitchUnZip = "-uz";

        public const string SwitchRegister = "-r";

        public const string SwitchUnRegister = "-ur";

        public const string SwitchHelp = "-h";

        /// <summary>
        /// 合法的开关白名单。判断方式是「命中白名单才生效」，而不是「像开关且不在已知列表里就算未知」。
        ///
        /// 这两种写法的差别在边界上：
        ///   - 白名单：/A、--help、-abc 之类一律是未知开关，会被告知并拒绝；
        ///   - 反选法：解析器必须"猜"什么不算开关，一旦猜错就静默按路径处理。
        /// 命令行的契约是「参数写错必须让调用方知道」，所以按白名单来。
        /// </summary>
        private static readonly string[] _knownSwitches =
        {
            SwitchAuto, SwitchZip, SwitchUnZip, SwitchRegister, SwitchUnRegister, SwitchHelp
        };

        /// <summary>
        /// 参数是否命中白名单。大小写不敏感（README 承诺 <c>-A</c> 与 <c>-a</c> 等价）。
        ///
        /// 白名单只有 _knownSwitches 这一处定义，解析循环与外部校验都从这里查：
        /// 新增或删除开关只需要改那张表，不必再分别修改「长得像开关」的各种判断。
        /// </summary>
        public static bool IsKnownSwitch(string arg)
        {
            if (string.IsNullOrWhiteSpace(arg)) return false;

            foreach (string known in _knownSwitches)
            {
                if (string.Equals(arg, known, StringComparison.OrdinalIgnoreCase)) return true;
            }

            return false;
        }

        /// <summary>
        /// 把模式开关翻译成「执行哪个任务」。未知开关一律按自动分派处理。
        ///
        /// 为什么这个映射放在本类：本类持有开关的定义（<c>SwitchAuto</c> 等常量与白名单），
        /// 「某个开关该干什么」属于同一份知识。早先 ConsoleTaskRunner 与
        /// ShellMenuTaskRunner 各写了一遍同样的 switch，两份的分支与默认值必须永远一致 ——
        /// 将来加一个模式开关（比如 -t 指定 tar），漏改一处就出现
        /// 「命令行能用、右键菜单不认」这种只在特定入口复现的怪问题。
        ///
        /// 参数只有一个：是否带进度回调。命令行路径传 null（进度直接写控制台），
        /// 外壳菜单路径传真实的回调（进度要刷 ProcessWindow）。
        /// </summary>
        /// <param name="viewModel">要驱动任务的 ViewModel。</param>
        /// <param name="mode">模式开关，可能为 null。</param>
        /// <param name="report">进度回调，可为 null。</param>
        public static void RunByMode(Auto7zViewModel viewModel, string mode, System.Action<ProgressInfo> report)
        {
            if (viewModel == null) return;

            if (string.Equals(mode, SwitchUnZip, StringComparison.OrdinalIgnoreCase))
            {
                viewModel.RunExtractAndWait(report);
                return;
            }

            if (string.Equals(mode, SwitchZip, StringComparison.OrdinalIgnoreCase))
            {
                viewModel.RunCompressAndWait(report);
                return;
            }

            // SwitchAuto 与 null（不带开关）都走自动分派，
            // 与用法说明里的默认行为一致。
            viewModel.RunAutoDispatchAndWait(report);
        }

        /// <summary>
        /// 把模式开关翻译成 <see cref="OperationMode"/>。
        ///
        /// 早先有两份几乎相同但**兜底方向相反**的实现：
        ///   App.ParseMode         未知 → Zip    （默认压缩）
        ///   MainWindow.ParseHanded 未知 → Auto   （默认自动）
        /// 两条路径的 mode 都只来自白名单，所以眼下不可达；但「同一个问题、
        /// 两个互相矛盾的默认值」是典型的埋雷 —— 将来某个入口放宽了校验，
        /// 同一个非法开关就会在命令行压成包、在拖拽入口却能自动识别。
        ///
        /// 现在统一：未知 / null 一律按 <see cref="OperationMode.Auto"/> 处理，
        /// 与「不带开关时的默认行为」保持一致。
        /// </summary>
        /// <param name="mode">模式开关，可能为 null。</param>
        /// <returns>对应的操作模式。</returns>
        public static OperationMode ParseOperationMode(string mode)
        {
            if (string.Equals(mode, SwitchZip, StringComparison.OrdinalIgnoreCase))
            {
                return OperationMode.Zip;
            }

            if (string.Equals(mode, SwitchUnZip, StringComparison.OrdinalIgnoreCase))
            {
                return OperationMode.UnZip;
            }

            // SwitchAuto、null、以及任何无法识别的值：都按自动模式。
            return OperationMode.Auto;
        }

        #endregion

        #region 参数解析
        public static void Parse(string[] args, out List<string> paths, out string mode,
            out bool register, out bool unregister, out bool help, out bool fromShell)
        {
            Parse(args, out paths, out mode, out register, out unregister, out help, out fromShell,
                out _, out _);
        }

        public static void Parse(string[] args, out List<string> paths, out string mode,
            out bool register, out bool unregister, out bool help, out bool fromShell,
            out List<string> unknownSwitches)
        {
            Parse(args, out paths, out mode, out register, out unregister, out help, out fromShell,
                out unknownSwitches, out _);
        }

        /// <summary>
        /// 完整解析入口。
        ///
        /// <paramref name="conflictingSwitches"/> 与 <paramref name="unknownSwitches"/> 分开：
        /// 两者都是"参数写错了"，但用户要做的事不同 ——
        /// 前者是「两个互斥开关同时给了，删掉一个」，后者是「这个开关根本不存在」。
        /// 合并成一句「无法识别的选项」会让用户去查文档找那个"不存在"的开关，
        /// 而它其实存在、只是不能和另一个同时用。
        /// </summary>
        public static void Parse(string[] args, out List<string> paths, out string mode,
            out bool register, out bool unregister, out bool help, out bool fromShell,
            out List<string> unknownSwitches, out List<string> conflictingSwitches)
        {
            // 先清零全部输出参数，调用方按 null 与 false 判断「本次没有指定」。
            paths = new List<string>();
            mode = null;
            register = false;
            unregister = false;
            help = false;
            fromShell = false;
            unknownSwitches = new List<string>();
            conflictingSwitches = new List<string>();

            if (args == null) return;

            foreach (string arg in args)
            {
                if (string.IsNullOrWhiteSpace(arg)) continue;

                if (arg.Length > 1 && arg[0] == MapPrefix)
                {
                    List<string> mapped = ReadPathMap(arg);
                    if (mapped != null)
                    {
                        paths.AddRange(mapped);
                        fromShell = true;
                    }
                    continue;
                }

                // 白名单命中才生效。这里逐一比对，未命中的一律不进 paths ——
                // 是否算「未知开关」由下面的 LooksLikeSwitch 决定，避免把普通相对路径当开关。
                //
                // 三个模式开关互斥。早先的写法是「后来者覆盖先来者」，于是
                //     Auto7z.exe -z -uz a.txt
                // 会静默按 -uz 执行（解压一个文本文件，失败），而用户以为自己同时给了
                // 两个开关、程序应当报错。命令行的契约是「参数写错必须让调用方知道」。
                if (string.Equals(arg, SwitchAuto, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(arg, SwitchZip, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(arg, SwitchUnZip, StringComparison.OrdinalIgnoreCase))
                {
                    if (mode != null && !string.Equals(mode, arg, StringComparison.OrdinalIgnoreCase))
                    {
                        // 两个**不同**的模式开关：冲突。记下两个，让调用方能给出
                        // 「-z 与 -uz 不能同时使用」这种用户看得懂的提示。
                        conflictingSwitches.Add(mode);
                        conflictingSwitches.Add(arg);
                        continue;
                    }
                    mode = arg;
                    continue;
                }

                if (string.Equals(arg, SwitchRegister, StringComparison.OrdinalIgnoreCase))
                {
                    register = true;
                    continue;
                }
                if (string.Equals(arg, SwitchUnRegister, StringComparison.OrdinalIgnoreCase))
                {
                    unregister = true;
                    continue;
                }
                if (string.Equals(arg, SwitchHelp, StringComparison.OrdinalIgnoreCase))
                {
                    help = true;
                    continue;
                }

                // 走到这里说明上面的分发没有命中。
                // 先用白名单判一次：命中说明「是已知开关但没有对应的分发分支」，
                // 那是代码缺陷（新增开关时漏改解析），不该静默当路径、更不该报成未知开关
                // —— 那两种处理都会把维护错误伪装成用户输入错误，排查极其困难。
                if (IsKnownSwitch(arg))
                {
                    unknownSwitches.Add(arg);
                    continue;
                }

                // 未命中白名单：形如开关的一律按未知处理，并全部收集起来一次报出。
                // 只报第一个的话，用户改掉一个还会再撞上下一个，反复试错。
                // 磁盘上真有同名文件（名字恰好以 - 开头）时仍按路径处理，这是 README 的承诺。
                if (LooksLikeSwitch(arg) && !ExistsOnDisk(arg))
                {
                    unknownSwitches.Add(arg);
                    continue;
                }

                paths.Add(arg);
            }
        }

        /// <summary>
        /// 判断参数长得像开关：至少两个字符且以 - 或 / 开头。
        ///
        /// 注意这里**不**做精确匹配 —— 精确匹配的结果交给白名单决定。
        /// 本方法的职责只有一件事：区分「用户想写的开关」与「一个普通路径」。
        /// </summary>
        private static bool LooksLikeSwitch(string arg)
        {
            return arg.Length > 1 && (arg[0] == '-' || arg[0] == '/');
        }

        /// <summary>磁盘上真有以 - 开头的文件时仍按路径处理，避免把真实文件当成拼错的开关。</summary>
        private static bool ExistsOnDisk(string path)
        {
            try
            {
                return File.Exists(path) || Directory.Exists(path);
            }
            catch
            {
                return false;
            }
        }
        #endregion

        #region 共享内存路径映射
        /// <summary>
        /// 解析共享内存参数（冒号是协议分隔符，不是中文标点），读出以 \0 分隔的 UTF-16 路径列表。
        /// 三段缺一不可，格式或长度不对就返回 null，调用方据此当成普通参数丢弃。
        /// </summary>
        private static List<string> ReadPathMap(string argument)
        {
            string body = argument.Substring(1);
            string[] parts = body.Split(':');
            if (parts.Length != 3) return null;

            string mapName = parts[0];
            long totalBytes;
            if (!long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out totalBytes))
                return null;
            string eventName = parts[2];

            // 上界必须在这里挡住：totalBytes 来自不可信的参数文本，(int) 强转 unchecked，
            // 超过 int.MaxValue 会翻成负数，紧接着 new byte[负数] 抛异常被外层 catch 吞成
            // 「不是有效映射」，真实原因完全看不到；更糟的是合法的大值会直接申请数 GB 内存。
            // 外壳扩展写进来的路径串最大也就几十 MB，64MB 足够覆盖 200 个长路径。
            const long MaxMapBytes = 64L * 1024 * 1024;
            if (mapName.Length == 0 || totalBytes <= 0 || totalBytes > MaxMapBytes) return null;

            List<string> paths = new List<string>();

            try
            {
                using (var mapping = MemoryMappedFile.OpenExisting(mapName, MemoryMappedFileRights.Read))
                {
                    int byteCount = (int)totalBytes;
                    using (var view = mapping.CreateViewAccessor(0, byteCount, MemoryMappedFileAccess.Read))
                    {
                        byte[] buffer = new byte[byteCount];
                        view.ReadArray(0, buffer, 0, byteCount);

                        // 前 2 个字节是协议标记，真正的路径从其后的偏移开始平铺。
                        int position = 2;
                        bool truncated = false;
                        while (position + 2 <= buffer.Length)
                        {
                            int end = position;
                            while (end + 1 < buffer.Length && (buffer[end] | (buffer[end + 1] << 8)) != 0)
                            {
                                end += 2;
                            }

                            // 走到缓冲区末尾还没遇到 NUL：发送方写的字节数比实际内容短，
                            // 剩下的这半条路径不可信。已经解出来的路径照常返回，但一条都没解出来时
                            // 必须按「不是有效映射参数」处理 —— 否则空列表会让调用方以为
                            // 这是一次合法的空选区，进而弹出主窗口而不是报错。
                            if (end + 1 >= buffer.Length)
                            {
                                truncated = true;
                                break;
                            }

                            int charCount = (end - position) / 2;
                            if (charCount > 0)
                            {
                                paths.Add(Encoding.Unicode.GetString(buffer, position, charCount * 2));
                            }

                            position = end + 2;
                        }

                        if (truncated && paths.Count == 0) return null;
                    }
                }
            }
            catch (Exception)
            {
                // 映射打不开或被对方提前释放，都按「不是有效的映射参数」处理，绝不把异常抛给命令行入口。
                return null;
            }

            try
            {
                IntPtr handle = OpenEvent(EventModifyState, false, eventName);
                if (handle != IntPtr.Zero)
                {
                    SetEvent(handle);
                    CloseHandle(handle);
                }
            }
            catch (Exception)
            {
                // 通知事件置位失败不影响已经读到的路径，扩展那边最多等到它自己的兜底超时。
            }

            return paths;
        }
        #endregion

        #region 事件句柄互操作
        /// <summary>OpenEvent 所需的修改状态访问权。</summary>
        private const uint EventModifyState = 0x0002;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr OpenEvent(uint desiredAccess, bool inheritHandle, string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetEvent(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);
        #endregion
    }
}
