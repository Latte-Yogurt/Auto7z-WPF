using Auto7z_WPF.Services;
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace Auto7z_WPF
{
    /// <summary>
    /// 关于窗口。只负责视图相关职责，标题/版权等文本来自视图模型与语言资源。
    /// </summary>
    public partial class AboutWindow : Window
    {
        private readonly LogService _logService = new LogService();
        private readonly MessageService _msgService = new MessageService();

        private readonly int _width = 260;
        private readonly int _height = 260;

        private readonly Auto7zViewModel _vm;

        #region 原生窗口接口
        private const int GWL_STYLE = -16;
        private const int WS_MAXIMIZEBOX = 0x00010000;

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
        #endregion

        /// <summary>
        /// 构造关于窗口。
        /// </summary>
        public AboutWindow()
        {
            InitializeComponent();

            // 关于窗口使用独立的视图模型实例：
            // 主窗口的视图模型已经绑定了主窗口的关闭/最小化事件，
            // 共用实例会导致“关于”窗口的关闭按钮连带关掉主窗口。
            _vm = new Auto7zViewModel();
            DataContext = _vm;

            _vm.RequestClose += (s, e) => Close();
            _vm.RequestMinimize += (s, e) => WindowState = WindowState.Minimized;

            // 语言资源字典是应用程序级共享的，主窗口启动时已按配置加载完毕，
            // 这里只需读到当前语言用于标题与版权文本。
            _vm.CurrentLanguage = ResolveCurrentLanguage();
            _vm.LoadAboutWindowResource();

            InitializeFormSize();
            InitializeFormControlButton();

            // 关于窗口固定置顶，保持原有交互行为
            Topmost = true;

            WindowHelper.EnableWindowDragging(this);
        }

        /// <summary>
        /// 取当前语言：优先复用主窗口记录的语言，其次取全局状态，最后回退到默认语言。
        /// </summary>
        private static string ResolveCurrentLanguage()
        {
            if (LanguageService.IsSupported(MainWindow.currentLanguage))
            {
                return MainWindow.currentLanguage;
            }

            if (LanguageService.IsSupported(AppState.CurrentLanguage))
            {
                return AppState.CurrentLanguage;
            }

            return "zh-CN";
        }

        private void InitializeFormSize()
        {
            MinWidth = _width;
            Width = MaxWidth = _width * 2;
            Height = MaxHeight = MinHeight = _height;
        }

        private void InitializeFormControlButton()
        {
            SourceInitialized += (s, e) =>
            {
                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                int currentStyle = GetWindowLong(hwnd, GWL_STYLE);

                // 移除最大化按钮样式  ，核心作用是禁止拖拽吸附
                SetWindowLong(hwnd, GWL_STYLE, currentStyle & ~WS_MAXIMIZEBOX);
            };
        }

        private void GithubLink_Click(object sender, MouseButtonEventArgs e)
        {
            OpenLink("https://github.com/Latte-Yogurt");
        }

        private void LicenseLink_Click(object sender, MouseButtonEventArgs e)
        {
            OpenLink("https://www.gnu.org/licenses/gpl-3.0.html");
        }

        private void OpenLink(string url)
        {
            try
            {
                WindowState = WindowState.Minimized;

                Process.Start(new ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                _msgService.ShowError(ex.Message, out string log);
                _logService.WriteLog(log, LogService.ErrorLevel.Error);
            }
        }
    }
}
