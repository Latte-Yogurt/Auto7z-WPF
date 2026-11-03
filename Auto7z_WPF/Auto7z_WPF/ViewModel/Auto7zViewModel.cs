using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Auto7z_WPF.Models;
using Auto7z_WPF.Services;

namespace Auto7z_WPF
{
    public class Auto7zViewModel : ObservableObject
    {
        #region 状态字段与构造
        private readonly ConfigService _configService;
        private readonly SevenZipService _sevenZipService;
        private readonly MessageService _messageService;
        private readonly LogService _logService;
        private readonly LanguageService _languageService;

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

        private string _volume = "2000";
        private ArchiveFormat _format = ArchiveFormat.SevenZip;
        private string _password = string.Empty;
        private bool _zstd = true;
        private bool _autoSave = true;
        private bool _generateSHA256 = true;
        private bool _maxLevelCompress;

        private bool _isTopMost;
        private RuntimeModePreset _runtimeMode = RuntimeModePreset.Balanced;
        private string _outputPath = string.Empty;
        private string _currentLanguage = LanguageService.FallbackLanguage;
        private LogService.ErrorLevel _errorLevel = LogService.ErrorLevel.Warning;

        // 初值不能落在 0 到 100 之间：否则控制台第一次真正的 0% 进度会被当成重复而吞掉。
        private int _lastConsolePercent = -1;

        private bool _awaitingPassword;

        private bool _isPortable;

        private bool _isSolidMode;
        private bool _isEncFileName;
        private CompressionLevelPreset _compressionLevel = CompressionLevelPreset.Normal;

        private string _unzipPassword = string.Empty;

        private PendingFileBatch _pendingBatch = new PendingFileBatch(null);
        // 取消源的生命周期与任务严格对齐：任务开始时创建、finally 里释放，
        // 任务之外保持 null，CurrentToken 才能安全退化成 None。
        private CancellationTokenSource _cancellation;

        private ICommand _toggleModePanelCommand;

        private string _progressTextTemplate;
        private string _sha256ProgressTextTemplate;

        private string _consoleCompressTemplate;
        private string _consoleExtractTemplate;
        private string _consoleSHA256Template;

        private string _autoModeText;
        private string _zipModeText;
        private string _unzipModeText;

        private string _resultText = string.Empty;
        private bool _isResultFailed;


        public Auto7zViewModel()
        {
            // 便携判定必须最先定下来：紧接着的 EnsureDataDirectory 要靠它决定
            // 数据目录落在便携根还是本机 AppData。
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
            // 没在跑任务时点取消没有意义，顺带避免把已释放的取消源再取消一次。
            CancelCommand = new RelayCommand(() => { if (IsBusy) CancelCurrentTask(); });

            // 彩蛋入口用连击计数防误触，进入之后不再累计。
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

            // CanExecute 挂 !IsBusy：任务执行期间压缩与解压入口自动禁用，
            // 避免同一时间有两条 7z 命令行争抢同一个输出目录。
            ExecuteCompressCommand = new RelayCommand<object>(_ => StartTaskInBackground(StartCompress), param => !IsBusy);
            ExecuteExtractCommand = new RelayCommand<object>(_ => StartTaskInBackground(StartExtract), param => !IsBusy);

            SaveConfigCommand = new RelayCommand<object>(_ => SaveConfig());

            _autoModeText = EnumText.ToText(OperationMode.Auto);
            _zipModeText = EnumText.ToText(OperationMode.Zip);
            _unzipModeText = EnumText.ToText(OperationMode.UnZip);
            // 模板要等 LanguageService 就绪后才能取，所以放在构造的最后一步。
            _mainText = _autoModeText;
            RefreshProgressTemplates();
        }

        private void RefreshProgressTemplates()
        {
            // 进度回调的触发频率远高于界面刷新，模板在这里取一次缓存，回调里不再反复查资源字典。
            _progressTextTemplate = EnumText.GetResourceText("ProgressText", "7z: {0}%");
            _sha256ProgressTextTemplate = EnumText.GetResourceText("SHA256ProgressText", "SHA256: {0}%");
            _consoleCompressTemplate = EnumText.GetResourceText("Console_ProgressCompress", "7z processed...{0}%");
            _consoleExtractTemplate = EnumText.GetResourceText("Console_ProgressExtract", "7z processed...{0}%");
            _consoleSHA256Template = EnumText.GetResourceText("Console_ProgressSHA256", "SHA256 processed...{0}%");
        }

        #endregion
        #region 配置装载与便携模式
        public event EventHandler RequestClose;

        public event EventHandler OutputPathInvalidated;

        public event EventHandler RequestMinimize;

        public event EventHandler RequestShowAbout;

        public event EventHandler TaskCompleted;


        public void LoadConfig()
        {
            var config = _configService.Load();

            Volume = config.Volume;
            Format = config.Format;
            Password = config.Password;
            Zstd = config.Zstd;
            AutoSave = config.AutoSave;
            GenerateSHA256 = config.GenerateSHA256;
            MaxLevelCompress = config.MaxLevelCompress;
            IsTopMost = config.IsTopMost;
            // 与探测结果取或：配置文件里可能残留上次切换时的旧值，
            // 不能让它推翻这次启动得出的结论。
            _isPortable = config.IsPortable || AppPaths.IsPortable;
            RuntimeMode = config.RuntimeMode;
            OutputPath = config.OutputPath;
            IsSolidMode = config.IsSolidMode;
            IsEncFileName = config.IsEncFileName;
            CompressionLevel = config.CompressionLevel;
            UnzipPassword = config.UnzipPassword;
            CurrentLanguage = config.Language;

            ErrorLevel = config.ErrorLevel;

            // 使用次数是全局状态，配置读回来要同步进去，才不会被下一次保存清零。
            AppState.SevenZUsageCount = config.SevenZUsageCount;
            _mainText = GetDefaultModeText();
        }


        // 默认按普通模式：判定依赖文件系统，异常时返回 false 让数据留在本机目录更稳妥。
        // 判定看的是文件事实而不是配置标记，绿色版从没写过配置也能被认出来。
        private static bool DetectPortableMode()
        {
            try
            {
                string portableConfig = Path.Combine(
                    AppPaths.GetPortableRootDirectory(), AppPaths.DataFolderName, AppPaths.ConfigFileName);
                // 便携目录里有配置文件，说明上次跑的就是便携版，此时不必再看配置里的标记。
                if (File.Exists(portableConfig)) return true;

                string localConfig = Path.Combine(
                    AppPaths.GetLocalRootDirectory(), AppPaths.DataFolderName, AppPaths.ConfigFileName);
                if (!File.Exists(localConfig)) return false;

                // 只看一个键就把整份 JSON 读进来：配置很小，解析成本可以忽略，
                // 而单独实现一个"只读某个键"的路径扫描器会和正式解析器产生第二套语义。
                var values = JsonConfig.Parse(File.ReadAllText(localConfig));
                if (values == null) return false;

                // 本机配置里的标记用来覆盖「装在本机但用户勾了便携」的情况。
                if (values.TryGetValue("IsPortable", out string text) && bool.TryParse(text, out bool portable))
                {
                    return portable;
                }

                return false;
            }
            catch
            {
                return false;
            }
        }

        public bool SetPortableMode(bool portable)
        {
            if (portable == _isPortable) return true;

            // 先落盘再搬迁：这一步只是搬迁意图的记录，真正成败由下面的移动结果决定。
            SavePortableSetting(portable);

            bool moved = portable ? MoveToPortable() : MoveToLocal();

            if (!moved)
            {
                // 搬迁失败要同时还原配置文件与内存字段：只还原一边，
                // 会让下次启动读到的状态和界面当前显示的对不上。
                SavePortableSetting(_isPortable);
                AppPaths.IsPortable = _isPortable;
                ReportError(
                    EnumText.GetErrorText("Err_PortableMoveFailed", "切换{0}失败：数据目录没能移动", portable ? "便携模式" : "普通模式"));
                return false;
            }

            // 字段必须等搬迁成功后再改：它是上面那个短路条件的一部分，
            // 提前改会让搬迁分支被直接跳过（历史上就出过这个问题）。
            _isPortable = portable;
            AppPaths.IsPortable = portable;
            _logService.WriteLog(
                EnumText.GetErrorText("Log_PortableSwitched", "已切换到{0}，数据目录：{1}",
                    portable ? "便携模式" : "普通模式", AppPaths.DataDirectory),
                LogService.ErrorLevel.Info);

#if DEBUG
            // 该开关只在 DEBUG 下编译进来：测试用它跳过注册表同步，避免污染真实注册表。
            if (SkipShellMenuSync) return true;
#endif
            // 便携模式不该在宿主系统留下右键菜单痕迹，只有切回普通模式才注册。
            if (portable) ShellRegistrationService.UnregisterWithElevation();
            else ShellRegistrationService.RegisterWithElevation();

            return true;
        }

#if DEBUG
        public bool SkipShellMenuSync { get; set; }
#endif

        #endregion
        #region 便携数据目录搬迁
        // 两个方向都走 MoveDataDirectory，失败要如实返回，上层才能回滚配置。
        private static bool MoveToPortable()
        {
            return MoveDataDirectory(
                Path.Combine(AppPaths.GetLocalRootDirectory(), AppPaths.DataFolderName),
                Path.Combine(AppPaths.GetPortableRootDirectory(), AppPaths.DataFolderName));
        }

        private static bool MoveToLocal()
        {
            string portableRoot = AppPaths.GetPortableRootDirectory();
            bool moved = MoveDataDirectory(
                Path.Combine(portableRoot, AppPaths.DataFolderName),
                Path.Combine(AppPaths.GetLocalRootDirectory(), AppPaths.DataFolderName));

            // 搬迁成功才清理：残留的空便携根目录会让下次启动误判成便携模式。
            if (moved) RemoveEmptyDirectory(portableRoot);

            return moved;
        }


        // 只删空目录：便携根下可能有用户自己放的文件，非空一律保留。
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

        // 删除边界按「数据目录 + 分隔符」比较前缀，并先做 Path.GetFullPath 归一化：
        // 只比字符串相等会放行 ...\Auto7z-evil 这类同前缀目录，不归一化则会漏掉 ..\ 绕行。
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

                    // 完全相等也要再核对目录名：盘根这类没有目录名的路径可能凑巧等于拼接结果。
                    if (string.Equals(full, dataDirectory, StringComparison.OrdinalIgnoreCase))
                    {
                        return string.Equals(Path.GetFileName(full), AppPaths.DataFolderName,
                            StringComparison.Ordinal);
                    }

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
                return false;
            }
        }

        private static bool MoveDataDirectory(string sourceDirectory, string targetDirectory)
        {
            try
            {
                // 不整目录 Directory.Move：源和目标分属不同卷时目录级移动会直接失败，只能逐个文件搬。
                // 同名文件直接覆盖：搬迁语义是以源为准，逐个询问会让切换卡在中途。
                if (!Directory.Exists(sourceDirectory))
                {
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

                string fullSource = Path.GetFullPath(sourceDirectory);
                // 文件已经搬完，但源目录要过安全边界才敢删；拿不到结论就保留现场，由上层回滚。
                if (!IsSafeToDeleteRecursively(fullSource)) return false;

                Directory.Delete(sourceDirectory, true);
                return true;
            }
            catch
            {
                return false;
            }
        }

        // 递归合并按文件粒度覆盖：目标位置可能已有同名目录，整体移动在这种情况下会失败。
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

            string fullSource = Path.GetFullPath(sourceDirectory);
            if (!IsSafeToDeleteRecursively(fullSource)) return;

            Directory.Delete(sourceDirectory, true);
        }


        #endregion
        #region 配置保存与关闭收尾
        // 写失败不上抛：切换成败以数据搬迁为准，这一项由后续的 SaveConfig 收口。
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

        // 全量写回而不是逐键更新：字段一多就容易漏，整体构造能保证磁盘与界面一致。
        public void SaveConfig()
        {
            var config = new AppConfig
            {
                Language = CurrentLanguage,
                Volume = Volume,
                Format = Format,
                Password = Password,
                Zstd = Zstd,
                GenerateSHA256 = GenerateSHA256,
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

        public void SaveAutoSaveSetting(bool autoSave)
        {
            _configService.Update("AutoSave", autoSave.ToString());
        }

        // 顺序不能反：先发取消让 7z 有机会自行收尾，再强杀残留进程，避免留下残缺的分卷。
        public void OnClosing()
        {
            CancelCurrentTask();

            _sevenZipService.KillRunningProcesses();

            if (AutoSave)
            {
                SaveConfig();
            }
            else
            {
                // 关窗时未开自动保存，就只把这一项落盘，不覆盖本次运行中未确认的其他改动。
                SaveAutoSaveSetting(false);
            }
        }


        #endregion
        #region 标题与模式文案
        public string FullTitle
        {
            get => _fullTitle;
            set => SetProperty(ref _fullTitle, value);
        }

        public string CopyRightText
        {
            get => _copyRightText;
            set => SetProperty(ref _copyRightText, value);
        }

        public string MainText
        {
            get => _mainText;
            set => SetProperty(ref _mainText, value);
        }

        // 结果文本与颜色分开存放：文本负责显示，颜色由 IsResultFailed 单独驱动。
        public string ResultText
        {
            get => _resultText;
            set => SetProperty(ref _resultText, value);
        }

        public bool IsResultFailed
        {
            get => _isResultFailed;
            set => SetProperty(ref _isResultFailed, value);
        }

        // 界面需要恢复默认提示时统一走这里，避免各处自己拼模式文案。
        public string GetDefaultModeText()
        {
            return GetCachedModeText(CurrentOperationMode);
        }

        // 先查缓存再回落实时取：语言服务未就绪时缓存字段还是 null。
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

        // 任务运行中主文本显示的是进度，语言切换不能把它顶回默认提示。
        private void OnLanguageChanged()
        {
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
        #region 压缩与运行参数属性
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

        public bool GenerateSHA256
        {
            get => _generateSHA256;
            set => SetProperty(ref _generateSHA256, value);
        }

        public bool MaxLevelCompress
        {
            get => _maxLevelCompress;
            set => SetProperty(ref _maxLevelCompress, value);
        }

        public string ZipPasswordInputText
        {
            get => _passwordBoxZipText;
            set
            {
                if (SetProperty(ref _passwordBoxZipText, value))
                {
                    // 有效性跟着输入内容走，打包按钮的可用性直接读这个标志。
                    IsPasswordValid = !string.IsNullOrWhiteSpace(value);
                }
            }
        }

        // 只允许由输入内容推导，外部改不了，避免按钮状态和输入框脱节。
        public bool IsPasswordValid
        {
            get => _isPasswordValid;
            private set => SetProperty(ref _isPasswordValid, value);
        }


        public bool IsTopMost
        {
            get => _isTopMost;
            set => SetProperty(ref _isTopMost, value);
        }

        public bool IsPortable
        {
            get => _isPortable;
            set
            {
                if (value == _isPortable) return;

                if (SetPortableMode(value)) return;

                // 切换失败时 _isPortable 没有变化，不会触发属性通知，
                // 必须手动通知一次，否则勾选框会停在用户点击后的错误状态。
                OnPropertyChanged(nameof(IsPortable));
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

        public LogService.ErrorLevel ErrorLevel
        {
            get => _errorLevel;
            set
            {
                if (SetProperty(ref _errorLevel, value))
                {
                    // 日志级别是全局静态设置，改属性时要同步过去，否则界面档位与实际过滤不一致。
                    LogService.MinLogLevel = value;
                }
            }
        }


        #endregion
        #region 压缩细节与模式属性
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

        public string UnzipPassword
        {
            get => _unzipPassword;
            set => SetProperty(ref _unzipPassword, value);
        }


        // 这几个只读属性由模式推导，只能靠 CurrentOperationMode 的 setter 手动通知。
        public bool IsAutoModeSelected => CurrentOperationMode == OperationMode.Auto;
        public bool IsZipModeSelected => CurrentOperationMode == OperationMode.Zip;
        public bool IsUnZipModeSelected => CurrentOperationMode == OperationMode.UnZip;
        public string CurrentOperationModeName => CurrentOperationMode.ToString();

        // 模式变化会连带影响上面几个只读属性，必须逐个通知，否则界面高亮不跟着变。
        // 忙时主文本是进度，别被模式切换的默认文案顶掉。
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
        #region 控制台输出与进度文案
        public RunMode CurrentRunMode { get; set; } = RunMode.Gui;

        // 运行模式决定输出通道：控制台只认标准输出，GUI 与 Shell 菜单走窗口绑定。
        public bool ConsoleProgressEnabled => CurrentRunMode == RunMode.Console;

        public bool ShellMenuMode => CurrentRunMode == RunMode.ShellMenu;


        private static void WriteConsoleLine(string text)
        {
            WriteConsoleText(text, true);
        }

        // 控制台代码页不是 UTF-8 时，直接 WriteLine 会按旧代码页编码，中文会变成问号。
        private static void WriteConsoleText(string text, bool newLine)
        {
            if (string.IsNullOrEmpty(text)) return;

            try
            {
                // 非 UTF-8 代码页下手工按 OutputEncoding 转字节写 stdout，才能保住中文。
                var encoding = Console.OutputEncoding;
                if (encoding == null || encoding.CodePage == 65001)
                {
                    if (newLine) Console.WriteLine(text);
                    else Console.Write(text);
                    return;
                }

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

        // 控制台模式下进度直接落在标准输出上，界面绑定完全不参与。
        private void WriteConsoleProgress(ProgressInfo info)
        {
            // 只有这三类阶段带百分比，其他阶段输出只会变成无意义的 0%。
            if (info.Stage != ProgressStage.Compressing
                && info.Stage != ProgressStage.Extracting
                && info.Stage != ProgressStage.CalculatingSHA256)
            {
                return;
            }

            // 等密码期间必须静音：7z 的进度行会把刚打出的密码提示冲掉，
            // 用户会以为程序卡住而反复敲键盘。
            if (_awaitingPassword) return;

            // 同一百分点只输出一次：进度回调远比百分比变化密集，不节流会刷出满屏重复行。
            if (_lastConsolePercent == info.Percent) return;
            _lastConsolePercent = info.Percent;

            string template = GetProgressTextTemplate(info.Stage);
            WriteConsoleLine(string.Format(template, info.Percent));
        }


        private string GetProgressTextTemplate(ProgressStage stage)
        {
            switch (stage)
            {
                case ProgressStage.Compressing:
                    return _consoleCompressTemplate ?? "7z processed...{0}%";

                case ProgressStage.Extracting:
                    return _consoleExtractTemplate ?? "7z processed...{0}%";

                case ProgressStage.CalculatingSHA256:
                    return _consoleSHA256Template ?? "SHA256 processed...{0}%";

                default:
                    return _progressTextTemplate ?? "7z: {0}%";
            }
        }

        // 界面与阶段名两处复用同一套模板，集中在这里取值，避免两处措辞走偏。
        public string GetProgressDisplayText(ProgressStage stage, int percent)
        {
            switch (stage)
            {
                case ProgressStage.Compressing:
                case ProgressStage.Extracting:
                case ProgressStage.CalculatingSHA256:
                    return string.Format(GetProgressTextTemplate(stage), percent);

                case ProgressStage.Finished:
                    return GetStageText(ProgressStage.Finished);

                case ProgressStage.Failed:
                    return GetStageText(ProgressStage.Failed);

                default:
                    return GetStageText(stage);
            }
        }

        public string GetStageText(ProgressStage stage)
        {
            switch (stage)
            {
                case ProgressStage.Compressing:
                    return EnumText.GetResourceText("ProcessStageCompress", "Compressing…");

                case ProgressStage.Extracting:
                    return EnumText.GetResourceText("ProcessStageExtract", "Extracting…");

                case ProgressStage.CalculatingSHA256:
                    return EnumText.GetResourceText("ProcessStageSHA256", "Calculating SHA256…");

                case ProgressStage.Finished:
                    return EnumText.GetResourceText("ProcessStageFinished", "Done");

                case ProgressStage.Failed:
                    return EnumText.GetResourceText("ProcessStageFailed", "Failed");

                default:
                    return EnumText.GetResourceText("ProcessWaiting", "Preparing…");
            }
        }

        #endregion
        #region 忙碌状态与待处理文件
        public bool IsBusy
        {
            get => _isBusy;
            private set
            {
                // 忙碌状态是命令可用性的唯一开关，集中在 setter 里刷新，避免有路径漏掉。
                if (SetProperty(ref _isBusy, value))
                {
                    RaiseCommandStates();

                    // 只在忙转闲的跳变上触发：所有任务路径的收尾都经过这里，窗口不必各自判断时机。
                    if (!value) TaskCompleted?.Invoke(this, EventArgs.Empty);
                }
            }
        }


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


        // 任务失败标记由失败路径写入，供调用方在回调之后判断这次结果。
        public bool LastTaskFailed { get; private set; }

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

                string full = NormalizeInputPath(path);

                try
                {
                    if (File.Exists(full))
                    {
                        // 懒加载：先把路径收下，等真要压缩时再去统计大小与文件数。
                        items.Add(PendingFile.CreateLazy(full, false));
                    }
                    else if (Directory.Exists(full))
                    {
                        items.Add(PendingFile.CreateLazy(full, true));
                    }
                    else
                    {
                        rejected++;
                    }
                }
                catch
                {
                    rejected++;
                }
            }

            // 失效路径只跳过、不整批拒绝：拖入的文件里混进一个无效项，不该让其余可用输入作废。
            if (rejected > 0)
            {
                _logService.WriteLog(
                    EnumText.GetErrorText("Log_InputRejected", "{0} 个输入路径不存在或不可访问，已跳过", rejected),
                    LogService.ErrorLevel.Warning);
            }

            _pendingBatch = new PendingFileBatch(items);
        }


        #endregion
        #region 等待式任务入口
        public void BeginCompressTask() => StartTaskInBackground(report => CompressFiles(_pendingBatch.Files.ToList(), report));

        public void BeginExtractTask() => StartTaskInBackground(StartExtract);

        // 控制台与 Shell 菜单进程没有消息循环，任务必须同步跑完才能退出，所以保留这组阻塞式入口。
        // 任务无论成败都走 finally：取消源在那里释放并清空，IsBusy 才不会被卡在 true。
        // 进度先更新界面再转给外部回调，两边逻辑独立，顺序不影响各自结果。
        public void RunTaskAndWait(Action<Action<ProgressInfo>> task)
        {
            RunTaskAndWait(task, null);
        }

        // 外部回调用于把进度同时转给调用方，界面更新仍然照常走 UpdateProgress。
        public void RunTaskAndWait(Action<Action<ProgressInfo>> task, Action<ProgressInfo> externalReport)
        {
            // 忙时直接放弃而不是排队：同时跑两条 7z 会争抢同一个输出目录。
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
                SetResult(EnumText.GetResourceText("Result_Failed", "Failed"), failed: true);
            }
            finally
            {
                _cancellation?.Dispose();
                _cancellation = null;
                IsBusy = false;
            }
        }

        public void RunCompressAndWait()
        {
            RunCompressAndWait(null);
        }

        public void RunCompressAndWait(Action<ProgressInfo> externalReport)
        {
            RunTaskAndWait(report => CompressFiles(_pendingBatch.Files.ToList(), report), externalReport);
        }

        public void RunExtractAndWait()
        {
            RunExtractAndWait(null);
        }

        public void RunExtractAndWait(Action<ProgressInfo> externalReport)
        {
            RunTaskAndWait(StartExtract, externalReport);
        }

        public void RunAutoDispatchAndWait()
        {
            RunAutoDispatchAndWait(null);
        }

        public void RunAutoDispatchAndWait(Action<ProgressInfo> externalReport)
        {
            RunTaskAndWait(StartAutoDispatch, externalReport);
        }

        public void BeginAutoDispatchTask() => StartTaskInBackground(StartAutoDispatch);


        #endregion
        #region 后台任务调度
        // 自动模式按输入类型分流：压缩包逐个解压，其余文件压缩，混合输入就两件事都做。
        private void StartAutoDispatch(Action<ProgressInfo> report)
        {
            var batch = _pendingBatch;
            if (batch.IsEmpty)
            {
                CompleteWithoutWork(report);
                return;
            }

            var archives = new List<PendingFile>();
            var others = new List<PendingFile>();
            foreach (var f in batch.Files)
            {
                // 目录一律当普通输入：名字里带 .zip 的文件夹不是压缩包，先按 IsDirectory 排除。
                if (!f.IsDirectory && _sevenZipService.IsArchiveFile(f.Path)) archives.Add(f);
                else others.Add(f);
            }

            if (archives.Count == 0 && others.Count == 0)
            {
                CompleteWithoutWork(report);
                return;
            }

            // 一个可压缩的输入都没有，就只剩逐个解压这一条路。
            if (others.Count == 0)
            {
                _logService.WriteLog(EnumText.GetErrorText("Log_AutoExtractEach", "自动模式：逐个解压 {0} 个压缩包", archives.Count), LogService.ErrorLevel.Info);
                ExtractEach(archives, report);
                return;
            }

            if (archives.Count == 0)
            {
                CompressFiles(others, report);
                return;
            }

            _logService.WriteLog(EnumText.GetErrorText("Log_AutoMixed",
                "自动模式：混合输入，压缩包 {0} 个逐个解压，其余 {1} 个压缩", archives.Count, others.Count),
                LogService.ErrorLevel.Info);

            ExtractEach(archives, report);
            CompressFiles(others, report);
        }

        // 逐个解压：每个包各自解到自己的目标目录，互不干扰，取消时直接中断循环。
        private void ExtractEach(List<PendingFile> files, Action<ProgressInfo> report)
        {
            foreach (var f in files)
            {
                if (CurrentToken.IsCancellationRequested) return;

                StartExtract(report, f.Path);
            }
        }

        // StartCompress 只从 _pendingBatch 取输入，逐个压缩要临时换入单个文件，并在 finally 里还原。
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


        // 丢到后台并立刻返回：命令处理发生在 UI 线程上，
        // 在这里等待任务会把界面冻住，取消按钮也点不动。
        private void StartTaskInBackground(Action<Action<ProgressInfo>> task) => _ = RunTaskAsync(task);

        private async Task RunTaskAsync(Action<Action<ProgressInfo>> task)
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
                // 任务体内部是阻塞式等待 7z，整体挪到线程池；await 只负责把收尾动作带回 UI 线程。
                await System.Threading.Tasks.Task.Run(() => task(report));
            }
            catch (Exception ex)
            {
                AppState.CurrentStatus = AppState.OperationStatus.Error;
                _logService.WriteLog(EnumText.GetErrorText("Log_TaskException", "任务执行异常：{0}", ex.Message), LogService.ErrorLevel.Error);
                ReportError(ex.Message);
                MainText = GetDefaultModeText();
                SetResult(EnumText.GetResourceText("Result_Failed", "Failed"), failed: true);
            }
            finally
            {
                _cancellation?.Dispose();
                _cancellation = null;
                IsBusy = false;
            }
        }

        #endregion
        #region 进度更新与任务日志
        private void UpdateProgress(ProgressInfo info)
        {
            if (info == null) return;

            // 先分流运行模式：GUI 进程没有真实控制台，往控制台写会抛异常或丢字，
            // 只有控制台模式才走输出分支。
            if (ConsoleProgressEnabled)
            {
                WriteConsoleProgress(info);
                return;
            }

            // Shell 菜单进程同样没有窗口可更新，进度直接忽略。
            if (CurrentRunMode == RunMode.ShellMenu)
            {
                return;
            }

            var app = Application.Current;
            // 回调来自线程池，而绑定属性只能在 UI 线程上改：
            // 这里切回 Dispatcher 再走一遍，避免跨线程通知打到界面上。
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

                case ProgressStage.CalculatingSHA256:
                    MainText = string.Format(
                        _sha256ProgressTextTemplate ?? "SHA256: {0}%", info.Percent);
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


        // 控制台进度行同样走资源模板，和界面文案保持同一套措辞。
        private void ReportTaskStart(string templateKey, string fallback, string argument)
        {
            if (CurrentRunMode == RunMode.Console)
            {
                WriteConsoleLine(EnumText.GetFormattedText(templateKey, fallback, argument));
            }
        }

        // 只在控制台模式下输出终态：GUI 的结果文字由 SetResult 负责。
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

        #endregion
        #region 压缩分发
        private void CompressFiles(List<PendingFile> files, Action<ProgressInfo> report)
        {
            if (files == null || files.Count == 0)
            {
                CompleteWithoutWork(report);
                return;
            }

            ReportTaskStart("Console_CompressStart", "开始压缩：{0}",
                string.Join("; ", files.Select(f => f.Path)));

            // 多个输入时合并与逐个压缩的产物差别很大，只能问过用户再决定。
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


        // 把整批文件捏成一个批次交给 StartCompress，用完立刻还原原来的批次引用。
        // 借助 _pendingBatch 传参，StartCompress 的签名只认当前批次，不必额外加参数。
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
        #region 压缩命令执行
        private void StartCompress(Action<ProgressInfo> report)
        {
            // 先置运行状态：界面的模式提示与日志都靠它判断当前在做什么。
            AppState.CurrentStatus = AppState.OperationStatus.CompressFile;

            var batch = _pendingBatch;
            if (batch.IsEmpty)
            {
                CompleteWithoutWork(report);
                return;
            }

            if (!EnsureComponents(report)) return;

            string validatedOutputPath = ValidateOutputPath();

            if (!_sevenZipService.TryPrepareCompressOutput(validatedOutputPath, batch.Files[0], out string outputDirectory, out Exception pathError))
            {
                FailWithError(pathError, report);
                return;
            }

            string baseName = BuildArchiveBaseName(batch);

            // 先补齐输入总大小，后面决定分卷时才有依据：
            // 源目录体积大但文件数很少时不该切分，只按体积判断会切出没意义的卷。
            if (SevenZipService.CanSplitVolume(Format, Volume))
            {
                batch.EnsureSizes(path => _sevenZipService.GetSizeInBytes(path));
            }

            var runtimeConfig = BuildCompressConfig(batch, baseName);
            string archivePath = _sevenZipService.BuildArchiveFilePath(outputDirectory, baseName, runtimeConfig);

            var commands = _sevenZipService.BuildCompressCommands(
                batch.Files.Select(f => f.Path).ToArray(), runtimeConfig, archivePath);
            if (commands.Count == 0)
            {
                FailWithError(new ArgumentException(EnumText.GetErrorText("Err_BuildCompressCommand", "无法构建压缩命令")), report);
                return;
            }

            if (!RunCompressCommands(commands, archivePath, outputDirectory, report)) return;

            // SHA256 必须等压缩全部结束：校验对象是最终产物，分卷清单要到这时才确定。
            if (GenerateSHA256)
            {
                WriteChecksumFiles(archivePath, outputDirectory, report);
            }

            AppState.SevenZUsageCount++;
            AppState.CurrentStatus = AppState.OperationStatus.Finish;
            report(new ProgressInfo(100, ProgressStage.Finished));
            string doneText = EnumText.GetResourceText("Result_CompressDone", "压缩完成");
            SetResult(doneText, failed: false);
            ReportTaskResult(true, doneText);
        }

        // 把界面属性快照成配置对象：压缩跑在线程池上，服务层不该直接读绑定属性。
        private AppConfig BuildCompressConfig(PendingFileBatch batch, string baseName)
        {
            return new AppConfig
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
        }


        private bool RunCompressCommands(List<string> commands, string archivePath, string outputDirectory, Action<ProgressInfo> report)
        {
            var produced = new List<string> { archivePath };
            bool hasWarning = false;

            // 多条命令存在先后依赖（先打包 tar 再压缩），必须串行执行；
            // 前一条失败时后一条的输入根本不存在，继续跑只会产出更混乱的结果。
            // 单个命令带警告不算失败：7z 跳过个别文件时压缩包仍然可用，降级成日志提示即可。
            for (int i = 0; i < commands.Count; i++)
            {
                // 中间 tar 是临时产物，先登记下来，无论成败都要清理，避免残留占空间。
                if (i > 0)
                {
                    produced.Add(SevenZipService.BuildIntermediateTarPath(archivePath));
                }

                var result = _sevenZipService.ExecuteCommand(commands[i], p => report(new ProgressInfo(p, ProgressStage.Compressing)), CurrentToken);

                if (result.HasWarning)
                {
                    hasWarning = true;
                    _logService.WriteLog(EnumText.GetErrorText("Log_SevenZipWarning", "7-Zip 提示（有警告但已完成）：{0}", result.GetFailureDetail()), LogService.ErrorLevel.Warning);
                }

                // 失败要清掉半成品：残留的分卷会被下一次压缩当成已有文件，产出错乱的包。
                if (!result.Succeeded)
                {
                    CleanupProducedFiles(produced);
                    _sevenZipService.CleanupUnfinished(outputDirectory);
                    FailFromCommand(result, report, result.Cancelled);
                    return false;
                }
            }

            // 退出码正常不代表真有产出，必须回磁盘确认压缩包存在。
            if (!_sevenZipService.ArchiveExists(archivePath))
            {
                CleanupProducedFiles(produced);
                _sevenZipService.CleanupUnfinished(outputDirectory);
                FailWithError(new FileNotFoundException(EnumText.GetErrorText("Err_ArchiveNotCreated", "压缩命令已结束，但未生成压缩文件：{0}", archivePath)), report);
                return false;
            }

            if (commands.Count > 1)
            {
                // 跳过第 0 项：那是最终压缩包，需要保留；后面的中间 tar 才要清。
                CleanupProducedFiles(produced.Skip(1));
            }

            if (hasWarning)
            {
                _messageService.ShowWarning(EnumText.GetResourceText("Result_PartialWarning", "压缩已完成，但部分文件被跳过，详情见日志。"), out string warnLog);
                _logService.WriteLog(warnLog, LogService.ErrorLevel.Warning);
            }

            return true;
        }


        #endregion
        #region 校验文件与命名
        private void WriteChecksumFiles(string archivePath, string outputDirectory, Action<ProgressInfo> report)
        {
            // 分卷清单要到压缩结束后才能收集完整，所以校验单独走一趟，不塞进压缩循环。
            var produced = _sevenZipService.CollectProducedFiles(archivePath);

            try
            {
                for (int partIndex = 0; partIndex < produced.Count; partIndex++)
                {
                    // 多个分卷共用一条进度条，按分卷序号折算成整体百分比，避免进度来回跳。
                    int slice = partIndex;
                    _sevenZipService.GenerateSHA256(produced[partIndex],
                        p => report(new ProgressInfo(
                            MapSHA256Progress(slice, produced.Count, p), ProgressStage.CalculatingSHA256)),
                        CurrentToken);
                }
            }
            // 取消同样会留下半成品，清理后再按取消路径收尾。
            catch (OperationCanceledException)
            {
                CleanupProducedFiles(produced);
                _sevenZipService.CleanupUnfinished(outputDirectory);
                FailFromCommand(null, report, cancelled: true);
            }
            catch (Exception ex)
            {
                _logService.WriteLog(
                    // 校验失败只记警告：压缩包本身已经产出，不该因为附加的校验文件把任务判失败。
                    EnumText.GetErrorText("Log_SHA256Failed", "生成 SHA256 失败：{0}", ex.Message),
                    LogService.ErrorLevel.Warning);
            }
        }


        // 目录取目录名、文件去掉扩展名：否则会产出 a.zip.zip 这类双重后缀。
        private static string BuildArchiveBaseName(PendingFileBatch batch)
        {
            if (batch == null || batch.IsEmpty) return "Archive";

            var first = batch.Files[0];
            string baseName = first.IsDirectory
                ? first.Name
                : Path.GetFileNameWithoutExtension(first.Path);

            if (string.IsNullOrWhiteSpace(baseName)) baseName = "Archive";

            // 合并包用「首个名字 + 数量」命名，让人一眼看出包里不止一个输入。
            if (batch.Files.Count > 1)
            {
                string pattern = EnumText.GetResourceText("Archive_MergedNamePattern", "{0} 等 {1} 个");
                baseName = pattern
                    .Replace("{0}", baseName)
                    .Replace("{1}", batch.Files.Count.ToString(CultureInfo.InvariantCulture));
            }

            return baseName;
        }


        #endregion
        #region 解压启动
        // 无参重载从 _pendingBatch 取第一个输入，带 override 的重载供逐个解压直接指定路径。
        private void StartExtract(Action<ProgressInfo> report)
        {
            StartExtract(report, null);
        }

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

            // 分卷包跳过存在性检查：7z 自己会报缺哪一卷，这里提前判不存在反而掩盖真实原因。
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
                // 格式交给魔数探测：解压不依赖扩展名，用户改过名也要能认出来。
                Format = DetectArchiveFormat(archive),
                Password = UnzipPassword,
                CurrentRunStatus = AppState.OperationStatus.ExtractFile
            };

            // 分卷必须从第一卷喂给 7z，拿中间卷当入参会被直接拒绝。
            string sourceArchive = isSplit ? SevenZipService.ResolveFirstVolumePath(archive) : archive;

            RunExtractWithPasswordRetry(sourceArchive, destinationDirectory, runtimeConfig, report);
        }


        #endregion
        #region 密码重试与解压收尾
        private void RunExtractWithPasswordRetry(string sourceArchive, string destinationDirectory, AppConfig runtimeConfig, Action<ProgressInfo> report)
        {
            // 限次是为了在用户手误和无限循环之间取平衡：超过上限就放弃，避免流程卡死。
            const int MaxPasswordAttempts = 3;
            for (int attempt = 1; attempt <= MaxPasswordAttempts; attempt++)
            {
                // 每轮都重新构建命令：上一轮可能刚更新了密码，命令文本必须跟着变。
                string command = _sevenZipService.BuildExtractCommand(sourceArchive, destinationDirectory, runtimeConfig);
                if (string.IsNullOrWhiteSpace(command))
                {
                    FailWithError(new ArgumentException(EnumText.GetErrorText("Err_BuildExtractCommand", "无法构建解压命令")), report);
                    return;
                }

                var result = _sevenZipService.ExecuteCommand(command, p => report(new ProgressInfo(p, ProgressStage.Extracting)), CurrentToken);

                // 把退出码与密码状态逐次记下来，事后能从日志区分是密码问题还是包损坏。
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

                // 只有密码相关的失败才值得重试，其他错误重试不会有不同结果。
                if (result.RequiresPassword || result.IsWrongPassword)
                {
                    if (!AskPasswordAndRetry(result, attempt, MaxPasswordAttempts, destinationDirectory, runtimeConfig, report))
                    {
                        return;
                    }

                    continue;
                }

                _sevenZipService.CleanupUnfinished(destinationDirectory);
                // 非密码类失败没有重试机会，先把解压出的碎片清掉，再记录失败原因。
                LogExtractFailure(result, sourceArchive);
                FailFromCommand(result, report, result.Cancelled);
                return;
            }
        }

        // 询问前先清理上一次尝试的碎片，避免新旧文件混在一起。
        private bool AskPasswordAndRetry(CommandResult result, int attempt, int maxAttempts,
            string destinationDirectory, AppConfig runtimeConfig, Action<ProgressInfo> report)
        {
            bool isWrong = result.IsWrongPassword;
            _logService.WriteLog(
                isWrong
                    ? EnumText.GetErrorText("Log_PasswordWrong", "密码错误，第 {0} 次尝试，重新询问密码", attempt)
                    : EnumText.GetErrorText("Log_PasswordAsked", "7-Zip 索要密码，第 {0} 次尝试，弹窗请求输入", attempt),
                LogService.ErrorLevel.Info);

            string prompt = isWrong
                ? (CurrentRunMode == RunMode.Console
                    ? EnumText.GetFormattedText("Console_PasswordWrong", "密码错误，第 {0} 次尝试，重新询问密码：", attempt)
                    : EnumText.GetFormattedText("PasswordWrongAttempt", "密码错误，第 {0} 次尝试，重新询问密码：", attempt))
                : (CurrentRunMode == RunMode.Console
                    ? EnumText.GetResourceText("Console_PasswordPrompt", "该压缩包已加密，请输入密码：")
                    : EnumText.GetResourceText("PasswordPrompt", "该压缩包已加密，请输入密码："));

            _sevenZipService.CleanupUnfinished(destinationDirectory);

            // 次数用尽才放弃：先清干净现场，再报连续多次密码错误。
            if (attempt >= maxAttempts)
            {
                FailWithError(new InvalidOperationException(EnumText.GetErrorText("Err_PasswordRetries", "连续多次密码错误，已放弃解压")), report);
                return false;
            }

            // 挂起控制台进度输出：等密码期间 7z 仍在跑，进度行会冲掉密码提示。
            _awaitingPassword = true;
            string password;
            try
            {
                // 弹窗与控制台读取都可能长时间阻塞，标志位必须用 try/finally 保证还原。
                password = RequestPassword(prompt, attempt);
            }
            finally
            {
                _awaitingPassword = false;
            }

            // 拿不到密码（用户取消或没有可读输入）按取消处理：用空密码重试只会再报一次密码错。
            if (password == null)
            {
                _sevenZipService.CleanupUnfinished(destinationDirectory);
                FailFromCommand(result, report, cancelled: true);
                return false;
            }

            runtimeConfig.Password = password;
            // 回写到界面属性：同一次运行里再遇到加密包就不必重复输入。
            UnzipPassword = password;
            return true;
        }


        private void FinishExtract(Action<ProgressInfo> report, string destinationDirectory, string sourceArchive)
        {
            // .tar.gz/.tar.zst 是两层容器：第一层只解出中间的 .tar，还要再解一次才是真实内容。
            if (SevenZipService.IsCompressedTar(sourceArchive))
            {
                // 内层失败时目录里只剩一个中间 tar，清掉避免被误认为解压结果。
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
        #region 密码获取
        private string RequestPassword(string prompt = null, int attempt = 1)
        {
            if (CurrentRunMode == RunMode.Console)
            {
                // 运行模式决定取密码的方式：控制台读标准输入，GUI 与 Shell 菜单弹窗。
                return RequestPasswordOnConsole(prompt, attempt);
            }

            var app = Application.Current;
            if (app == null) return null;

            PasswordWindow window = null;
            // 调用方通常在后台线程，而弹窗只能在 UI 线程创建，用事件把用户输入同步回来。
            var completed = new ManualResetEventSlim(false);

            try
            {
                // 已在 UI 线程上就直接建窗：再 BeginInvoke 的话下面的 Wait 会把 UI 线程锁死。
                if (app.Dispatcher.CheckAccess())
                {
                    window = CreatePasswordWindow(completed, prompt);
                }
                else
                {
                    app.Dispatcher.BeginInvoke(new Action(() => window = CreatePasswordWindow(completed, prompt)));
                }
            }
            catch (Exception ex)
            {
                _logService.WriteLog(EnumText.GetErrorText("Log_PasswordDialogFailed", "弹出密码输入框失败：{0}", ex.Message), LogService.ErrorLevel.Error);
                completed.Dispose();
                return null;
            }

            // 阻塞到窗口关闭：压缩与解压任务本来就跑在后台线程，这里等不会冻住界面。
            completed.Wait();

            string password = window != null && window.Confirmed ? window.Password : null;
            completed.Dispose();

            _logService.WriteLog(password == null ? EnumText.GetErrorText("Log_PasswordCancelled", "用户取消了密码输入") : EnumText.GetErrorText("Log_PasswordGot", "已获取密码"), LogService.ErrorLevel.Info);
            return password;
        }

        private string RequestPasswordOnConsole(string prompt, int attempt)
        {
            string message = !string.IsNullOrWhiteSpace(prompt)
                ? prompt
                : EnumText.GetResourceText("Console_PasswordPrompt", "该压缩包已加密，请输入密码：");

            WriteConsoleText(message, false);

            // 管道或计划任务下没有可读输入，ReadConsoleLine 会立刻返回 null，这种情况按用户取消处理。
            string line = ReadConsoleLine();
            if (line == null)
            {
                _logService.WriteLog(EnumText.GetErrorText("Log_PasswordCancelled", "用户取消了密码输入"), LogService.ErrorLevel.Info);
                WriteConsoleLine(string.Empty);
                return null;
            }

            WriteConsoleLine(string.Empty);

            _logService.WriteLog(EnumText.GetErrorText("Log_PasswordGot", "已获取密码"), LogService.ErrorLevel.Info);
            return line;
        }

        // 重定向被关闭等异常同样视为读不到输入，统一返回 null 交给调用方判断。
        private static string ReadConsoleLine()
        {
            try
            {
                string line = Console.ReadLine();
                if (line == null) return null;
                return line.Trim();
            }
            catch
            {
                return null;
            }
        }

        private static PasswordWindow CreatePasswordWindow(ManualResetEventSlim completed, string prompt = null)
        {
            var window = new PasswordWindow { Owner = Application.Current.MainWindow };
            if (!string.IsNullOrWhiteSpace(prompt)) window.SetPrompt(prompt);
            window.Closed += (s, e) => completed.Set();
            window.Show();
            return window;
        }

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
        #region tar 二次解压与格式判定
        private bool ExtractInnerTar(string destinationDirectory, string sourceArchive, Action<ProgressInfo> report)
        {
            // 中间 tar 的名字没有清单可查，只能按外层名剥掉压缩后缀反推出来。
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
            // 找不到就按失败返回：上层会据此清理目标目录，不留下半个解压结果。
            if (!File.Exists(innerTarPath))
            {
                _logService.WriteLog(EnumText.GetErrorText("Log_InnerTarMissing", "未找到中间 tar 文件：{0}", innerTarPath), LogService.ErrorLevel.Warning);
                return false;
            }

            // 内层 tar 不带密码：加密只在外层，给 tar 传密码会让 7z 直接报错。
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

            try
            {
                // 中间 tar 只是过渡产物，解开后立即删除，避免占双份空间。
                if (File.Exists(innerTarPath)) File.Delete(innerTarPath);
            }
            catch
            {
                // 删不掉不影响结果
            }

            return true;
        }

        // tar 系列没有可用魔数（就是未压缩的归档流），只能先靠扩展名认出来。
        private ArchiveFormat DetectArchiveFormat(string archivePath)
        {
            if (IsTarExtension(archivePath))
            {
                return ArchiveFormat.Tar;
            }

            try
            {
                // 按文件头魔数判断而不是扩展名：改名或后缀缺失时扩展名不可信。
                string detected = ArchiveFormatMap.DetectByMagic(archivePath);
                var parsed = ArchiveFormatMap.Parse(detected);
                if (parsed.HasValue) return parsed.Value;
            }
            catch
            {
                // 识别失败时按默认处理
            }

            // 认不出来就交给 7z 自己判断：它对多数格式都能识别，这里抛错反而挡住可用的解压。
            return ArchiveFormat.SevenZip;
        }

        private static bool IsTarExtension(string archivePath)
        {
            if (string.IsNullOrWhiteSpace(archivePath)) return false;

            string name = Path.GetFileName(archivePath).ToLowerInvariant();

            if (name.EndsWith(".tar", StringComparison.Ordinal)) return true;
            if (name.EndsWith(".tgz", StringComparison.Ordinal)) return true;
            if (name.EndsWith(".tbz2", StringComparison.Ordinal)) return true;
            if (name.EndsWith(".txz", StringComparison.Ordinal)) return true;
            if (name.EndsWith(".tzst", StringComparison.Ordinal)) return true;

            if (name.EndsWith(".tar.gz", StringComparison.Ordinal)) return true;
            if (name.EndsWith(".tar.zst", StringComparison.Ordinal)) return true;
            if (name.EndsWith(".tar.bz2", StringComparison.Ordinal)) return true;
            if (name.EndsWith(".tar.xz", StringComparison.Ordinal)) return true;

            return false;
        }


        #endregion
        #region 多文件打包询问
        // 询问方式随运行模式切换：GUI 弹对话框，控制台读标准输入。
        private bool AskPackInOneFile()
        {
            if (CurrentRunMode == RunMode.Console)
            {
                return AskPackInOneFileOnConsole();
            }

            string question = EnumText.GetResourceText("MultiFilePackQuery",
                "Multiple files have been detected. Pack into a single file?");
            if (string.IsNullOrWhiteSpace(question)) return true;

            bool result = _messageService.AskYesNo(question, out string log);
            _logService.WriteLog(log, LogService.ErrorLevel.Info);

            return result;
        }

        private bool AskPackInOneFileOnConsole()
        {
            string question = EnumText.GetResourceText("Console_MergeQuestion",
                "Multiple files detected. Pack into a single archive? (Y/N)");

            WriteConsoleText(question, false);

            bool hasInput = TryReadConsoleAnswer(out bool answer);

            WriteConsoleLine(string.Empty);

            // 拿不到输入（管道、计划任务）时默认合并：非交互环境下无从追问，合并是更安全的默认。
            return hasInput ? answer : true;
        }

        private static bool TryReadConsoleAnswer(out bool answer)
        {
            answer = true;

            try
            {
                string line = Console.ReadLine();
                if (line == null) return false;

                line = line.Trim();
                if (line.Length == 0) return true;

                // 只有明确的否定才算不合并，其余输入含打错字一律按合并处理，避免误判。
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
        #region 校验与失败汇报
        private string ValidateOutputPath()
        {
            if (string.IsNullOrWhiteSpace(OutputPath)) return string.Empty;

            if (FileSystemHelper.TryValidateOutputPath(OutputPath, out string directory))
            {
                return directory ?? string.Empty;
            }

            _logService.WriteLog(EnumText.GetErrorText("Log_OutputPathInvalid", "输出路径不存在，已清空：{0}", OutputPath), LogService.ErrorLevel.Warning);
            // 失效路径就地清空并通知界面：留在属性里，下次压缩会继续拿着同一个坏路径重试。
            OutputPath = string.Empty;
            OutputPathInvalidated?.Invoke(this, EventArgs.Empty);

            return string.Empty;
        }

        private bool EnsureComponents(Action<ProgressInfo> report)
        {
            // 先让服务层准备（必要时落地）组件，再确认文件真的存在，只检查会漏掉首次运行的情况。
            _sevenZipService.EnsureComponents(out Exception error);
            if (error == null && _sevenZipService.CheckComponentsExist()) return true;

            FailWithError(error ?? new FileNotFoundException(EnumText.GetErrorText("Err_ComponentMissing", "未找到 7-Zip 组件")), report);
            return false;
        }

        private void CompleteWithoutWork(Action<ProgressInfo> report)
        {
            // 没活可干也要发一次完成事件，否则界面会一直停在准备中。
            AppState.CurrentStatus = AppState.OperationStatus.Normal;
            report(new ProgressInfo(0, ProgressStage.Finished));
            MainText = GetDefaultModeText();
        }

        private void ReportError(string detail)
        {
            if (CurrentRunMode == RunMode.Console)
            {
                // 控制台与无人值守场景不能弹窗：错误只写日志，避免任务卡在没人看的对话框上。
                _logService.WriteLog(detail, LogService.ErrorLevel.Error);
                return;
            }

            _messageService.ShowError(detail, out string log);
            _logService.WriteLog(log, LogService.ErrorLevel.Error);
        }


        // 失败路径除了写日志和发进度，还必须调 SetResult：
        // 否则主窗口的状态文字会停在默认提示，看起来像什么都没发生。
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

        // 取消与其他失败分开处理：取消是用户意图，运行状态回到 Normal、结果文字用已取消，
        // 而真正的失败要落到 Error 状态并把细节记进日志。
        private void FailFromCommand(CommandResult result, Action<ProgressInfo> report, bool cancelled)
        {
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
            SetResult(EnumText.GetResourceText("Result_Failed", "Failed"), failed: true);
            ReportTaskResult(false, detail);
        }

        // 必须先写 IsResultFailed 再写文本：颜色跟着这个标志走，
        // 顺序反了会用上一次的颜色渲染新文本（历史上就出过这个顺序问题）。
        public void SetResult(string text, bool failed)
        {
            IsResultFailed = failed;

            // 文本相同也不会跳过通知：颜色由上面的标志驱动，刚变过就必须让界面重读一次。
            if (!SetProperty(ref _resultText, text ?? string.Empty, nameof(ResultText)))
            {
                OnPropertyChanged(nameof(ResultText));
            }
        }


        #endregion
        #region 辅助与收尾
        // 归一化失败就原样返回：路径合不合法交给后面的存在性判断，这里不该提前丢弃输入。
        private static string NormalizeInputPath(string path)
        {
            try
            {
                return Path.GetFullPath(path);
            }
            catch (Exception)
            {
                return path;
            }
        }

        // 把「第几个分卷 + 卷内百分比」折算成整体百分比，进度条才不会在每个分卷开头回退。
        private static int MapSHA256Progress(int index, int count, int percent)
        {
            if (count <= 1) return percent;
            if (percent < 0) percent = 0;
            if (percent > 100) percent = 100;
            int mapped = (int)Math.Round((index * 100.0 + percent) / count);
            return mapped > 100 ? 100 : mapped;
        }

        // 清理属于收尾动作：删不掉也只是残留文件，绝不能抛异常盖掉真正的失败原因。
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


        // 关于窗口每次打开都重新取值：版本号与版权年份不能是启动时算好的快照。
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
            // 起始年份是常量，跨年后自动变成年份区间，不需要每年改代码。
            const int startYear = 2024;
            int currentYear = DateTime.Now.Year;
            string yearRange = currentYear == startYear ? startYear.ToString(CultureInfo.InvariantCulture) : $"{startYear}-{currentYear}";
            string rightsText = EnumText.GetResourceText("CopyRightText", "All rights reserved.");
            CopyRightText = $"Copyright © {yearRange} LatteYogurt, {rightsText}";
        }

        public void ApplyLanguage()
        {
            // 配置里可能残留下线或拼错的语言代码，先退回默认语言，避免界面文案全部落空。
            if (!LanguageService.IsSupported(CurrentLanguage))
            {
                CurrentLanguage = LanguageService.FallbackLanguage;
            }

            _languageService.SwitchLanguage(CurrentLanguage);

            // 右键菜单文案存在注册表里，语言切换要同步过去，否则菜单还是旧语言。
            ShellRegistrationService.SetLanguage(CurrentLanguage);
        }


        #endregion
    }
}
