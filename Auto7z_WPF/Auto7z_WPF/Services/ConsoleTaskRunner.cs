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
    /// 从资源管理器右键进来时不需要界面: 进度直接打到控制台,
    /// 合并压缩包这种需要确认的事也在控制台里问,
    /// 任务跑完进程就退出。
    ///
    /// 多文件压缩要问"是否合并"。界面模式下是弹 MessageWindow,
    /// 这里改成控制台问答, 免得为了问一句话就把主窗口拉起来。
    /// </summary>
    public class ConsoleTaskRunner
    {
        private readonly Auto7zViewModel _viewModel;

        public ConsoleTaskRunner(Auto7zViewModel viewModel)
        {
            _viewModel = viewModel;
        }

        /// <summary>
        /// 跑一个任务, 跑完返回。
        /// </summary>
        /// <param name="paths">要处理的路径。</param>
        /// <param name="mode">模式开关：-A 自动、-Z 压缩、-U 解压。</param>
        public void Run(string[] paths, string mode)
        {
            if (paths == null || paths.Length == 0) return;

            _viewModel.SetPendingFiles(paths);
            _viewModel.ConsoleProgressEnabled = true;

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
                    // 没给模式开关就用自动模式, 跟把文件拖进窗口一样:
                    // 压缩包解开, 其余的压起来
                    _viewModel.RunAutoDispatchAndWait();
                    break;
            }
        }
    }
}
