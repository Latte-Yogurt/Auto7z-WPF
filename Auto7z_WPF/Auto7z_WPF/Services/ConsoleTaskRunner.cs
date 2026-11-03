using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Auto7z_WPF.Models;

namespace Auto7z_WPF.Services
{
    /// <summary>
    /// 命令行触发的任务执行器，进度直接写控制台，不涉及任何窗口。
    /// </summary>
    public class ConsoleTaskRunner
    {
        private readonly Auto7zViewModel _viewModel;

        public ConsoleTaskRunner(Auto7zViewModel viewModel)
        {
            _viewModel = viewModel;
        }

        /// <summary>按开关执行任务，进度输出到控制台。</summary>
        /// <param name="paths">待处理的路径。</param>
        /// <param name="mode">命令行开关。</param>
        public void Run(string[] paths, string mode)
        {
            if (paths == null || paths.Length == 0) return;

            // 一律调用同步版本：进程要等任务做完才好用退出码回报结果，
            // 异步版本会让 Main 提前返回，脚本随后读到的退出码就没意义了。
            _viewModel.SetPendingFiles(paths);
            // 置成 Console 而不是默认值：调用方靠它决定提示信息往控制台写还是往窗口里塞。
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
                    // 不带开关时按自动模式处理，与用法说明里的默认行为一致。
                    _viewModel.RunAutoDispatchAndWait();
                    break;
            }
        }
    }
}
