using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Auto7z_WPF.Models;

namespace Auto7z_WPF.Services
{
    /// <summary>
    /// 命令行模式的任务执行。
    ///
    /// 用户在 cmd 里带参数启动：信息写到控制台，多文件合并用 Y/N 问答，
    /// 加密压缩包解压时在控制台提示输入密码。
    /// 任务跑完进程就退出。
    ///
    /// 注意与右键菜单模式的区别：右键菜单模式下控制台看不见，
    /// 所以那边用对话框问、用进度窗口显示进度。
    /// </summary>
    public class ConsoleTaskRunner
    {
        private readonly Auto7zViewModel _viewModel;

        public ConsoleTaskRunner(Auto7zViewModel viewModel)
        {
            _viewModel = viewModel;
        }

        /// <summary>
        /// 跑一个任务，跑完返回。
        /// </summary>
        /// <param name="paths">要处理的路径。</param>
        /// <param name="mode">模式开关：-A 自动、-Z 压缩、-U 解压。</param>
        public void Run(string[] paths, string mode)
        {
            if (paths == null || paths.Length == 0) return;

            _viewModel.SetPendingFiles(paths);
            _viewModel.CurrentRunMode = RunMode.Console;

            switch (mode)
            {
                case ShellCommandService.SwitchAuto:
                    _viewModel.RunAutoDispatchAndWait();
                    break;

                case ShellCommandService.SwitchUnZip:
                    _viewModel.RunExtractAndWait();
                    break;

                case ShellCommandService.SwitchZip:
                    _viewModel.RunCompressAndWait();
                    break;

                default:
                    // 没给模式开关就用自动模式，跟把文件拖进窗口一样：
                    // 压缩包解开，其余的压起来
                    _viewModel.RunAutoDispatchAndWait();
                    break;
            }
        }
    }
}
