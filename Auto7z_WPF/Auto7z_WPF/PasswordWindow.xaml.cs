using Auto7z_WPF.Services;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Auto7z_WPF
{
    /// <summary>
    /// 密码输入窗口。
    ///
    /// 7-Zip 解压加密存档时，如果命令行没有提供密码，它会在 stdout 输出
    /// “Enter password (will not be echoed):”并以退出码 255 结束。
    /// 此时由主窗口调起本窗口收集密码，再把密码回传给 7-Zip 重试。
    ///
    /// 输入框用 TextBox：PasswordBox 不接受 IME 组合输入，中文密码打不进去。
    ///
    /// 本窗口只负责收集密码，不直接执行任何 7-Zip 命令。
    /// </summary>
    public partial class PasswordWindow : Window
    {
        private string _password = string.Empty;

        public PasswordWindow()
        {
            InitializeComponent();

            // 标题使用语言资源，跟随当前界面语言
            Title = EnumText.GetResourceText("PasswordWindowTitle", "Enter password");

            Loaded += (s, e) =>
            {
                // 打开即聚焦输入框
                var box = this.FindName("PasswordInput") as TextBox;
                if (box != null) box.Focus();
            };

            WindowHelper.EnableWindowDragging(this);
            WindowHelper.EnableArrowNavigation(this,
                this.FindName("ConfirmButton") as FrameworkElement,
                this.FindName("CancelButton") as FrameworkElement);
        }

        /// <summary>
        /// 用户输入的密码。未输入时为空字符串。
        /// </summary>
        public string Password => _password;

        /// <summary>
        /// 设置提示文字。
        /// 密码输错时把“该压缩包已加密，请输入密码”换成“密码错误，第 N 次尝试…”，让用户知道上一次没通过。
        /// </summary>
        /// <param name="text">提示文字。</param>
        public void SetPrompt(string text)
        {
            var prompt = this.FindName("PromptText") as TextBlock;
            if (prompt != null && !string.IsNullOrWhiteSpace(text)) prompt.Text = text;
        }

        /// <summary>
        /// 用户是否点了确定（而非取消）。
        /// </summary>
        public bool Confirmed { get; private set; }

        /// <summary>
        /// 供“确定”按钮调用的方法：取回输入的密码并关闭窗口。
        /// </summary>
        public void Confirm(string password)
        {
            _password = password ?? string.Empty;
            Confirmed = true;
            Close();
        }

        /// <summary>
        /// 供“取消”按钮调用的方法：不设置密码，直接关闭。
        /// </summary>
        public void Cancel()
        {
            _password = string.Empty;
            Confirmed = false;
            Close();
        }

        /// <summary>
        /// 回车是确定，Esc 是取消。
        ///
        /// 窗口是无边框透明的，系统的默认按钮不一定生效，
        /// 这里自己处理一遍最稳妥。
        /// </summary>
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                Cancel();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Enter)
            {
                ConfirmWithInput();
                e.Handled = true;
                return;
            }

            base.OnKeyDown(e);
        }

        /// <summary>
        /// 取回输入框里的密码并确认。
        ///
        /// 输入框用的是 TextBox 而不是 PasswordBox：PasswordBox 不接受 IME 的组合输入，
        /// 中文密码打不进去。改成 TextBox 后密码是明文显示的，
        /// 但压缩包的密码本来就允许中文，能输进去比看不见更重要。
        /// </summary>
        private void ConfirmWithInput()
        {
            var box = this.FindName("PasswordInput") as TextBox;
            Confirm(box == null ? string.Empty : box.Text);
        }

        /// <summary>
        /// “确定”按钮：取回输入的密码并关闭。
        /// </summary>
        private void ConfirmButton_Click(object sender, RoutedEventArgs e)
        {
            ConfirmWithInput();
        }

        /// <summary>
        /// “取消”按钮：不返回密码。
        /// </summary>
        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            Cancel();
        }

        /// <summary>
        /// 标题栏的关闭按钮。
        /// </summary>
        private void CloseButton_Click(object sender, MouseButtonEventArgs e)
        {
            Cancel();
        }

        /// <summary>
        /// 标题栏的最小化按钮。
        /// </summary>
        private void MinimizeButton_Click(object sender, MouseButtonEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }


    }
}
