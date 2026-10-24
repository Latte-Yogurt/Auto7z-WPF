using Auto7z_WPF.Models;
using Auto7z_WPF.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Windows;

namespace Auto7z_WPF
{
    /// <summary>
    /// 应用程序入口。负责启动前的可写性检查与主窗口创建。
    /// </summary>
    public partial class App : Application
    {
        /// <summary>单实例控制。</summary>
        private SingleInstanceService _singleInstance;

        static App()
        {
            // Auto7z.exe 是 Console 子系统，双击运行时 Windows 会在托管代码开始
            // 执行前就分配一个控制台窗口，应在托管代码中尽早把它隐藏掉，
            // 避免一闪而过的黑窗口。只有控制台独占时（双击启动）才隐藏；
            // 在 cmd 里启动时控制台是共享的（count > 1），不隐藏，避免把
            // 用户的 cmd 窗口关掉而导致输入/输出异常（即 Bug 2 的根因）。
            // Auto7zGUI.exe 是 GUI 子系统，根本没有控制台，这里直接返回。
            ConsoleAttach.HideOwnConsoleWindow();
        }

        private void Application_Startup(object sender, StartupEventArgs e)
        {

#if DEBUG
            // 测试注入入口（仅 Debug 构建；Release 里整段被条件编译掉）。
            // 与「把文件拖到主窗口」走同一条路径：拿到单实例锁就建主窗口并 EnqueueFiles，
            // 拿不到就说明已有实例在跑，把路径交给它 —— 与用户拖拽后的处理完全一致。
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


            // 命令行参数：文件路径、模式开关、注册与注销
            ShellCommandService.Parse(e.Args, out List<string> paths, out string mode,
                out bool register, out bool unregister, out bool help, out bool setMainExePath,
                out bool fromShell);

            // 两个可执行体的分工见 Auto7z_WPF.Gui.csproj：
            //   Auto7z.exe     命令行入口，只做命令行的事；
            //   Auto7zGUI.exe  图形入口，只做图形的事。
#if GUI_ENTRY
            // 图形入口没有控制台，也不接控制台：-h / -r / -ur 属于命令行入口，
            // 在这里一律不处理，免得图形进程往一个不存在的控制台里写东西。
            bool dragAndDrop = paths.Count > 0;
            ConsoleAttach.HideOwnConsoleWindow();
#else
            // 命令行模式：借父进程(cmd)的控制台。
            //
            // Auto7z.exe 是 Console 子系统，在 cmd 里启动时本来就有控制台，
            // 标准句柄是好的，不需要借；双击它时 Windows 会分配一个没人看的黑窗口，
            // 得藏掉 —— 判断依据是控制台里只挂着自己一个进程。
            //
            // 只在「用户自己敲命令」时接：
            //   - 右键菜单 / 拖拽模式不接，父进程是 explorer，
            //     它的控制台用户根本看不见；
            //   - 双击运行不接，本来也没有控制台。
            bool dragAndDrop = LaunchContext.IsDragAndDrop(paths.Count > 0);
            bool consoleMode = paths.Count > 0 && !fromShell && !dragAndDrop;

            if (consoleMode || help || register || unregister)
            {
                ConsoleAttach.TryAttachToParent();
            }
            else
            {
                // 非命令行模式：把程序自己分配的那个控制台藏掉，
                // 免得双击运行或右键菜单时凭空多一个黑窗口。
                ConsoleAttach.HideOwnConsoleWindow();
            }
#endif

            // 提权后只写主程序路径
            if (setMainExePath)
            {
                ShellRegistrationService.SetMainExePath();
                Shutdown();
                return;
            }

#if !GUI_ENTRY
            // 注册 / 注销 / 显示用法，都是命令行干完就退出
            if (register || unregister || help)
            {
                RunCommandLineOnly(register, unregister, help);
                return;
            }

            // 没有路径、也没有任何开关：这是「双击了命令行版本」。
            // 命令行入口不再拉起主窗口 —— 图形入口是 Auto7zGUI.exe。
            if (paths.Count == 0 && !fromShell)
            {
                ShellRegistrationService.WriteUsage();
                Shutdown(2);
                return;
            }
#endif

            // 先清理过期日志，再做其余初始化
            LogService.CleanupOldLogs();

            // 已经有实例在跑就把文件交过去，自己退出
            _singleInstance = new SingleInstanceService();
            if (!_singleInstance.TryAcquire())
            {
                if (paths.Count > 0)
                {
                    bool handed = SingleInstanceService.SendToRunningInstance(paths.ToArray());
                    if (!handed)
                    {
                        // 交不上（对方还没准备好），只能自己接着做
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
                // 右键菜单 与 拖拽到 exe：都用无主窗口模式
                // （拖拽的父进程也是 explorer，路径参数跟右键一样是直接给路径，
                //   区别只在于有没有 #共享内存 标记）
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

            // 没有文件进来，就是正常启动
            StartNormalMode();
        }

        /// <summary>
        /// 只做命令行的事，完事就退出，不弹界面。
        /// 注册要管理员权限，权限不够时用 UAC 提权跑一次 regsvr32。
        /// </summary>
        /// <param name="register">是否注册右键菜单。</param>
        /// <param name="unregister">是否注销右键菜单。</param>
        /// <param name="help">是否显示用法说明。</param>
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
                bool ok = ShellRegistrationService.RegisterWithElevation();
                if (ok)
                {
                    Console.WriteLine("右键菜单注册成功");
                }
                else
                {
                    Console.WriteLine("右键菜单注册失败, 请以管理员身份运行");
                }
                Shutdown();
                return;
            }

            if (unregister)
            {
                bool ok = ShellRegistrationService.UnregisterWithElevation();
                Console.WriteLine(ok ? "右键菜单已注销" : "右键菜单注销失败");
                Shutdown();
                return;
            }
        }

        /// <summary>
        /// 命令行模式。不建主窗口，任务在控制台里跑，完事就退出。
        ///
        /// 控制台看得见、也读得到输入，所以：进度写控制台，
        /// 多文件合并用 Y/N 问答，加密压缩包解压时在控制台提示输入密码。
        /// </summary>
        private void RunFromCommandLine(string[] paths, string mode)
        {
            var viewModel = new Auto7zViewModel();
            viewModel.LoadConfig();
            viewModel.ApplyLanguage();

            if (mode != null) viewModel.CurrentOperationMode = ParseMode(mode);

            // 输入路径一个都不存在时，命令行要明确报错并返回非 0。
            // 以前是静默丢弃、退出码 0，脚本与计划任务没法判断成败。
            viewModel.SetPendingFiles(paths);
            if (!viewModel.HasPendingFiles)
            {
                Console.WriteLine(EnumText.GetErrorText(
                    "Err_InputNotFound", "找不到指定的文件或目录：{0}", string.Join("; ", paths)));
                _singleInstance.Dispose();
                Shutdown(2);
                return;
            }

            var runner = new ConsoleTaskRunner(viewModel);
            runner.Run(paths, mode);

            // 任务跑完直接退出，不留在后台。
            // 用 Shutdown 而不是 Environment.Exit，让日志正常落盘。
            // 失败或取消返回非 0，调用方才能判断成败。
            _singleInstance.Dispose();
            Shutdown(viewModel.LastTaskFailed ? 1 : 0);
            return;
        }

        /// <summary>
        /// 右键菜单模式。永远不建主窗口。
        ///
        /// 从资源管理器右键进来时控制台是看不见的，读不到输入，
        /// 所以：进度用独立的进度窗口显示，需要确认的地方
        /// （合并询问、输入密码）弹小对话框。
        ///
        /// 绝不拉起主窗口 —— 右键压缩的预期是"点了就走"。
        /// </summary>
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

        /// <summary>
        /// 正常启动：建主窗口并显示。
        /// </summary>
        private void StartNormalMode()
        {
            var mainWindow = new MainWindow();
            Current.MainWindow = mainWindow;
            mainWindow.Show();

            // 其他实例送来的文件排进任务队列
            _singleInstance.StartListener(files => mainWindow.EnqueueFiles(files));
        }

        /// <summary>把命令行里的模式开关转成操作模式。</summary>
        private static OperationMode ParseMode(string mode)
        {
            if (string.Equals(mode, ShellCommandService.SwitchAuto, StringComparison.OrdinalIgnoreCase))
                return OperationMode.Auto;
            if (string.Equals(mode, ShellCommandService.SwitchUnZip, StringComparison.OrdinalIgnoreCase))
                return OperationMode.UnZip;
            return OperationMode.Zip;
        }

        /// <summary>
        /// 检查程序目录能不能写。
        /// 7-Zip 组件与配置都放在 %LOCALAPPDATA%\Auto7z，程序目录本身不用可写，
        /// 这里只记日志，不拦着启动。
        /// </summary>
        private void CheckInstallDirectory(string currentDir)
        {
            if (string.IsNullOrWhiteSpace(currentDir)) return;

            try
            {
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
                // 探测失败忽略
            }
        }

        /// <summary>
        /// 退出时释放单实例占用的资源。
        /// </summary>
        protected override void OnExit(ExitEventArgs e)
        {
            _singleInstance?.Dispose();

            // 右键菜单模式没有主窗口，关闭流程走不到 ViewModel.OnClosing，
            // 这里兜底结束 7z 子进程：否则程序退出后 7z.exe 仍在后台跑，
            // 既占着 CPU，也可能锁住还没删干净的半成品目录。
            SevenZipService.KillAllRunningProcesses();

            base.OnExit(e);
        }
    }
}
