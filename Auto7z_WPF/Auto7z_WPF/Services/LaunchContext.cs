using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Auto7z_WPF.Services
{
    /// <summary>
    /// 判定"程序是怎么被启动的"。
    ///
    /// 同样的 exe，用户可以用好几种方式启动它，而每种方式的期望不一样：
    ///
    ///   方式                        父进程        期望
    ///   --------------------------  -----------  ----------------------------
    ///   双击 exe                    explorer      出主窗口
    ///   cmd 里敲命令                cmd/powershell 出命令行输出，**不出主窗口**
    ///   资源管理器右键（外壳扩展）    explorer      出小对话框 + 进度窗口，**不出主窗口**
    ///   把文件拖到 exe 图标上        explorer      同右键：**不出主窗口**
    ///
    /// 最后一种是拖拽：用户把文件拖到 exe 上，Windows 通过 explorer 启动本进程。
    /// 它跟右键菜单的期望一致 —— 点了就走，不要冒出整个主窗口。
    ///
    /// 但它跟"双击 exe"在命令行参数上长得一样（都是直接给路径，
    /// 没有 #共享内存 那种标记），光看参数分不出来，只能看父进程。
    ///
    /// 怎么判断
    /// --------
    /// 看父进程是谁：
    ///   explorer.exe  —— 说明是资源管理器发起的（双击或拖拽）
    ///   cmd / powershell / 其他 —— 说明是用户在终端里敲的
    ///
    /// 但双击和拖拽都是 explorer 父进程，还要再分一层：
    ///   拖拽一定带文件路径参数；双击不带任何路径参数。
    ///   所以：有路径参数 + 父进程是 explorer => 拖拽。
    /// </summary>
    public static class LaunchContext
    {
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

        private static string _parentName;
        private static bool _parentChecked;

        /// <summary>
        /// 父进程的可执行文件名（小写，不带扩展名）。
        /// 取不到返回空字符串。
        /// </summary>
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

        /// <summary>
        /// 父进程是不是资源管理器。
        /// </summary>
        public static bool ParentIsExplorer
        {
            get
            {
                string name = ParentProcessName;
                return name == "explorer";
            }
        }

        /// <summary>
        /// 本次启动是不是"把文件拖到 exe 上"。
        ///
        /// 判据：父进程是 explorer.exe（资源管理器发起的），
        /// 且命令行里带了文件路径（拖拽必然带路径，双击不带）。
        /// </summary>
        /// <param name="hasPaths">命令行里解析出了文件路径。</param>
        /// <returns>是否是拖拽启动。</returns>
        public static bool IsDragAndDrop(bool hasPaths)
        {
            return hasPaths && ParentIsExplorer;
        }

        /// <summary>
        /// 查父进程的可执行文件名。
        ///
        /// 父进程 PID 用 ToolHelp32 快照拿（Process 类的 Parent 属性
        /// 在 .NET Framework 里没有）。名字用 Process.GetProcessById 拿 ——
        /// 不用快照再扫一遍，快照的枚举器扫到一半就断了，容易拿不到。
        ///
        /// 父进程可能已经退出（拖拽时启动器会先退），这时拿不到名字，
        /// 返回空字符串，由调用方按"查不到就是非拖拽"处理。
        /// </summary>
        private static string QueryParentProcessName()
        {
            try
            {
                uint parentPid = QueryParentProcessId();
                if (parentPid == 0) return string.Empty;

                try
                {
                    string name = Process.GetProcessById((int)parentPid).ProcessName;
                    if (string.IsNullOrEmpty(name)) return string.Empty;
                    return name.ToLowerInvariant();
                }
                catch
                {
                    // 父进程已经退出
                    return string.Empty;
                }
            }
            catch
            {
                // 查不到就当没有，按最保守的方式处理
            }

            return string.Empty;
        }

        /// <summary>
        /// 查父进程的 PID。
        /// </summary>
        private static uint QueryParentProcessId()
        {
            try
            {
                uint self = (uint)Process.GetCurrentProcess().Id;

                IntPtr snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
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
                    CloseHandle(snapshot);
                }
            }
            catch
            {
                // 忽略
            }

            return 0;
        }
    }
}
