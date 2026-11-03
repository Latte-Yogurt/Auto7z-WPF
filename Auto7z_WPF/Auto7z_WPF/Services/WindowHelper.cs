using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace Auto7z_WPF.Services
{
    /// <summary>
    /// 无边框窗口的通用交互：拖动窗体与左右方向键切换焦点。
    /// </summary>
    public static class WindowHelper
    {
        #region 窗体拖动
        /// <summary>让整个窗口都能拖动，但按在可交互控件上时不拖。</summary>
        /// <param name="window">目标窗口。</param>
        public static void EnableWindowDragging(Window window)
        {
            if (window == null) return;

            window.MouseLeftButtonDown += (s, e) =>
            {
                // 无边框窗口没有标题栏，只能靠窗口自己的身体拖动；
                // 但按在输入框或按钮上时用户想的是操作那个控件，此时拖动会把点击吞掉。
                if (e.OriginalSource is DependencyObject source && IsInteractive(source)) return;

                try
                {
                    window.DragMove();
                }
                catch (InvalidOperationException)
                {
                    // 鼠标键在 DragMove 之前就松开时会抛这个异常，属于正常竞态，忽略即可。
                }
            };
        }

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
        #endregion

        #region 方向键导航
        /// <summary>左右方向键在两个元素之间移动焦点。</summary>
        /// <param name="window">目标窗口。</param>
        /// <param name="left">左侧元素。</param>
        /// <param name="right">右侧元素。</param>
        public static void EnableArrowNavigation(Window window, FrameworkElement left, FrameworkElement right)
        {
            if (window == null || left == null || right == null) return;

            window.PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Left && IsFocusedOn(right))
                {
                    MoveFocusTo(left);
                    // 置 Handled 是为了不让 TextBox 之类再把方向键当成光标移动处理一遍。
                    e.Handled = true;
                }
                else if (e.Key == Key.Right && IsFocusedOn(left))
                {
                    MoveFocusTo(right);
                    e.Handled = true;
                }
            };
        }

        private static bool IsFocusedOn(FrameworkElement element)
        {
            var focused = Keyboard.FocusedElement as FrameworkElement;
            return focused != null && ReferenceEquals(focused, element);
        }

        private static void MoveFocusTo(FrameworkElement element)
        {
            if (element == null) return;
            // Focus() 只改逻辑焦点，键盘焦点还得单独交给 Keyboard，否则方向键仍留在原控件上。
            element.Focus();
            Keyboard.Focus(element);
        }
        #endregion
    }
}
