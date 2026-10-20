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

        private void Application_Startup(object sender, StartupEventArgs e)
        {
            // 命令行参数: 文件路径、模式开关、注册与注销
            ShellCommandService.Parse(e.Args, out List<string> paths, out string mode,
                out bool register, out bool unregister, out bool help, out bool setMainExePath);

            // 提权后只写主程序路径
            if (setMainExePath)
            {
                ShellRegistrationService.SetMainExePath();
                Shutdown();
                return;
            }

            // 注册 / 注销 / 显示用法, 都是命令行干完就退出
            if (register || unregister || help)
            {
                RunCommandLineOnly(register, unregister, help);
                return;
            }

            // 先清理过期日志，再做其余初始化
            LogService.CleanupOldLogs();

            // 已经有实例在跑就把文件交过去, 自己退出
            _singleInstance = new SingleInstanceService();
            if (!_singleInstance.TryAcquire())
            {
                if (paths.Count > 0)
                {
                    bool handed = SingleInstanceService.SendToRunningInstance(paths.ToArray());
                    if (!handed)
                    {
                        // 交不上(对方还没准备好), 只能自己接着做
                        RunFromCommandLine(paths.ToArray(), mode);
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
                RunFromCommandLine(paths.ToArray(), mode);
                return;
            }

            // 没有文件进来, 就是正常启动
            StartNormalMode();
        }

        /// <summary>
        /// 只做命令行的事, 完事就退出, 不弹界面。
        /// 注册要管理员权限, 权限不够时用 UAC 提权跑一次 regsvr32。
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

        /// <summary>命令行模式。不建主窗口, 任务在控制台里跑, 完事就退出。</summary>
        private void RunFromCommandLine(string[] paths, string mode)
        {
            var viewModel = new Auto7zViewModel();
            viewModel.LoadConfig();
            viewModel.ApplyLanguage();

            if (mode != null) viewModel.CurrentOperationMode = ParseMode(mode);

            var runner = new ConsoleTaskRunner(viewModel);
            runner.Run(paths, mode);

            // 任务跑完直接退出, 不留在后台。
            // 用 Shutdown 而不是 Environment.Exit, 让日志正常落盘。
            _singleInstance.Dispose();
            Shutdown();
            return;
        }

        /// <summary>
        /// 正常启动: 建主窗口并显示。
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
        /// 7-Zip 组件与配置都放在 %LOCALAPPDATA%\Auto7z, 程序目录本身不用可写,
        /// 这里只记日志, 不拦着启动。
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
            base.OnExit(e);
        }
    }
}
