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
    /// 命令行参数的解析。
    ///
    /// 除了直接给出文件路径, 还支持这些形式:
    ///
    ///   #映射名:字节数:事件名
    ///       文件列表放在共享内存里, 由资源管理器的右键菜单扩展写入。
    ///       一次选中上百个长路径时命令行装不下, 只能走这条路。
    ///
    ///   -A / -Z / -U
    ///       指定任务模式: 自动 / 压缩 / 解压。
    ///       不给就用自动模式, 跟把文件拖进窗口一样。
    ///
    ///   -r / -ur
    ///       注册 / 注销右键菜单的外壳扩展。
    ///       注册要管理员权限, 权限不够时会用 UAC 提权再试一次。
    ///
    ///   -h
    ///       显示命令行用法。
    /// </summary>
    public class ShellCommandService
    {
        /// <summary>共享内存引用的前缀。</summary>
        private const char MapPrefix = '#';

        /// <summary>命令行里的模式开关。</summary>
        public const string SwitchAuto = "-A";
        public const string SwitchZip = "-Z";
        public const string SwitchUnZip = "-U";

        /// <summary>注册右键菜单。</summary>
        public const string SwitchRegister = "-r";

        /// <summary>注销右键菜单。</summary>
        public const string SwitchUnRegister = "-ur";

        /// <summary>显示命令行用法。</summary>
        public const string SwitchHelp = "-h";

        /// <summary>提权后只写主程序路径。</summary>
        public const string SwitchSetMainExePath = "--set-main-exe-path";

        private ShellCommandService()
        {
        }

        /// <summary>
        /// 解析命令行参数。
        /// </summary>
        /// <param name="args">原始参数。</param>
        /// <param name="paths">解析出来的文件路径。</param>
        /// <param name="mode">解析出来的模式；没有指定时为 null。</param>
        /// <param name="register">是否要注册右键菜单。</param>
        /// <param name="unregister">是否要注销右键菜单。</param>
        /// <param name="help">是否要显示用法说明。</param>
        /// <param name="setMainExePath">是否只写主程序路径。</param>
        public static void Parse(string[] args, out List<string> paths, out string mode,
            out bool register, out bool unregister, out bool help, out bool setMainExePath)
        {
            paths = new List<string>();
            mode = null;
            register = false;
            unregister = false;
            help = false;
            setMainExePath = false;

            if (args == null) return;

            foreach (string arg in args)
            {
                if (string.IsNullOrWhiteSpace(arg)) continue;

                if (arg.Length > 1 && arg[0] == MapPrefix)
                {
                    // 共享内存形式, 路径要从内存里取
                    List<string> mapped = ReadPathMap(arg);
                    if (mapped != null) paths.AddRange(mapped);
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
                if (string.Equals(arg, SwitchSetMainExePath, StringComparison.OrdinalIgnoreCase))
                {
                    setMainExePath = true;
                    continue;
                }

                paths.Add(arg);
            }
        }

        /// <summary>
        /// 从共享内存里读出路径列表。
        ///
        /// 参数格式是 #映射名:字节数:事件名。
        /// 读完后会打开事件通知写入方, 写入方据此可以释放自己那份句柄。
        /// </summary>
        /// <param name="argument">命令行里的那一项。</param>
        /// <returns>路径列表；读不到时返回 null。</returns>
        private static List<string> ReadPathMap(string argument)
        {
            // 去掉前缀后按冒号拆成三段
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

                        // 第一个字符是标记, 后面是按 \0 分隔的 UTF-16 路径
                        int position = 2;
                        while (position + 2 <= buffer.Length)
                        {
                            string path = Encoding.Unicode.GetString(buffer, position, buffer.Length - position);
                            int end = path.IndexOf('\0');
                            if (end < 0) break;
                            if (end > 0) paths.Add(path.Substring(0, end));
                            position += (end + 1) * 2;
                        }
                    }
                }
            }
            catch (Exception)
            {
                // 映射不存在或已被释放, 说明写入方没能建好
                return null;
            }

            // 通知写入方: 路径已经取走, 可以释放内存了
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
                // 通知失败不影响路径已经读到
            }

            return paths;
        }

        /// <summary>EVENT_MODIFY_STATE, 打开事件时需要写权限。</summary>
        private const uint EventModifyState = 0x0002;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr OpenEvent(uint desiredAccess, bool inheritHandle, string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetEvent(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
