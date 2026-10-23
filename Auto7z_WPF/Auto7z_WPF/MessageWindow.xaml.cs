using Auto7z_WPF.Services;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Auto7z_WPF
{
    /// <summary>
    /// 消息对话框。
    ///
    /// 两种用法：
    ///   是/否询问 —— 标题栏文字加“是”“否”两个按钮
    ///   单条提示 —— 只有“确定”一个按钮，用于错误与警告
    ///
    /// 用来代替 MessageBox：
    /// MessageBox 是 Win32 模态框，在 WPF 的 Dispatcher 回调里调用会与 WPF 的消息循环互锁，
    /// 表现为弹窗显示得出但点击无响应，任务一直卡住。
    /// WPF 窗口没有这个问题。
    /// </summary>
    public partial class MessageWindow : Window
    {
        public MessageWindow()
        {
            InitializeComponent();

            // 标题取自语言资源，跟随当前界面语言
            Title = EnumText.GetResourceText("InfoTitle", "Info");

            // 默认是“是/否”两个按钮的询问模式
            ApplyYesNoButtons();

            WindowHelper.EnableWindowDragging(this);
            WindowHelper.EnableArrowNavigation(this,
                this.FindName("ConfirmButton") as FrameworkElement,
                this.FindName("CancelButton") as FrameworkElement);
        }

        /// <summary>
        /// 把按钮文字设为“是 / 否”，两个按钮都显示。
        /// </summary>
        public void ApplyYesNoButtons()
        {
            SetButtonText("ConfirmButton", "YesButton", "Yes");
            SetButtonText("CancelButton", "NoButton", "No");

            var cancel = this.FindName("CancelButton") as FrameworkElement;
            if (cancel != null) cancel.Visibility = Visibility.Visible;

            var placeholder = this.FindName("PlaceHolderGrid") as FrameworkElement;
            if (placeholder != null) placeholder.Visibility = Visibility.Visible;

            // 询问模式下“是”在左、“否”在右
            MoveConfirmToColumn(0);
        }

        /// <summary>
        /// 把按钮文字设为“确定”，只留一个按钮并让它居中。
        /// </summary>
        public void ApplyOkButton()
        {
            SetButtonText("ConfirmButton", "OkButton", "OK");

            var cancel = this.FindName("CancelButton") as FrameworkElement;
            if (cancel != null) cancel.Visibility = Visibility.Collapsed;

            var placeholder = this.FindName("PlaceHolderGrid") as FrameworkElement;
            if (placeholder != null) placeholder.Visibility = Visibility.Collapsed;

            // 单按钮时挪到中间的弹性列，靠这个列居中
            MoveConfirmToColumn(1);
        }

        /// <summary>
        /// 设置按钮文字，优先用指定资源键，取不到时退回备用文本。
        /// </summary>
        private void SetButtonText(string elementName, string resourceKey, string fallback)
        {
            var button = this.FindName(elementName) as Button;
            if (button != null) button.Content = EnumText.GetResourceText(resourceKey, fallback);
        }

        /// <summary>
        /// 把“确定”按钮放到指定的列。
        /// </summary>
        private void MoveConfirmToColumn(int column)
        {
            var confirm = this.FindName("ConfirmButton") as FrameworkElement;
            if (confirm == null) return;

            confirm.SetValue(Grid.ColumnProperty, column);
            confirm.HorizontalAlignment = column == 1 ? HorizontalAlignment.Center : HorizontalAlignment.Stretch;
        }

        /// <summary>
        /// 设置正文文字。
        /// </summary>
        public void SetMessage(string text)
        {
            var message = this.FindName("QuestionText") as TextBlock;
            if (message != null && !string.IsNullOrWhiteSpace(text)) message.Text = text;
        }

        /// <summary>
        /// 设置标题文字。
        /// 窗口标题栏与左上角的标题文字要一起改，
        /// 否则错误提示的标题栏写着“错误”、左上角却还是“提示”。
        /// </summary>
        public void SetTitle(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;

            Title = text;

            var titleText = this.FindName("MessageTitle") as TextBlock;
            if (titleText != null) titleText.Text = text;
        }

        /// <summary>用户是否点了“是”。</summary>
        public bool Confirmed { get; private set; }

        /// <summary>供“是”按钮调用的方法。</summary>
        public void Confirm()
        {
            Confirmed = true;
            Close();
        }

        /// <summary>供“否”按钮和标题栏关闭按钮调用的方法。</summary>
        public void Cancel()
        {
            Confirmed = false;
            Close();
        }

        /// <summary>
        /// 回车是确定，Esc 是取消。
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
                Confirm();
                e.Handled = true;
                return;
            }

            base.OnKeyDown(e);
        }

        private void ConfirmButton_Click(object sender, RoutedEventArgs e)
        {
            new LogService().WriteLog(
                EnumText.GetErrorText("Log_DialogConfirmed", "询问框：用户选择“是”"),
                LogService.ErrorLevel.Info);

            Confirm();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            new LogService().WriteLog(
                EnumText.GetErrorText("Log_DialogCancelled", "询问框：用户选择“否”"),
                LogService.ErrorLevel.Info);

            Cancel();
        }

        private void CloseButton_Click(object sender, MouseButtonEventArgs e)
        {
            Cancel();
        }

        private void MinimizeButton_Click(object sender, MouseButtonEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }
    }
}
