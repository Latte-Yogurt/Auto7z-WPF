using Auto7z_WPF.Services;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Auto7z_WPF
{
    public partial class MessageWindow : Window
    {
        #region 构造与布局
        public MessageWindow()
        {
            InitializeComponent();

            // 标题走资源键现取，XAML 里那个只是设计期占位。
            Title = EnumText.GetResourceText("InfoTitle", "Info");

            // 默认按「是/否」两键布局，调用方要单按钮时再切到 ApplyOkButton。
            ApplyYesNoButtons();

            // 无边框窗口自己处理拖动；左右方向键在确认与取消之间挪焦点。
            WindowHelper.EnableWindowDragging(this);
            WindowHelper.EnableArrowNavigation(this, ConfirmButton, CancelButton);
        }

        // 调用方在窗口关闭之后才知道用户选了「是」还是「否」，结论留在这个属性上。
        public bool Confirmed { get; private set; }

        // 「是/否」与「确定」两种形态共用同一组按钮，只改列位置与可见性，不另建控件。
        public void ApplyYesNoButtons()
        {
            ConfirmButton.Content = EnumText.GetResourceText("YesButton", "Yes");
            CancelButton.Content = EnumText.GetResourceText("NoButton", "No");

            CancelButton.Visibility = Visibility.Visible;
            // 中间的占位格撑开两键间距，取消键重新占回第三列。
            PlaceHolderGrid.Visibility = Visibility.Visible;

            // 确认键回到第一列并拉伸，与取消键分列两端。
            MoveConfirmToColumn(0);
        }

        public void ApplyOkButton()
        {
            ConfirmButton.Content = EnumText.GetResourceText("OkButton", "OK");

            // 单按钮形态下第三列整个收起，确认键居中才不显得偏左。
            CancelButton.Visibility = Visibility.Collapsed;
            PlaceHolderGrid.Visibility = Visibility.Collapsed;

            // 挪到中间列才能居中，此时左右两列都没有内容。
            MoveConfirmToColumn(1);
        }

        // 两键布局靠拉伸补齐宽度，单键布局要居中，对齐方式必须跟着列位一起换。
        private void MoveConfirmToColumn(int column)
        {
            ConfirmButton.SetValue(Grid.ColumnProperty, column);
            ConfirmButton.HorizontalAlignment = column == 1 ? HorizontalAlignment.Center : HorizontalAlignment.Stretch;
        }

        #endregion
        #region 文案与结论
        public void SetMessage(string text)
        {
            // 空文案不覆盖 XAML 里的默认提示，免得出现一个没有文字的询问框。
            if (!string.IsNullOrWhiteSpace(text)) QuestionText.Text = text;
        }

        public void SetTitle(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;

            // 标题要同时写到窗口与标题栏文本块，只改一处会看到两个不同的名字。
            Title = text;
            MessageTitle.Text = text;
        }

        // 先落结论再关窗：Closed 之后外面才读得到 Confirmed。
        public void Confirm()
        {
            Confirmed = true;
            Close();
        }

        // 取消一律把结论置否，Esc 与点关闭都走这里。
        public void Cancel()
        {
            Confirmed = false;
            Close();
        }

        #endregion
        #region 键盘与按钮回调
        // 放在 OnKeyDown 而不是 XAML 的 InputBindings：焦点在按钮上时按键先到窗口这一层，才能统一处理。
        protected override void OnKeyDown(KeyEventArgs e)
        {
            // Esc 直接视为取消，这是这类小对话框的通行约定，鼠标不可用时也能退出。
            // 标记已处理，按键不再往下传给焦点控件，避免二次触发。
            if (e.Key == Key.Escape)
            {
                Cancel();
                e.Handled = true;
                return;
            }

            // 回车等同点「是」，与确认按钮的 IsDefault 行为一致。
            if (e.Key == Key.Enter)
            {
                Confirm();
                e.Handled = true;
                return;
            }

            // 其它按键照常往下走，不拦截 Tab 与方向键。
            base.OnKeyDown(e);
        }

        // 记一条日志：这类询问框出现在打包或解压中途，事后要能还原用户当时的选择。
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

        // 右上角关闭按取消处理，不让窗口关掉却留下一个悬空的结论。
        private void CloseButton_Click(object sender, MouseButtonEventArgs e)
        {
            Cancel();
        }

        // 最小化不改变结论，窗口还开着，用户可以再回来选。
        private void MinimizeButton_Click(object sender, MouseButtonEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }
        #endregion
    }
}
