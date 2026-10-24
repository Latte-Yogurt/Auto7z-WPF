using Auto7z_WPF.Services;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Auto7z_WPF
{
    /// <summary>
    /// 任务处理进度窗口。
    ///
    /// 右键菜单模式下没有主窗口，但用户点了右键之后需要看到"到底在不在动"，
    /// 否则任务跑多久、有没有卡住全都不知道。
    /// 这个窗口只负责显示进度文本与进度条，不做任何业务判断。
    ///
    /// 窗口样式沿用消息窗口那一套（无边框透明 + WindowChrome），
    /// 跟主窗口保持一致的观感。
    /// </summary>
    public partial class ProcessWindow : Window
    {
        private bool _cancelled;

        public ProcessWindow()
        {
            InitializeComponent();

            Title = EnumText.GetResourceText("ProcessWindowTitle", "Processing...");
        }

        /// <summary>
        /// 用户是否点了取消。
        /// </summary>
        public bool Cancelled => _cancelled;

        /// <summary>
        /// 任务已经收尾，允许窗口真正关闭。
        ///
        /// 任务进行中窗口不允许直接关 —— 关窗口一律转成取消请求，
        /// 否则窗口没了、7z 还在跑，压到一半的目录也没人清。
        /// </summary>
        public bool AllowClose { get; set; }

        /// <summary>
        /// 把「关窗口」当成取消请求。
        /// </summary>
        public void RequestCancel()
        {
            _cancelled = true;
        }

        /// <summary>
        /// 设置进度文本。
        /// 文本由调用方从语言资源里取，这里只负责显示。
        /// </summary>
        /// <param name="text">进度描述文本。</param>
        public void SetStatus(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            var label = this.FindName("PromptText") as TextBlock;
            if (label != null) label.Text = text;
        }

        /// <summary>
        /// 设置进度百分比。
        /// </summary>
        /// <param name="percent">百分比，0-100。</param>
        public void SetProgress(int percent)
        {
            if (percent < 0) percent = 0;
            if (percent > 100) percent = 100;
            var bar = this.FindName("ProcessBar") as ProgressBar;
            if (bar != null) bar.Value = percent;
        }

        /// <summary>
        /// 取消按钮：只记下取消请求，不直接关窗口。
        /// 真正结束由任务的取消流程决定，关早了会丢掉最后的进度显示。
        /// </summary>
        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            _cancelled = true;
        }

        /// <summary>
        /// 标题栏的关闭按钮：等同取消。
        /// </summary>
        private void CloseButton_Click(object sender, MouseButtonEventArgs e)
        {
            _cancelled = true;
        }
    }
}
