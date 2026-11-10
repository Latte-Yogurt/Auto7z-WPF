using Auto7z_WPF.Models;
using Auto7z_WPF.Services;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Auto7z_WPF
{
    public partial class MainWindow : Window
    {
        #region 字段与常量
        private readonly LogService _logService = new LogService();

        // 收起态的窗口宽度与高度；展开时宽度翻倍，动画与复位都按这组基准值算。
        private readonly int _width = 260;
        private readonly int _height = 260;
        // 输出路径框在侧栏展开后仍用固定宽度，再宽会把「浏览」按钮挤出可点区域。
        private readonly int _filePathWidth = 160;

        private bool _isExpanded;
        // 回填配置期间置位，挡掉 TextChanged、SelectionChanged 与 Checked 引发的连环回调。
        private bool _isSyncingControls;

        // 单实例转发与拖放可能来自不同线程，入队要线程安全；消费只在界面线程做。
        //
        // 队列元素带模式开关：第二个实例可能是「右键解压」的 -uz 交过来的，
        // 若只排队路径，接收方会按自己的界面模式处理，解压就成了压缩。
        private readonly ConcurrentQueue<QueuedFile> _pendingQueue = new ConcurrentQueue<QueuedFile>();

        // 队列里的一项：路径 + 交接时携带的模式开关（null 表示沿用界面当前模式）。
        private sealed class QueuedFile
        {
            public QueuedFile(string path, string mode)
            {
                Path = path;
                Mode = mode;
            }

            public string Path { get; }

            public string Mode { get; }
        }

        private readonly Auto7zViewModel _vm;

        private const int GWL_STYLE = -16;
        // 供 SetWindowLong 使用：ResizeMode 只挡拖边框，系统菜单里的「最大化」要另外摘掉。
        private const int WS_MAXIMIZEBOX = 0x00010000;

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        #endregion
        #region 构造与初始化
        public MainWindow()
        {
            InitializeComponent();
            // 先按收起态摆好列宽与侧栏可见性，省得构造期间侧栏闪一下。
            SetGridColumnLength();
            SetSidePanelVisibility();

            _vm = new Auto7zViewModel();
            // 主窗口只服务于 Gui 模式，显式声明后 ViewModel 才知道提示该写界面还是写控制台。
            _vm.CurrentRunMode = RunMode.Gui;
            DataContext = _vm;

            // ViewModel 不直接持有窗口引用，关闭、最小化、关于都靠事件回传到窗口上执行。
            _vm.RequestClose += (s, e) => Close();
            _vm.RequestMinimize += (s, e) => WindowState = WindowState.Minimized;
            _vm.RequestShowAbout += (s, e) => ShowAboutWindow();

            // 任务结束才复位队列闸门并再拉一次：排队中的文件要等上一个任务真正跑完才能交给 7z。
            _vm.TaskCompleted += (s, e) =>
            {
                Interlocked.Exchange(ref _processingQueue, 0);
                Dispatcher.Invoke(TryStartQueueProcessing);
            };


            _vm.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(_vm.CurrentLanguage))
                {
                    // 换语言后资源字典已替换，工具提示是现取文案的，必须重挂一遍；BeginInvoke 让这一步排在语言应用之后。
                    Dispatcher.BeginInvoke(new Action(AttachSettingToolTips));
                }
            };

            _vm.OutputPathInvalidated += (s, e) =>
            {
                // ViewModel 清掉失效路径时只改自己的字段，输入框里还留着旧字符串，要另外清。
                Dispatcher.BeginInvoke(new Action(() => TextBox_FilePath.Clear()));
            };

            _vm.LoadConfig();
            _vm.ApplyLanguage();

            // 配置灌进控件必须早于事件挂载，顺序反了会先触发一轮写回。
            SyncConfigToControls();
            // 置顶是窗口属性而不是控件状态，绑定带不到，只能手工同步。
            Topmost = _vm.IsTopMost;

            InitializeFormSize();
            InitializeFormControlButton();
            // 运行期的 PropertyChanged 订阅在初始化之后才挂：它只处理任务反馈，不参与配置加载。

            _vm.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(_vm.ResultText))
                {
                    // 结果文案可能在任务线程上产生，用 BeginInvoke 转回界面线程再碰控件。
                    string resultText = _vm.ResultText;
                    bool resultFailed = _vm.IsResultFailed;
                    Dispatcher.BeginInvoke(new Action(() => ShowResult(resultText, resultFailed)));
                }

                // 忙闲切换会改变关闭按钮的提示文案，这里同步刷新。
                if (e.PropertyName == nameof(_vm.IsBusy))
                {
                    Dispatcher.BeginInvoke(new Action(UpdateCloseButtonToolTip));
                }
            };

            // 窗口整体接受拖放，文件落在任何位置都能起任务。
            AllowDrop = true;
            DragEnter += Window_DragEnter;
            Drop += Window_Drop;

            MouseDown += Window_MouseDown;
            // 关闭按钮的左键由 XAML 的命令处理，右键取消要在它上面单独挂一遍。
            CloseButton.MouseDown += CloseButton_MouseDown;

            // TextBox 内部会消化拖放事件，只挂冒泡的 DragOver 与 Drop 收不到，必须同时接 Preview。
            TextBox_FilePath.PreviewDragOver += TextBox_FilePath_PreviewDragOver;
            TextBox_FilePath.PreviewDrop += TextBox_FilePath_PreviewDrop;
            TextBox_FilePath.DragOver += TextBox_FilePath_DragOver;
            TextBox_FilePath.Drop += TextBox_FilePath_Drop;

            // 关窗前要取消任务并回收 7z 进程，统一交给 ViewModel 处理。
            Closing += Window_Closing;

            // 窗口去掉了系统标题栏（WindowChrome 把整窗当作标题栏），拖动、最小化、关闭都只能自己接。
            WindowHelper.EnableWindowDragging(this);

            // 首屏的提示文案同样来自语言资源，构造末尾补一次。
            AttachSettingToolTips();
            UpdateCloseButtonToolTip();
        }

        #endregion
        #region 结果提示与工具提示
        private const int ResultDisplayMilliseconds = 5000;
        // 结果只停留几秒就自动消失，计时器懒建一次并反复重启，避免叠加多个 Tick。
        private DispatcherTimer _resultTimer;

        private void ShowResult(string text, bool failed)
        {
            // 懒建：构造阶段不碰 DispatcherTimer。
            if (_resultTimer == null)
            {
                _resultTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ResultDisplayMilliseconds) };
                _resultTimer.Tick += (s, e) =>
                {
                    _resultTimer.Stop();
                    ClearResult();
                };
            }

            if (string.IsNullOrEmpty(text))
            {
                ClearResult();
                return;
            }

            // 直接写 Text 会压过样式里的绑定与悬停触发器，这里正需要结果文字盖住提示。
            MainText.Text = text;
            MainText.Foreground = failed
                ? (Brush)new SolidColorBrush(Color.FromRgb(0xC0, 0x39, 0x2B))
                : (Brush)new SolidColorBrush(Color.FromRgb(0x1E, 0x88, 0x3B));

            // 每条结果重新计时，免得上一条的 Tick 提前清掉这一条。
            _resultTimer.Stop();
            _resultTimer.Start();
        }

        private void ClearResult()
        {
            // 必须用 ClearValue：只有清掉本地值，样式里的 Text 绑定与悬停提示才会重新生效。
            MainText.ClearValue(TextBlock.TextProperty);
            MainText.ClearValue(TextBlock.ForegroundProperty);
        }

        private void UpdateCloseButtonToolTip()
        {
            // 运行中把提示换成「右键可取消」——这个入口没有按钮，只能靠提示告诉用户。
            string key = _vm.IsBusy ? "Tip_CloseButtonCancel" : "Tip_CloseButton";
            SetToolTip(CloseButton, key);
        }

        private static void SetToolTip(DependencyObject target, string resourceKey)
        {
            if (target == null) return;

            string text = EnumText.GetResourceText(resourceKey, null);
            // 资源缺失时取到 null，此时不设提示，免得留下一个空白的提示框。
            if (string.IsNullOrEmpty(text)) return;

            ToolTipService.SetToolTip(target, text);
        }

        private void AttachSettingToolTips()
        {
            // 提示文案要跟界面语言走，不能写死在 XAML 里，只能运行时按资源键现取。
            SetToolTip(CheckBox_TopMost, "Tip_TopMost");
            SetToolTip(CheckBox_AutoSave, "Tip_AutoSave");
            SetToolTip(CheckBox_Portable, "Tip_Portable");
            SetToolTip(ComboBox_RuntimeMode, "Tip_RuntimeMode");
            SetToolTip(TextBox_FilePath, "Tip_OutputPath");
            SetToolTip(TextBox_Volume, "Tip_Volume");
            SetToolTip(ComboBox_Format, "Tip_Format");
            SetToolTip(CheckBox_SolidMode, "Tip_SolidMode");
            SetToolTip(CheckBox_Zstd, "Tip_Zstd");
            SetToolTip(CheckBox_ENCFileName, "Tip_EncFileName");
            SetToolTip(CheckBox_GenerateSHA256, "Tip_GenerateSHA256");
            SetToolTip(ComboBox_ZipLevel, "Tip_CompressionLevel");
            SetToolTip(ZipPasswordInput, "Tip_ZipPassword");
            SetToolTip(UnZipPasswordInput, "Tip_UnzipPassword");
        }

        #endregion
        #region 取消任务与标题栏按钮
        // 关闭按钮左键是关闭窗口，右键改成取消当前任务：直接关窗会带走正在跑的 7z 并留下半截归档。
        private void CloseButton_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (!TryCancelTaskByRightClick(e)) return;

            e.Handled = true;
        }

        // 整个窗口的右键也走同一条判定，落点是否在关闭按钮内由下面统一核对。
        private void Window_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (!TryCancelTaskByRightClick(e)) return;

            e.Handled = true;
        }

        // 右键事件会从子元素冒泡上来，必须复核落点确实在关闭按钮内，否则窗口任意处右键都会取消任务。
        private bool TryCancelTaskByRightClick(MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Right) return false;
            if (!_vm.IsBusy) return false;

            Point position = e.GetPosition(CloseButton);
            if (position.X < 0 || position.Y < 0
                || position.X > CloseButton.ActualWidth || position.Y > CloseButton.ActualHeight)
            {
                return false;
            }

            _vm.CancelCurrentTask();
            return true;
        }

        #endregion
        #region 窗口尺寸与子窗口
        // 宽高各自钉死最小与最大值，等价于禁止缩放；动画改 Width 时也不会被夹住。
        private void InitializeFormSize()
        {
            Width = MinWidth = MaxWidth = _width;
            Height = MaxHeight = MinHeight = _height;
        }

        private void SetGridColumnLength()
        {
            // 收起时把侧栏那一列压到 0：控件仍留在可视树里，只是不占位置。
            RootGrid.ColumnDefinitions[0].Width = GridLength.Auto;
            RootGrid.ColumnDefinitions[1].Width = _isExpanded ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        }

        private void SetSidePanelVisibility()
        {
            SidePanel.Visibility = _isExpanded ? Visibility.Visible : Visibility.Collapsed;
        }

        private void InitializeFormControlButton()
        {
            SourceInitialized += (s, e) =>
            {
                // 句柄创建前拿不到 HWND，改窗口样式只能挂到 SourceInitialized 上。
                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                int currentStyle = GetWindowLong(hwnd, GWL_STYLE);

                // 摘掉 WS_MAXIMIZEBOX 之后，任务栏右键菜单里的「最大化」随之变灰。
                SetWindowLong(hwnd, GWL_STYLE, currentStyle & ~WS_MAXIMIZEBOX);
            };
        }

        // 关于窗口是独立窗口，重复点击只激活已开的那个，不再开第二个。
        private void ShowAboutWindow()
        {
            var existingWindow = Application.Current.Windows.OfType<AboutWindow>().FirstOrDefault();
            if (existingWindow != null)
            {
                if (existingWindow.WindowState == WindowState.Minimized) existingWindow.WindowState = WindowState.Normal;
                existingWindow.Activate();
                existingWindow.Focus();
            }
            else
            {
                // 设 Owner 才能跟随主窗口的层级与最小化，也让它在任务栏归组。
                var aboutWindow = new AboutWindow { Owner = this };
                aboutWindow.Show();
            }
        }

        private void BrowseOutputFolder_Click(object sender, RoutedEventArgs e)
        {
            // WPF 在 .NET Framework 4.7.2 上没有选目录的对话框，借用 WinForms 那个。
            using (var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                // 对话框描述按当前语言取，取不到时退回英文兜底。
                Description = EnumText.GetResourceText("OutputPathLabel", "Output path:")
            })
            {
                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK && !string.IsNullOrWhiteSpace(dialog.SelectedPath))
                {
                    TextBox_FilePath.Text = dialog.SelectedPath;
                }
            }
        }

        #endregion
        #region 侧栏展开与收起
        // 一次点击要同时改窗口宽度、列宽、侧栏可见性与透明度四处，状态先落在 _isExpanded 上。
        private void SettingBtn_Click(object sender, RoutedEventArgs e)
        {
            _isExpanded = !_isExpanded;
            // Tag 供样式里的 DataTrigger 使用，按钮外观的切换靠它。
            SettingButton.Tag = _isExpanded ? "Expanded" : "Collapsed";

            // 动画期间不能让 Min/Max 夹住宽度：目标值先算好，等动画结束再钉死。
            double maxWidth = _width * 2;
            double minWidth = _width;

            double targetWindowWidth = _isExpanded ? maxWidth : minWidth;
            double targetOpacity = _isExpanded ? 1 : 0;

            SetGridColumnLength();
            SetSidePanelVisibility();

            TextBox_FilePath.Width = _filePathWidth;

            if (_isExpanded)
            {
                // 展开要先放宽 MaxWidth，收起要先放宽 MinWidth，否则当前值会把动画卡在半路。
                MaxWidth = maxWidth;
            }
            else
            {
                MinWidth = minWidth;
            }

            // 宽度与透明度共用一条缓动曲线，收起时侧栏是淡出而不是瞬间消失。
            DoubleAnimation windowAnim = new DoubleAnimation
            {
                To = targetWindowWidth,
                Duration = TimeSpan.FromSeconds(0.2),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };

            DoubleAnimation opacityAnim = new DoubleAnimation
            {
                To = targetOpacity,
                Duration = TimeSpan.FromSeconds(0.2),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };

            windowAnim.Completed += (s, _) =>
            {
                // 动画收尾把宽度钉死在目标值，避免停在一个中间态。
                MinWidth = targetWindowWidth;
                MaxWidth = targetWindowWidth;
                SetSidePanelVisibility();
            };

            // 列宽与可见性在动画开始前就切好，否则侧栏内容会在错误的宽度下重排。
            BeginAnimation(WidthProperty, windowAnim);
            SidePanel.BeginAnimation(OpacityProperty, opacityAnim);
        }

        #endregion
        #region 配置同步与事件绑定
        private void SyncConfigToControls()
        {
            // 回填期间必须挂起控件事件：属性变更触发的回调会把刚读出的配置又写回 ViewModel。
            _isSyncingControls = true;
            try
            {
                TextBox_Volume.Text = _vm.Volume;
                // 归档格式在界面上是文案，ViewModel 存的是枚举，中间靠 ArchiveFormatMap 转换。
                SelectComboBoxItem(ComboBox_Format, ArchiveFormatMap.ToSwitchValue(_vm.Format));
                ZipPasswordInput.Text = _vm.Password;
                CheckBox_Zstd.IsChecked = _vm.Zstd;
                CheckBox_SolidMode.IsChecked = _vm.IsSolidMode;
                CheckBox_ENCFileName.IsChecked = _vm.IsEncFileName;
                CheckBox_GenerateSHA256.IsChecked = _vm.GenerateSHA256;
                SelectComboBoxItem(ComboBox_ZipLevel, EnumText.ToText(_vm.CompressionLevel));

                CheckBox_TopMost.IsChecked = _vm.IsTopMost;
                CheckBox_Portable.IsChecked = _vm.IsPortable;
                CheckBox_AutoSave.IsChecked = _vm.AutoSave;
                SelectComboBoxItem(ComboBox_RuntimeMode, EnumText.ToText(_vm.RuntimeMode));
                TextBox_FilePath.Text = _vm.OutputPath;

                UnZipPasswordInput.Text = _vm.UnzipPassword;

                _vm.ZipPasswordInputText = _vm.Password;
            }
            finally
            {
                // 抑制只覆盖回填这一段，回填结束立刻复位，不能跨到用户操作之后。
                _isSyncingControls = false;
            }

            // 回填全部结束后才接事件，顺序反了会先跑一轮写回。
            RegisterControlEvents();
        }


        // 按内容文本匹配而不是按索引：项的顺序或语言变了也不会选错。
        private static void SelectComboBoxItem(ComboBox comboBox, string content)
        {
            if (comboBox == null) return;

            var match = comboBox.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(i => string.Equals(i.Content?.ToString(), content, StringComparison.Ordinal));

            // 匹配不到就退回第一项，界面上不留空选择。
            comboBox.SelectedItem = match ?? comboBox.Items.OfType<ComboBoxItem>().FirstOrDefault();
        }

        private static string GetSelectedComboBoxContent(ComboBox comboBox)
        {
            return (comboBox?.SelectedItem as ComboBoxItem)?.Content?.ToString();
        }

        // 先摘再挂：这个方法可能被重复调用，不去重会让同一个处理器叠加执行。
        private void RegisterControlEvents()
        {
            TextBox_Volume.TextChanged -= TextBox_Volume_TextChanged;
            TextBox_Volume.TextChanged += TextBox_Volume_TextChanged;

            ComboBox_Format.SelectionChanged -= ComboBox_Format_SelectionChanged;
            ComboBox_Format.SelectionChanged += ComboBox_Format_SelectionChanged;

            ZipPasswordInput.TextChanged -= ZipPasswordInput_TextChanged;
            ZipPasswordInput.TextChanged += ZipPasswordInput_TextChanged;

            // 复选框统一走 HookCheckBox，省得每处把 Checked 与 Unchecked 各写一遍。
            HookCheckBox(CheckBox_Zstd, CheckBox_Zstd_CheckedChanged);
            HookCheckBox(CheckBox_AutoSave, CheckBox_AutoSave_CheckedChanged);
            HookCheckBox(CheckBox_TopMost, CheckBox_TopMost_CheckedChanged);
            HookCheckBox(CheckBox_Portable, CheckBox_Portable_CheckedChanged);
            HookCheckBox(CheckBox_SolidMode, CheckBox_SolidMode_CheckedChanged);
            HookCheckBox(CheckBox_ENCFileName, CheckBox_ENCFileName_CheckedChanged);
            HookCheckBox(CheckBox_GenerateSHA256, CheckBox_GenerateSHA256_CheckedChanged);

            ComboBox_RuntimeMode.SelectionChanged -= ComboBox_RuntimeMode_SelectionChanged;
            ComboBox_RuntimeMode.SelectionChanged += ComboBox_RuntimeMode_SelectionChanged;

            ComboBox_ZipLevel.SelectionChanged -= ComboBox_ZipLevel_SelectionChanged;
            ComboBox_ZipLevel.SelectionChanged += ComboBox_ZipLevel_SelectionChanged;

            TextBox_FilePath.TextChanged -= TextBox_FilePath_TextChanged;
            TextBox_FilePath.TextChanged += TextBox_FilePath_TextChanged;

            UnZipPasswordInput.TextChanged -= UnZipPasswordInput_TextChanged;
            UnZipPasswordInput.TextChanged += UnZipPasswordInput_TextChanged;
        }

        // 两个事件共用一个处理器，回调只读 IsChecked，不关心是勾上还是取消。
        private static void HookCheckBox(CheckBox checkBox, RoutedEventHandler handler)
        {
            checkBox.Checked += handler;
            checkBox.Unchecked += handler;
        }

        #endregion
        #region 控件事件处理
        // 每个处理器开头都要放过回填期的回调，否则会拿控件的中间态覆盖配置。
        private void TextBox_Volume_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isSyncingControls) return;

            string text = TextBox_Volume.Text.Trim();

            if (text.Length > 0 && (!long.TryParse(text, out long volume) || volume < 0))
            {
                // 非法值只标红并换提示，不回滚输入——强制改回去反而妨碍用户接着编辑。
                TextBox_Volume.Background = System.Windows.Media.Brushes.MistyRose;
                SetToolTip(TextBox_Volume, "Tip_Volume_Invalid");
                return;
            }

            // 恢复常态外观之后，再把合法值写进 ViewModel。
            TextBox_Volume.Background = null;
            SetToolTip(TextBox_Volume, "Tip_Volume");
            _vm.Volume = text;
        }

        private void ComboBox_Format_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isSyncingControls) return;

            // 界面显示归档格式名，ViewModel 要的是枚举，转换放在映射表里。
            var format = ArchiveFormatMap.Parse(GetSelectedComboBoxContent(ComboBox_Format));
            if (format.HasValue) _vm.Format = format.Value;
        }

        private void ZipPasswordInput_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isSyncingControls) return;

            // 压缩密码有两处消费者：任务用 Password，界面用 ZipPasswordInputText，两个都要更新。
            _vm.Password = ZipPasswordInput.Text;
            _vm.ZipPasswordInputText = ZipPasswordInput.Text;
        }

        private void CheckBox_Zstd_CheckedChanged(object sender, RoutedEventArgs e)
        {
            if (_isSyncingControls) return;
            _vm.Zstd = CheckBox_Zstd.IsChecked == true;
        }

        private void CheckBox_AutoSave_CheckedChanged(object sender, RoutedEventArgs e)
        {
            if (_isSyncingControls) return;
            _vm.AutoSave = CheckBox_AutoSave.IsChecked == true;
        }

        private void CheckBox_TopMost_CheckedChanged(object sender, RoutedEventArgs e)
        {
            if (_isSyncingControls) return;

            // 置顶是窗口属性，勾选后要立刻作用到窗口上。
            _vm.IsTopMost = CheckBox_TopMost.IsChecked == true;
            Topmost = _vm.IsTopMost;
        }

        private async void CheckBox_Portable_CheckedChanged(object sender, RoutedEventArgs e)
        {
            if (_isSyncingControls) return;

            bool portable = CheckBox_Portable.IsChecked == true;
            try
            {
                // 切换便携模式要搬配置目录、改注册表，放到线程池执行，界面不跟着卡。
                await System.Threading.Tasks.Task.Run(() => _vm.IsPortable = portable).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                _logService.WriteLog(EnumText.GetErrorText("Log_PortableSwitchFailed", "切换便携模式失败：{0}", ex.Message), LogService.ErrorLevel.Error);
                // 失败就把勾选翻回去，不让界面显示一个并未生效的开关。
                CheckBox_Portable.IsChecked = !portable;
            }
        }

        private void ComboBox_RuntimeMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isSyncingControls) return;

            var text = GetSelectedComboBoxContent(ComboBox_RuntimeMode);
            if (!string.IsNullOrWhiteSpace(text)) _vm.RuntimeMode = EnumText.ParseRuntimeMode(text);
        }

        private void CheckBox_SolidMode_CheckedChanged(object sender, RoutedEventArgs e)
        {
            if (_isSyncingControls) return;
            _vm.IsSolidMode = CheckBox_SolidMode.IsChecked == true;
        }

        private void CheckBox_ENCFileName_CheckedChanged(object sender, RoutedEventArgs e)
        {
            if (_isSyncingControls) return;
            _vm.IsEncFileName = CheckBox_ENCFileName.IsChecked == true;
        }

        private void CheckBox_GenerateSHA256_CheckedChanged(object sender, RoutedEventArgs e)
        {
            if (_isSyncingControls) return;
            _vm.GenerateSHA256 = CheckBox_GenerateSHA256.IsChecked == true;
        }

        private void ComboBox_ZipLevel_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isSyncingControls) return;

            var text = GetSelectedComboBoxContent(ComboBox_ZipLevel);
            if (!string.IsNullOrWhiteSpace(text)) _vm.CompressionLevel = EnumText.ParseCompressionLevel(text);
        }

        private void UnZipPasswordInput_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isSyncingControls) return;
            _vm.UnzipPassword = UnZipPasswordInput.Text;
        }

        private void TextBox_FilePath_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isSyncingControls) return;
            _vm.OutputPath = TextBox_FilePath.Text;
        }

        #endregion
        #region 拖放与路径解析
        // 拖到输入框上的路径要落到输出目录，先看能不能解析出目录，再决定光标效果。
        private void TextBox_FilePath_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = TryGetOutputPathFromDrop(e) != null ? DragDropEffects.Copy : DragDropEffects.None;

            e.Handled = true;
        }

        private void TextBox_FilePath_PreviewDragOver(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;

            e.Effects = TryGetOutputPathFromDrop(e) != null ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void TextBox_FilePath_Drop(object sender, DragEventArgs e)
        {
            string path = TryGetOutputPathFromDrop(e);
            if (path == null) return;

            TextBox_FilePath.Text = path;
            e.Handled = true;
        }

        private void TextBox_FilePath_PreviewDrop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;

            string path = TryGetOutputPathFromDrop(e);
            if (path == null) return;

            TextBox_FilePath.Text = path;
            e.Handled = true;
        }

        private static string TryGetOutputPathFromDrop(DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return null;

            var paths = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (paths == null || paths.Length == 0) return null;

            // 逐个 try：权限不足或已失效的项会抛异常，不能让它拖累同一批里的其它路径。
            foreach (var p in paths)
            {
                try
                {
                    // 拖进来的条目可能是文件也可能是目录：目录直接用，文件取它所在的目录。
                    if (Directory.Exists(p)) return p;

                    if (File.Exists(p))
                    {
                        string dir = Path.GetDirectoryName(p);
                        if (!string.IsNullOrWhiteSpace(dir)) return dir;
                    }
                }
                catch
                {
                    // 非法路径跳过
                }
            }

            return null;
        }


        // 只有拖进来的是文件或目录才给 Copy 光标。
        private void Window_DragEnter(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        }

        // 落在窗口空白处直接起任务，与单实例转发走的那条入队路径是两回事。
        private void Window_Drop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;

            var files = e.Data.GetData(DataFormats.FileDrop) as string[];
            StartTaskForFiles(files);
        }

        #endregion
        #region 队列与任务启动
        // 单实例转发进来的列表可能为空，空批直接忽略。
        public void EnqueueFiles(string[] files)
        {
            EnqueueFiles(files, null);
        }

        /// <summary>
        /// 入队一批路径，可携带交接来的模式开关。
        /// </summary>
        /// <param name="files">待处理路径。</param>
        /// <param name="handedMode">第二个实例带来的模式（-a / -z / -uz），null 表示沿用界面模式。</param>
        public void EnqueueFiles(string[] files, string handedMode)
        {
            if (files == null || files.Length == 0) return;

            // 入队本身线程安全，界面线程与命名管道回调都可能走到这里。
            foreach (string file in files) _pendingQueue.Enqueue(new QueuedFile(file, handedMode));

            // 有任务在跑就只入队，等 TaskCompleted 再来取，保证一次只跑一个任务。
            if (_vm.IsBusy) return;

            TryStartQueueProcessing();
        }

        // 队列闸门：0 表示没有批次在处理，1 表示已有批次跑在路上。
        private int _processingQueue = 0;

        private void TryStartQueueProcessing()
        {
            // 比较交换当闸门：多少调用方同时进来，也只有一个能继续往下走。
            if (Interlocked.CompareExchange(ref _processingQueue, 1, 0) != 0) return;

            ProcessNextQueued();
        }

        private void ProcessNextQueued()
        {
            try
            {
                var batch = new List<string>();
                string batchMode = null;
                bool modeKnown = false;

                // 一次把队列取空并成一批，免得每个文件都单独起一次 7z。
                // 同一批只认第一个带模式的项：交接是按批进行的，一批里模式不该变。
                while (_pendingQueue.TryDequeue(out QueuedFile item))
                {
                    batch.Add(item.Path);
                    if (!modeKnown && item.Mode != null)
                    {
                        batchMode = item.Mode;
                        modeKnown = true;
                    }
                }

                if (batch.Count == 0) return;

                StartTaskForFiles(batch.ToArray(), batchMode);
            }
            finally
            {
                if (!_vm.IsBusy)
                {
                    // 只有任务没跑起来（例如路径全失效）才立刻放开闸门，否则等 TaskCompleted 复位。
                    Interlocked.Exchange(ref _processingQueue, 0);
                }
            }
        }

        // 先筛掉不存在或不可访问的路径，剩下的才值得起任务。
        private void StartTaskForFiles(string[] files)
        {
            StartTaskForFiles(files, null);
        }

        // 先筛掉不存在或不可访问的路径，剩下的才值得起任务。
        private void StartTaskForFiles(string[] files, string handedMode)
        {
            if (files == null || files.Length == 0) return;

            var existing = new List<string>();
            foreach (var file in files)
            {
                if (string.IsNullOrWhiteSpace(file)) continue;

                try
                {
                    if (File.Exists(file) || Directory.Exists(file)) existing.Add(file);
                }
                catch
                {
                    // 非法路径直接跳过
                }
            }

            // 整批都不可用时只记日志：这些路径是外部传进来的，没有界面可以问用户。
            if (existing.Count == 0)
            {
                _logService.WriteLog(
                    EnumText.GetErrorText("Log_PathsNotFound", "传入的路径均不存在或不可访问：{0}", string.Join("; ", files)),
                    LogService.ErrorLevel.Warning);
                return;
            }

            // 队列已保证串行，这里再挡一次，防止直接调用与排队批次撞在一起。
            if (_vm.IsBusy)
            {
                _logService.WriteLog(
                    EnumText.GetErrorText("Log_BusyIgnore", "已有任务正在执行，忽略本次传入的 {0} 个项目", existing.Count),
                    LogService.ErrorLevel.Warning);
                return;
            }

            _logService.WriteLog(
                EnumText.GetErrorText("Log_ReceivedItems", "接收 {0} 个项目：{1}",
                    existing.Count, string.Join("; ", existing)),
                LogService.ErrorLevel.Info);

            // 这里只传路径，目录大小由 ViewModel 交给任务线程再统计，界面线程不做递归遍历。
            _vm.SetPendingFiles(existing);

            StartModeSpecificTask(handedMode);
        }

        #endregion
        #region 交接模式与任务起法
        /// <summary>
        /// 按「交接模式优先、其次界面模式」决定起哪种任务。
        ///
        /// 「交接模式优先」是必须的：第二个实例是「右键解压」时带着 -uz 来的，
        /// 接收方界面可能停在压缩模式，若听界面的就会把分卷压成包 ——
        /// 用户点了解压却得到压缩，而这正是曾经的真实缺陷。
        /// </summary>
        private void StartModeSpecificTask(string handedMode)
        {
            OperationMode mode = handedMode != null
                ? ParseHandedMode(handedMode)
                : _vm.CurrentOperationMode;

            switch (mode)
            {
                case OperationMode.UnZip:
                    _vm.BeginExtractTask();
                    break;

                case OperationMode.Auto:
                    _vm.BeginAutoDispatchTask();
                    break;

                default:
                    _vm.BeginCompressTask();
                    break;
            }
        }

        /// <summary>把交接来的模式开关换成枚举，无法识别时按自动处理。</summary>
        private static OperationMode ParseHandedMode(string mode)
        {
            if (string.Equals(mode, ShellCommandService.SwitchZip, StringComparison.OrdinalIgnoreCase))
                return OperationMode.Zip;
            if (string.Equals(mode, ShellCommandService.SwitchUnZip, StringComparison.OrdinalIgnoreCase))
                return OperationMode.UnZip;
            return OperationMode.Auto;
        }

        // 关闭时先取消任务再杀 7z 进程：只发取消信号不杀进程，7z 会带着半截文件继续跑。
        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            _vm.OnClosing();
        }
        #endregion
    }
}
