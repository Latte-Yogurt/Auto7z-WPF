using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Auto7z_WPF.Services
{
    /// <summary>
    /// 消息提示服务。
    /// 负责在界面线程上弹出提示框，并返回用于写日志的文本。
    ///
    /// 对话框一律用 WPF 窗口而不是 MessageBox：
    /// MessageBox 是 Win32 模态框，在 WPF 的 Dispatcher 回调里调用会与 WPF 的消息循环互锁，
    /// 表现为弹窗显示得出但点击无响应，任务一直卡住。
    /// </summary>
    public class MessageService
    {
        private const string ErrorTitleKey = "ErrorTitle";
        private const string WarningTitleKey = "WarningTitle";
        private const string ErrorHeadingKey = "ErrorMessage";
        private const string WarningHeadingKey = "WarningMessage";

        /// <summary>错误提示。</summary>
        public void ShowError(string detail, out string logMessage)
        {
            logMessage = Compose(ErrorHeadingKey, "Error: ", detail);
            Show(logMessage, ErrorTitleKey, "Error");
        }

        /// <summary>警告提示。</summary>
        public void ShowWarning(string detail, out string logMessage)
        {
            logMessage = Compose(WarningHeadingKey, "Warning: ", detail);
            Show(logMessage, WarningTitleKey, "Warning");
        }

        /// <summary>
        /// 询问式提示（是/否）。
        /// </summary>
        /// <param name="detail">提示内容。</param>
        /// <param name="logMessage">用于写日志的文本。</param>
        /// <returns>用户点“是”返回 true；点“否”或直接关闭返回 false。</returns>
        public bool AskYesNo(string detail, out string logMessage)
        {
            logMessage = detail;

            var app = Application.Current;
            if (app == null) return false;

            // 应用正在退出时不再弹窗
            if (app.Dispatcher.HasShutdownStarted || app.Dispatcher.HasShutdownFinished) return false;

            bool confirmed = false;

            ShowDialogAndWait(
                app,
                () =>
                {
                    var window = new MessageWindow { Owner = app.MainWindow };
                    window.ApplyYesNoButtons();
                    window.SetMessage(detail);
                    window.Closed += (s, e) => confirmed = window.Confirmed;
                    return window;
                });

            return confirmed;
        }

        /// <summary>
        /// 显示提示框。只有“确定”一个按钮，用于错误与警告。
        /// </summary>
        private static void Show(string message, string titleKey, string titleFallback)
        {
            var app = Application.Current;
            if (app == null) return;

            // 应用正在退出时不再弹窗，否则会阻塞关闭流程
            if (app.Dispatcher.HasShutdownStarted || app.Dispatcher.HasShutdownFinished) return;

            ShowDialogAndWait(
                app,
                () =>
                {
                    var window = new MessageWindow { Owner = app.MainWindow };
                    window.SetTitle(EnumText.GetResourceText(titleKey, titleFallback));

                    // 单按钮模式：正文换成提示内容，只留“确定”
                    window.ApplyOkButton();
                    window.SetMessage(message);

                    return window;
                });
        }

        /// <summary>
        /// 在界面线程上显示一个对话框，等它关闭后返回。
        ///
        /// 对话框用 Show() 显示, 外面套一个嵌套的 DispatcherFrame 等它关掉。
        ///
        /// 不用 Dispatcher.Invoke 等待：
        /// Invoke 会把界面线程的调度优先级压到 Send，
        /// 渲染和输入消息都排不上队，表现为弹窗显示得出却点不动。
        /// DispatcherFrame 以正常优先级跑嵌套循环，界面在弹窗期间依旧能正常响应。
        ///
        /// 主窗口开启“窗口置顶”时会把对话框盖住，弹窗期间临时取消置顶，关闭后恢复。
        ///
        /// <param name="app">应用程序对象。</param>
        /// <param name="createWindow">在界面线程上创建对话框。</param>
        private static void ShowDialogAndWait(Application app, Func<Window> createWindow)
        {
            // 后台线程在这里等，界面线程跑嵌套循环
            var completed = new ManualResetEventSlim(false);

            Action show = () =>
            {
                // MainWindow 只能在界面线程上取
                Window main = app.MainWindow;
                bool wasTopmost = false;

                try
                {
                    if (main != null && main.Topmost)
                    {
                        wasTopmost = true;
                        main.Topmost = false;
                    }

                    Window window = createWindow();

                    window.Closed += (s, e) =>
                    {
                        if (wasTopmost && main != null)
                        {
                            main.Topmost = true;
                        }
                    };

                    window.Show();

                    // 跑嵌套循环直到窗口关闭
                    var frame = new DispatcherFrame();
                    window.Closed += (s, e) => frame.Continue = false;
                    Dispatcher.PushFrame(frame);
                }
                catch
                {
                    // 退出过程中窗口创建失败
                }
                finally
                {
                    completed.Set();
                }
            };

            try
            {
                if (app.Dispatcher.CheckAccess())
                {
                    show();
                }
                else
                {
                    // 用 BeginInvoke 而不是 Invoke:
                    // Invoke 会把界面线程的优先级压到 Send，渲染与输入都排不上队，
                    // 弹窗会显示得出但点不动。
                    app.Dispatcher.BeginInvoke(show);
                }

                completed.Wait();
            }
            catch
            {
                // 退出过程中封送失败，忽略
            }
            finally
            {
                completed.Dispose();
            }
        }

        private static string Compose(string headingKey, string headingFallback, string detail)
        {
            string heading = EnumText.GetResourceText(headingKey, headingFallback);
            return string.IsNullOrWhiteSpace(detail) ? heading : heading + detail;
        }
    }
}
