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
                out string unknownSwitch);

            // 两个可执行体的分工是编译期定死的：Auto7zGUI.exe 没有控制台，
            // 也就没有「借父控制台」这回事；Auto7z.exe 才需要按启动方式决定接不接控制台。
#if GUI_ENTRY
            bool dragAndDrop = paths.Count > 0;
            ConsoleAttach.HideOwnConsoleWindow();
#else
            // 拖拽进来的父进程是 explorer，它的控制台用户看不见；双击时本来也没有控制台。
            // 只有「用户自己敲命令」这一种才值得把父控制台接过来。
            bool dragAndDrop = LaunchContext.IsDragAndDrop(paths.Count > 0);
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

            #if !GUI_ENTRY
            if (register || unregister || help)
            {
                RunCommandLineOnly(register, unregister, help);
                return;
            }

            // 拼错/未知的开关必须报出来并以退出码 2 结束：
            // 静默忽略会让脚本把「参数写错了」误判成「任务成功」，那是比失败更坏的信号。
            // 图形入口不认这些开关（`-h`/`-r`/`-ur` 都归 Auto7z.exe），所以只在这条编译分支里报。
            if (unknownSwitch != null)
            {
                Console.WriteLine(EnumText.GetErrorText(
                    "Err_UnknownSwitch", "无法识别的选项：{0}", unknownSwitch));
                ShellRegistrationService.WriteUsage();
                Shutdown(2);
                return;
            }

            // 命令行入口不再拉起主窗口：图形入口是 Auto7zGUI.exe，
            // 这里只打印用法并以退出码 2 结束，脚本才分得清「没跑」和「跑失败」。
            if (paths.Count == 0 && !fromShell)
            {
                ShellRegistrationService.WriteUsage();
                Shutdown(2);
                return;
            }
#endif

            LogService.CleanupOldLogs();

            _singleInstance = new SingleInstanceService();
            if (!_singleInstance.TryAcquire())
            {
                if (paths.Count > 0)
                {
                    bool handed = SingleInstanceService.SendToRunningInstance(paths.ToArray());
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

        private void RunCommandLineOnly(bool register, bool unregister, bool help)
        {
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
            _singleInstance.StartListener(files => mainWindow.EnqueueFiles(files));
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
