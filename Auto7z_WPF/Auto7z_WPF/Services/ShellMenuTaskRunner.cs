using System;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Auto7z_WPF.Models;

namespace Auto7z_WPF.Services
{
    /// <summary>
    /// 右键菜单模式的任务执行。
    ///
    /// 从资源管理器右键进来时不建主窗口：任务跑完进程就退出。
    ///
    /// 与命令行模式的区别：
    ///   命令行模式 —— 控制台看得见，信息写控制台，多文件用 Y/N 问答
    ///   右键菜单模式 —— 控制台看不见，用一个独立的进度窗口显示进度，
    ///                   需要确认时弹小对话框（合并询问、输入密码）
    ///
    /// 进度窗口用的是 ProcessWindow，不是主窗口：
    /// 右键压缩的预期是"点了就走"，冒出整个主窗口很突兀。
    ///
    /// 线程模型
    /// --------
    /// 任务本体在**后台线程**上跑，界面线程只负责调度与等待。
    ///
    /// 之前是在界面线程上同步跑的 —— 压缩一个几十兆的目录要好几秒，
    /// 界面线程被占满的结果是：
    ///   - 进度条不动、状态文字不变（进度回调是 BeginInvoke 投递的，
    ///     界面线程不空出来就永远排在队列里）
    ///   - 窗口整个卡住，标题栏、关闭按钮都点不动
    ///   - 弹对话框也弹不出来（要等界面线程）
    ///
    /// 等待任务结束用的是**嵌套的 DispatcherFrame**，
    /// 不是 Thread.WaitOne / EventWaitHandle.WaitOne ——
    /// 后者在界面线程上会把消息泵整个卡住，窗口照样点不动。
    /// 嵌套消息循环（跟 MessageService 弹对话框用的是同一套机制）
    /// 会继续处理重绘与输入消息，所以进度条能刷新、窗口能拖动。
    /// </summary>
    public class ShellMenuTaskRunner
    {
        private readonly Auto7zViewModel _viewModel;

        public ShellMenuTaskRunner(Auto7zViewModel viewModel)
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
            _viewModel.CurrentRunMode = RunMode.ShellMenu;

            // 进度窗口：整个任务期间开着，任务结束或取消后关掉。
            // Window 不是 IDisposable，不能用 using，只能自己保证关掉。
            var progress = new ProcessWindow();
            progress.Show();

            // 任务结束信号。界面线程不直接阻塞等它，
            // 而是用嵌套消息循环等 —— 期间界面照常响应。
            Exception failure = null;

            // 嵌套消息循环。任务结束时由后台线程投递一个动作把循环退出掉。
            var frame = new DispatcherFrame();

            var worker = new Thread(() =>
            {
                try
                {
                    // 进度回调从后台线程过来，要切回界面线程才能碰 WPF 控件。
                    Action<ProgressInfo> report = info =>
                    {
                        if (info == null) return;
                        var app = Application.Current;
                        if (app == null) return;
                        app.Dispatcher.BeginInvoke(new Action(() =>
                        {
                            progress.SetStatus(_viewModel.GetStageText(info.Stage));
                            progress.SetProgress(info.Percent);
                            if (info.Stage == ProgressStage.Failed) progress.SetProgress(0);
                        }));
                    };

                    RunTask(mode, report);
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
                finally
                {
                    // 让界面线程退出嵌套消息循环。
                    // 投递而不是直接设 —— frame.Continue 必须在界面线程上改。
                    var app = Application.Current;
                    if (app != null)
                    {
                        app.Dispatcher.BeginInvoke(new Action(() => frame.Continue = false));
                    }
                }
            });

            // 前台线程：进程不会在任务没跑完时提前退出。
            worker.IsBackground = false;
            worker.Start();

            // 取消轮询：进度窗口的取消 / 关闭按钮只置一个标志，
            // 真正的取消要由界面线程转成 CancellationToken 才能传到 7-Zip。
            // 之前没人读这个标志 —— 点取消、点关闭都没有反应，任务照跑、窗口不动。
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

            // 标题栏的 X（Alt+F4）不许直接关：先转成取消请求，
            // 否则窗口没了、7z 还在跑，压到一半的安全目录也没人清。
            progress.Closing += (s, e) =>
            {
                if (progress.AllowClose) return;
                e.Cancel = true;
                progress.RequestCancel();
            };

            // 界面线程用嵌套消息循环等任务结束。
            // 期间消息泵继续转 —— 进度条能刷新、窗口能拖动、能弹对话框。
            Dispatcher.PushFrame(frame);

            cancelTimer.Stop();

            // 让界面线程把最后的进度显示出来再关窗
            progress.AllowClose = true;
            progress.Close();

            if (failure != null)
            {
                new LogService().WriteLog(
                    EnumText.GetErrorText("Log_TaskException", "任务执行异常：{0}", failure.Message),
                    LogService.ErrorLevel.Error);
            }
        }

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
                    // 没给模式开关就用自动模式，跟把文件拖进窗口一样：
                    // 压缩包解开，其余的压起来
                    _viewModel.RunAutoDispatchAndWait(report);
                    break;
            }
        }
    }
}
