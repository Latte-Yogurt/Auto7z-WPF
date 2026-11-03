using System;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Auto7z_WPF.Models;

namespace Auto7z_WPF.Services
{
    /// <summary>
    /// 右键菜单触发的任务执行器，只显示进度窗口，绝不会拉起主窗口。
    /// </summary>
    public class ShellMenuTaskRunner
    {
        private readonly Auto7zViewModel _viewModel;

        public ShellMenuTaskRunner(Auto7zViewModel viewModel)
        {
            _viewModel = viewModel;
        }

        #region 任务调度
        /// <summary>
        /// 在后台线程执行任务，同时用进度窗口占住前台。
        /// </summary>
        /// <param name="paths">待处理的路径。</param>
        /// <param name="mode">命令行开关，决定压缩、解压还是自动分派。</param>
        public void Run(string[] paths, string mode)
        {
            if (paths == null || paths.Length == 0) return;

            _viewModel.SetPendingFiles(paths);
            _viewModel.CurrentRunMode = RunMode.ShellMenu;

            // 从菜单点进来期望「点了就走」，只开进度窗口；完整主窗口会连带加载目录树等初始状态，既慢又突兀。
            var progress = new ProcessWindow();
            progress.Show();

            Exception failure = null;

            var frame = new DispatcherFrame();

            var worker = new Thread(() =>
            {
                try
                {
                    Action<ProgressInfo> report = info =>
                    {
                        if (info == null) return;
                        var app = Application.Current;
                        if (app == null) return;
                        // 进度回调来自后台线程，控件只能在 UI 线程上动，统一转投到 Dispatcher。
                        app.Dispatcher.BeginInvoke(new Action(() =>
                        {
                            // 7-Zip 的进度是流式吐出来的，百分比与阶段文字一起刷新才看得出还在动；
                            // 只写日志不刷界面，用户会以为程序卡死了。
                            progress.SetStatus(_viewModel.GetProgressDisplayText(info.Stage, info.Percent));
                            progress.SetProgress(info.Percent);
                            // 失败时把进度条清回去，免得停在半截看起来像卡在中途。
                            if (info.Stage == ProgressStage.Failed) progress.SetProgress(0);
                        }));
                    };

                    RunTask(mode, report);
                }
                catch (Exception ex)
                {
                    // 先存下来，等进度窗口关掉之后再写日志：
                    // 在窗口生命周期里抛出去会把 PushFrame 的等待一起打断。
                    failure = ex;
                }
                finally
                {
                    var app = Application.Current;
                    if (app != null)
                    {
                        // 后台线程结束时必须让 PushFrame 退出，否则 UI 线程会一直停在这条语句上。
                        app.Dispatcher.BeginInvoke(new Action(() => frame.Continue = false));
                    }
                }
            });

            // 非后台线程：任务跑完之前不能让进程提前退出，否则进度窗口会先消失。
            worker.IsBackground = false;
            worker.Start();

            var cancelTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(150)
            };
            cancelTimer.Tick += (s, e) =>
            {
                if (!progress.Cancelled) return;
                cancelTimer.Stop();
                _viewModel.CancelCurrentTask();
            };
            cancelTimer.Start();

            progress.Closing += (s, e) =>
            {
                if (progress.AllowClose) return;
                // 任务还没结束就点关闭时不能直接关窗，先转成取消请求，
                // 等后台线程真正收尾后再由下面的 AllowClose 放行。
                e.Cancel = true;
                progress.RequestCancel();
            };

            Dispatcher.PushFrame(frame);

            cancelTimer.Stop();

            progress.AllowClose = true;
            progress.Close();

            if (failure != null)
            {
                new LogService().WriteLog(
                    EnumText.GetErrorText("Log_TaskException", "任务执行异常：{0}", failure.Message),
                    LogService.ErrorLevel.Error);
            }
        }
        #endregion

        #region 模式分派
        private void RunTask(string mode, Action<ProgressInfo> report)
        {
            switch (mode)
            {
                case ShellCommandService.SwitchAuto:
                    _viewModel.RunAutoDispatchAndWait(report);
                    break;

                case ShellCommandService.SwitchUnZip:
                    _viewModel.RunExtractAndWait(report);
                    break;

                case ShellCommandService.SwitchZip:
                    _viewModel.RunCompressAndWait(report);
                    break;

                default:
                    // 菜单没有传开关时按自动模式走，与命令行不带选项时的行为保持一致。
                    _viewModel.RunAutoDispatchAndWait(report);
                    break;
            }
        }
        #endregion
    }
}
