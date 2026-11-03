using Auto7z_WPF.Services;
using System;
using System.Windows;
using System.Windows.Input;

namespace Auto7z_WPF
{
    public partial class PasswordWindow : Window
    {
        #region 构造与对外接口
        // 空密码也是合法输入（等于不加密），因此默认值用空串而不是 null。
        private string _password = string.Empty;

        public PasswordWindow()
        {
            InitializeComponent();

            // 标题按资源键现取；XAML 里的同一键只用于设计期。
            Title = EnumText.GetResourceText("PasswordWindowTitle", "Enter password");

            // 等布局完成再抢焦点：构造阶段控件还没进可视树，Focus 会落空。
            Loaded += (s, e) => PasswordInput.Focus();

            // 无边框窗口自行处理拖动；左右方向键在确认与取消之间挪焦点。
            WindowHelper.EnableWindowDragging(this);
            WindowHelper.EnableArrowNavigation(this, ConfirmButton, CancelButton);
        }

        // 密码只在窗口关闭后由调用方取一次，因此用只读属性暴露。
        public string Password => _password;

        // 提示语由调用方给出（例如「该压缩包已加密」），空串时保留 XAML 里的默认文案。
        public void SetPrompt(string text)
        {
            if (!string.IsNullOrWhiteSpace(text)) PromptText.Text = text;
        }

        public bool Confirmed { get; private set; }

        // 先落密码与结论再关窗：Closed 之后外面才读得到这两个值。
        public void Confirm(string password)
        {
            // null 也归一成空串，调用方不必再判一次。
            _password = password ?? string.Empty;
            Confirmed = true;
            Close();
        }

        // 取消要把密码清空，不能留下上一次输入的残留。
        public void Cancel()
        {
            _password = string.Empty;
            Confirmed = false;
            Close();
        }

        #endregion
        #region 键盘响应
        // 放在 OnKeyDown 而不是按钮的 IsDefault/IsCancel：输入框里的回车先到窗口这一层，才能在按下时就取到文本。
        protected override void OnKeyDown(KeyEventArgs e)
        {
            // Esc 视为放弃输入，语义与点取消相同。
            // 标记已处理，避免按键继续下传引发第二次关闭。
            if (e.Key == Key.Escape)
            {
                Cancel();
                e.Handled = true;
                return;
            }

            // 回车确认，输完密码直接敲回车即可，不用去够确认按钮。
            if (e.Key == Key.Enter)
            {
                ConfirmWithInput();
                e.Handled = true;
                return;
            }

            // 其余按键照常下传，保证 Tab 与方向键可用。
            base.OnKeyDown(e);
        }

        #endregion
        #region 按钮回调
        // 确认与回车共用同一条路径，两种入口拿到的结果不会出现分歧。
        private void ConfirmWithInput()
        {
            // PasswordInput 用 TextBox 而不是 PasswordBox：PasswordBox 收不到中文输入法的组合键，密码打不进去；代价是明文显示。
            Confirm(PasswordInput.Text);
        }

        private void ConfirmButton_Click(object sender, RoutedEventArgs e)
        {
            ConfirmWithInput();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            Cancel();
        }

        // 关闭按钮按取消处理，并顺手清掉已输入的密码。
        private void CloseButton_Click(object sender, MouseButtonEventArgs e)
        {
            Cancel();
        }

        // 最小化不改结论，窗口还开着，用户可以再回来继续输入。
        private void MinimizeButton_Click(object sender, MouseButtonEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }
        #endregion
    }
}
