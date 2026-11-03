using Auto7z_WPF.Services;
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace Auto7z_WPF
{
    public partial class AboutWindow : Window
    {
        #region 构造与初始化
        // 日志与弹窗服务各自持有：链接打不开时先告诉用户，再落一条日志。
        private readonly LogService _logService = new LogService();
        private readonly MessageService _msgService = new MessageService();

        private readonly int _width = 260;
        private readonly int _height = 260;

        private readonly Auto7zViewModel _vm;

        private const int GWL_STYLE = -16;
        // 与主窗口同一套做法：ResizeMode 挡不住系统菜单里的最大化，要摘掉 WS_MAXIMIZEBOX 位。
        private const int WS_MAXIMIZEBOX = 0x00010000;

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);


        public AboutWindow()
        {
            InitializeComponent();

            // 单独建一个 ViewModel 只为取标题与版权文案，不共享主窗口那份运行状态。
            _vm = new Auto7zViewModel();
            DataContext = _vm;

            _vm.RequestClose += (s, e) => Close();
            _vm.RequestMinimize += (s, e) => WindowState = WindowState.Minimized;

            // 语言先定下来，随后取资源才能拿到对应语种的文案。
            _vm.CurrentLanguage = ResolveCurrentLanguage();
            // 语言与配置都就绪后才加载文案，避免先按默认语言取一遍再重取。
            _vm.LoadAboutWindowResource();

            InitializeFormSize();
            InitializeFormControlButton();

            // 关于窗口压在其它窗口之上：点开就在眼前，不用再去任务栏找。
            Topmost = true;

            // 无边框窗口没有标题栏，拖动交给 WindowHelper。
            WindowHelper.EnableWindowDragging(this);
        }

        #endregion
        #region 语言与窗口尺寸
        // CurrentLanguage 可能来自配置文件里手改的非法值，取用前先校验一次。
        private static string ResolveCurrentLanguage()
        {
            if (LanguageService.IsSupported(AppState.CurrentLanguage))
            {
                return AppState.CurrentLanguage;
            }

            // 配置里是未受支持的语言代码时退回默认语言，避免资源键全部落空。
            return LanguageService.FallbackLanguage;
        }

        private void InitializeFormSize()
        {
            MinWidth = _width;
            // 正文长度固定，宽度直接按最小宽度的两倍铺开；高度同理一次定死，不留垂直调整余地。
            Width = MaxWidth = _width * 2;
            Height = MaxHeight = MinHeight = _height;
        }

        private void InitializeFormControlButton()
        {
            // 句柄到手后才能改样式，因此挂在 SourceInitialized 上；此时 Show 还没返回，不会闪一帧。
            SourceInitialized += (s, e) =>
            {
                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                int currentStyle = GetWindowLong(hwnd, GWL_STYLE);

                // 无边框窗口照样会被 Aero Snap 最大化，必须摘掉最大化位，标题栏系统菜单里也就不会出现这一项。
                SetWindowLong(hwnd, GWL_STYLE, currentStyle & ~WS_MAXIMIZEBOX);
            };
        }


        #endregion
        #region 链接跳转
        // 两个入口只是地址不同，其余处理共用 OpenLink。
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
                // 先最小化自己：浏览器拿到前台时，关于窗口不会正好挡在它前面。
                WindowState = WindowState.Minimized;

                Process.Start(new ProcessStartInfo
                {
                    FileName = url,
                    // 必须用 ShellExecute 交给系统默认浏览器，否则 Process.Start 会把网址当成文件名去执行。
                    UseShellExecute = true
                });
            }
            // 系统没装默认浏览器或协议关联被改坏时 Process.Start 会抛异常，这里兜住并提示用户。
            catch (Exception ex)
            {
                // 失败原因既进弹窗也进日志：用户看到提示，事后还能查到细节。
                _msgService.ShowError(ex.Message, out string log);
                _logService.WriteLog(log, LogService.ErrorLevel.Error);
            }
        }


        #endregion
    }
}
