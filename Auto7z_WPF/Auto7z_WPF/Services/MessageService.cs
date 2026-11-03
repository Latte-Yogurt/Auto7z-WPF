using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Auto7z_WPF.Services
{
    /// <summary>
    /// 统一的提示框入口，负责把消息同步地显示出来并把日志文案回传给调用方。
    /// </summary>
    public class MessageService
    {
        #region 资源键与提示框
        private const string ErrorTitleKey = "ErrorTitle";
        private const string WarningTitleKey = "WarningTitle";
        private const string ErrorHeadingKey = "ErrorMessage";
        private const string WarningHeadingKey = "WarningMessage";

        /// <summary>显示错误提示，并通过 <paramref name="logMessage"/> 交回可写入日志的整段文案。</summary>
        /// <param name="detail">错误详情。</param>
        /// <param name="logMessage">拼好的日志文案。</param>
        public void ShowError(string detail, out string logMessage)
        {
            logMessage = Compose(ErrorHeadingKey, "Error: ", detail);
            Show(logMessage, ErrorTitleKey, "Error");
        }

        /// <summary>显示警告提示，并通过 <paramref name="logMessage"/> 交回可写入日志的整段文案。</summary>
        /// <param name="detail">警告详情。</param>
        /// <param name="logMessage">拼好的日志文案。</param>
        public void ShowWarning(string detail, out string logMessage)
        {
            logMessage = Compose(WarningHeadingKey, "Warning: ", detail);
            Show(logMessage, WarningTitleKey, "Warning");
        }

        /// <summary>弹出是/否询问框并阻塞等待用户选择。</summary>
        /// <param name="detail">询问内容。</param>
        /// <param name="logMessage">回传的日志文案。</param>
        /// <returns>用户选了「是」返回 true。</returns>
        public bool AskYesNo(string detail, out string logMessage)
        {
            logMessage = detail;

            var app = Application.Current;
            if (app == null) return false;

            if (app.Dispatcher.HasShutdownStarted || app.Dispatcher.HasShutdownFinished) return false;

            bool confirmed = false;

            ShowDialogAndWait(
                app,
                () =>
                {
                    var window = new MessageWindow { Owner = ResolveOwner(app) };
                    window.ApplyYesNoButtons();
                    window.SetMessage(detail);
                    window.Closed += (s, e) => confirmed = window.Confirmed;
                    return window;
                });

            return confirmed;
        }
        #endregion

        #region 窗体归属
        private static Window ResolveOwner(Application app)
        {
            Window main = app.MainWindow;
            if (main == null) return null;
            // 主窗口本身就是提示框时不能再把它设成 Owner：
            // 自引用会在 Show 时抛 InvalidOperationException，而这个异常会把提示框整个吞掉。
            if (main is MessageWindow) return null;
            return main;
        }
        #endregion

        #region 提示框显示
        private static void Show(string message, string titleKey, string titleFallback)
        {
            var app = Application.Current;
            if (app == null) return;

            if (app.Dispatcher.HasShutdownStarted || app.Dispatcher.HasShutdownFinished) return;

            ShowDialogAndWait(
                app,
                () =>
                {
                    var window = new MessageWindow { Owner = ResolveOwner(app) };
                    window.SetTitle(EnumText.GetResourceText(titleKey, titleFallback));

                    window.ApplyOkButton();
                    window.SetMessage(message);

                    return window;
                });
        }

        private static void ShowDialogAndWait(Application app, Func<Window> createWindow)
        {
            var completed = new ManualResetEventSlim(false);

            Action show = () =>
            {
                Window main = app.MainWindow;
                bool wasTopmost = false;

                try
                {
                    // 主窗口开着置顶时提示框会被压在下面，看起来就像程序没反应；
                    // 这里先临时摘掉置顶，关窗时再恢复原状。
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

                    // Show 之后立刻返回会让提示框一闪而过，用 DispatcherFrame 把当前调用栈停在这里，
                    // 等到窗口关闭事件把 Continue 置 false 才继续往下走，效果等价于模态对话框。
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
                    app.Dispatcher.BeginInvoke(show);
                }

                // 非 UI 线程调用时必须等 UI 线程把窗口关掉才返回，
                // 否则调用方拿到的永远是最初的默认结果。
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
        #endregion
    }
}
