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

            // 这里**不再** new 一个 Auto7zViewModel。
            //
            // 早先的写法是 `_vm = new Auto7zViewModel(); DataContext = _vm;`，
            // 只为让标题栏两个 Grid 的 MouseBinding 有 MinimizeCommand / CloseCommand 可绑。
            // 代价是两个真实问题：
            //   ① 每开一次进度窗就多一个完整 ViewModel（含 ConfigService、SevenZipService、
            //      LanguageService 及其订阅），而它从不 Dispose、也不 LoadConfig ——
            //      纯属浪费，且会随右键菜单反复调用累积；
            //   ② 那两条命令转发的是**这个假实例**的 RequestMinimize / RequestClose，
            //      与真正在跑任务的 ViewModel 毫无关系，语义上就是错的。
            //
            // 进度窗只需要「最小化」与「关闭」两个动作，直接在代码里接鼠标事件即可，
            // 不必为此拉起一整套业务对象。XAML 外观未作任何改动。
            Title = EnumText.GetResourceText("ProcessWindowTitle", "Processing...");

            MinimizeButton.MouseLeftButtonDown += (s, e) =>
            {
                WindowState = WindowState.Minimized;
                e.Handled = true;
            };

            CloseButton.MouseLeftButtonDown += (s, e) =>
            {
                // 点关闭等于请求取消：真正关窗由 ShellMenuTaskRunner 在任务收尾后
                // （AllowClose=true）执行，这里只置标志，避免任务还在跑就被关掉。
                _cancelled = true;
                e.Handled = true;
            };
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
