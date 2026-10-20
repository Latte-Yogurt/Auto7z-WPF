using Auto7z_WPF.Models;
using Auto7z_WPF.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Auto7z_WPF
{
    /// <summary>
    /// 主窗口。只负责视图相关职责：外观、动画、控件与视图模型之间的双向同步、拖放事件。
    /// 业务逻辑全部在 <see cref="Auto7zViewModel"/> 与 Service 层。
    /// </summary>
    public partial class MainWindow : Window
    {
        private readonly LogService _logService = new LogService();

        private readonly int _width = 260;
        private readonly int _height = 260;
        private readonly int _filePathWidth = 160;

        public static string currentLanguage;

        private bool _isExpanded;
        private bool _isSyncingControls;

        /// <summary>排队等待处理的文件。连续右键时后面的批次在这里等。</summary>
        private readonly Queue<string> _pendingQueue = new Queue<string>();

        private readonly Auto7zViewModel _vm;

        #region 原生窗口接口
        private const int GWL_STYLE = -16;
        private const int WS_MAXIMIZEBOX = 0x00010000;

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
        #endregion

        public MainWindow()
        {
            InitializeComponent();
            SetGridColumnLength();
            SetSidePanelVisibility();

            // 创建 ViewModel：先加载配置，再同步到控件（不再用硬编码值覆盖刚刚读出的设置）
            _vm = new Auto7zViewModel();
            DataContext = _vm;

            _vm.RequestClose += (s, e) => Close();
            _vm.RequestMinimize += (s, e) => WindowState = WindowState.Minimized;
            _vm.RequestShowAbout += (s, e) => ShowAboutWindow();

            // 上一个任务跑完后接着处理排队的那一批
            _vm.TaskCompleted += (s, e) => Dispatcher.Invoke(ProcessNextQueued);


            // 语言切换后重新套用提示文本，保证提示跟随当前语言
            _vm.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(_vm.CurrentLanguage))
                {
                    Dispatcher.BeginInvoke(new Action(AttachSettingToolTips));
                }
            };

            // 输出路径非法时清空输入框留白
            _vm.OutputPathInvalidated += (s, e) =>
            {
                Dispatcher.BeginInvoke(new Action(() => TextBox_FilePath.Clear()));
            };

            _vm.LoadConfig();
            _vm.ApplyLanguage();
            currentLanguage = _vm.CurrentLanguage;

            SyncConfigToControls();
            Topmost = _vm.IsTopMost;

            InitializeFormSize();
            InitializeFormControlButton();

            // MainText 走绑定, 不在这里赋值。
            // XAML 里直接写的值优先级高于 Style 的 DataTrigger,
            // 一旦赋值, 悬停时 Style 里的触发器就换不掉文本了。
            _vm.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(_vm.MainText))
                {
                    // 绑定会自己更新
                }

                // 任务结果：显示彩色文本，5 秒后自动恢复
                if (e.PropertyName == nameof(_vm.ResultText))
                {
                    // 任务跑在后台线程, 改控件要封送到界面线程
                    string resultText = _vm.ResultText;
                    bool resultFailed = _vm.IsResultFailed;
                    Dispatcher.BeginInvoke(new Action(() => ShowResult(resultText, resultFailed)));
                }

                // 任务开始或结束时，关闭按钮的提示文字随之变化
                if (e.PropertyName == nameof(_vm.IsBusy))
                {
                    Dispatcher.BeginInvoke(new Action(UpdateCloseButtonToolTip));
                }
            };

            // 注册拖放事件
            AllowDrop = true;
            DragEnter += Window_DragEnter;
            Drop += Window_Drop;

            // 关闭按钮：任务运行中右键可终止任务
            MouseDown += Window_MouseDown;
            CloseButton.MouseDown += CloseButton_MouseDown;

            // 输出路径输入框的拖放（控件已在 XAML 中声明 AllowDrop="True"）
            // 预览阶段先接管，避免 TextBox 把文件当成文本拖放处理
            TextBox_FilePath.PreviewDragOver += TextBox_FilePath_PreviewDragOver;
            TextBox_FilePath.PreviewDrop += TextBox_FilePath_PreviewDrop;
            TextBox_FilePath.DragOver += TextBox_FilePath_DragOver;
            TextBox_FilePath.Drop += TextBox_FilePath_Drop;

            // 窗口关闭时保存配置并清理
            Closing += Window_Closing;

            WindowHelper.EnableWindowDragging(this);

            AttachSettingToolTips();
            UpdateCloseButtonToolTip();
        }

        #region 结果提示

        private const int ResultDisplayMilliseconds = 5000;
        private DispatcherTimer _resultTimer;

        /// <summary>
        /// 显示任务结果。
        ///
        /// 结果期间用 local value 直接写 Text 与 Foreground：
        /// local value 的优先级高于 Style 里的 Setter 与 DataTrigger，
        /// 因此结果文本不会被悬停提示覆盖，颜色也能生效。
        /// 5 秒后清掉 local value，界面随即回到常规的悬停提示。
        /// </summary>
        private void ShowResult(string text, bool failed)
        {
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

            MainText.Text = text;
            MainText.Foreground = failed
                ? (Brush)new SolidColorBrush(Color.FromRgb(0xC0, 0x39, 0x2B))
                : (Brush)new SolidColorBrush(Color.FromRgb(0x1E, 0x88, 0x3B));

            _resultTimer.Stop();
            _resultTimer.Start();
        }

        /// <summary>
        /// 清除结果提示，界面回到常规的悬停提示。
        /// </summary>
        private void ClearResult()
        {
            // 清除 local value 后，Text 重新由 Style 的绑定提供
            MainText.ClearValue(TextBlock.TextProperty);
            MainText.ClearValue(TextBlock.ForegroundProperty);
        }

        #endregion

        /// <summary>
        /// 为设置面板中的选项挂上悬停提示（功能说明）。
        ///
        /// 全部在代码里完成，不改动 XAML，因此不涉及任何外观变化；
        /// 提示文本取自语言资源字典，切换语言后会自动跟随。
        /// </summary>
        private void AttachSettingToolTips()
        {
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
            SetToolTip(CheckBox_GenerateMD5, "Tip_GenerateMD5");
            SetToolTip(ComboBox_ZipLevel, "Tip_CompressionLevel");
            SetToolTip(PasswordBox_Zip, "Tip_ZipPassword");
            SetToolTip(PasswordBox_UnZip, "Tip_UnzipPassword");
        }

        /// <summary>
        /// 关闭按钮的悬停提示。
        ///
        /// 任务运行时右键该按钮可终止任务，提示里要写明这一点；
        /// 空闲时没有可终止的任务，只提示关闭作用。
        /// </summary>
        private void UpdateCloseButtonToolTip()
        {
            string key = _vm.IsBusy ? "Tip_CloseButtonCancel" : "Tip_CloseButton";
            SetToolTip(CloseButton, key);
        }

        /// <summary>
        /// 给控件设置悬停提示，文本从语言资源字典读取，取不到时忽略。
        /// </summary>
        private static void SetToolTip(DependencyObject target, string resourceKey)
        {
            if (target == null) return;

            string text = EnumText.GetResourceText(resourceKey, null);
            if (string.IsNullOrEmpty(text)) return;

            ToolTipService.SetToolTip(target, text);
        }



        #region 界面与交互

        /// <summary>
        /// 关闭按钮的右键：任务运行中时终止当前任务。
        ///
        /// 在窗口与关闭按钮上都监听鼠标按下：
        /// 关闭按钮是 Grid（不可获焦），右键不一定能路由到它，
        /// 挂在窗口这一层作为兜底，两者任一收到即可。
        /// 用 MouseDown 而不是 MouseRightButtonDown，是因为后者在部分情况下不触发。
        /// </summary>
        private void CloseButton_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (!TryCancelTaskByRightClick(e)) return;

            e.Handled = true;
        }

        private void Window_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (!TryCancelTaskByRightClick(e)) return;

            e.Handled = true;
        }

        /// <summary>
        /// 判断这次鼠标按下是不是“在关闭按钮上按右键”，是则终止当前任务。
        /// </summary>
        /// <returns>已处理时返回 true。</returns>
        private bool TryCancelTaskByRightClick(MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Right) return false;
            if (!_vm.IsBusy) return false;

            // 判断鼠标落点是否在关闭按钮范围内
            Point position = e.GetPosition(CloseButton);
            if (position.X < 0 || position.Y < 0
                || position.X > CloseButton.ActualWidth || position.Y > CloseButton.ActualHeight)
            {
                return false;
            }

            _vm.CancelCurrentTask();
            return true;
        }

        private void InitializeFormSize()
        {
            Width = MinWidth = MaxWidth = _width;
            Height = MaxHeight = MinHeight = _height;
        }

        private void SetGridColumnLength()
        {
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
                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                int currentStyle = GetWindowLong(hwnd, GWL_STYLE);

                // 移除最大化按钮样式 , 核心作用是禁止拖拽吸附
                SetWindowLong(hwnd, GWL_STYLE, currentStyle & ~WS_MAXIMIZEBOX);
            };
        }

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
                var aboutWindow = new AboutWindow { Owner = this };
                aboutWindow.Show();
            }
        }

        private void BrowseOutputFolder_Click(object sender, RoutedEventArgs e)
        {
            using (var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = EnumText.GetResourceText("OutputPathLabel", "Output path:")
            })
            {
                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK && !string.IsNullOrWhiteSpace(dialog.SelectedPath))
                {
                    TextBox_FilePath.Text = dialog.SelectedPath;
                }
            }
        }

        private void SettingBtn_Click(object sender, RoutedEventArgs e)
        {
            _isExpanded = !_isExpanded;
            SettingButton.Tag = _isExpanded ? "Expanded" : "Collapsed";

            double maxWidth = _width * 2;
            double minWidth = _width;

            double targetWindowWidth = _isExpanded ? maxWidth : minWidth;
            double targetOpacity = _isExpanded ? 1 : 0;

            SetGridColumnLength();
            SetSidePanelVisibility();

            TextBox_FilePath.Width = _filePathWidth;

            if (_isExpanded)
            {
                MaxWidth = maxWidth;
            }
            else
            {
                MinWidth = minWidth;
            }

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
                MinWidth = targetWindowWidth;
                MaxWidth = targetWindowWidth;
                SetSidePanelVisibility();
            };

            BeginAnimation(WidthProperty, windowAnim);
            SidePanel.BeginAnimation(OpacityProperty, opacityAnim);
        }

        #endregion

        #region 配置与控件同步

        /// <summary>
        /// 将视图模型的配置同步到设置面板控件。同步期间抑制回写，避免把程序默认值写回配置。
        /// </summary>
        private void SyncConfigToControls()
        {
            _isSyncingControls = true;
            try
            {
                // 压缩面板
                TextBox_Volume.Text = _vm.Volume;
                SelectComboBoxItem(ComboBox_Format, ArchiveFormatMap.ToSwitchValue(_vm.Format));
                PasswordBox_Zip.Text = _vm.Password;
                CheckBox_Zstd.IsChecked = _vm.Zstd;
                CheckBox_SolidMode.IsChecked = _vm.IsSolidMode;
                CheckBox_ENCFileName.IsChecked = _vm.IsEncFileName;
                CheckBox_GenerateMD5.IsChecked = _vm.GenerateMd5;
                SelectComboBoxItem(ComboBox_ZipLevel, EnumText.ToText(_vm.CompressionLevel));

                // 基础设置面板
                CheckBox_TopMost.IsChecked = _vm.IsTopMost;
                CheckBox_Portable.IsChecked = _vm.IsPortable;
                CheckBox_AutoSave.IsChecked = _vm.AutoSave;
                SelectComboBoxItem(ComboBox_RuntimeMode, EnumText.ToText(_vm.RuntimeMode));
                TextBox_FilePath.Text = _vm.OutputPath;

                // 解压面板
                PasswordBox_UnZip.Text = _vm.UnzipPassword;

                // 同步密码有效性（用于控制“完全加密”是否可用）
                _vm.PasswordBox_Zip_Text = _vm.Password;
            }
            finally
            {
                _isSyncingControls = false;
            }

            RegisterControlEvents();
        }

        private static void SelectComboBoxItem(ComboBox comboBox, string content)
        {
            if (comboBox == null) return;

            var match = comboBox.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(i => string.Equals(i.Content?.ToString(), content, StringComparison.Ordinal));

            comboBox.SelectedItem = match ?? comboBox.Items.OfType<ComboBoxItem>().FirstOrDefault();
        }

        private static string GetSelectedComboBoxContent(ComboBox comboBox)
        {
            return (comboBox?.SelectedItem as ComboBoxItem)?.Content?.ToString();
        }

        /// <summary>
        /// 注册控件事件。先解绑再绑定，保证不会重复注册。
        /// </summary>
        private void RegisterControlEvents()
        {
            TextBox_Volume.TextChanged -= TextBox_Volume_TextChanged;
            TextBox_Volume.TextChanged += TextBox_Volume_TextChanged;

            ComboBox_Format.SelectionChanged -= ComboBox_Format_SelectionChanged;
            ComboBox_Format.SelectionChanged += ComboBox_Format_SelectionChanged;

            PasswordBox_Zip.TextChanged -= PasswordBox_Zip_TextChanged;
            PasswordBox_Zip.TextChanged += PasswordBox_Zip_TextChanged;

            HookCheckBox(CheckBox_Zstd, CheckBox_Zstd_CheckedChanged);
            HookCheckBox(CheckBox_AutoSave, CheckBox_AutoSave_CheckedChanged);
            HookCheckBox(CheckBox_TopMost, CheckBox_TopMost_CheckedChanged);
            HookCheckBox(CheckBox_Portable, CheckBox_Portable_CheckedChanged);
            HookCheckBox(CheckBox_SolidMode, CheckBox_SolidMode_CheckedChanged);
            HookCheckBox(CheckBox_ENCFileName, CheckBox_ENCFileName_CheckedChanged);
            HookCheckBox(CheckBox_GenerateMD5, CheckBox_GenerateMD5_CheckedChanged);

            ComboBox_RuntimeMode.SelectionChanged -= ComboBox_RuntimeMode_SelectionChanged;
            ComboBox_RuntimeMode.SelectionChanged += ComboBox_RuntimeMode_SelectionChanged;

            ComboBox_ZipLevel.SelectionChanged -= ComboBox_ZipLevel_SelectionChanged;
            ComboBox_ZipLevel.SelectionChanged += ComboBox_ZipLevel_SelectionChanged;

            TextBox_FilePath.TextChanged -= TextBox_FilePath_TextChanged;
            TextBox_FilePath.TextChanged += TextBox_FilePath_TextChanged;

            PasswordBox_UnZip.TextChanged -= PasswordBox_UnZip_TextChanged;
            PasswordBox_UnZip.TextChanged += PasswordBox_UnZip_TextChanged;
        }

        private static void HookCheckBox(CheckBox checkBox, RoutedEventHandler handler)
        {
            checkBox.Checked -= handler;
            checkBox.Unchecked -= handler;
            checkBox.Checked += handler;
            checkBox.Unchecked += handler;
        }

        private void TextBox_Volume_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isSyncingControls) return;

            string text = TextBox_Volume.Text.Trim();

            // 允许用户临时清空输入框（正在编辑），但非法值会给出提示：
            // 直接传给 7-Zip 会得到退出码 7（命令行语法错误），用户完全看不懂。
            // 0 是合法值，表示“不分卷”。
            if (text.Length > 0 && (!long.TryParse(text, out long volume) || volume < 0))
            {
                TextBox_Volume.Background = System.Windows.Media.Brushes.MistyRose;
                SetToolTip(TextBox_Volume, "Tip_Volume_Invalid");
                return;
            }

            TextBox_Volume.Background = null;
            SetToolTip(TextBox_Volume, "Tip_Volume");
            _vm.Volume = text;
        }

        private void ComboBox_Format_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isSyncingControls) return;

            var format = ArchiveFormatMap.Parse(GetSelectedComboBoxContent(ComboBox_Format));
            if (format.HasValue) _vm.Format = format.Value;
        }

        private void PasswordBox_Zip_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isSyncingControls) return;

            _vm.Password = PasswordBox_Zip.Text;
            _vm.PasswordBox_Zip_Text = PasswordBox_Zip.Text;
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

            _vm.IsTopMost = CheckBox_TopMost.IsChecked == true;
            Topmost = _vm.IsTopMost;
        }

        private void CheckBox_Portable_CheckedChanged(object sender, RoutedEventArgs e)
        {
            if (_isSyncingControls) return;

            // 切换便携模式会搬动整个数据目录，耗时略长，放到后台做，界面不卡
            bool portable = CheckBox_Portable.IsChecked == true;
            System.Threading.Tasks.Task.Run(() => _vm.IsPortable = portable);
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

        private void CheckBox_GenerateMD5_CheckedChanged(object sender, RoutedEventArgs e)
        {
            if (_isSyncingControls) return;
            _vm.GenerateMd5 = CheckBox_GenerateMD5.IsChecked == true;
        }

        private void ComboBox_ZipLevel_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isSyncingControls) return;

            var text = GetSelectedComboBoxContent(ComboBox_ZipLevel);
            if (!string.IsNullOrWhiteSpace(text)) _vm.CompressionLevel = EnumText.ParseCompressionLevel(text);
        }

        private void PasswordBox_UnZip_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isSyncingControls) return;
            _vm.UnzipPassword = PasswordBox_UnZip.Text;
        }

        private void TextBox_FilePath_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isSyncingControls) return;
            _vm.OutputPath = TextBox_FilePath.Text;
        }

        /// <summary>
        /// 输出路径输入框的拖放悬停：只接受文件夹或文件（取其所在目录）。
        /// </summary>
        private void TextBox_FilePath_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = TryGetOutputPathFromDrop(e) != null ? DragDropEffects.Copy : DragDropEffects.None;

            // 标记已处理，避免事件继续冒泡到窗口从而误触发一次压缩任务
            e.Handled = true;
        }

        /// <summary>
        /// 输出路径输入框的拖放悬停（在预览阶段先行拦截）。
        ///
        /// TextBox 自带文本拖放能力，拖入文件时可能先被当成文本处理。
        /// 这里在预览阶段就把文件拖放接管掉，只留下普通文字的默认行为。
        /// </summary>
        private void TextBox_FilePath_PreviewDragOver(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;

            e.Effects = TryGetOutputPathFromDrop(e) != null ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        /// <summary>
        /// 输出路径输入框的拖放：
        /// 拖入文件夹 → 该文件夹本身作为输出路径；
        /// 拖入文件 → 该文件所在目录作为输出路径。
        /// </summary>
        private void TextBox_FilePath_Drop(object sender, DragEventArgs e)
        {
            string path = TryGetOutputPathFromDrop(e);
            if (path == null) return;

            TextBox_FilePath.Text = path;
            e.Handled = true;
        }

        /// <summary>
        /// 输出路径输入框的拖放（在预览阶段先行拦截）。
        /// </summary>
        private void TextBox_FilePath_PreviewDrop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;

            string path = TryGetOutputPathFromDrop(e);
            if (path == null) return;

            TextBox_FilePath.Text = path;
            e.Handled = true;
        }

        /// <summary>
        /// 从拖放数据中解析出可用的输出目录。
        /// </summary>
        private static string TryGetOutputPathFromDrop(DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return null;

            var paths = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (paths == null || paths.Length == 0) return null;

            foreach (var p in paths)
            {
                try
                {
                    if (Directory.Exists(p)) return p;

                    if (File.Exists(p))
                    {
                        // 拖入文件：取其上一级目录
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

        #endregion

        #region 拖放处理

        private void Window_DragEnter(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        }

        private void Window_Drop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;

            var files = e.Data.GetData(DataFormats.FileDrop) as string[];
            StartTaskForFiles(files);
        }

        /// <summary>
        /// 把其他实例送来的文件排进队列。
        ///
        /// 一次右键只处理一批, 连续右键时后面的批次排队等着,
        /// 不跟正在跑的任务抢。
        /// </summary>
        /// <param name="files">文件路径。</param>
        public void EnqueueFiles(string[] files)
        {
            if (files == null || files.Length == 0) return;

            foreach (string file in files) _pendingQueue.Enqueue(file);

            if (!_vm.IsBusy) ProcessNextQueued();
        }

        /// <summary>
        /// 处理队列里的下一批文件。
        /// </summary>
        private void ProcessNextQueued()
        {
            if (_pendingQueue.Count == 0) return;

            var batch = new List<string>();
            while (_pendingQueue.Count > 0) batch.Add(_pendingQueue.Dequeue());

            StartTaskForFiles(batch.ToArray());
        }

        /// <summary>
        /// 按当前模式启动任务。
        /// 拖入的路径只作为任务输入，不再写进"输出路径"输入框
        /// （文件路径不能直接当作输出目录）。
        /// </summary>
        private void StartTaskForFiles(string[] files)
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

            if (existing.Count == 0)
            {
                _logService.WriteLog(
                    EnumText.GetErrorText("Log_PathsNotFound", "传入的路径均不存在或不可访问：{0}", string.Join("; ", files)),
                    LogService.ErrorLevel.Warning);
                return;
            }

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

            _vm.SetPendingFiles(existing);

            // 解压模式：逐个解压
            // 压缩模式：多文件时询问是否合并为单个压缩包
            // 自动模式：压缩包逐个解压，其余文件逐个压缩（不丢弃任何一个）
            switch (_vm.CurrentOperationMode)
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

        #endregion

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            _vm.OnClosing();
        }
    }
}
