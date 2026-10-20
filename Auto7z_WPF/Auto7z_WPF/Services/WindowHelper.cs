using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace Auto7z_WPF.Services
{
    /// <summary>
    /// 无边框窗口的公共处理。
    /// </summary>
    public static class WindowHelper
    {
        /// <summary>
        /// 让无边框窗口可以用鼠标拖动移动。
        ///
        /// WindowStyle=None 的窗口没有标题栏，系统不会替它处理拖动，
        /// 这里在窗口背景区域按下左键时调用 DragMove 实现。
        /// 落在按钮、输入框等可交互控件上的按下不处理，避免影响点击。
        /// </summary>
        /// <param name="window">目标窗口。</param>
        public static void EnableWindowDragging(Window window)
        {
            if (window == null) return;

            window.MouseLeftButtonDown += (s, e) =>
            {
                if (e.OriginalSource is DependencyObject source && IsInteractive(source)) return;

                try
                {
                    window.DragMove();
                }
                catch (InvalidOperationException)
                {
                    // 鼠标已释放时忽略
                }
            };
        }

        /// <summary>
        /// 判断落点是不是需要交互的控件：是的话按下鼠标不应该触发拖动。
        /// </summary>
        private static bool IsInteractive(DependencyObject source)
        {
            return source is TextBoxBase
                || source is TextBox
                || source is ButtonBase
                || source is ComboBox
                || source is CheckBox
                || source is ListBoxItem
                || source is RadioButton;
        }

        /// <summary>
        /// 让左右方向键在指定的控件之间移动焦点。
        ///
        /// WPF 默认只有 Tab 能换焦点，方向键只对单选框、下拉框有效。
        /// 对话框里两个按钮挨着放，用方向键在它们之间移动更顺手。
        ///
        /// 只在焦点确实落在这两个控件上时才接管方向键，
        /// 否则会连带把下拉框、单选框的方向键也吃掉。
        /// </summary>
        /// <param name="window">所在窗口。</param>
        /// <param name="left">向左移动时到达的控件。</param>
        /// <param name="right">向右移动时到达的控件。</param>
        public static void EnableArrowNavigation(Window window, FrameworkElement left, FrameworkElement right)
        {
            if (window == null || left == null || right == null) return;

            window.PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Left && IsFocusedOn(right))
                {
                    MoveFocusTo(left);
                    e.Handled = true;
                }
                else if (e.Key == Key.Right && IsFocusedOn(left))
                {
                    MoveFocusTo(right);
                    e.Handled = true;
                }
            };
        }

        /// <summary>
        /// 焦点是不是在指定控件上。
        /// </summary>
        private static bool IsFocusedOn(FrameworkElement element)
        {
            var focused = Keyboard.FocusedElement as FrameworkElement;
            return focused != null && ReferenceEquals(focused, element);
        }

        /// <summary>
        /// 把焦点移到指定控件上。
        /// </summary>
        private static void MoveFocusTo(FrameworkElement element)
        {
            if (element == null) return;
            element.Focus();
            Keyboard.Focus(element);
        }
    }
}
