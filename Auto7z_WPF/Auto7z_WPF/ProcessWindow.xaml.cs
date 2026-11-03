using Auto7z_WPF.Services;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Auto7z_WPF
{
    public partial class ProcessWindow : Window
    {
        private bool _cancelled;

        public ProcessWindow()
        {
            InitializeComponent();

            Title = EnumText.GetResourceText("ProcessWindowTitle", "Processing...");
        }

        public bool Cancelled => _cancelled;

        public bool AllowClose { get; set; }

        public void RequestCancel()
        {
            _cancelled = true;
        }

        public void SetStatus(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            PromptText.Text = text;
        }

        public void SetProgress(int percent)
        {
            // 进度值在这里夹紧：7-Zip 的百分比流偶尔越界，
            // 直接赋给 ProgressBar 会抛 ArgumentException。
            if (percent < 0) percent = 0;
            if (percent > 100) percent = 100;
            ProcessBar.Value = percent;
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            _cancelled = true;
        }

        private void CloseButton_Click(object sender, MouseButtonEventArgs e)
        {
            _cancelled = true;
        }
    }
}
