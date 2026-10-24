using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using Auto7z_WPF.Models;
using Auto7z_WPF.Services;

namespace Auto7z_WPF
{
    /// <summary>
    /// 主界面视图模型。
    /// 负责编排任务（压缩/解压）、同步设置、生成界面文本；
    /// 具体文件操作全部委托给 <see cref="SevenZipService"/>，界面文本全部来自语言资源字典。
    /// </summary>
    public class Auto7zViewModel : ObservableObject
    {
        private readonly ConfigService _configService;
        private readonly SevenZipService _sevenZipService;
        private readonly MessageService _messageService;
        private readonly LogService _logService;
        private readonly LanguageService _languageService;

        #region 字段

        private string _fullTitle;
        private string _copyRightText;
        private string _passwordBoxZipText;
        private bool _isPasswordValid;
        private bool _isModePanelVisible;
        private bool _isEggMode;
        private int _eggClickCount;
        private OperationMode _currentOperationMode = OperationMode.Auto;
        private string _mainText = string.Empty;
        private bool _isBusy;

        // 压缩/解压参数
        private string _volume = "2000";
        private ArchiveFormat _format = ArchiveFormat.SevenZip;
        private string _password = string.Empty;
        private bool _zstd = true;
        private bool _autoSave = true;
        private bool _generateMd5 = true;
        private bool _maxLevelCompress;

        // 基础设置面板
        private bool _isTopMost;
        private RuntimeModePreset _runtimeMode = RuntimeModePreset.Balanced;
        private string _outputPath = string.Empty;
        private string _currentLanguage = LanguageService.FallbackLanguage;
        private LogService.ErrorLevel _errorLevel = LogService.ErrorLevel.Warning;

        /// <summary>控制台模式下已经输出过的百分比，用来避免重复刷同一行。</summary>
        private int _lastConsolePercent = -1;

        /// <summary>
        /// 是否正在等用户输入密码。
        ///
        /// 这期间不打进度行：7-Zip 在索要密码那次会先吐 "  0%"，
        /// 密码错了那次会吐 "100% T xxx"，这些行混在密码交互中间，
        /// 会让用户以为程序已经在解压了，实际它正卡在等输入。
        /// </summary>
        private bool _awaitingPassword;

        private bool _isPortable;

        // 压缩面板
        private bool _isSolidMode;
        private bool _isEncFileName;
        private CompressionLevelPreset _compressionLevel = CompressionLevelPreset.Normal;

        // 解压面板
        private string _unzipPassword = string.Empty;

        private PendingFileBatch _pendingBatch = new PendingFileBatch(null);
        private CancellationTokenSource _cancellation;

        private ICommand _toggleModePanelCommand;

        // 进度文本模板：在界面线程上预先取好，后台线程只做 string.Format，
        // 避免后台线程访问 Application 资源字典（ResourceDictionary 非线程安全）。
        private string _progressTextTemplate;
        private string _md5ProgressTextTemplate;

        // 各操作模式的默认提示文本，同样在界面线程预先缓存
        private string _autoModeText;
        private string _zipModeText;
        private string _unzipModeText;

        // 任务结果反馈
        private string _resultText = string.Empty;
        private bool _isResultFailed;

        #endregion

        #region 构造与初始化

        public Auto7zViewModel()
        {
            // 数据目录的位置取决于是否便携，先按现有配置文件判断模式：
            // 便携目录下有配置就按便携模式走，否则用 %LOCALAPPDATA%
            AppPaths.IsPortable = DetectPortableMode();

            AppPaths.EnsureDataDirectory();

            _configService = new ConfigService();
            _sevenZipService = new SevenZipService();
            _messageService = new MessageService();
            _logService = new LogService();
            _languageService = new LanguageService();
            _languageService.LanguageChanged += (s, e) => OnLanguageChanged();

            CloseCommand = new RelayCommand<object>(_ => RequestClose?.Invoke(this, EventArgs.Empty));
            MinimizeCommand = new RelayCommand<object>(_ => RequestMinimize?.Invoke(this, EventArgs.Empty));
            AboutCommand = new RelayCommand<object>(_ => RequestShowAbout?.Invoke(this, EventArgs.Empty));
            // CanExecute 恒为 true：IsBusy 在后台线程变化，
            // 而输入绑定缓存的命令状态不一定随之更新，导致运行中反而无法右键取消。
            // 是否可取消改在命令内部判断。
            CancelCommand = new RelayCommand(() => { if (IsBusy) CancelCurrentTask(); });

            EggClickCommand = new RelayCommand<object>(_ =>
            {
                if (IsEggMode) return;
                _eggClickCount++;
                if (_eggClickCount >= 7) IsEggMode = true;
            });

            SelectOperationModeCommand = new RelayCommand<string>(modeText =>
            {
                if (Enum.TryParse(modeText, out OperationMode mode) && Enum.IsDefined(typeof(OperationMode), mode))
                {
                    CurrentOperationMode = mode;
                    IsModePanelVisible = false;
                }
            });

            ExecuteCompressCommand = new RelayCommand<object>(_ => RunTask(StartCompress), param => !IsBusy);
            ExecuteExtractCommand = new RelayCommand<object>(_ => RunTask(StartExtract), param => !IsBusy);

            SaveConfigCommand = new RelayCommand<object>(_ => SaveConfig());

            _autoModeText = EnumText.ToText(OperationMode.Auto);
            _zipModeText = EnumText.ToText(OperationMode.Zip);
            _unzipModeText = EnumText.ToText(OperationMode.UnZip);
            _mainText = _autoModeText;
            RefreshProgressTemplates();
        }

        /// <summary>
        /// 刷新进度文本模板。必须在界面线程调用（会读取资源字典）。
        /// </summary>
        private void RefreshProgressTemplates()
        {
            _progressTextTemplate = EnumText.GetResourceText("ProgressText", "7z: {0}%");
            _md5ProgressTextTemplate = EnumText.GetResourceText("Md5ProgressText", "MD5: {0}%");
        }

        /// <summary>请求关闭窗口。</summary>
        public event EventHandler RequestClose;

        /// <summary>
        /// 输出路径被判定为非法时触发，界面据此清空输入框留白。
        /// </summary>
        public event EventHandler OutputPathInvalidated;

        /// <summary>请求最小化窗口。</summary>
        public event EventHandler RequestMinimize;

        /// <summary>请求显示关于窗口。</summary>
        public event EventHandler RequestShowAbout;

        /// <summary>任务执行完毕时触发。</summary>
        public event EventHandler TaskCompleted;


        /// <summary>
        /// 从配置文件加载参数。
        /// </summary>
        public void LoadConfig()
        {
            var config = _configService.Load();

            Volume = config.Volume;
            Format = config.Format;
            Password = config.Password;
            Zstd = config.Zstd;
            AutoSave = config.AutoSave;
            GenerateMd5 = config.GenerateMd5;
            MaxLevelCompress = config.MaxLevelCompress;
            IsTopMost = config.IsTopMost;
            // 便携模式在构造时就定好了，这里只把界面上的勾选状态对上
            _isPortable = config.IsPortable || AppPaths.IsPortable;
            RuntimeMode = config.RuntimeMode;
            OutputPath = config.OutputPath;
            IsSolidMode = config.IsSolidMode;
            IsEncFileName = config.IsEncFileName;
            CompressionLevel = config.CompressionLevel;
            UnzipPassword = config.UnzipPassword;
            CurrentLanguage = config.Language;

            // 日志级别是配置里的隐藏开关，读取后立即生效
            ErrorLevel = config.ErrorLevel;

            AppState.SevenZUsageCount = config.SevenZUsageCount;
            _mainText = GetDefaultModeText();
        }

        #endregion

        #region 便携模式

        /// <summary>
        /// 判断当前是不是便携模式。
        ///
        /// 便携目录里有配置文件就按便携模式处理，
        /// 否则看普通目录里的配置里记的是什么。
        /// </summary>
        private static bool DetectPortableMode()
        {
            try
            {
                string portableConfig = Path.Combine(
                    AppPaths.GetPortableRootDirectory(), AppPaths.DataFolderName, AppPaths.ConfigFileName);
                if (File.Exists(portableConfig)) return true;

                string localConfig = Path.Combine(
                    AppPaths.GetLocalRootDirectory(), AppPaths.DataFolderName, AppPaths.ConfigFileName);
                if (!File.Exists(localConfig)) return false;

                var doc = System.Xml.Linq.XDocument.Load(localConfig);
                var node = doc.Root?.Element("IsPortable");
                if (node != null && bool.TryParse(node.Value, out bool portable)) return portable;

                return false;
            }
            catch
            {
                // 判断不出来时按普通模式走
                return false;
            }
        }

        /// <summary>
        /// 切换便携模式。
        ///
        /// 步骤是“先存键值再搬目录”：
        /// 配置里先记下目标模式，这样即便搬目录中途失败，
        /// 下次启动也能知道用户想要的是哪一种。
        /// </summary>
        /// <param name="portable">true 为便携模式。</param>
        public void SetPortableMode(bool portable)
        {
            if (portable == IsPortable) return;

            // 先把目标模式写进当前配置
            _isPortable = portable;
            SavePortableSetting(portable);

            // 再搬数据目录
            bool moved = portable ? MoveToPortable() : MoveToLocal();

            if (!moved)
            {
                // 搬不动就退回原模式，配置里的值也跟着改回来
                _isPortable = !portable;
                SavePortableSetting(!portable);
                AppPaths.IsPortable = !portable;
                ReportError(
                    EnumText.GetErrorText("Err_PortableMoveFailed", "切换{0}失败：数据目录没能移动", portable ? "便携模式" : "普通模式"));
                IsPortable = !portable;
                return;
            }

            AppPaths.IsPortable = portable;
            IsPortable = portable;
            _logService.WriteLog(
                EnumText.GetErrorText("Log_PortableSwitched", "已切换到{0}，数据目录：{1}",
                    portable ? "便携模式" : "普通模式", AppPaths.DataDirectory),
                LogService.ErrorLevel.Info);

            // 便携模式下右键菜单不该留在系统里，勾选时注销掉；
            // 取消勾选回到普通模式再注册回来。
            // 注册表得管理员权限才能写，这里会弹 UAC。
            //
            // SkipShellMenuSync 只给测试用：便携模式的测试要验的是"数据目录搬没搬对"，
            // 注册表只是搬目录的副产物。让测试去动注册表就得弹 UAC 让人点，
            // 一次测试打扰一次，很烦人。关掉之后行为只差"右键菜单没跟着切换"，
            // 跟被测的搬迁逻辑无关。
            if (!SkipShellMenuSync)
            {
                if (portable) ShellRegistrationService.UnregisterWithElevation();
                else ShellRegistrationService.RegisterWithElevation();
            }
        }

        /// <summary>
        /// 切换便携模式时要不要同步右键菜单的注册状态。
        ///
        /// 默认 true（正常行为）。测试可以置成 false 跳过 ——
        /// 那一步要提权写注册表，会弹 UAC。
        /// </summary>
        public bool SkipShellMenuSync { get; set; }

        /// <summary>
        /// 把数据目录搬到便携位置。
        ///
        /// 搬的是 %LOCALAPPDATA%\Auto7z，落到程序目录的 .config\Auto7z。
        /// </summary>
        private static bool MoveToPortable()
        {
            return MoveDataDirectory(
                Path.Combine(AppPaths.GetLocalRootDirectory(), AppPaths.DataFolderName),
                Path.Combine(AppPaths.GetPortableRootDirectory(), AppPaths.DataFolderName));
        }

        /// <summary>
        /// 把数据目录搬回 %LOCALAPPDATA%。
        ///
        /// 搬的是程序目录的 .config\Auto7z，落到 %LOCALAPPDATA%\Auto7z。
        /// 搬完把空的 .config 目录清掉，不然程序目录里会留个空壳。
        /// </summary>
        private static bool MoveToLocal()
        {
            string portableRoot = AppPaths.GetPortableRootDirectory();
            bool moved = MoveDataDirectory(
                Path.Combine(portableRoot, AppPaths.DataFolderName),
                Path.Combine(AppPaths.GetLocalRootDirectory(), AppPaths.DataFolderName));

            if (moved) RemoveEmptyDirectory(portableRoot);

            return moved;
        }

        #endregion

        #region 数据目录搬运

        /// <summary>
        /// 删掉空目录。里面还有东西就留着。
        /// </summary>
        /// <param name="path">目录路径。</param>
        private static void RemoveEmptyDirectory(string path)
        {
            try
            {
                if (!Directory.Exists(path)) return;
                if (Directory.EnumerateFileSystemEntries(path).Any()) return;

                Directory.Delete(path);
            }
            catch
            {
                // 删不掉就留着，一个空目录不影响使用
            }
        }

        /// <summary>
        /// 判断某个目录能不能安全地递归删除。
        ///
        /// 递归删除不可逆，被删的必须是程序自己的数据目录：
        /// 要么是数据目录本身（目录名必须是 <see cref="AppPaths.DataFolderName"/>），
        /// 要么是它下面的子目录（合并时搬空的那几个）。
        /// 根目录本身（.config 或 %LOCALAPPDATA%）不许删，
        /// 别的路径更不许删 —— 搬移过程中路径一旦算错就是整片目录没了。
        /// </summary>
        /// <param name="directoryPath">待删除的目录路径。</param>
        /// <returns>可以安全删除则返回 true。</returns>
        private static bool IsSafeToDeleteRecursively(string directoryPath)
        {
            if (string.IsNullOrWhiteSpace(directoryPath)) return false;

            try
            {
                string full = Path.GetFullPath(directoryPath);
                string[] roots =
                {
                    Path.GetFullPath(AppPaths.GetPortableRootDirectory()),
                    Path.GetFullPath(AppPaths.GetLocalRootDirectory())
                };

                foreach (string root in roots)
                {
                    string dataDirectory = Path.GetFullPath(Path.Combine(root, AppPaths.DataFolderName));

                    // 数据目录本身：名字要对得上
                    if (string.Equals(full, dataDirectory, StringComparison.OrdinalIgnoreCase))
                    {
                        return string.Equals(Path.GetFileName(full), AppPaths.DataFolderName,
                            StringComparison.Ordinal);
                    }

                    // 数据目录下面的子目录
                    if (full.StartsWith(dataDirectory + Path.DirectorySeparatorChar,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }

                return false;
            }
            catch
            {
                // 路径算不出来就当不安全，不删
                return false;
            }
        }

        /// <summary>
        /// 移动数据目录。目标已存在时先合并过去，再删掉源目录。
        /// </summary>
        /// <param name="sourceDirectory">源目录。</param>
        /// <param name="targetDirectory">目标目录。</param>
        /// <returns>是否搬运成功。</returns>
        private static bool MoveDataDirectory(string sourceDirectory, string targetDirectory)
        {
            try
            {
                if (!Directory.Exists(sourceDirectory))
                {
                    // 源目录本来就不在（首次切换），只要目标能建出来就算成功
                    Directory.CreateDirectory(targetDirectory);
                    return true;
                }

                Directory.CreateDirectory(targetDirectory);

                foreach (string file in Directory.GetFiles(sourceDirectory))
                {
                    string target = Path.Combine(targetDirectory, Path.GetFileName(file));
                    if (File.Exists(target)) File.Delete(target);
                    File.Move(file, target);
                }

                foreach (string directory in Directory.GetDirectories(sourceDirectory))
                {
                    string target = Path.Combine(targetDirectory, Path.GetFileName(directory));
                    MergeDirectory(directory, target);
                }

                // 递归删除不可逆，先把路径规整成绝对路径再做边界校验
                string fullSource = Path.GetFullPath(sourceDirectory);
                if (!IsSafeToDeleteRecursively(fullSource)) return false;

                Directory.Delete(sourceDirectory, true);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 把一个目录的内容并到另一个目录里，源目录用完删掉。
        /// 目标已存在时是合并而不是整个替换，免得把另一边的文件弄丢。
        /// </summary>
        /// <param name="sourceDirectory">源目录。</param>
        /// <param name="targetDirectory">目标目录。</param>
        private static void MergeDirectory(string sourceDirectory, string targetDirectory)
        {
            Directory.CreateDirectory(targetDirectory);

            foreach (string file in Directory.GetFiles(sourceDirectory))
            {
                string target = Path.Combine(targetDirectory, Path.GetFileName(file));
                if (File.Exists(target)) File.Delete(target);
                File.Move(file, target);
            }

            foreach (string directory in Directory.GetDirectories(sourceDirectory))
            {
                MergeDirectory(directory, Path.Combine(targetDirectory, Path.GetFileName(directory)));
            }

            // 递归删除不可逆，先把路径规整成绝对路径再做边界校验
            string fullSource = Path.GetFullPath(sourceDirectory);
            if (!IsSafeToDeleteRecursively(fullSource)) return;

            Directory.Delete(sourceDirectory, true);
        }

        #endregion

        #region 便携配置写入

        /// <summary>
        /// 只把便携模式这一个键写进当前配置文件。
        /// 目录搬动过程中配置本身可能已经换了位置，所以单独写一次当前所在的那份。
        /// </summary>
        private void SavePortableSetting(bool portable)
        {
            try
            {
                _configService.Update("IsPortable", portable.ToString());
            }
            catch
            {
                // 写不进去不影响后续流程
            }
        }

        #endregion

        #region 配置保存

        /// <summary>
        /// 把当前设置写回配置文件。
        /// </summary>
        public void SaveConfig()
        {
            var config = new AppConfig
            {
                Language = CurrentLanguage,
                Volume = Volume,
                Format = Format,
                Password = Password,
                Zstd = Zstd,
                GenerateMd5 = GenerateMd5,
                AutoSave = AutoSave,
                MaxLevelCompress = MaxLevelCompress,
                IsTopMost = IsTopMost,
                IsPortable = _isPortable,
                RuntimeMode = RuntimeMode,
                OutputPath = OutputPath,
                IsSolidMode = IsSolidMode,
                IsEncFileName = IsEncFileName,
                CompressionLevel = CompressionLevel,
                UnzipPassword = UnzipPassword,
                SevenZUsageCount = AppState.SevenZUsageCount,
                ErrorLevel = ErrorLevel
            };

            _configService.Save(config);
        }

        /// <summary>
        /// 仅写入 AutoSave 键值（取消勾选“自动保存”时使用）。
        /// </summary>
        public void SaveAutoSaveSetting(bool autoSave)
        {
            _configService.Update("AutoSave", autoSave.ToString());
        }

        /// <summary>
        /// 关闭程序时的收尾：按设置保存配置、清理临时组件。
        /// </summary>
        public void OnClosing()
        {
            CancelCurrentTask();

            // 先结束仍在运行的 7z 子进程：
            // 否则关闭程序后 7z.exe 还在后台跑，还可能锁住临时组件导致删除失败。
            _sevenZipService.KillRunningProcesses();

            if (AutoSave)
            {
                SaveConfig();
            }
            else
            {
                SaveAutoSaveSetting(false);
            }
        }

        #endregion

        #region 界面文本

        /// <summary>主窗体标题。</summary>
        public string FullTitle
        {
            get => _fullTitle;
            set => SetProperty(ref _fullTitle, value);
        }

        /// <summary>版权信息。</summary>
        public string CopyRightText
        {
            get => _copyRightText;
            set => SetProperty(ref _copyRightText, value);
        }

        /// <summary>
        /// 主提示文本（任务中显示进度，任务结束恢复为当前模式的默认文本）。
        /// </summary>
        public string MainText
        {
            get => _mainText;
            set => SetProperty(ref _mainText, value);
        }

        /// <summary>
        /// 任务结束后在主提示区显示的结果文本。
        /// 为空表示不显示结果，界面回到常规的悬停提示。
        /// </summary>
        public string ResultText
        {
            get => _resultText;
            set => SetProperty(ref _resultText, value);
        }

        /// <summary>
        /// 结果文本是否为失败状态（决定显示红色还是绿色）。
        /// </summary>
        public bool IsResultFailed
        {
            get => _isResultFailed;
            set => SetProperty(ref _isResultFailed, value);
        }

        /// <summary>当前操作模式对应的默认提示文本。</summary>
        public string GetDefaultModeText()
        {
            // 任务在后台线程执行时也会取这段文本，
            // 因此用预先缓存好的文本，避免后台线程直接访问资源字典。
            return GetCachedModeText(CurrentOperationMode);
        }

        /// <summary>
        /// 取指定模式的默认提示文本（优先用缓存）。
        /// </summary>
        private string GetCachedModeText(OperationMode mode)
        {
            switch (mode)
            {
                case OperationMode.Zip: return _zipModeText ?? EnumText.ToText(mode);
                case OperationMode.UnZip: return _unzipModeText ?? EnumText.ToText(mode);
                case OperationMode.Auto:
                default: return _autoModeText ?? EnumText.ToText(mode);
            }
        }

        /// <summary>
        /// 语言切换后刷新主提示文本与标题。
        /// </summary>
        private void OnLanguageChanged()
        {
            // 切换语言后重新缓存各模式的提示文本与进度模板
            _autoModeText = EnumText.ToText(OperationMode.Auto);
            _zipModeText = EnumText.ToText(OperationMode.Zip);
            _unzipModeText = EnumText.ToText(OperationMode.UnZip);
            RefreshProgressTemplates();

            if (!IsBusy)
            {
                MainText = GetDefaultModeText();
            }

            RefreshTitle();
        }

        #endregion

        #region 压缩与解压参数

        public string Volume
        {
            get => _volume;
            set => SetProperty(ref _volume, value);
        }

        public ArchiveFormat Format
        {
            get => _format;
            set => SetProperty(ref _format, value);
        }

        public string Password
        {
            get => _password;
            set => SetProperty(ref _password, value);
        }

        public bool Zstd
        {
            get => _zstd;
            set => SetProperty(ref _zstd, value);
        }

        public bool AutoSave
        {
            get => _autoSave;
            set => SetProperty(ref _autoSave, value);
        }

        public bool GenerateMd5
        {
            get => _generateMd5;
            set => SetProperty(ref _generateMd5, value);
        }

        public bool MaxLevelCompress
        {
            get => _maxLevelCompress;
            set => SetProperty(ref _maxLevelCompress, value);
        }

        /// <summary>
        /// 密码框内容。用于控制“完全加密”是否可用。
        /// </summary>
        public string ZipPasswordInputText
        {
            get => _passwordBoxZipText;
            set
            {
                if (SetProperty(ref _passwordBoxZipText, value))
                {
                    IsPasswordValid = !string.IsNullOrWhiteSpace(value);
                }
            }
        }

        /// <summary>是否已填写密码。</summary>
        public bool IsPasswordValid
        {
            get => _isPasswordValid;
            private set => SetProperty(ref _isPasswordValid, value);
        }

        #endregion

        #region 基础设置

        public bool IsTopMost
        {
            get => _isTopMost;
            set => SetProperty(ref _isTopMost, value);
        }

        /// <summary>        /// 是否为便携模式。
        /// 改动会立即把数据目录搬到新位置。
        /// </summary>
        public bool IsPortable
        {
            get => _isPortable;
            set
            {
                if (value == _isPortable) return;
                SetPortableMode(value);
            }
        }

        public RuntimeModePreset RuntimeMode
        {
            get => _runtimeMode;
            set => SetProperty(ref _runtimeMode, value);
        }

        public string OutputPath
        {
            get => _outputPath;
            set => SetProperty(ref _outputPath, value);
        }

        public string CurrentLanguage
        {
            get => _currentLanguage;
            set => SetProperty(ref _currentLanguage, value);
        }

        /// <summary>
        /// 日志级别。与 <see cref="LogService.MinLogLevel"/> 保持一致，
        /// 构造时即生效。默认只记录 Error 级日志，需要更多细节时可在设置里调低。
        /// </summary>
        public LogService.ErrorLevel ErrorLevel
        {
            get => _errorLevel;
            set
            {
                if (SetProperty(ref _errorLevel, value))
                {
                    LogService.MinLogLevel = value;
                }
            }
        }

        #endregion

        #region 压缩设置

        public bool IsSolidMode
        {
            get => _isSolidMode;
            set => SetProperty(ref _isSolidMode, value);
        }

        public bool IsEncFileName
        {
            get => _isEncFileName;
            set => SetProperty(ref _isEncFileName, value);
        }

        public CompressionLevelPreset CompressionLevel
        {
            get => _compressionLevel;
            set => SetProperty(ref _compressionLevel, value);
        }

        #endregion

        #region 解压设置

        public string UnzipPassword
        {
            get => _unzipPassword;
            set => SetProperty(ref _unzipPassword, value);
        }

        #endregion

        #region 模式与状态

        public bool IsAutoModeSelected => CurrentOperationMode == OperationMode.Auto;
        public bool IsZipModeSelected => CurrentOperationMode == OperationMode.Zip;
        public bool IsUnZipModeSelected => CurrentOperationMode == OperationMode.UnZip;
        public string CurrentOperationModeName => CurrentOperationMode.ToString();

        public OperationMode CurrentOperationMode
        {
            get => _currentOperationMode;
            set
            {
                if (SetProperty(ref _currentOperationMode, value))
                {
                    OnPropertyChanged(nameof(IsAutoModeSelected));
                    OnPropertyChanged(nameof(IsZipModeSelected));
                    OnPropertyChanged(nameof(IsUnZipModeSelected));
                    OnPropertyChanged(nameof(CurrentOperationModeName));

                    if (!IsBusy)
                    {
                        MainText = GetDefaultModeText();
                    }
                }
            }
        }

        public bool IsModePanelVisible
        {
            get => _isModePanelVisible;
            set => SetProperty(ref _isModePanelVisible, value);
        }

        public bool IsEggMode
        {
            get => _isEggMode;
            private set => SetProperty(ref _isEggMode, value);
        }

        #endregion

        #region 运行方式

        /// <summary>
        /// 本次的运行方式。
        ///
        /// 三种方式的差异不只是"有没有界面"：
        ///   Gui       —— 主窗口在场，进度显示在主窗口提示区
        ///   Console   —— 信息写控制台，多文件用 Y/N 问答
        ///   ShellMenu —— 永远不建主窗口，进度用独立进度窗口，需要确认时弹小对话框
        /// 所以显式区分，不要靠"有没有主窗口"去反推。
        /// </summary>
        public RunMode CurrentRunMode { get; set; } = RunMode.Gui;

        /// <summary>
        /// 是否为命令行模式。
        ///
        /// 命令行模式下不弹界面，进度输出到控制台。
        /// </summary>
        public bool ConsoleProgressEnabled => CurrentRunMode == RunMode.Console;

        /// <summary>
        /// 是否为右键菜单模式。
        ///
        /// 右键菜单模式同样不建主窗口，但需要用户确认的地方
        /// （比如多文件是否合并）要弹小对话框问，不能只往控制台写。
        /// </summary>
        public bool ShellMenuMode => CurrentRunMode == RunMode.ShellMenu;

        #endregion

        #region 控制台输出

        /// <summary>
        /// 往控制台写一行文字。
        ///
        /// 控制台代码页不一定是 UTF-8，直接写中文会变成问号，
        /// 这里按控制台实际用的编码来转。
        ///
        /// 命令行模式下程序已经把父 cmd 的控制台接过来了
        /// （见 <see cref="ConsoleAttach"/>），Console.Out 绑的就是它，
        /// 所以这里写进去用户就能在 cmd 窗口里看到。
        /// </summary>
        private static void WriteConsoleLine(string text)
        {
            WriteConsoleText(text, true);
        }

        /// <summary>
        /// 往控制台写一段文字，可以不换行。
        ///
        /// 问用户输入的地方（密码、Y/N）要用不换行的那种，
        /// 这样用户的输入会紧跟在提示后面，看起来是同一行：
        ///     该压缩包已加密，请输入密码：test123
        /// 换行的话输入会跑到下一行，提示和输入看起来断开了。
        /// </summary>
        /// <param name="text">要写的文字。</param>
        /// <param name="newLine">末尾是否换行。</param>
        private static void WriteConsoleText(string text, bool newLine)
        {
            if (string.IsNullOrEmpty(text)) return;

            try
            {
                var encoding = Console.OutputEncoding;
                if (encoding == null || encoding.CodePage == 65001)
                {
                    if (newLine) Console.WriteLine(text);
                    else Console.Write(text);
                    return;
                }

                // 按控制台代码页转成字节，直接写标准输出。
                // 不能写成 "写字节 + Console.Out.WriteLine()"：
                // 那样会多出一个换行，而且 Console.Out 的编码可能跟字节不一致。
                string payload = newLine ? text + Environment.NewLine : text;
                byte[] bytes = encoding.GetBytes(payload);
                Stream stdout = Console.OpenStandardOutput();
                stdout.Write(bytes, 0, bytes.Length);
                stdout.Flush();
            }
            catch
            {
                try
                {
                    if (newLine) Console.WriteLine(text);
                    else Console.Write(text);
                }
                catch
                {
                    // 连控制台都没有（比如双击运行），那就只能丢掉
                }
            }
        }

        /// <summary>
        /// 把进度写到控制台。同一百分比只写一次，免得刷屏。
        /// </summary>
        private void WriteConsoleProgress(ProgressInfo info)
        {
            if (info.Stage != ProgressStage.Compressing
                && info.Stage != ProgressStage.Extracting
                && info.Stage != ProgressStage.CalculatingMd5)
            {
                return;
            }

            // 正在等用户输密码时不要打进度。
            //
            // 7-Zip 在索要密码那次会先吐一行 "  0%"（扫描存档的起始行），
            // 密码错了那次会吐 "100% T xxx"（读到文件头但解密失败）。
            // 这些行混在密码交互中间，看起来像是"已经开始解压了"，
            // 实际程序正卡在等输入 —— 会把交互流程搅乱。
            if (_awaitingPassword) return;

            if (_lastConsolePercent == info.Percent) return;
            _lastConsolePercent = info.Percent;

            // 进度文本走语言资源，三种阶段各有各的措辞
            string template = GetProgressTextTemplate(info.Stage);
            WriteConsoleLine(string.Format(template, info.Percent));
        }

        #endregion

        #region 进度文本

        /// <summary>
        /// 取某个阶段的进度文本模板。
        ///
        /// 命令行模式与进度窗口共用同一批键，但措辞分开：
        /// 控制台上是 "7z 已处理...x%"，进度窗口里还要说明在干什么。
        /// </summary>
        /// <param name="stage">任务阶段。</param>
        /// <returns>带 {0} 占位符的文本模板。</returns>
        private string GetProgressTextTemplate(ProgressStage stage)
        {
            switch (stage)
            {
                case ProgressStage.Compressing:
                    return EnumText.GetResourceText("Console_ProgressCompress", "7z processed...{0}%");

                case ProgressStage.Extracting:
                    return EnumText.GetResourceText("Console_ProgressExtract", "7z processed...{0}%");

                case ProgressStage.CalculatingMd5:
                    return EnumText.GetResourceText("Console_ProgressMd5", "MD5 processed...{0}%");

                default:
                    return EnumText.GetResourceText("ProgressText", "7z: {0}%");
            }
        }

        /// <summary>
        /// 取某个阶段的进度窗口标题文本。
        /// </summary>
        /// <param name="stage">任务阶段。</param>
        /// <returns>阶段描述文本。</returns>
        public string GetStageText(ProgressStage stage)
        {
            switch (stage)
            {
                case ProgressStage.Compressing:
                    return EnumText.GetResourceText("ProcessStageCompress", "Compressing…");

                case ProgressStage.Extracting:
                    return EnumText.GetResourceText("ProcessStageExtract", "Extracting…");

                case ProgressStage.CalculatingMd5:
                    return EnumText.GetResourceText("ProcessStageMd5", "Calculating MD5…");

                case ProgressStage.Finished:
                    return EnumText.GetResourceText("ProcessStageFinished", "Done");

                case ProgressStage.Failed:
                    return EnumText.GetResourceText("ProcessStageFailed", "Failed");

                default:
                    return EnumText.GetResourceText("ProcessWaiting", "Preparing…");
            }
        }

        /// <summary>是否有任务正在执行。</summary>
        public bool IsBusy
        {
            get => _isBusy;
            private set
            {
                if (SetProperty(ref _isBusy, value))
                {
                    RaiseCommandStates();

                    // 任务跑完时通知外面，让排队的下一批接着做
                    if (!value) TaskCompleted?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        #endregion

        #region 命令

        public ICommand ToggleModePanelCommand
            => _toggleModePanelCommand ?? (_toggleModePanelCommand = new RelayCommand<object>(_ => IsModePanelVisible = !IsModePanelVisible));

        public ICommand SelectOperationModeCommand { get; }
        public ICommand CloseCommand { get; }
        public ICommand MinimizeCommand { get; }
        public ICommand AboutCommand { get; }
        public ICommand EggClickCommand { get; }
        public ICommand ExecuteCompressCommand { get; }
        public ICommand ExecuteExtractCommand { get; }
        public ICommand SaveConfigCommand { get; }
        public ICommand CancelCommand { get; }

        private void RaiseCommandStates()
        {
            (ExecuteCompressCommand as RelayCommand<object>)?.RaiseCanExecuteChanged();
            (ExecuteExtractCommand as RelayCommand<object>)?.RaiseCanExecuteChanged();
            (CancelCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }

        #endregion

        #region 任务输入

        /// <summary>
        /// 设置本次任务的输入。
        /// 大小在接收阶段（界面线程）统计一次，任务执行期间不再重复遍历目录。
        /// </summary>
        /// <summary>
        /// 上一次任务是否以失败或取消结束。命令行模式据此决定进程退出码。
        /// </summary>
        public bool LastTaskFailed { get; private set; }

        /// <summary>
        /// 上一次 SetPendingFiles 里被丢弃的输入个数（路径不存在或无法访问）。
        /// </summary>
        public int RejectedInputCount { get; private set; }

        /// <summary>
        /// 当前待处理批次里是否还有条目。
        /// </summary>
        public bool HasPendingFiles => _pendingBatch != null && !_pendingBatch.IsEmpty;

        public void SetPendingFiles(IEnumerable<string> files)
        {
            if (files == null)
            {
                _pendingBatch = new PendingFileBatch(null);
                return;
            }

            var items = new List<PendingFile>();
            int rejected = 0;
            foreach (var path in files)
            {
                if (string.IsNullOrWhiteSpace(path)) continue;

                // 相对路径先转成绝对路径：后面推导输出目录用的是 Path.GetDirectoryName，
                // 相对路径会得到空串，任务会以「无法确定输出目录」失败。
                string full = NormalizeInputPath(path);

                try
                {
                    if (File.Exists(full))
                    {
                        long size = 0;
                        try { size = new FileInfo(full).Length; } catch { /* 读不到大小按 0 处理 */ }
                        items.Add(new PendingFile(full, size, false));
                    }
                    else if (Directory.Exists(full))
                    {
                        long size = _sevenZipService.GetSizeInBytes(full);
                        items.Add(new PendingFile(full, size < 0 ? 0 : size, true));
                    }
                    else
                    {
                        // 路径不存在：记下来，命令行模式据此报错并返回非 0，
                        // 而不是像以前那样静默丢弃、退出码还是 0。
                        rejected++;
                    }
                }
                catch
                {
                    rejected++;
                }
            }

            RejectedInputCount = rejected;
            _pendingBatch = new PendingFileBatch(items);
        }

        #endregion

        #region 任务启动

        /// <summary>
        /// 启动压缩任务（自动模式下会在识别到压缩文件时转为解压）。
        /// </summary>
        public void BeginCompressTask() => RunTask(report => CompressFiles(_pendingBatch.Files.ToList(), report));

        /// <summary>
        /// 启动解压任务。
        /// </summary>
        public void BeginExtractTask() => RunTask(StartExtract);

        /// <summary>
        /// 同步跑一个任务，等它结束才返回。
        ///
        /// 命令行模式要用这个：界面模式是异步的，跑完不管，
        /// 但命令行模式跑完就要退出进程，得等任务真的做完。
        /// </summary>
        /// <param name="task">任务本体。</param>
        public void RunTaskAndWait(Action<Action<ProgressInfo>> task)
        {
            RunTaskAndWait(task, null);
        }

        /// <summary>
        /// 同步跑一个任务，等它结束才返回。
        ///
        /// 进度除了走内部通道（主窗口提示区 / 控制台）之外，
        /// 还可以额外转给一个回调 —— 右键菜单模式要把进度送到
        /// 独立的进度窗口上。
        /// </summary>
        /// <param name="task">任务本体。</param>
        /// <param name="externalReport">额外的进度回调；为空表示不转发。</param>
        public void RunTaskAndWait(Action<Action<ProgressInfo>> task, Action<ProgressInfo> externalReport)
        {
            if (IsBusy) return;

            IsBusy = true;
            LastTaskFailed = false;
            _cancellation = new CancellationTokenSource();

            Action<ProgressInfo> report = info =>
            {
                UpdateProgress(info);
                externalReport?.Invoke(info);
            };

            try
            {
                task(report);
            }
            catch (Exception ex)
            {
                AppState.CurrentStatus = AppState.OperationStatus.Error;
                _logService.WriteLog(EnumText.GetErrorText("Log_TaskException", "任务执行异常：{0}", ex.Message), LogService.ErrorLevel.Error);
                WriteConsoleLine("错误: " + ex.Message);
                ReportTaskResult(false, ex.Message);
            }
            finally
            {
                _cancellation?.Dispose();
                _cancellation = null;
                IsBusy = false;
            }
        }

        /// <summary>
        /// 同步跑压缩任务。
        /// </summary>
        public void RunCompressAndWait()
        {
            RunCompressAndWait(null);
        }

        /// <summary>
        /// 同步跑压缩任务。
        /// </summary>
        /// <param name="externalReport">额外的进度回调。</param>
        public void RunCompressAndWait(Action<ProgressInfo> externalReport)
        {
            RunTaskAndWait(report => CompressFiles(_pendingBatch.Files.ToList(), report), externalReport);
        }

        /// <summary>
        /// 同步跑解压任务。
        /// </summary>
        public void RunExtractAndWait()
        {
            RunExtractAndWait(null);
        }

        /// <summary>
        /// 同步跑解压任务。
        /// </summary>
        /// <param name="externalReport">额外的进度回调。</param>
        public void RunExtractAndWait(Action<ProgressInfo> externalReport)
        {
            RunTaskAndWait(StartExtract, externalReport);
        }
        #endregion

        #region 自动模式分发


        /// <summary>
        /// 同步跑自动模式任务。
        /// </summary>
        public void RunAutoDispatchAndWait()
        {
            RunAutoDispatchAndWait(null);
        }

        /// <summary>
        /// 同步跑自动模式任务。
        /// </summary>
        /// <param name="externalReport">额外的进度回调。</param>
        public void RunAutoDispatchAndWait(Action<ProgressInfo> externalReport)
        {
            RunTaskAndWait(StartAutoDispatch, externalReport);
        }

        /// <summary>
        /// 启动“按输入逐项处理”的任务。
        ///
        /// 自动模式下输入可能同时包含压缩包与普通文件：
        ///   压缩包 → 逐个解压
        ///   普通文件 → 逐个压缩
        /// 不能因为第一个是压缩包就把其余文件丢掉。
        /// </summary>
        public void BeginAutoDispatchTask() => RunTask(StartAutoDispatch);

        /// <summary>
        /// 自动模式下的任务分发：把输入按类型拆开，分别压缩/解压。
        /// </summary>
        private void StartAutoDispatch(Action<ProgressInfo> report)
        {
            var batch = _pendingBatch;
            if (batch.IsEmpty)
            {
                CompleteWithoutWork(report);
                return;
            }

            // 拆出压缩包与普通文件
            var archives = new List<PendingFile>();
            var others = new List<PendingFile>();
            foreach (var f in batch.Files)
            {
                if (!f.IsDirectory && _sevenZipService.IsArchiveFile(f.Path)) archives.Add(f);
                else others.Add(f);
            }

            if (archives.Count == 0 && others.Count == 0)
            {
                CompleteWithoutWork(report);
                return;
            }

            // 全部是压缩包 → 逐个解压
            if (others.Count == 0)
            {
                _logService.WriteLog(EnumText.GetErrorText("Log_AutoExtractEach", "自动模式：逐个解压 {0} 个压缩包", archives.Count), LogService.ErrorLevel.Info);
                ExtractEach(archives, report);
                return;
            }

            // 全部是普通文件 → 走常规压缩流程（内部会询问是否合并）
            if (archives.Count == 0)
            {
                CompressFiles(others, report);
                return;
            }

            // 混合输入：压缩包逐个解压，普通文件按用户的合并选择处理
            _logService.WriteLog("自动模式：混合输入，压缩包 " + archives.Count
                + " 个逐个解压，其余 " + others.Count + " 个压缩", LogService.ErrorLevel.Info);

            ExtractEach(archives, report);
            CompressFiles(others, report);
        }

        /// <summary>
        /// 逐个解压。
        /// </summary>
        private void ExtractEach(List<PendingFile> files, Action<ProgressInfo> report)
        {
            foreach (var f in files)
            {
                if (CurrentToken.IsCancellationRequested) return;

                StartExtract(report, f.Path);
            }
        }

        /// <summary>
        /// 逐个压缩（每个文件单独生成一个压缩包，不合并）。
        /// </summary>
        private void CompressEach(List<PendingFile> files, Action<ProgressInfo> report)
        {
            var original = _pendingBatch;
            try
            {
                foreach (var f in files)
                {
                    if (CurrentToken.IsCancellationRequested) return;

                    _pendingBatch = new PendingFileBatch(new[] { f });
                    StartCompress(report);
                }
            }
            finally
            {
                _pendingBatch = original;
            }
        }
        #endregion

        #region 任务执行与取消


        /// <summary>
        /// 在后台线程执行任务，异常不会逃逸到界面线程。
        /// </summary>
        private async void RunTask(Action<Action<ProgressInfo>> task)
        {
            if (IsBusy)
            {
                return;
            }

            IsBusy = true;
            _cancellation = new CancellationTokenSource();

            Action<ProgressInfo> report = info => UpdateProgress(info);

            try
            {
                await System.Threading.Tasks.Task.Run(() => task(report));
            }
            catch (Exception ex)
            {
                AppState.CurrentStatus = AppState.OperationStatus.Error;
                _logService.WriteLog(EnumText.GetErrorText("Log_TaskException", "任务执行异常：{0}", ex.Message), LogService.ErrorLevel.Error);
                ReportError(ex.Message);
                MainText = GetDefaultModeText();
            }
            finally
            {
                _cancellation?.Dispose();
                _cancellation = null;
                IsBusy = false;
            }
        }

        /// <summary>
        /// 更新进度文本。任务在后台线程执行。
        /// 文本模板在界面线程预先取好（见 <see cref="RefreshProgressTemplates"/>），
        /// 这里只用 string.Format，不再从后台线程读取资源字典。
        /// </summary>
        private void UpdateProgress(ProgressInfo info)
        {
            if (info == null) return;

            // 命令行模式：进度打到控制台，界面不参与
            if (ConsoleProgressEnabled)
            {
                WriteConsoleProgress(info);
                return;
            }

            // 右键菜单模式：进度显示在独立的进度窗口上（由调用方转发），
            // 这里不碰 MainText —— 那是没有主窗口的模式，
            // 而且任务在后台线程上跑，跨线程写绑定属性不安全。
            if (CurrentRunMode == RunMode.ShellMenu)
            {
                return;
            }

            // 图形界面模式下 MainText 绑在主窗口上，只能在界面线程改。
            // 任务本身跑在后台线程（Task.Run），所以这里统一切回界面线程。
            var app = Application.Current;
            if (app != null && !app.Dispatcher.CheckAccess())
            {
                app.Dispatcher.BeginInvoke(new Action(() => UpdateProgress(info)));
                return;
            }

            switch (info.Stage)
            {
                case ProgressStage.Compressing:
                case ProgressStage.Extracting:
                    MainText = string.Format(
                        _progressTextTemplate ?? "7z: {0}%", info.Percent);
                    break;

                case ProgressStage.CalculatingMd5:
                    MainText = string.Format(
                        _md5ProgressTextTemplate ?? "MD5: {0}%", info.Percent);
                    break;

                case ProgressStage.Finished:
                    MainText = GetDefaultModeText();
                    break;

                case ProgressStage.Failed:
                case ProgressStage.Idle:
                default:
                    break;
            }
        }

        /// <summary>
        /// 中断当前任务。
        /// </summary>
        public void CancelCurrentTask()
        {
            try
            {
                _cancellation?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // 任务已结束
            }
        }

        private CancellationToken CurrentToken => _cancellation?.Token ?? CancellationToken.None;

        #endregion

        #region 压缩实现


        /// <summary>
        /// 报告任务开始。
        ///
        /// 命令行模式下写控制台；右键菜单模式下由进度窗口显示；
        /// 图形界面模式下主窗口自己有提示区，不用额外输出。
        /// </summary>
        /// <param name="templateKey">语言资源键。</param>
        /// <param name="fallback">资源缺失时的备用文案。</param>
        /// <param name="argument">要填进文案的参数。</param>
        private void ReportTaskStart(string templateKey, string fallback, string argument)
        {
            if (CurrentRunMode == RunMode.Console)
            {
                WriteConsoleLine(EnumText.GetFormattedText(templateKey, fallback, argument));
            }
        }

        /// <summary>
        /// 报告任务结果。
        /// </summary>
        /// <param name="success">是否成功。</param>
        /// <param name="detail">结果描述。</param>
        /// <param name="cancelled">是否被取消。</param>
        private void ReportTaskResult(bool success, string detail, bool cancelled = false)
        {
            if (CurrentRunMode != RunMode.Console) return;

            if (cancelled)
            {
                WriteConsoleLine(EnumText.GetResourceText("Console_TaskCancelled", "已取消"));
                return;
            }

            string template = success
                ? EnumText.GetResourceText("Console_TaskDone", "完成：{0}")
                : EnumText.GetResourceText("Console_TaskFailed", "失败：{0}");
            WriteConsoleLine(string.Format(template, detail ?? string.Empty));
        }

        /// <summary>
        /// 压缩一组文件。
        ///
        /// 多个文件时先询问用户是否合并为单个压缩包：
        ///   合并 → 整组打成一个包
        ///   不合并 → 每个文件各自打一个包
        /// </summary>
        private void CompressFiles(List<PendingFile> files, Action<ProgressInfo> report)
        {
            if (files == null || files.Count == 0)
            {
                CompleteWithoutWork(report);
                return;
            }

            ReportTaskStart("Console_CompressStart", "开始压缩：{0}",
                string.Join("; ", files.Select(f => f.Path)));

            if (files.Count > 1)
            {
                if (AskPackInOneFile())
                {
                    _logService.WriteLog(EnumText.GetErrorText("Log_ChoseMerge", "用户选择合并为单个压缩包，共 {0} 个文件", files.Count), LogService.ErrorLevel.Info);
                    CompressMerged(files, report);
                }
                else
                {
                    _logService.WriteLog(EnumText.GetErrorText("Log_ChoseSeparate", "用户选择不合并，{0} 个文件分别压缩", files.Count), LogService.ErrorLevel.Info);
                    CompressEach(files, report);
                }
                return;
            }

            CompressMerged(files, report);
        }
        #endregion

        #region 合并压缩


        /// <summary>
        /// 把给定文件合并压缩成一个包。
        /// </summary>
        private void CompressMerged(List<PendingFile> files, Action<ProgressInfo> report)
        {
            var batch = new PendingFileBatch(files);
            var original = _pendingBatch;
            _pendingBatch = batch;
            try
            {
                StartCompress(report);
            }
            finally
            {
                _pendingBatch = original;
            }
        }
        #endregion

        #region 压缩入口


        /// <summary>
        /// 执行压缩任务。
        /// </summary>
        private void StartCompress(Action<ProgressInfo> report)
        {
            AppState.CurrentStatus = AppState.OperationStatus.CompressFile;

            var batch = _pendingBatch;
            if (batch.IsEmpty)
            {
                CompleteWithoutWork(report);
                return;
            }


            if (!EnsureComponents(report)) return;

            // 运行任务前先校验“输出路径”：
            // 无论是打字输入还是拖拽填入，路径不合法（不存在等）时清空输入框留白，
            // 并退回“跟随输入文件所在目录”，避免整个任务因一个无效路径而失败。
            string validatedOutputPath = ValidateOutputPath();

            // 输出目录：用户指定 > 输入所在目录（文件夹输入时自动上移一级，避免输出被自己打包）
            if (!_sevenZipService.TryPrepareCompressOutput(validatedOutputPath, batch.Files[0], out string outputDirectory, out Exception pathError))
            {
                FailWithError(pathError, report);
                return;
            }

            // 归档名称与目标路径
            string baseName = BuildArchiveBaseName(batch);

            var runtimeConfig = new AppConfig
            {
                Format = Format,
                Volume = Volume,
                Password = Password,
                Zstd = Zstd,
                MaxLevelCompress = MaxLevelCompress,
                IsSolidMode = IsSolidMode,
                IsEncFileName = IsEncFileName,
                CompressionLevel = CompressionLevel,
                RuntimeMode = RuntimeMode,
                UnzipPassword = UnzipPassword,
                FileName = baseName,
                TotalSize = batch.TotalSizeInBytes,
                CurrentRunStatus = AppState.OperationStatus.CompressFile
            };

            string archivePath = _sevenZipService.BuildArchiveFilePath(outputDirectory, baseName, runtimeConfig);

            var commands = _sevenZipService.BuildCompressCommands(
                batch.Files.Select(f => f.Path).ToArray(), runtimeConfig, archivePath);
            if (commands.Count == 0)
            {
                FailWithError(new ArgumentException(EnumText.GetErrorText("Err_BuildCompressCommand", "无法构建压缩命令")), report);
                return;
            }

            // 记录本次任务产生的中间文件，失败时清理
            var produced = new List<string> { archivePath };

            bool hasWarning = false;

            #endregion

            #region 压缩命令执行

            for (int i = 0; i < commands.Count; i++)
            {
                if (i > 0)
                {
                    // tar+zstd 的中间产物是 <名字>.tar，不能用 ChangeExtension
                    produced.Add(SevenZipService.BuildIntermediateTarPath(archivePath));
                }

                var result = _sevenZipService.ExecuteCommand(commands[i], p => report(new ProgressInfo(p, ProgressStage.Compressing)), CurrentToken);

                if (result.HasWarning)
                {
                    hasWarning = true;
                    _logService.WriteLog(EnumText.GetErrorText("Log_SevenZipWarning", "7-Zip 提示（有警告但已完成）：{0}", result.GetFailureDetail()), LogService.ErrorLevel.Warning);
                }

                if (!result.Succeeded)
                {
                    bool cancelled = result.Cancelled;
                    CleanupProducedFiles(produced);
                    // 任务被取消或失败时，整个安全命名文件夹一起删掉，
                    // 只删里面的文件会留下一个空目录。
                    _sevenZipService.CleanupUnfinished(outputDirectory);
                    FailFromCommand(result, report, cancelled);
                    return;
                }
            }

            // 校验最终产物确实存在
            // 分卷时 7-Zip 生成的是 name.7z.001，不会生成 name.7z 本身，必须一并判断
            if (!_sevenZipService.ArchiveExists(archivePath))
            {
                CleanupProducedFiles(produced);
                _sevenZipService.CleanupUnfinished(outputDirectory);
                FailWithError(new FileNotFoundException(EnumText.GetErrorText("Err_ArchiveNotCreated", "压缩命令已结束，但未生成压缩文件：{0}", archivePath)), report);
                return;
            }

            // tar+zstd 两步流程的中间产物（<名字>.tar）用完即删，
            // 否则用户目录里会多出一个体积接近原包的无用文件。
            if (commands.Count > 1)
            {
                CleanupProducedFiles(produced.Skip(1));
            }

            // MD5 校验文件（可选）
            if (GenerateMd5)
            {
                try
                {
                    // 分卷时每个分卷各生成一份 md5。
                    // 进度按整批折算：每份自己的 0-100 映射到这一批的区间里，
                    // 否则第一份跑完进度会跳回 0，看起来像重头再来一遍。
                    var parts = _sevenZipService.CollectProducedFiles(archivePath);
                    for (int partIndex = 0; partIndex < parts.Count; partIndex++)
                    {
                        int slice = partIndex;
                        _sevenZipService.GenerateMd5(parts[partIndex],
                            p => report(new ProgressInfo(
                                MapMd5Progress(slice, parts.Count, p), ProgressStage.CalculatingMd5)),
                            CurrentToken);
                    }
                }
                catch (OperationCanceledException)
                {
                    // 取消发生在 MD5 阶段：压缩包已经生成，整包一起清掉
                    CleanupProducedFiles(produced);
                    _sevenZipService.CleanupUnfinished(outputDirectory);
                    FailFromCommand(null, report, cancelled: true);
                    return;
                }
                catch (Exception ex)
                {
                    // MD5 失败不影响压缩结果，仅记录日志
                    _logService.WriteLog(
                        EnumText.GetErrorText("Log_Md5Failed", "生成 MD5 失败：{0}", ex.Message),
                        LogService.ErrorLevel.Warning);
                }
            }

            if (hasWarning)
            {
                _messageService.ShowWarning(EnumText.GetResourceText("Result_PartialWarning", "压缩已完成，但部分文件被跳过，详情见日志。"), out string warnLog);
                _logService.WriteLog(warnLog, LogService.ErrorLevel.Warning);
            }

            AppState.SevenZUsageCount++;
            AppState.CurrentStatus = AppState.OperationStatus.Finish;
            report(new ProgressInfo(100, ProgressStage.Finished));
            string doneText = EnumText.GetResourceText("Result_CompressDone", "压缩完成");
            SetResult(doneText, failed: false);
            ReportTaskResult(true, doneText);
        }

        #endregion

        #region 归档命名

        /// <summary>
        /// 生成归档文件的基础名（不含扩展名）。
        /// 单个输入时用该输入自身的名字；多个输入时用“第一个名字 + 等 N 个”，
        /// 避免出现“压缩了 5 个文件却只叫第一个文件名”的误解。
        /// </summary>
        private static string BuildArchiveBaseName(PendingFileBatch batch)
        {
            if (batch == null || batch.IsEmpty) return "Archive";

            var first = batch.Files[0];
            string baseName = first.IsDirectory
                ? first.Name
                : Path.GetFileNameWithoutExtension(first.Path);

            if (string.IsNullOrWhiteSpace(baseName)) baseName = "Archive";

            if (batch.Files.Count > 1)
            {
                // 合并压缩时用“首个名字 + 等 N 个”标明包里装了几个
                string pattern = EnumText.GetResourceText("Archive_MergedNamePattern", "{0} 等 {1} 个");
                baseName = pattern
                    .Replace("{0}", baseName)
                    .Replace("{1}", batch.Files.Count.ToString(CultureInfo.InvariantCulture));
            }

            return baseName;
        }

        #endregion

        #region 解压实现


        /// <summary>
        /// 执行解压任务。
        /// </summary>
        private void StartExtract(Action<ProgressInfo> report)
        {
            StartExtract(report, null);
        }

        /// <summary>
        /// 执行解压任务。
        /// </summary>
        /// <param name="report">进度上报回调。</param>
        /// <param name="archiveOverride">指定要解压的存档；为空时取输入批次的第一项。</param>
        private void StartExtract(Action<ProgressInfo> report, string archiveOverride)
        {
            AppState.CurrentStatus = AppState.OperationStatus.ExtractFile;

            var batch = _pendingBatch;
            if (batch.IsEmpty && string.IsNullOrWhiteSpace(archiveOverride))
            {
                CompleteWithoutWork(report);
                return;
            }

            if (!EnsureComponents(report)) return;

            string archive = !string.IsNullOrWhiteSpace(archiveOverride) ? archiveOverride : batch.Files[0].Path;

            ReportTaskStart("Console_ExtractStart", "开始解压：{0}", archive);

            // 分卷包：name.7z 本身不存在，实际是 name.7z.001 …
            bool isSplit = SevenZipService.IsSplitArchive(archive);
            if (!File.Exists(archive) && !isSplit)
            {
                FailWithError(new FileNotFoundException(EnumText.GetErrorText("Err_ArchiveNotFound", "压缩文件不存在"), archive), report);
                return;
            }

            if (!_sevenZipService.TryPrepareExtractOutput(archive, out string destinationDirectory, out Exception pathError))
            {
                FailWithError(pathError, report);
                return;
            }

            var runtimeConfig = new AppConfig
            {
                // 解压时的“格式”必须取存档的真实格式，而不是界面上选中的生成格式：
                // 界面上选的是 tar，但拖进来的是加密的 7z 时，按 tar 处理就不会下发密码，导致解不开。
                Format = DetectArchiveFormat(archive),
                Password = UnzipPassword,
                CurrentRunStatus = AppState.OperationStatus.ExtractFile
            };

            // 分卷包要指向第一分卷，7-Zip 才能自动串起后续分卷
            string sourceArchive = isSplit ? SevenZipService.ResolveFirstVolumePath(archive) : archive;

            // 加密存档：7-Zip 会输出 “Enter password …” 并以退出码 255 结束。
            // 此时弹窗向用户索取密码并重试，最多尝试 3 次。
            const int MaxPasswordAttempts = 3;
            for (int attempt = 1; attempt <= MaxPasswordAttempts; attempt++)
            {
                string command = _sevenZipService.BuildExtractCommand(sourceArchive, destinationDirectory, runtimeConfig);
                if (string.IsNullOrWhiteSpace(command))
                {
                    FailWithError(new ArgumentException(EnumText.GetErrorText("Err_BuildExtractCommand", "无法构建解压命令")), report);
                    return;
                }

                var result = _sevenZipService.ExecuteCommand(command, p => report(new ProgressInfo(p, ProgressStage.Extracting)), CurrentToken);

                _logService.WriteLog("解压尝试 " + attempt + ": exit=" + result.ExitCode +
                    " RequiresPassword=" + result.RequiresPassword +
                    " Cancelled=" + result.Cancelled + " TimedOut=" + result.TimedOut,
                    LogService.ErrorLevel.Info);

                if (result.HasWarning)
                {
                    _logService.WriteLog(EnumText.GetErrorText("Log_SevenZipWarning", "7-Zip 提示（有警告但已完成）：{0}", result.GetFailureDetail()), LogService.ErrorLevel.Warning);
                }

                if (result.Succeeded)
                {
                    FinishExtract(report, destinationDirectory, sourceArchive);
                    return;
                }

                // 7-Zip 在索要密码，或提示密码错误：都回到弹窗让用户重新输入
                if (result.RequiresPassword || result.IsWrongPassword)
                {
                    bool isWrong = result.IsWrongPassword;
                    _logService.WriteLog(
                        isWrong
                            ? EnumText.GetErrorText("Log_PasswordWrong", "密码错误，第 {0} 次尝试，重新询问密码", attempt)
                            : EnumText.GetErrorText("Log_PasswordAsked", "7-Zip 索要密码，第 {0} 次尝试，弹窗请求输入", attempt),
                        LogService.ErrorLevel.Info);

                    // 密码错或 7-Zip 索要密码时都要重新问用户。
                    // 命令行模式用 Console_ 前缀的键，界面模式用普通键 ——
                    // 措辞可以各自调整（控制台上要带冒号引导输入），
                    // 又不会因为改了一处而牵连另一处。
                    string prompt = isWrong
                        ? (CurrentRunMode == RunMode.Console
                            ? EnumText.GetFormattedText("Console_PasswordWrong", "密码错误，第 {0} 次尝试，重新询问密码：", attempt)
                            : EnumText.GetFormattedText("PasswordWrongAttempt", "密码错误，第 {0} 次尝试，重新询问密码：", attempt))
                        : (CurrentRunMode == RunMode.Console
                            ? EnumText.GetResourceText("Console_PasswordPrompt", "该压缩包已加密，请输入密码：")
                            : EnumText.GetResourceText("PasswordPrompt", "该压缩包已加密，请输入密码："));

                    if (attempt >= MaxPasswordAttempts)
                    {
                        _sevenZipService.CleanupUnfinished(destinationDirectory);
                        FailWithError(new InvalidOperationException(EnumText.GetErrorText("Err_PasswordRetries", "连续多次密码错误，已放弃解压")), report);
                        return;
                    }

                    // 清掉上一次失败留下的半截内容，避免与重试结果混在一起
                    _sevenZipService.CleanupUnfinished(destinationDirectory);

                    // 从这里到拿到密码为止，不打进度行：
                    // 下一次 7-Zip 跑起来会先吐 "  0%"，那行会被用户看成
                    // "已经开始解压了"，实际程序还卡在等密码。
                    _awaitingPassword = true;
                    string password;
                    try
                    {
                        password = RequestPassword(prompt, attempt);
                    }
                    finally
                    {
                        _awaitingPassword = false;
                    }

                    if (password == null)
                    {
                        // 用户取消
                        _sevenZipService.CleanupUnfinished(destinationDirectory);
                        FailFromCommand(result, report, cancelled: true);
                        return;
                    }

                    runtimeConfig.Password = password;
                    UnzipPassword = password;
                    continue;
                }

                // 失败时清掉半截的解压目录，避免留下不完整内容
                _sevenZipService.CleanupUnfinished(destinationDirectory);
                LogExtractFailure(result, sourceArchive);
                FailFromCommand(result, report, result.Cancelled);
                return;
            }
        }
        #endregion

        #region 解压收尾


        /// <summary>
        /// 解压成功后的收尾：处理“压缩的 tar”需要再解一层的情况。
        /// </summary>
        private void FinishExtract(Action<ProgressInfo> report, string destinationDirectory, string sourceArchive)
        {
            // 压缩的 tar（.tar.zst / .tar.gz 等）单步解压只会得到中间的 tar 文件，
            // 需要再解一层才能拿到原始内容。
            if (SevenZipService.IsCompressedTar(sourceArchive))
            {
                if (!ExtractInnerTar(destinationDirectory, sourceArchive, report))
                {
                    _sevenZipService.CleanupUnfinished(destinationDirectory);
                    FailWithError(new InvalidOperationException(EnumText.GetErrorText("Err_InnerTarFailed", "解压压缩 tar 失败：未能解开内部的 tar")), report);
                    return;
                }
            }

            AppState.SevenZUsageCount++;
            AppState.CurrentStatus = AppState.OperationStatus.Finish;
            report(new ProgressInfo(100, ProgressStage.Finished));
            string doneText = EnumText.GetResourceText("Result_ExtractDone", "解压完成");
            SetResult(doneText, failed: false);
            ReportTaskResult(true, doneText);
        }

        #endregion

        #region 解压密码询问

        /// <summary>
        /// 向用户索取解压密码。
        ///
        /// 三种方式各走各的路：
        ///   命令行模式 —— 在控制台提示并读一行输入
        ///   右键菜单模式 —— 弹 PasswordWindow
        ///   图形界面模式 —— 弹 PasswordWindow
        /// </summary>
        /// <param name="prompt">提示文字；为空时用默认的"该压缩包已加密，请输入密码"。</param>
        /// <param name="attempt">第几次尝试；用来拼"密码错误，第 N 次"那句话。</param>
        /// <returns>用户输入的密码；用户取消时返回 null。</returns>
        private string RequestPassword(string prompt = null, int attempt = 1)
        {
            // 命令行模式：控制台看得见，直接在控制台里问
            if (CurrentRunMode == RunMode.Console)
            {
                return RequestPasswordOnConsole(prompt, attempt);
            }

            var app = Application.Current;
            if (app == null) return null;

            PasswordWindow window = null;
            var completed = new ManualResetEventSlim(false);

            try
            {
                // 窗口必须在界面线程上创建
                if (app.Dispatcher.CheckAccess())
                {
                    window = CreatePasswordWindow(completed, prompt);
                }
                else
                {
                    // 后台线程：用 BeginInvoke 投递到界面线程创建窗口（不阻塞界面）
                    app.Dispatcher.BeginInvoke(new Action(() => window = CreatePasswordWindow(completed, prompt)));
                }
            }
            catch (Exception ex)
            {
                _logService.WriteLog(EnumText.GetErrorText("Log_PasswordDialogFailed", "弹出密码输入框失败：{0}", ex.Message), LogService.ErrorLevel.Error);
                completed.Dispose();
                return null;
            }

            // 等待用户输入完成（当前在后台线程，不影响界面响应）
            completed.Wait();

            string password = window != null && window.Confirmed ? window.Password : null;
            completed.Dispose();

            _logService.WriteLog(password == null ? EnumText.GetErrorText("Log_PasswordCancelled", "用户取消了密码输入") : EnumText.GetErrorText("Log_PasswordGot", "已获取密码"), LogService.ErrorLevel.Info);
            return password;
        }

        /// <summary>
        /// 在控制台里索取解压密码。
        ///
        /// 命令行模式下控制台看得见、也读得到输入，所以直接问用户。
        /// 读不到输入（管道、计划任务等场景）时返回 null，
        /// 由上层按"用户没给密码"处理，不让任务卡住。
        /// </summary>
        /// <param name="prompt">提示文字；为空时用默认提示。</param>
        /// <param name="attempt">第几次尝试。</param>
        /// <returns>用户输入的密码；读不到输入时返回 null。</returns>
        private string RequestPasswordOnConsole(string prompt, int attempt)
        {
            string message = !string.IsNullOrWhiteSpace(prompt)
                ? prompt
                : EnumText.GetResourceText("Console_PasswordPrompt", "该压缩包已加密，请输入密码：");

            // 提示后面不换行：用户输入的密码会紧跟在冒号后面，
            // 看起来是同一行。换行的话输入会跑到下一行，提示和输入就断开了。
            WriteConsoleText(message, false);

            string line = ReadConsoleLine();
            if (line == null)
            {
                _logService.WriteLog(EnumText.GetErrorText("Log_PasswordCancelled", "用户取消了密码输入"), LogService.ErrorLevel.Info);
                // 读不到输入，提示得补个换行，后面的输出才不会跟这行挤在一起
                WriteConsoleLine(string.Empty);
                return null;
            }

            // 读到了，把用户敲的内容换行收尾
            WriteConsoleLine(string.Empty);

            _logService.WriteLog(EnumText.GetErrorText("Log_PasswordGot", "已获取密码"), LogService.ErrorLevel.Info);
            return line;
        }

        /// <summary>
        /// 从控制台读一行。读不到返回 null。
        /// </summary>
        /// <returns>读到的内容（已去掉行尾空白）；读不到时返回 null。</returns>
        private static string ReadConsoleLine()
        {
            try
            {
                // 读不到就返回 null：管道已关闭、计划任务没有输入等情况下
                // Console.ReadLine 会抛异常或直接返回 null。
                string line = Console.ReadLine();
                if (line == null) return null;
                return line.Trim();
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 在界面线程上创建并显示密码输入窗口（必须在界面线程调用）。
        ///
        /// 用非模态的 Show() 而不是 ShowDialog()：
        ///   ShowDialog() 会阻塞调用它的线程直到窗口关闭，
        ///   让主界面处于“无响应”状态，而且该窗口不属于主 UI 线程的窗口树。
        /// 改为 Show() + 关闭事件，界面保持响应，窗口也能被正常的自动化工具识别。
        /// </summary>
        /// <param name="completed">窗口关闭时置位的信号。</param>
        /// <param name="prompt">提示文字；为空时用默认提示。</param>
        private static PasswordWindow CreatePasswordWindow(ManualResetEventSlim completed, string prompt = null)
        {
            var window = new PasswordWindow { Owner = Application.Current.MainWindow };
            if (!string.IsNullOrWhiteSpace(prompt)) window.SetPrompt(prompt);
            window.Closed += (s, e) => completed.Set();
            window.Show();
            return window;
        }

        /// <summary>
        /// 记录解压失败的原因，便于排查。
        /// </summary>
        private void LogExtractFailure(CommandResult result, string archive)
        {
            _logService.WriteLog(
                "解压失败 exit=" + result.ExitCode +
                " RequiresPassword=" + result.RequiresPassword +
                " IsWrongPassword=" + result.IsWrongPassword +
                " 存档=" + archive +
                " 详情=" + result.GetFailureDetail(),
                LogService.ErrorLevel.Error);
        }

        #endregion

        #region 归档格式识别

        /// <summary>
        /// 把“压缩的 tar”解出的中间 tar 文件再解一层，得到原始内容。
        /// </summary>
        /// <param name="destinationDirectory">第一层解压的目标目录。</param>
        /// <param name="sourceArchive">原始压缩包路径（用于推导中间 tar 名）。</param>
        /// <param name="report">进度上报回调。</param>
        /// <returns>是否成功解出原始内容。</returns>
        private bool ExtractInnerTar(string destinationDirectory, string sourceArchive, Action<ProgressInfo> report)
        {
            // 中间 tar 的文件名：name.tar.zst → name.tar
            string innerTarName = Path.GetFileName(sourceArchive);
            if (innerTarName.EndsWith(".zst", StringComparison.OrdinalIgnoreCase))
            {
                innerTarName = innerTarName.Substring(0, innerTarName.Length - 4);
            }
            else if (innerTarName.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
            {
                innerTarName = innerTarName.Substring(0, innerTarName.Length - 3);
            }
            else if (innerTarName.EndsWith(".bz2", StringComparison.OrdinalIgnoreCase))
            {
                innerTarName = innerTarName.Substring(0, innerTarName.Length - 4);
            }
            else if (innerTarName.EndsWith(".xz", StringComparison.OrdinalIgnoreCase))
            {
                innerTarName = innerTarName.Substring(0, innerTarName.Length - 3);
            }

            string innerTarPath = Path.Combine(destinationDirectory, innerTarName);
            if (!File.Exists(innerTarPath))
            {
                _logService.WriteLog(EnumText.GetErrorText("Log_InnerTarMissing", "未找到中间 tar 文件：{0}", innerTarPath), LogService.ErrorLevel.Warning);
                return false;
            }

            // 把中间 tar 的内容解到最终目录
            string innerCommand = _sevenZipService.BuildExtractCommand(innerTarPath, destinationDirectory, new AppConfig
            {
                Format = ArchiveFormat.Tar,
                Password = string.Empty
            });

            var innerResult = _sevenZipService.ExecuteCommand(
                innerCommand, p => report(new ProgressInfo(p, ProgressStage.Extracting)), CurrentToken);

            if (!innerResult.Succeeded)
            {
                _logService.WriteLog(EnumText.GetErrorText("Log_InnerTarFailed", "解开中间 tar 失败：{0}", innerResult.GetFailureDetail()), LogService.ErrorLevel.Error);
                return false;
            }

            // 删掉中间 tar，让用户看到的就是原始内容
            try
            {
                if (File.Exists(innerTarPath)) File.Delete(innerTarPath);
            }
            catch
            {
                // 删不掉不影响结果
            }

            return true;
        }

        /// <summary>
        /// 识别存档的真实格式，用于决定是否下发解压密码。
        /// 识别不出时按 7z 处理（最常见的加密格式），避免因为格式误判而漏传密码。
        /// </summary>
        private ArchiveFormat DetectArchiveFormat(string archivePath)
        {
            // 先看扩展名：.tar.zst / .tgz 这类双扩展名按魔数只会识别出 zst / gz，
            // 但它们本质上是 tar，扩展名判断更准确。
            if (IsTarExtension(archivePath))
            {
                return ArchiveFormat.Tar;
            }

            try
            {
                string detected = ArchiveFormatMap.DetectByMagic(archivePath);
                var parsed = ArchiveFormatMap.Parse(detected);
                if (parsed.HasValue) return parsed.Value;
            }
            catch
            {
                // 识别失败时按默认处理
            }

            return ArchiveFormat.SevenZip;
        }

        /// <summary>
        /// 判断路径是否属于 tar 系列（tar / tar.gz / tgz / tar.zst / txz …）。
        /// </summary>
        private static bool IsTarExtension(string archivePath)
        {
            if (string.IsNullOrWhiteSpace(archivePath)) return false;

            string name = Path.GetFileName(archivePath).ToLowerInvariant();

            if (name.EndsWith(".tar", StringComparison.Ordinal)) return true;
            if (name.EndsWith(".tgz", StringComparison.Ordinal)) return true;
            if (name.EndsWith(".tbz2", StringComparison.Ordinal)) return true;
            if (name.EndsWith(".txz", StringComparison.Ordinal)) return true;
            if (name.EndsWith(".tzst", StringComparison.Ordinal)) return true;

            // .tar.gz / .tar.zst / .tar.bz2 / .tar.xz
            if (name.EndsWith(".tar.gz", StringComparison.Ordinal)) return true;
            if (name.EndsWith(".tar.zst", StringComparison.Ordinal)) return true;
            if (name.EndsWith(".tar.bz2", StringComparison.Ordinal)) return true;
            if (name.EndsWith(".tar.xz", StringComparison.Ordinal)) return true;

            return false;
        }

        #endregion

        #region 合并询问


        /// <summary>
        /// 询问用户是否把多个文件合并为单个压缩包。
        /// 提示文本取自语言资源字典，随界面语言变化。
        ///
        /// 命令行模式下改成控制台问答 (Y/N)，免得为了问一句话就把主窗口拉起来。
        /// 右键菜单模式下同样不建主窗口，但控制台看不见，改用小对话框问。
        /// </summary>
        /// <returns>true = 合并为一个压缩包；false = 每个文件各自压缩。</returns>
        private bool AskPackInOneFile()
        {
            // 命令行模式：控制台问 Y/N
            if (CurrentRunMode == RunMode.Console)
            {
                return AskPackInOneFileOnConsole();
            }

            // 图形界面与右键菜单：都弹对话框问（右键模式下没有主窗口，
            // MessageService 里的 ResolveOwner 会让对话框独立显示）
            string question = EnumText.GetResourceText("MultiFilePackQuery",
                "Multiple files have been detected. Pack into a single file?");
            if (string.IsNullOrWhiteSpace(question)) return true;

            bool result = _messageService.AskYesNo(question, out string log);
            _logService.WriteLog(log, LogService.ErrorLevel.Info);

            return result;
        }

        /// <summary>
        /// 在控制台里问是否合并。
        ///
        /// 命令行模式下用户在 cmd 里看得见也读得到输入，所以问 Y/N。
        /// 读不到输入（管道、计划任务等场景）时按默认的"合并"处理，
        /// 不让任务卡住。
        /// </summary>
        private bool AskPackInOneFileOnConsole()
        {
            string question = EnumText.GetResourceText("Console_MergeQuestion",
                "Multiple files detected. Pack into a single archive? (Y/N)");

            // 提示后面不换行：用户的回答紧跟在问句后面，看起来是同一行
            WriteConsoleText(question, false);

            bool hasInput = TryReadConsoleAnswer(out bool answer);

            // 收尾换行，后面的输出不会跟这行挤在一起
            WriteConsoleLine(string.Empty);

            if (!hasInput)
            {
                // 读不到输入就按合并处理
                answer = true;
            }

            _logService.WriteLog(
                EnumText.GetErrorText("Log_ChoseMerge", "用户选择合并为单个压缩包，共 {0} 个文件", _pendingBatch.Files.Count),
                LogService.ErrorLevel.Info);

            return answer;
        }

        /// <summary>
        /// 从控制台读一个 y/n。
        ///
        /// 交互式控制台和重定向的管道都能读；读不到（比如计划任务没有输入）
        /// 返回 false，让调用方走默认分支。
        /// </summary>
        private static bool TryReadConsoleAnswer(out bool answer)
        {
            answer = true;

            try
            {
                string line = Console.ReadLine();
                if (line == null) return false;

                line = line.Trim();
                if (line.Length == 0) return true;

                answer = !(line.Equals("n", StringComparison.OrdinalIgnoreCase)
                           || line.Equals("no", StringComparison.OrdinalIgnoreCase)
                           || line.Equals("否", StringComparison.Ordinal));
                return true;
            }
            catch
            {
                return false;
            }
        }
        #endregion

        #region 输出路径校验


        /// <summary>
        /// 校验输出路径。
        /// 路径非法（不存在等）时清空输入框留白，并退回“跟随输入文件所在目录”。
        /// </summary>
        /// <returns>可用的输出路径；非法时返回空字符串。</returns>
        private string ValidateOutputPath()
        {
            if (string.IsNullOrWhiteSpace(OutputPath)) return string.Empty;

            if (FileSystemHelper.TryValidateOutputPath(OutputPath, out string directory))
            {
                return directory ?? string.Empty;
            }

            _logService.WriteLog(EnumText.GetErrorText("Log_OutputPathInvalid", "输出路径不存在，已清空：{0}", OutputPath), LogService.ErrorLevel.Warning);
            OutputPath = string.Empty;
            OutputPathInvalidated?.Invoke(this, EventArgs.Empty);

            return string.Empty;
        }

        private bool EnsureComponents(Action<ProgressInfo> report)
        {
            _sevenZipService.EnsureComponents(out Exception error);
            if (error == null && _sevenZipService.CheckComponentsExist()) return true;

            FailWithError(error ?? new FileNotFoundException(EnumText.GetErrorText("Err_ComponentMissing", "未找到 7-Zip 组件")), report);
            return false;
        }

        private void CompleteWithoutWork(Action<ProgressInfo> report)
        {
            AppState.CurrentStatus = AppState.OperationStatus.Normal;
            report(new ProgressInfo(0, ProgressStage.Finished));
            MainText = GetDefaultModeText();
        }

        /// <summary>
        /// 报告错误。
        ///
        /// 三种方式各走各的：
        ///   图形界面 / 右键菜单 —— 弹对话框（用户看得见）
        ///   命令行模式 —— 写控制台。
        ///
        /// 命令行模式**不能**弹窗：那是个模态对话框，会把 cmd 窗口堵住，
        /// 而且用户的注意力在控制台上，不在屏幕上弹出来的窗体上。
        /// 用户看到的应该是控制台里的一行错误文本。
        /// </summary>
        /// <param name="detail">错误详情。</param>
        private void ReportError(string detail)
        {
            if (CurrentRunMode == RunMode.Console)
            {
                // 控制台模式：只写文本，不弹窗
                _logService.WriteLog(detail, LogService.ErrorLevel.Error);
                return;
            }

            _messageService.ShowError(detail, out string log);
            _logService.WriteLog(log, LogService.ErrorLevel.Error);
        }

        private void FailWithError(Exception error, Action<ProgressInfo> report)
        {
            LastTaskFailed = true;
            AppState.CurrentStatus = AppState.OperationStatus.Error;

            string detail = error?.Message ?? string.Empty;
            ReportError(detail);

            report(new ProgressInfo(0, ProgressStage.Failed));
            MainText = GetDefaultModeText();
            SetResult(EnumText.GetResourceText("Result_Failed", "Failed"), failed: true);
            ReportTaskResult(false, detail);
        }

        private void FailFromCommand(CommandResult result, Action<ProgressInfo> report, bool cancelled)
        {
            // 取消也算「没成功」：命令行/脚本据此拿到非 0 退出码。
            LastTaskFailed = true;
            AppState.CurrentStatus = cancelled ? AppState.OperationStatus.Normal : AppState.OperationStatus.Error;

            if (cancelled)
            {
                _logService.WriteLog(EnumText.GetErrorText("Log_TaskCancelled", "任务已取消"), LogService.ErrorLevel.Warning);
                report(new ProgressInfo(0, ProgressStage.Failed));
                MainText = GetDefaultModeText();
                SetResult(EnumText.GetResourceText("Result_Cancelled", EnumText.GetErrorText("Log_TaskCancelled", "任务已取消")), failed: true);
                ReportTaskResult(false, null, cancelled: true);
                return;
            }

            string detail = result?.GetFailureDetail();
            if (string.IsNullOrWhiteSpace(detail))
            {
                detail = EnumText.GetErrorText("Err_UnexpectedExitCode", "7-Zip 返回非 0 退出码：{0}",
                    result?.ExitCode.ToString(CultureInfo.InvariantCulture) ?? "未知");
            }

            ReportError(detail);

            report(new ProgressInfo(0, ProgressStage.Failed));
            MainText = GetDefaultModeText();
            ReportTaskResult(false, detail);
        }

        /// <summary>
        /// 在主提示区显示任务结果。
        /// 界面负责计时与恢复，这里只提供文本和成败状态。
        /// </summary>
        /// <param name="text">结果文本。</param>
        /// <param name="failed">是否失败（失败用红色，成功用绿色）。</param>
        public void SetResult(string text, bool failed)
        {
            ResultText = text ?? string.Empty;
            IsResultFailed = failed;
        }

        #endregion

        #region 输入路径与进度工具

        /// <summary>
        /// 把输入路径规整成绝对路径；路径非法时原样返回，交给后面的存在性检查。
        /// </summary>
        private static string NormalizeInputPath(string path)
        {
            try
            {
                return Path.GetFullPath(path);
            }
            catch (Exception)
            {
                // 非法路径（含非法字符等）：原样返回，后面按「不存在」处理
                return path;
            }
        }

        /// <summary>
        /// 把「第 index 份的 0-100」折算成「整批的 0-100」，保证一轮任务内进度只增不减。
        /// </summary>
        private static int MapMd5Progress(int index, int count, int percent)
        {
            if (count <= 1) return percent;
            if (percent < 0) percent = 0;
            if (percent > 100) percent = 100;
            int mapped = (int)Math.Round((index * 100.0 + percent) / count);
            return mapped > 100 ? 100 : mapped;
        }

        private void CleanupProducedFiles(IEnumerable<string> paths)
        {
            foreach (var path in paths)
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) File.Delete(path);
                }
                catch
                {
                    // 文件被占用时保留
                }
            }
        }

        #endregion

        #region 关于窗口信息

        /// <summary>
        /// 加载关于窗口需要的数据。
        /// </summary>
        public void LoadAboutWindowResource()
        {
            RefreshTitle();
            RefreshCopyright();
        }

        private void RefreshTitle()
        {
            string baseTitle = EnumText.GetResourceText("AboutTitle", "About Auto7z");
            var version = Assembly.GetExecutingAssembly().GetName().Version;
            string versionStr = version != null ? $"v{version.Major}.{version.Minor}.{version.Build}" : string.Empty;
            FullTitle = $"{baseTitle} {versionStr}";
        }

        private void RefreshCopyright()
        {
            const int startYear = 2024;
            int currentYear = DateTime.Now.Year;
            string yearRange = currentYear == startYear ? startYear.ToString(CultureInfo.InvariantCulture) : $"{startYear}-{currentYear}";
            string rightsText = EnumText.GetResourceText("CopyRightText", "All rights reserved.");
            CopyRightText = $"Copyright © {yearRange} LatteYogurt, {rightsText}";
        }

        /// <summary>
        /// 加载配置中的语言并应用到界面资源。
        /// 用已经载入的 <see cref="CurrentLanguage"/>,
        /// 不能反过来用 AppState.CurrentLanguage（静态默认值）覆盖它，
        /// 否则每次启动都会把用户保存的语言设置重置为默认语言。
        /// </summary>
        public void ApplyLanguage()
        {
            if (!LanguageService.IsSupported(CurrentLanguage))
            {
                CurrentLanguage = LanguageService.FallbackLanguage;
            }

            _languageService.SwitchLanguage(CurrentLanguage);

            // 右键菜单的文字也按这个语言显示
            ShellRegistrationService.SetLanguage(CurrentLanguage);
        }

        #endregion
    }
}
