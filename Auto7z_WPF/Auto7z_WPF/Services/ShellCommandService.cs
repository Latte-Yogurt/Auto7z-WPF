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
    /// 命令行解析入口，并负责把外壳扩展经共享内存传来的路径读出来。
    /// </summary>
    public class ShellCommandService
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

        private ShellCommandService()
        {
        }
        #endregion

        #region 参数解析
        public static void Parse(string[] args, out List<string> paths, out string mode,
            out bool register, out bool unregister, out bool help, out bool fromShell)
        {
            Parse(args, out paths, out mode, out register, out unregister, out help, out fromShell,
                out _);
        }

        public static void Parse(string[] args, out List<string> paths, out string mode,
            out bool register, out bool unregister, out bool help, out bool fromShell,
            out string unknownSwitch)
        {
            // 先清零全部输出参数，调用方按 null 与 false 判断「本次没有指定」。
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

                if (LooksLikeSwitch(arg) && !ExistsOnDisk(arg))
                {
                    if (unknownSwitch == null) unknownSwitch = arg;
                    continue;
                }

                paths.Add(arg);
            }
        }

        /// <summary>判断参数长得像开关：至少两个字符且以 - 或 / 开头。</summary>
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

                        // 前 2 个字节是协议标记，真正的路径从其后的偏移开始平铺。
                        int position = 2;
                        while (position + 2 <= buffer.Length)
                        {
                            int end = position;
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
