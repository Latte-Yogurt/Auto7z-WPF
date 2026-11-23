using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;

namespace Auto7z_WPF.Services
{
#if DEBUG
    /// <summary>
    /// 自动化动作类型。语义与用户真实操作一一对应，不做任何"跳过 UI"的旁路。
    /// </summary>
    public enum AutoAction
    {
        /// <summary>点「确定 / 是」——找 ConfirmButton。</summary>
        Confirm,

        /// <summary>点「取消 / 否」——找 CancelButton；没有则退回关闭。</summary>
        Cancel,

        /// <summary>点标题栏关闭——找 CloseButton；没有则调 Window.Close()。</summary>
        Close,

        /// <summary>点「最小化」——找 MinimizeButton。</summary>
        Minimize,

        /// <summary>点「关于」——找 AboutButton。</summary>
        About,

        /// <summary>点「设置」——找 SettingButton。</summary>
        Settings,

        /// <summary>点「浏览」——找 BrowseButton。</summary>
        Browse,

        /// <summary>在密码框输入指定文本后点确认。</summary>
        TypeAndConfirm,
    }

    /// <summary>
    /// 自动化动作驱动器（**仅 Debug 构建**）。
    ///
    /// 用一套共用逻辑覆盖所有窗口、所有按钮的"点击"，而不是给每个窗口各写一份。
    ///
    /// 三条原则：
    ///   1. **不绕过 UI**：窗口照常 Show()，动画照常播放，只是"点击"这件事由代码在
    ///      DispatcherTimer 里抛出按钮自己的 Click 路由事件来完成 —— 与鼠标点击走同一条路。
    ///   2. **可排队**：一次任务里可能连续弹多个框（例如先问是否合并、再问密码），
    ///      所以答案是一个**队列**，而不是单个布尔值。
    ///   3. **可观测**：每次自动动作都写日志，跑完能回答"到底自动点了什么"。
    ///
    /// 之所以用 DispatcherTimer 而不是立即执行：Show() 之后布局与焦点还没跑完，
    /// 此刻点击会让按钮的 IsDefault/Esc 逻辑撞上未初始化的状态；延后一轮再点，
    /// 时序与真人操作一致（实测 60ms 足够，且窗口在采样中确实"出现过"）。
    /// </summary>
    public static class DebugAutoDriver
    {
        /// <summary>
        /// 动作序列的环境变量。取值形如 <c>confirm,cancel,confirm</c>，
        /// 逐个消费；队列耗尽后回落到最后一个动作。
        /// </summary>
        public const string ActionsVariable = "AUTO7Z_AUTOANSWER";

        /// <summary>密码输入框自动填入的文本。</summary>
        public const string PasswordVariable = "AUTO7Z_AUTOANSWER_PASSWORD";

        /// <summary>点击前的延迟（毫秒）。给布局与焦点留出时间。</summary>
        private const int PressDelayMs = 60;

        private static readonly object _sync = new object();
        private static Queue<AutoAction> _queue;
        private static AutoAction _defaultAction = AutoAction.Confirm;
        private static bool _initialized;
        private static string _password;

        /// <summary>是否启用了自动驱动（即环境变量给了有效动作）。</summary>
        public static bool IsEnabled
        {
            get
            {
                EnsureInitialized();
                lock (_sync)
                {
                    return _queue != null;
                }
            }
        }

        /// <summary>密码框自动输入的内容；未设置时为 null。</summary>
        public static string Password
        {
            get
            {
                EnsureInitialized();
                return _password;
            }
        }

        /// <summary>取下一个动作：队列还有就出队，否则用默认动作（不消耗）。</summary>
        public static AutoAction NextAction()
        {
            EnsureInitialized();
            lock (_sync)
            {
                if (_queue == null) return AutoAction.Confirm;
                if (_queue.Count > 1) return _queue.Dequeue();
                if (_queue.Count == 1) return _queue.Peek();
                return _defaultAction;
            }
        }

        /// <summary>
        /// 为指定窗口挂上自动点击。窗口应当**已经 Show()**，
        /// 本方法只负责在延迟后点击，不改变窗口的显示逻辑。
        /// </summary>
        /// <param name="window">目标窗口。</param>
        /// <param name="forcedAction">强制动作；为 null 时从队列取。</param>
        /// <returns>是否挂上了驱动。</returns>
        public static bool Attach(Window window, AutoAction? forcedAction = null)
        {
            if (window == null) return false;
            if (!IsEnabled) return false;

            AutoAction action = forcedAction ?? NextAction();

            var timer = new DispatcherTimer(DispatcherPriority.Normal)
            {
                Interval = TimeSpan.FromMilliseconds(PressDelayMs),
            };

            timer.Tick += (s, e) =>
            {
                timer.Stop();
                try
                {
                    Perform(window, action);
                }
                catch (Exception ex)
                {
                    SafeLog("自动点击失败（" + action + "）：" + ex.Message);
                }
            };

            // 窗口已经关了就别再点了。
            window.Closed += (s, e) => timer.Stop();
            timer.Start();
            return true;
        }

        /// <summary>对窗口执行一次指定动作。</summary>
        public static void Perform(Window window, AutoAction action)
        {
            if (window == null) return;

            SafeLog("自动动作：" + action + "  窗口=" + window.GetType().Name +
                    "  标题=" + (window.Title ?? ""));

            switch (action)
            {
                case AutoAction.TypeAndConfirm:
                    FillPassword(window);
                    PressButton(window, "ConfirmButton");
                    break;

                case AutoAction.Confirm:
                    PressButton(window, "ConfirmButton");
                    break;

                case AutoAction.Cancel:
                    // 没有取消键的窗口（提示/错误只有确定键），取消等价于把它关掉。
                    if (!PressButton(window, "CancelButton") && !PressButton(window, "CloseButton"))
                    {
                        window.Close();
                    }
                    break;

                case AutoAction.Close:
                    if (!PressButton(window, "CloseButton"))
                    {
                        window.Close();
                    }
                    break;

                case AutoAction.Minimize:
                    if (!PressButton(window, "MinimizeButton")) window.WindowState = WindowState.Minimized;
                    break;

                case AutoAction.About:
                    PressButton(window, "AboutButton");
                    break;

                case AutoAction.Settings:
                    PressButton(window, "SettingButton");
                    break;

                case AutoAction.Browse:
                    PressButton(window, "BrowseButton");
                    break;

                default:
                    PressButton(window, "ConfirmButton");
                    break;
            }
        }

        private static void EnsureInitialized()
        {
            if (_initialized) return;
            lock (_sync)
            {
                if (_initialized) return;
                _initialized = true;

                string raw = ReadEnv(ActionsVariable);
                if (string.IsNullOrWhiteSpace(raw)) return;

                var queue = new Queue<AutoAction>();
                foreach (string token in raw.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (TryParseAction(token, out AutoAction action))
                    {
                        queue.Enqueue(action);
                    }
                }

                if (queue.Count > 0)
                {
                    _queue = queue;
                    _defaultAction = queue.Peek();
                }

                string pwd = ReadEnv(PasswordVariable);
                if (!string.IsNullOrEmpty(pwd)) _password = pwd;
            }
        }

        private static string ReadEnv(string name)
        {
            try { return Environment.GetEnvironmentVariable(name); }
            catch { return null; }
        }

        private static bool TryParseAction(string token, out AutoAction action)
        {
            switch ((token ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "1": case "y": case "yes": case "true": case "ok":
                case "confirm": case "确定": case "是":
                    action = AutoAction.Confirm; return true;

                case "0": case "n": case "no": case "false":
                case "cancel": case "取消": case "否":
                    action = AutoAction.Cancel; return true;

                case "close": case "x": case "关闭":
                    action = AutoAction.Close; return true;

                case "min": case "minimize": case "最小化":
                    action = AutoAction.Minimize; return true;

                case "about": case "关于":
                    action = AutoAction.About; return true;

                case "settings": case "setting": case "设置":
                    action = AutoAction.Settings; return true;

                case "browse": case "浏览":
                    action = AutoAction.Browse; return true;

                case "type": case "password": case "输入":
                    action = AutoAction.TypeAndConfirm; return true;

                default:
                    action = AutoAction.Confirm;
                    return false;
            }
        }

        /// <summary>
        /// 按名字找控件并抛它的 Click 事件。找不到返回 false。
        ///
        /// 用 FindName 而不是遍历可视化树：本项目所有按钮都带 x:Name，
        /// FindName 直接命中，且不受模板展开时机影响。
        ///
        /// 有些"按钮"其实是 Grid + 鼠标事件（MinimizeButton / CloseButton 就是），
        /// 这类要按鼠标左键事件驱动，所以两种都试。
        /// </summary>
        private static bool PressButton(Window window, string buttonName)
        {
            object found = window.FindName(buttonName);

            if (found is ButtonBase button)
            {
                button.ApplyTemplate();
                button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, button));
                return true;
            }

            if (found is FrameworkElement element)
            {
                var args = new System.Windows.Input.MouseButtonEventArgs(
                    System.Windows.Input.Mouse.PrimaryDevice,
                    Environment.TickCount,
                    System.Windows.Input.MouseButton.Left)
                {
                    RoutedEvent = System.Windows.Input.Mouse.MouseDownEvent,
                    Source = element,
                };
                try
                {
                    element.RaiseEvent(args);
                    return true;
                }
                catch
                {
                    return false;
                }
            }

            return false;
        }

        /// <summary>把密码填进窗口里的输入框。</summary>
        private static void FillPassword(Window window)
        {
            string pwd = Password;
            if (pwd == null) return;

            // 密码窗口的输入控件名是 PasswordInput。
            object found = window.FindName("PasswordInput");
            if (found is TextBox textBox)
            {
                textBox.Text = pwd;
                return;
            }
            if (found is PasswordBox passwordBox)
            {
                passwordBox.Password = pwd;
                return;
            }

            // 名字变了也要能用：退化为"窗口里第一个可输入控件"。
            foreach (object child in EnumerateVisual(window))
            {
                if (child is TextBox t2) { t2.Text = pwd; return; }
                if (child is PasswordBox p2) { p2.Password = pwd; return; }
            }
        }

        private static IEnumerable<object> EnumerateVisual(DependencyObject root)
        {
            if (root == null) yield break;
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(root, i);
                yield return child;
                foreach (object deep in EnumerateVisual(child))
                {
                    yield return deep;
                }
            }
        }

        private static void SafeLog(string message)
        {
            try
            {
                new LogService().WriteLog(message, LogService.ErrorLevel.Info);
            }
            catch
            {
                // 日志失败不能影响自动化本身
            }
        }
    }
#endif
}
