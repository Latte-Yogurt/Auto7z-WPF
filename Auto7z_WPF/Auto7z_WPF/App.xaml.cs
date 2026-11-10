using Auto7z_WPF.Models;
using Auto7z_WPF.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Windows;

namespace Auto7z_WPF
{
    public partial class App : Application
    {
        private SingleInstanceService _singleInstance;

        static App()
        {
            // 静态构造在托管入口最早执行：Auto7z.exe 是 Console 子系统，
            // 双击它时 Windows 在托管代码运行前就分配好了黑窗口，越晚隐藏越可能看见闪烁。
            ConsoleAttach.HideOwnConsoleWindow();
        }

        private void Application_Startup(object sender, StartupEventArgs e)
        {

#if DEBUG
            // 测试注入只存在于 Debug 构建；它故意复用「拖入主窗口」之后的同一条路径，
            // 所以测到的是真实的入队逻辑，而不是另开一条只有测试才走的旁路。
            if (TestHooks.TryParseFeed(e.Args, out string[] feedPaths))
            {
                LogService.CleanupOldLogs();
                _singleInstance = new SingleInstanceService();
                if (!_singleInstance.TryAcquire())
                {
                    SingleInstanceService.SendToRunningInstance(feedPaths);
                    Shutdown();
                    return;
                }

                StartNormalMode();
                (Current.MainWindow as MainWindow)?.EnqueueFiles(feedPaths);
                return;
            }
#endif


            ShellCommandService.Parse(e.Args, out List<string> paths, out string mode,
                out bool register, out bool unregister, out bool help, out bool fromShell,
                out List<string> unknownSwitches);

            // 两个可执行体由同一份源码编译，所以「命令行管道」这段逻辑在两边都存在。
            // 差别只有一个：Auto7z.exe 是 Console 子系统，天然带控制台；
            // Auto7zGUI.exe 没有控制台，需要先借到父进程（cmd / PowerShell）的那一块。
            //
            // 因此不必起子进程去"移交"，只要在进程内先把控制台准备好，再走同一条管道。
            // 进程内路由的好处：没有第二个进程、没有参数转义、没有输出编码二次转录，
            // 退出码也天然就是本进程的。
            //
            // 「带开关」未必是命令行语义 —— 外壳扩展传进来的参数长这样：
            //     #映射名:字节数:事件名 -uz
            // 也就是「共享内存路径 + 模式开关」。它当然带开关，但它要的是
            // 无主窗口的小进度窗（ShellMenuTaskRunner），不是命令行管道。
            //
            // 所以正确判据是：来自外壳扩展（fromShell）或拖拽（dragAndDrop）一律走无主窗口流程，
            // 只有「用户真的在敲命令」才算命令行语义。曾经漏掉这一条，导致右键菜单
            // 既不出进度窗、又把模式开关当成普通开关塞进命令行管道，两个菜单项都变成压缩。

#if GUI_ENTRY
            // 图形入口的拖拽判据：有路径就算（父进程是 explorer 即拖拽；
            // 从快捷方式带路径启动同样期望无主窗口流程）。
            bool dragAndDrop = paths.Count > 0;
#else
            // 命令行入口：父进程是 explorer 才算拖拽 —— 用户自己敲命令时不该走无主窗口流程。
            bool dragAndDrop = LaunchContext.IsDragAndDrop(paths.Count > 0);
#endif

            bool shellMenuSemantics = fromShell || dragAndDrop;

            bool hasSwitch = unknownSwitches.Count > 0 || help || register || unregister
                             || mode != null;

            // 未知开关是例外：无论从哪来都要拒绝，不能让它悄悄变成一种模式。
            bool mustRejectUnknown = unknownSwitches.Count > 0;

#if GUI_ENTRY
            // 图形入口：
            //   - 外壳扩展 / 拖拽 → 无主窗口流程（不借控制台，也不开主窗口）
            //   - 其余带开关      → 命令行管道（先借父控制台）
            //   - 无参数          → 主窗口
            bool consoleSemantics = mustRejectUnknown
                                    || (!shellMenuSemantics && (hasSwitch || help || register || unregister));

            if (consoleSemantics) ConsoleAttach.TryAttachToParent();
            else ConsoleAttach.HideOwnConsoleWindow();
#else
            // 命令行入口：除了外壳扩展，其余一律命令行语义。
            bool consoleSemantics = !shellMenuSemantics || mustRejectUnknown;

            // 拖拽进来的父进程是 explorer，它的控制台用户看不见；双击时本来也没有控制台。
            // 只有「用户自己敲命令」这一种才值得把父控制台接过来。
            bool consoleMode = paths.Count > 0 && !fromShell && !dragAndDrop;

            if (consoleMode || help || register || unregister)
            {
                ConsoleAttach.TryAttachToParent();
            }
            else
            {
                // 隐藏自己那个控制台，免得双击或右键时凭空多一个黑窗口。
                ConsoleAttach.HideOwnConsoleWindow();
            }
#endif

            // 便携状态必须在任何会碰外壳扩展的分支之前定下来：
            // -r / -ur 在下面几行就要用到，命令行任务也可能会去写语言键。
            // 判据只看磁盘（程序目录旁有没有 .config\Auto7z），不读配置，避免自相矛盾。
            AppPaths.IsPortable = AppPaths.DetectPortableFromDisk();
            ShellRegistrationService.UnregisterQuietlyIfPortable();

            // 命令行语义一律在这条分支上处理 —— 两个入口共用，不再各写一份。
            // 未知开关必须报错并以退出码 2 结束：静默忽略会让调用方把「参数写错」
            // 误判成「任务成功」，那比失败更坏。
            if (consoleSemantics)
            {
                RunConsolePipeline(paths.ToArray(), mode, register, unregister, help, fromShell,
                    unknownSwitches, dragAndDrop);
                return;
            }

            LogService.CleanupOldLogs();

            _singleInstance = new SingleInstanceService();
            if (!_singleInstance.TryAcquire())
            {
                if (paths.Count > 0)
                {
                    // 带上模式：右键解压的 -uz 不能在交接这一步丢掉。
                    bool handed = SingleInstanceService.SendToRunningInstance(paths.ToArray(), mode);
                    if (!handed)
                    {
                        // 已有实例还没把监听管道挂上，交接必然失败。
                        // 这时宁可自己接着做，也不能把用户选中的文件丢掉。
                        if (fromShell || dragAndDrop)
                        {
                            RunFromShellMenu(paths.ToArray(), mode);
                        }
                        else
                        {
                            RunFromCommandLine(paths.ToArray(), mode);
                        }
                        return;
                    }
                }
                Shutdown();
                return;
            }

            string currentDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            CheckInstallDirectory(currentDir);

            if (paths.Count > 0)
            {
                // 共享内存标记与拖拽都归到无主窗口模式：两者都是「点了就走」的预期，
                // 弹出完整主窗口反而突兀。
                if (fromShell || dragAndDrop)
                {
                    RunFromShellMenu(paths.ToArray(), mode);
                }
                else
                {
                    RunFromCommandLine(paths.ToArray(), mode);
                }
                return;
            }

            StartNormalMode();
        }

        /// <summary>
        /// 命令行管道的唯一实现，两个可执行体共用。
        ///
        /// 处理顺序与退出码契约严格对应：
        ///   2 = 根本没跑（未知开关 / 参数缺失）
        ///   0 = 用法打印成功，或任务全部成功
        ///   1 = 任务跑了但失败或被取消
        ///
        /// 图形入口进入这里之前已经借到了父控制台，所以 Console.WriteLine 能落到用户的窗口里；
        /// 借不到（双击启动、父进程是 explorer）时输出会落空，但退出码依旧正确 ——
        /// 这正是脚本需要的那个信号。
        /// </summary>
        /// <param name="paths">解析出的待处理路径。</param>
        /// <param name="mode">模式开关（-a / -z / -uz），未指定为 null。</param>
        /// <param name="register">是否要求注册右键菜单。</param>
        /// <param name="unregister">是否要求注销右键菜单。</param>
        /// <param name="help">是否要求打印用法。</param>
        /// <param name="fromShell">参数是否来自共享内存（右键菜单）。</param>
        /// <param name="unknownSwitches">无法识别的开关，非空即拒绝。</param>
        /// <param name="dragAndDrop">是否由 explorer 拖拽启动。</param>
        private void RunConsolePipeline(string[] paths, string mode, bool register, bool unregister,
            bool help, bool fromShell, List<string> unknownSwitches, bool dragAndDrop)
        {
            // 一次把全部未知开关都报出来：只报第一个的话，用户改掉它还会再撞上下一个。
            if (unknownSwitches.Count > 0)
            {
                Console.WriteLine(EnumText.GetErrorText(
                    "Err_UnknownSwitch", "无法识别的选项：{0}", string.Join(" ", unknownSwitches)));
                ShellRegistrationService.WriteUsage();
                Shutdown(2);
                return;
            }

            if (help)
            {
                ShellRegistrationService.WriteUsage();
                Shutdown();
                return;
            }

            if (register)
            {
                ShellRegistrationService.WriteRegistrationResult(
                    ShellRegistrationService.RegisterWithElevation(), registering: true);
                Shutdown();
                return;
            }

            if (unregister)
            {
                ShellRegistrationService.WriteRegistrationResult(
                    ShellRegistrationService.UnregisterWithElevation(), registering: false);
                Shutdown();
                return;
            }

            // 只剩模式开关、没有路径：没有可处理的东西，按「根本没跑」处理。
            if (paths.Length == 0 && !fromShell)
            {
                ShellRegistrationService.WriteUsage();
                Shutdown(2);
                return;
            }

            // 真正要干活之前的收尾：与主流程一致，先清旧日志、再拿单实例。
            // 顺序不能反：RunFromCommandLine 结束时会对 _singleInstance 做 Dispose，
            // 这里不先把它创建出来的话，命令行任务一跑完就会撞上空引用。
            LogService.CleanupOldLogs();

            _singleInstance = new SingleInstanceService();
            if (!_singleInstance.TryAcquire())
            {
                // 交接必须带上模式开关：否则已在运行的实例会按自己的模式处理这批路径，
                // 「右键解压」就会变成压缩。
                bool handed = SingleInstanceService.SendToRunningInstance(paths, mode);
                if (!handed)
                {
                    // 已有实例还没把监听管道挂上，交接必然失败。
                    // 宁可自己接着做，也不能把用户选中的文件丢掉。
                    RunFromCommandLine(paths, mode);
                    return;
                }

                Shutdown();
                return;
            }

            RunFromCommandLine(paths, mode);
        }

        private void RunFromCommandLine(string[] paths, string mode)
        {
            var viewModel = new Auto7zViewModel();
            viewModel.LoadConfig();
            viewModel.ApplyLanguage();

            if (mode != null) viewModel.CurrentOperationMode = ParseMode(mode);

            viewModel.SetPendingFiles(paths);
            if (!viewModel.HasPendingFiles)
            {
                // 输入路径一个都不存在时必须报错并返回非 0：静默丢弃会让脚本
                // 与计划任务无法从退出码判断到底跑没跑。
                Console.WriteLine(EnumText.GetErrorText(
                    "Err_InputNotFound", "找不到指定的文件或目录：{0}", string.Join("; ", paths)));
                _singleInstance.Dispose();
                Shutdown(2);
                return;
            }

            var runner = new ConsoleTaskRunner(viewModel);
            runner.Run(paths, mode);

            // 用 Shutdown 而不是 Environment.Exit：日志与配置才有机会正常落盘。
            _singleInstance.Dispose();
            Shutdown(viewModel.LastTaskFailed ? 1 : 0);
            return;
        }

        private void RunFromShellMenu(string[] paths, string mode)
        {
            var viewModel = new Auto7zViewModel();
            viewModel.LoadConfig();
            viewModel.ApplyLanguage();

            if (mode != null) viewModel.CurrentOperationMode = ParseMode(mode);

            var runner = new ShellMenuTaskRunner(viewModel);
            runner.Run(paths, mode);

            _singleInstance.Dispose();
            Shutdown();
            return;
        }

        private void StartNormalMode()
        {
            var mainWindow = new MainWindow();
            Current.MainWindow = mainWindow;
            mainWindow.Show();

            // 监听必须等主窗口建好再挂：第二个实例随时可能把文件列表送过来，
            // 而此时队列的接收方还不存在。
            //
            // 交接里带着模式：右键「解压」送进来的 -uz 必须在接收方也生效，
            // 否则界面停在压缩模式时，这批路径会被压成包。
            _singleInstance.StartListener(
                (files, handedMode) => mainWindow.EnqueueFiles(files, handedMode));
        }

        private static OperationMode ParseMode(string mode)
        {
            if (string.Equals(mode, ShellCommandService.SwitchAuto, StringComparison.OrdinalIgnoreCase))
                return OperationMode.Auto;
            if (string.Equals(mode, ShellCommandService.SwitchUnZip, StringComparison.OrdinalIgnoreCase))
                return OperationMode.UnZip;
            return OperationMode.Zip;
        }

        private void CheckInstallDirectory(string currentDir)
        {
            if (string.IsNullOrWhiteSpace(currentDir)) return;

            try
            {
                // 只记日志不拦启动：组件与配置都写在 %LOCALAPPDATA%\Auto7z，
                // 程序目录不可写并不影响功能，装在 Program Files 下反而是常态。
                if (!FileSystemHelper.IsDirectoryWritable(currentDir))
                {
                    new LogService().WriteLog(
                        EnumText.GetErrorText("Log_InstallDirNotWritable",
                            "程序所在目录不可写（不影响运行，组件与配置均写入 %LOCALAPPDATA%\\Auto7z）：{0}", currentDir),
                        LogService.ErrorLevel.Warning);
                }
            }
            catch
            {
                // 可写性探测本身失败与「不可写」是两回事，这里既已降级为提示，就不必再报。
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _singleInstance?.Dispose();

            // 右键菜单模式没有主窗口，走不到 ViewModel.OnClosing，只能在这里兜底：
            // 否则程序退出后 7z.exe 仍在后台跑，既占 CPU 也会锁住没删干净的半成品目录。
            SevenZipService.KillAllRunningProcesses();

            base.OnExit(e);
        }
    }
}
