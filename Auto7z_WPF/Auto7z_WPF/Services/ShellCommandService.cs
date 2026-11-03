using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text;

namespace Auto7z_WPF.Services
{
    /// <summary>
    /// 命令行参数的解析入口，同时负责把 Shell 扩展传来的共享内存路径读出来。
    /// </summary>
    public class ShellCommandService
    {
        #region 开关定义
        // Shell 扩展把路径写进共享内存后，用一个以 # 开头的参数把映射名、字节数、事件名带过来；
        // 这样路径不必经过命令行，省掉空格、引号与超长命令行带来的转义麻烦。
        // 也正因为这个标记只可能由扩展产生，它出现就等价于「本次是右键菜单调用」。
        private const char MapPrefix = '#';

        public const string SwitchAuto = "-a";
        public const string SwitchZip = "-z";
        public const string SwitchUnZip = "-uz";

        public const string SwitchRegister = "-r";

        public const string SwitchUnRegister = "-ur";

        public const string SwitchHelp = "-h";

        private ShellCommandService()
        {
        }
        #endregion

        #region 参数解析
        // 路径有两种来源：命令行直接给，或 Shell 扩展预写进共享内存块。
        // 后者识别到就一并置上 fromShell，调用方据此区分「命令行调用」与「右键菜单调用」。
        public static void Parse(string[] args, out List<string> paths, out string mode,
            out bool register, out bool unregister, out bool help, out bool fromShell)
        {
            Parse(args, out paths, out mode, out register, out unregister, out help, out fromShell,
                out _);
        }

        // 带 unknownSwitch 的重载供命令行入口使用：拼错的开关要能报出来并影响退出码。
        public static void Parse(string[] args, out List<string> paths, out string mode,
            out bool register, out bool unregister, out bool help, out bool fromShell,
            out string unknownSwitch)
        {
            paths = new List<string>();
            mode = null;
            register = false;
            unregister = false;
            help = false;
            fromShell = false;
            unknownSwitch = null;

            if (args == null) return;

            foreach (string arg in args)
            {
                if (string.IsNullOrWhiteSpace(arg)) continue;

                // 长度判断把单独的 "#" 排除在外：那不是一个完整的映射标记。
                if (arg.Length > 1 && arg[0] == MapPrefix)
                {
                    List<string> mapped = ReadPathMap(arg);
                    if (mapped != null)
                    {
                        paths.AddRange(mapped);
                        fromShell = true;
                    }
                    // 读失败时这个参数既不算路径也不算来源，直接丢弃：
                    // 它本来就不是磁盘上真实存在的文件名，混进 paths 只会让后面报找不到文件。
                    continue;
                }

                if (string.Equals(arg, SwitchAuto, StringComparison.OrdinalIgnoreCase))
                {
                    mode = SwitchAuto;
                    continue;
                }
                if (string.Equals(arg, SwitchZip, StringComparison.OrdinalIgnoreCase))
                {
                    mode = SwitchZip;
                    continue;
                }
                if (string.Equals(arg, SwitchUnZip, StringComparison.OrdinalIgnoreCase))
                {
                    mode = SwitchUnZip;
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

                // 以 - 或 / 开头、又不是任何已知开关时，先看它是不是磁盘上真存在的路径：
                // 「一个名叫 -a.txt 的文件」是合法输入，不能因为长得像开关就拒收。
                if (LooksLikeSwitch(arg) && !ExistsOnDisk(arg))
                {
                    // 只记第一个：报错只需要一个代表，列一长串反而看不出重点。
                    if (unknownSwitch == null) unknownSwitch = arg;
                    continue;
                }

                paths.Add(arg);
            }
        }

        // 开关形态：以 - 或 / 打头且长度大于 1。单独一个 "-" 当作路径（极少见但合法）。
        private static bool LooksLikeSwitch(string arg)
        {
            return arg.Length > 1 && (arg[0] == '-' || arg[0] == '/');
        }

        // 盘上是否真的存在：存在就说明它是个路径，不是开关。
        private static bool ExistsOnDisk(string path)
        {
            try
            {
                return File.Exists(path) || Directory.Exists(path);
            }
            catch
            {
                // 路径非法（含非法字符、过长）时按「不存在」处理，让调用方报未知开关。
                return false;
            }
        }
        #endregion

        #region 共享内存路径映射
        // 参数形如 #映射名:字节数:事件名，三段缺一不可，长度不对就当成普通参数丢弃。
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

            if (mapName.Length == 0 || totalBytes <= 0) return null;

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

                        // 头两个字节是标记字符，只用于确认这块内存确实是本程序认得的格式。
                        int position = 2;
                        while (position + 2 <= buffer.Length)
                        {
                            int end = position;
                            // 内容整体是 UTF-16，所以按两字节一步找零终止符；
                            // 每段只在查完边界后解码一次，避免对同一块数据反复切片解码。
                            while (end + 1 < buffer.Length && (buffer[end] | (buffer[end + 1] << 8)) != 0)
                            {
                                end += 2;
                            }

                            if (end + 1 >= buffer.Length) break;

                            int charCount = (end - position) / 2;
                            if (charCount > 0)
                            {
                                paths.Add(Encoding.Unicode.GetString(buffer, position, charCount * 2));
                            }

                            position = end + 2;
                        }
                    }
                }
            }
            catch (Exception)
            {
                // 映射可能已被扩展关掉，或名字对不上；读不到就按「没有路径」处理，
                // 抛出去会让整个参数解析失败，连后面的模式开关都读不进来。
                return null;
            }

            try
            {
                // 置位后 Shell 扩展才知道映射可以撤了，否则它要一直等到超时才释放这块内存。
                IntPtr handle = OpenEvent(EventModifyState, false, eventName);
                if (handle != IntPtr.Zero)
                {
                    SetEvent(handle);
                    CloseHandle(handle);
                }
            }
            catch (Exception)
            {
                // 通知失败不影响路径已经读到
            }

            return paths;
        }
        #endregion

        #region 事件句柄互操作
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
