using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Auto7z_WPF.Services
{
    /// <summary>
    /// 判断本次启动的来源：父进程是 explorer 时视为资源管理器发起的拖拽或双击打开。
    /// </summary>
    public static class LaunchContext
    {
        #region 原生声明
        // 与 Win32 的 PROCESSENTRY32 逐字段对应，字段顺序和 szExeFile 的长度都不能改。
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct PROCESSENTRY32
        {
            public uint dwSize;
            public uint cntUsage;
            public uint th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID;
            public uint cntThreads;
            public uint th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szExeFile;
        }

        private const uint TH32CS_SNAPPROCESS = 0x00000002;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr hObject);

        #endregion
        #region 启动来源判断
        // 父进程在本次运行期间不会变，查一次就缓存，避免每次拖拽都做一遍进程快照。
        private static string _parentName;
        private static bool _parentChecked;

        public static string ParentProcessName
        {
            get
            {
                if (!_parentChecked)
                {
                    _parentChecked = true;
                    _parentName = QueryParentProcessName();
                }
                return _parentName;
            }
        }

        // 资源管理器打开文件时父进程就是 explorer，父进程名足以区分拖拽与命令行启动。
        public static bool ParentIsExplorer
        {
            get
            {
                string name = ParentProcessName;
                return name == "explorer";
            }
        }

        // 只带参数还不够：快捷方式与文件关联同样会传路径，必须配合来源判断才不会误判成拖拽。
        public static bool IsDragAndDrop(bool hasPaths)
        {
            return hasPaths && ParentIsExplorer;
        }

        #endregion
        #region 父进程查询
        // 查询失败一律返回空串，调用方按非拖拽处理，不影响正常启动。
        private static string QueryParentProcessName()
        {
            try
            {
                uint parentPid = QueryParentProcessId();
                if (parentPid == 0) return string.Empty;

                try
                {
                    // 父进程可能已经退出，取不到进程时只能放弃判断。
                    string name = Process.GetProcessById((int)parentPid).ProcessName;
                    if (string.IsNullOrEmpty(name)) return string.Empty;
                    // 系统返回的进程名大小写不固定，统一转小写后再与 explorer 比较才不会漏判。
                    return name.ToLowerInvariant();
                }
                catch
                {
                    return string.Empty;
                }
            }
            catch
            {
                // 查不到就当没有，按最保守的方式处理
            }

            return string.Empty;
        }

        // Toolhelp 快照遍历整个进程表，找到自己的 PID 后再取它的父 PID，.NET 没有等价的直接接口。
        private static uint QueryParentProcessId()
        {
            try
            {
                uint self = (uint)Process.GetCurrentProcess().Id;

                IntPtr snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
                // 句柄为 -1 表示快照创建失败，直接当作查不到父进程。
                if (snapshot == new IntPtr(-1)) return 0;

                try
                {
                    var entry = new PROCESSENTRY32();
                    entry.dwSize = (uint)Marshal.SizeOf(entry);

                    if (Process32First(snapshot, ref entry))
                    {
                        do
                        {
                            if (entry.th32ProcessID == self)
                            {
                                return entry.th32ParentProcessID;
                            }
                        }
                        while (Process32Next(snapshot, ref entry));
                    }
                }
                finally
                {
                    // 无论遍历是否成功都要关闭快照句柄，否则句柄会随每次调用累积。
                    CloseHandle(snapshot);
                }
            }
            catch
            {
                // 有意忽略：查询父进程失败时按「不是资源管理器」处理（返回 0）， // 调用方据此走非拖拽分支，不该把异常抛出去。
            }

            return 0;
        }
        #endregion
    }
}
