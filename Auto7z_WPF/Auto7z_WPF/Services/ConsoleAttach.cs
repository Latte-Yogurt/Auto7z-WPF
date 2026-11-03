using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Auto7z_WPF.Services
{
    /// <summary>
    /// 控制台附加与标准流修复：进程以 Console 子系统启动，cmd 才会等待它结束，
    /// Y/N 确认与密码输入也才能留在用户自己的控制台里；双击运行时系统新建的控制台则要单独隐藏。
    /// </summary>
    public static class ConsoleAttach
    {
        #region 原生声明
        // 传 0xFFFFFFFF 表示附加到父进程的控制台，而不是按 PID 指定。
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

        #endregion
        #region 控制台窗口处理
        // 附加只尝试一次：AttachConsole 失败后重试也不会成功，反复尝试只会打乱输出时序。
        private static bool _attached;
        private static bool _tried;

        public static bool Attached => _attached;

        // 双击启动时系统为本进程新建的控制台可以安全隐藏并释放；
        // 控制台若还挂着别的进程（例如从 cmd 启动），关闭它会连带毁掉用户的命令行窗口，
        // 所以查询失败时宁可留下控制台，也不要误关别人的窗口。
        public static void HideOwnConsoleWindow()
        {
            try
            {
                IntPtr console = GetConsoleWindow();
                if (console == IntPtr.Zero) return;

                uint[] ids = new uint[8];
                uint count = GetConsoleProcessList(ids, (uint)ids.Length);
                // 进程数大于 1 说明这块控制台是共享的，动它之前必须先确认独占。
                if (count > 1) return;

                ShowWindow(console, SW_HIDE);
                FreeConsole();
            }
            catch
            {
                // 藏不掉就让它留着，总比程序起不来强
            }
        }

        #endregion
        #region 附加父进程控制台
        public static bool TryAttachToParent()
        {
            if (_tried) return _attached;
            _tried = true;

            try
            {
                // 本进程已经挂着控制台（继承自父进程或由系统新建）就直接复用，无需再次附加。
                if (GetConsoleWindow() != IntPtr.Zero)
                {
                    _attached = true;
                    EnsureOutputMode();
                    return true;
                }

                // 没有控制台时尝试附加父进程的控制台，成功才能与调用方的 cmd 共享输出与输入。
                if (!AttachConsole(ATTACH_PARENT_PROCESS))
                {
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

        #endregion
        #region 标准流与编码
        // AttachConsole 只决定进程挂在哪块控制台上，.NET 的 Console 流仍指向启动时的空句柄，
        // 必须重新打开标准句柄并绑定，否则后续输出与 Y/N 读取都会落空。
        private static void RebuildStreams()
        {
            try
            {
                // 编码跟随控制台当前代码页，用户改过 chcp 后中文才不会乱码。
                Encoding outEnc = GetOutputEncoding();
                Encoding inEnc = GetInputEncoding();

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

                // AutoFlush 让每行立刻写出，进程异常退出时不会丢掉最后几行提示。
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

                Console.SetIn(new StreamReader(Console.OpenStandardInput(), inEnc));
            }
            catch
            {
                // 绑定失败就维持原样：总比整个程序起不来强
            }
        }

        // 优先读控制台代码页，取不到才退回 .NET 当前编码。
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

        // 输入按输入代码页解码，避免密码里的非 ASCII 字符被解错。
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

        #endregion
        #region 输出模式
        // 附加得来的句柄可能没开 ENABLE_PROCESSED_OUTPUT，换行只下移不回行首，这里补齐模式位。
        public static void EnsureOutputMode()
        {
            try
            {
                IntPtr h = GetStdHandle(STD_OUTPUT_HANDLE);
                if (h == IntPtr.Zero || h == new IntPtr(-1)) return;

                uint mode;
                // 控制台可能已被关闭，读不到当前模式就不要盲写。
                if (!GetConsoleMode(h, out mode)) return;

                mode |= ENABLE_PROCESSED_OUTPUT | ENABLE_WRAP_AT_EOL_OUTPUT;
                SetConsoleMode(h, mode);
            }
            catch
            {
                // 设置失败不影响输出
            }
        }
        #endregion
    }
}
