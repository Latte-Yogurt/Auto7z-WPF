using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Auto7z_WPF.Services
{
    /// <summary>
    /// 让命令行模式的输出与输入真正落到控制台上。
    ///
    /// 为什么需要这个
    /// ==============
    /// 同一个工程编两个可执行体：Auto7z.exe 是 Console 子系统（cmd 会等它跑完，
    /// 命令行问答与密码输入才落在控制台里），Auto7zGUI.exe 是 GUI 子系统
    /// （双击 / 开始菜单 / 右键菜单 / 拖拽，全程不创建控制台）。
    /// 后者被人从命令行启动时**没有**控制台，继承下来的标准句柄是无效的
    /// （实测 STDOUT/STDIN 都是 0）。
    ///
    /// 于是命令行模式下有两个问题：
    ///   1. 往 stdout 写的东西被直接丢弃 —— 命令行里看不到任何提示，
    ///      任务在后台静默跑，用户完全不知道发生了什么；
    ///   2. stdin 读不到东西 —— "是否合并"的 Y/N 问答、
    ///      加密压缩包的密码输入，全都无从下手。
    ///
    /// 怎么办
    /// ------
    /// Windows 给 GUI 程序准备了这条路：
    ///   AttachConsole(ATTACH_PARENT_PROCESS)
    /// 把**父进程**（也就是用户的 cmd 窗口）的控制台借过来用。
    /// 借到之后必须**重建** .NET 的标准流绑定，
    /// 因为 Console.Out / Console.In 是在进程启动时创建的，
    /// 绑在那个不存在的控制台上。
    ///
    /// 借不到怎么办
    /// ------------
    /// 父进程没有控制台时（双击运行、或者从没有控制台的宿主启动），
    /// AttachConsole 会失败。这时不能开新控制台 ——
    /// 双击运行时凭空多一个黑窗口，正是 WinExe 要避免的事。
    /// 所以借不到就当没有控制台：主窗口照常显示，
    /// 命令行模式下没有输出（跟以前一样），但程序不会崩。
    ///
    /// 什么时候调用
    /// ------------
    /// 只在命令行模式（用户自己在 cmd 里敲命令）下调用。
    /// 右键菜单模式不调用：父进程是 explorer.exe，它的控制台用户根本看不见，
    /// 借了也是往一个看不见的地方输出。
    /// </summary>
    public static class ConsoleAttach
    {
        private const uint ATTACH_PARENT_PROCESS = 0xFFFFFFFF;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AttachConsole(uint dwProcessId);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetConsoleWindow();

        [DllImport("kernel32.dll")]
        private static extern uint GetConsoleOutputCP();

        [DllImport("kernel32.dll")]
        private static extern uint GetConsoleCP();

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetStdHandle(int nStdHandle);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint GetConsoleProcessList(uint[] list, uint count);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FreeConsole();

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        private const int SW_HIDE = 0;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetConsoleMode(IntPtr hHandle, out uint lpMode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetConsoleMode(IntPtr hHandle, uint dwMode);

        private const int STD_OUTPUT_HANDLE = -11;

        private const uint ENABLE_PROCESSED_OUTPUT = 0x0001;
        private const uint ENABLE_WRAP_AT_EOL_OUTPUT = 0x0002;

        private static bool _attached;
        private static bool _tried;

        /// <summary>
        /// 是否已经接上了控制台。
        /// </summary>
        public static bool Attached => _attached;

        /// <summary>
        /// 把程序**自己独占**的那个控制台窗口藏掉。
        ///
        /// 为什么需要
        /// ----------
        /// 程序编成 Console 子系统之后，cmd 启动它时才会**等它跑完** ——
        /// GUI 子系统（WinExe）的程序 cmd 是不等的，提示符立刻还回来，
        /// 用户接着敲的输入会被 cmd 当成自己的命令执行。
        ///
        /// 但 Auto7z.exe 是 Console 子系统，双击它时 Windows 仍会先分配一个黑窗口，
        /// 所以启动时要自己把那个黑窗藏掉。
        /// Auto7zGUI.exe 没有控制台，这里直接返回，不会做任何事。
        ///
        /// 怎么区分「用户的 cmd」与「程序自己分配的黑窗」
        /// --------------------------------------
        /// 看控制台里挂着几个进程：
        ///   - 在用户的 cmd 里启动：控制台里至少有 cmd 自己和本程序（≥2 个）
        ///   - 双击运行：控制台是程序新分配的，只有它自己（1 个）
        ///
        /// 用 GetConsoleProcessList 数一下就知道。
        ///
        /// 只有独占时才处理 —— 用户自己的 cmd 藏了就看不到输出了。
        ///
        /// 怎么藏
        /// ----
        /// 双击时 Windows 会在托管代码开始运行之前就分配一个控制台。
        /// 用 ShowWindow(SW_HIDE) 把窗口先隐藏，避免闪一下；再用
        /// FreeConsole() 把进程和控制台整个拆掉，控制台窗口直接销毁，
        /// 不会有一闪而过的黑窗留在屏幕上。
        /// </summary>
        public static void HideOwnConsoleWindow()
        {
            try
            {
                IntPtr console = GetConsoleWindow();
                if (console == IntPtr.Zero) return;

                // 控制台里只有自己一个进程 —— 这是程序自己分配的那个
                uint[] ids = new uint[8];
                uint count = GetConsoleProcessList(ids, (uint)ids.Length);
                if (count > 1) return;

                // 先隐藏（立即生效，避免闪窗），再拆掉控制台（彻底销毁窗口）
                ShowWindow(console, SW_HIDE);
                FreeConsole();
            }
            catch
            {
                // 藏不掉就让它留着，总比程序起不来强
            }
        }

        /// <summary>
        /// 试着接上父进程(cmd)的控制台。
        ///
        /// 成功返回 true；父进程没有控制台（双击运行）返回 false。
        /// 失败不会抛异常 —— 双击运行是正常场景，不该因此报错。
        /// </summary>
        /// <returns>是否成功接上控制台。</returns>
        public static bool TryAttachToParent()
        {
            if (_tried) return _attached;
            _tried = true;

            try
            {
                // 已经自带控制台：什么都不用做。
                // 被重定向时（cmd /c ... > file）cmd 会给子进程分配控制台，
                // 这种情况标准句柄本来就是好的。
                if (GetConsoleWindow() != IntPtr.Zero)
                {
                    _attached = true;
                    EnsureOutputMode();
                    return true;
                }

                // 借父进程的控制台
                if (!AttachConsole(ATTACH_PARENT_PROCESS))
                {
                    // 借不到：父进程没有控制台。
                    // 不开新控制台 —— 双击运行时多一个黑窗口比没有输出更糟。
                    _attached = false;
                    return false;
                }

                RebuildStreams();
                EnsureOutputMode();
                _attached = true;
                return true;
            }
            catch
            {
                _attached = false;
                return false;
            }
        }

        /// <summary>
        /// 重新绑定 .NET 的标准流到刚接上的控制台。
        ///
        /// AttachConsole 之后，操作系统层面的标准句柄已经指向父控制台，
        /// 但 .NET 的 Console.Out / Console.In 是在进程启动时创建的，
        /// 仍然绑在原来那个不存在的控制台上，必须重建才生效。
        ///
        /// 编码要跟控制台当前的代码页对上：
        /// 中文系统上控制台通常是 936(GBK)，写 UTF-8 字节会显示成乱码。
        /// </summary>
        private static void RebuildStreams()
        {
            try
            {
                Encoding outEnc = GetOutputEncoding();
                Encoding inEnc = GetInputEncoding();

                // 让 .NET 知道控制台用的什么编码，
                // 否则它会用 UTF-8 写，父 cmd 那边按 GBK 解就成乱码了。
                try
                {
                    if (Console.OutputEncoding == null || Console.OutputEncoding.CodePage != outEnc.CodePage)
                    {
                        Console.OutputEncoding = outEnc;
                    }
                }
                catch
                {
                    // 有些环境改不了编码，忽略
                }

                var stdout = new StreamWriter(Console.OpenStandardOutput(), outEnc)
                {
                    AutoFlush = true
                };
                Console.SetOut(stdout);

                var stderr = new StreamWriter(Console.OpenStandardError(), outEnc)
                {
                    AutoFlush = true
                };
                Console.SetError(stderr);

                // 输入：Console.ReadLine 走的是 Console.In，
                // 重新打开一次让它绑到现在的控制台上
                Console.SetIn(new StreamReader(Console.OpenStandardInput(), inEnc));
            }
            catch
            {
                // 绑定失败就维持原样：总比整个程序起不来强
            }
        }

        /// <summary>
        /// 取控制台输出用的编码（跟控制台代码页一致）。
        /// </summary>
        private static Encoding GetOutputEncoding()
        {
            try
            {
                uint cp = GetConsoleOutputCP();
                if (cp != 0) return Encoding.GetEncoding((int)cp);
            }
            catch
            {
                // 下面兜底
            }
            try
            {
                return Console.OutputEncoding ?? Encoding.Default;
            }
            catch
            {
                return Encoding.Default;
            }
        }

        /// <summary>
        /// 取控制台输入用的编码（跟控制台代码页一致）。
        /// </summary>
        private static Encoding GetInputEncoding()
        {
            try
            {
                uint cp = GetConsoleCP();
                if (cp != 0) return Encoding.GetEncoding((int)cp);
            }
            catch
            {
                // 下面兜底
            }
            try
            {
                return Console.InputEncoding ?? Encoding.Default;
            }
            catch
            {
                return Encoding.Default;
            }
        }

        /// <summary>
        /// 让标准输出句柄支持换行处理。
        ///
        /// 附上父控制台之后，标准输出句柄可能处于"文本模式已关闭"的状态，
        /// 这时写 "\n" 不会自动回车，输出会连成一行。
        /// 这里把 ENABLE_PROCESSED_OUTPUT 和 ENABLE_WRAP_AT_EOL_OUTPUT 打开。
        /// </summary>
        public static void EnsureOutputMode()
        {
            try
            {
                IntPtr h = GetStdHandle(STD_OUTPUT_HANDLE);
                if (h == IntPtr.Zero || h == new IntPtr(-1)) return;

                uint mode;
                if (!GetConsoleMode(h, out mode)) return;

                mode |= ENABLE_PROCESSED_OUTPUT | ENABLE_WRAP_AT_EOL_OUTPUT;
                SetConsoleMode(h, mode);
            }
            catch
            {
                // 设置失败不影响输出
            }
        }
    }
}
