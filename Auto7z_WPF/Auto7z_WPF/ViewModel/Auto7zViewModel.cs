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

        private int _lastConsolePercent = -1;

        private bool _awaitingPassword;

        private bool _isPortable;

        private bool _isSolidMode;
        private bool _isEncFileName;
        private CompressionLevelPreset _compressionLevel = CompressionLevelPreset.Normal;

        private string _unzipPassword = string.Empty;

        private PendingFileBatch _pendingBatch = new PendingFileBatch(null);
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

            _autoModeText = EnumText.ToText(OperationMode.Auto);
            _zipModeText = EnumText.ToText(OperationMode.Zip);
            _unzipModeText = EnumText.ToText(OperationMode.UnZip);
            _mainText = _autoModeText;
            RefreshProgressTemplates();
        }

        private void RefreshProgressTemplates()
        {
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
            _isPortable = config.IsPortable || AppPaths.IsPortable;
            RuntimeMode = config.RuntimeMode;
            OutputPath = config.OutputPath;
            IsSolidMode = config.IsSolidMode;
            IsEncFileName = config.IsEncFileName;
            CompressionLevel = config.CompressionLevel;
            UnzipPassword = config.UnzipPassword;
            CurrentLanguage = config.Language;

            ErrorLevel = config.ErrorLevel;

            AppState.SevenZUsageCount = config.SevenZUsageCount;
            _mainText = GetDefaultModeText();
        }


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

                var values = JsonConfig.Parse(File.ReadAllText(localConfig));
                if (values == null) return false;

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

            SavePortableSetting(portable);

            bool moved = portable ? MoveToPortable() : MoveToLocal();

            if (!moved)
            {
                SavePortableSetting(_isPortable);
                AppPaths.IsPortable = _isPortable;
                ReportError(
                    EnumText.GetErrorText("Err_PortableMoveFailed", "切换{0}失败：数据目录没能移动", portable ? "便携模式" : "普通模式"));
                return false;
            }

            _isPortable = portable;
            AppPaths.IsPortable = portable;
            _logService.WriteLog(
                EnumText.GetErrorText("Log_PortableSwitched", "已切换到{0}，数据目录：{1}",
                    portable ? "便携模式" : "普通模式", AppPaths.DataDirectory),
                LogService.ErrorLevel.Info);

#if DEBUG
            if (SkipShellMenuSync) return true;
#endif
            if (portable) ShellRegistrationService.UnregisterWithElevation();
            else ShellRegistrationService.RegisterWithElevation();

            return true;
        }

#if DEBUG
        public bool SkipShellMenuSync { get; set; }
#endif

        #endregion
        #region 便携数据目录搬迁
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

            if (moved) RemoveEmptyDirectory(portableRoot);

            return moved;
        }


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
                // 删不掉就留着，一个空目录不影响后续流程。
            }
        }

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
                if (!IsSafeToDeleteRecursively(fullSource)) return false;

                Directory.Delete(sourceDirectory, true);
                return true;
            }
            catch
            {
                return false;
            }
        }

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
        private void SavePortableSetting(bool portable)
        {
            try
            {
                _configService.Update("IsPortable", portable.ToString());
            }
            catch
            {
                // 写配置失败不影响本次切换，下次启动会按目录事实重新探测。
            }
        }

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

        public string GetDefaultModeText()
        {
            return GetCachedModeText(CurrentOperationMode);
        }

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
            set => SetProperty(ref _passwordBoxZipText, value);
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
        #region 控制台输出与进度文案
        public RunMode CurrentRunMode { get; set; } = RunMode.Gui;

        public bool ConsoleProgressEnabled => CurrentRunMode == RunMode.Console;


        private static void WriteConsoleLine(string text)
        {
            WriteConsoleText(text, true);
        }

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
                    // 连标准流都写不进去（控制台已关闭）时只能丢弃这行输出。
                }
            }
        }

        private void WriteConsoleProgress(ProgressInfo info)
        {
            if (info.Stage != ProgressStage.Compressing
                && info.Stage != ProgressStage.Extracting
                && info.Stage != ProgressStage.CalculatingSHA256)
            {
                return;
            }

            if (_awaitingPassword) return;

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
                if (SetProperty(ref _isBusy, value))
                {
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

        public void RunTaskAndWait(Action<Action<ProgressInfo>> task)
        {
            RunTaskAndWait(task, null);
        }

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
                if (!f.IsDirectory && _sevenZipService.IsArchiveFile(f.Path)) archives.Add(f);
                else others.Add(f);
            }

            if (archives.Count == 0 && others.Count == 0)
            {
                CompleteWithoutWork(report);
                return;
            }

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

        private void ExtractEach(List<PendingFile> files, Action<ProgressInfo> report)
        {
            foreach (var f in files)
            {
                if (CurrentToken.IsCancellationRequested) return;

                StartExtract(report, f.Path);
            }
        }

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
                await System.Threading.Tasks.Task.Run(() => task(report));
            }
            catch (Exception ex)
            {
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

            if (ConsoleProgressEnabled)
            {
                WriteConsoleProgress(info);
                return;
            }

            if (CurrentRunMode == RunMode.ShellMenu)
            {
                return;
            }

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
                // 任务恰好在这一刻结束并释放了取消源，此时已无可取消。
            }
        }

        private CancellationToken CurrentToken => _cancellation?.Token ?? CancellationToken.None;


        private void ReportTaskStart(string templateKey, string fallback, string argument)
        {
            if (CurrentRunMode == RunMode.Console)
            {
                WriteConsoleLine(EnumText.GetFormattedText(templateKey, fallback, argument));
            }
        }

        private void ReportTaskResult(bool success, string detail, bool cancelled = false, bool partial = false)
        {
            if (CurrentRunMode != RunMode.Console) return;

            if (cancelled)
            {
                WriteConsoleLine(EnumText.GetResourceText("Console_TaskCancelled", "已取消"));
                return;
            }

            // 部分文件被跳过时既不是干净的成功，也不是彻底失败，单独给一句结论，
            // 免得用户读到「失败：压缩完成」这种自相矛盾的行。
            if (partial)
            {
                WriteConsoleLine(EnumText.GetFormattedText("Console_TaskPartial", "未完全成功：{0}",
                    detail ?? string.Empty));
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

            if (SevenZipService.CanSplitVolume(Format, Volume))
            {
                batch.EnsureSizes(path => _sevenZipService.GetSizeInBytes(path));
            }

            var runtimeConfig = BuildCompressConfig(batch);
            string archivePath = _sevenZipService.BuildArchiveFilePath(outputDirectory, baseName, runtimeConfig);

            var commands = _sevenZipService.BuildCompressCommands(
                batch.Files.Select(f => f.Path).ToArray(), runtimeConfig, archivePath);
            if (commands.Count == 0)
            {
                FailWithError(new ArgumentException(EnumText.GetErrorText("Err_BuildCompressCommand", "无法构建压缩命令")), report);
                return;
            }

            if (!RunCompressCommands(commands, archivePath, outputDirectory, report)) return;

            if (GenerateSHA256)
            {
                WriteChecksumFiles(archivePath, outputDirectory, report);
            }

            AppState.SevenZUsageCount++;
            report(new ProgressInfo(100, ProgressStage.Finished));
            string doneText = EnumText.GetResourceText("Result_CompressDone", "压缩完成");
            // 有文件被跳过时不能报「完成」：归档虽然产出，但内容缺了一块，
            // 结果行按失败着色、退出码给 1，界面与脚本拿到的是同一个结论。
            bool partial = LastTaskFailed;
            SetResult(doneText, failed: partial);
            ReportTaskResult(!partial, doneText, partial: partial);
        }

        private AppConfig BuildCompressConfig(PendingFileBatch batch)
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
                TotalSize = batch.TotalSizeInBytes
            };
        }


        private bool RunCompressCommands(List<string> commands, string archivePath, string outputDirectory, Action<ProgressInfo> report)
        {
            var produced = new List<string> { archivePath };
            bool hasWarning = false;

            for (int i = 0; i < commands.Count; i++)
            {
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

                if (!result.Succeeded)
                {
                    CleanupProducedFiles(produced);
                    _sevenZipService.CleanupUnfinished(outputDirectory);
                    FailFromCommand(result, report, result.Cancelled);
                    return false;
                }
            }

            if (!_sevenZipService.ArchiveExists(archivePath))
            {
                CleanupProducedFiles(produced);
                _sevenZipService.CleanupUnfinished(outputDirectory);
                FailWithError(new FileNotFoundException(EnumText.GetErrorText("Err_ArchiveNotCreated", "压缩命令已结束，但未生成压缩文件：{0}", archivePath)), report);
                return false;
            }

            if (commands.Count > 1)
            {
                CleanupProducedFiles(produced.Skip(1));
            }

            if (hasWarning)
            {
                // 命令行模式不能弹窗：ShowWarning 会推一个 DispatcherFrame 等用户点确定，
                // 而命令行根本没有界面线程在跑消息循环，弹出来就是永久挂起。
                string warnText = EnumText.GetResourceText("Result_PartialWarning", "压缩已完成，但部分文件被跳过，详情见日志。");
                if (CurrentRunMode == RunMode.Console)
                {
                    WriteConsoleText(warnText, true);
                    _logService.WriteLog(warnText, LogService.ErrorLevel.Warning);
                }
                else
                {
                    _messageService.ShowWarning(warnText, out string warnLog);
                    _logService.WriteLog(warnLog, LogService.ErrorLevel.Warning);
                }

                // 归档已产出但确有文件被跳过：按退出码契约属「任务跑了但未完全成功」，不能报 0。
                // 调用方拿 0 会把一次丢文件的操作当成成功，脚本无从察觉。
                LastTaskFailed = true;
            }

            return true;
        }


        #endregion
        #region 校验文件与命名
        private void WriteChecksumFiles(string archivePath, string outputDirectory, Action<ProgressInfo> report)
        {
            var produced = _sevenZipService.CollectProducedFiles(archivePath);

            try
            {
                for (int partIndex = 0; partIndex < produced.Count; partIndex++)
                {
                    int slice = partIndex;
                    _sevenZipService.GenerateSHA256(produced[partIndex],
                        p => report(new ProgressInfo(
                            MapSHA256Progress(slice, produced.Count, p), ProgressStage.CalculatingSHA256)),
                        CurrentToken);
                }
            }
            catch (OperationCanceledException)
            {
                CleanupProducedFiles(produced);
                _sevenZipService.CleanupUnfinished(outputDirectory);
                FailFromCommand(null, report, cancelled: true);
            }
            catch (Exception ex)
            {
                _logService.WriteLog(
                    EnumText.GetErrorText("Log_SHA256Failed", "生成 SHA256 失败：{0}", ex.Message),
                    LogService.ErrorLevel.Warning);
            }
        }


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
                string pattern = EnumText.GetResourceText("Archive_MergedNamePattern", "{0} 等 {1} 个");
                baseName = pattern
                    .Replace("{0}", baseName)
                    .Replace("{1}", batch.Files.Count.ToString(CultureInfo.InvariantCulture));
            }

            return baseName;
        }


        #endregion
        #region 解压启动
        private void StartExtract(Action<ProgressInfo> report)
        {
            StartExtract(report, null);
        }

        private void StartExtract(Action<ProgressInfo> report, string archiveOverride)
        {
            var batch = _pendingBatch;
            if (batch.IsEmpty && string.IsNullOrWhiteSpace(archiveOverride))
            {
                CompleteWithoutWork(report);
                return;
            }

            if (!EnsureComponents(report)) return;

            string archive = !string.IsNullOrWhiteSpace(archiveOverride) ? archiveOverride : batch.Files[0].Path;

            ReportTaskStart("Console_ExtractStart", "开始解压：{0}", archive);

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
                Format = DetectArchiveFormat(archive),
                Password = UnzipPassword
            };

            string sourceArchive = isSplit ? SevenZipService.ResolveFirstVolumePath(archive) : archive;

            RunExtractWithPasswordRetry(sourceArchive, destinationDirectory, runtimeConfig, report);
        }


        #endregion
        #region 密码重试与解压收尾
        private void RunExtractWithPasswordRetry(string sourceArchive, string destinationDirectory, AppConfig runtimeConfig, Action<ProgressInfo> report)
        {
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

                if (result.RequiresPassword || result.IsWrongPassword)
                {
                    if (!AskPasswordAndRetry(result, attempt, MaxPasswordAttempts, destinationDirectory, runtimeConfig, report))
                    {
                        return;
                    }

                    continue;
                }

                _sevenZipService.CleanupUnfinished(destinationDirectory);
                LogExtractFailure(result, sourceArchive);
                FailFromCommand(result, report, result.Cancelled);
                return;
            }
        }

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

            if (attempt >= maxAttempts)
            {
                FailWithError(new InvalidOperationException(EnumText.GetErrorText("Err_PasswordRetries", "连续多次密码错误，已放弃解压")), report);
                return false;
            }

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
                _sevenZipService.CleanupUnfinished(destinationDirectory);
                FailFromCommand(result, report, cancelled: true);
                return false;
            }

            runtimeConfig.Password = password;
            UnzipPassword = password;
            return true;
        }


        private void FinishExtract(Action<ProgressInfo> report, string destinationDirectory, string sourceArchive)
        {
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
                return RequestPasswordOnConsole(prompt, attempt);
            }

            var app = Application.Current;
            if (app == null) return null;

            PasswordWindow window = null;
            var completed = new ManualResetEventSlim(false);

            try
            {
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
                if (File.Exists(innerTarPath)) File.Delete(innerTarPath);
            }
            catch
            {
                // 中间 tar 删不掉不影响解压结果。
            }

            return true;
        }

        private ArchiveFormat DetectArchiveFormat(string archivePath)
        {
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
                // 读不到文件头就按默认的 7z 处理，格式判定失败不该让任务中止。
            }

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
            report(new ProgressInfo(0, ProgressStage.Finished));
            MainText = GetDefaultModeText();
        }

        private void ReportError(string detail)
        {
            if (CurrentRunMode == RunMode.Console)
            {
                _logService.WriteLog(detail, LogService.ErrorLevel.Error);
                return;
            }

            _messageService.ShowError(detail, out string log);
            _logService.WriteLog(log, LogService.ErrorLevel.Error);
        }


        private void FailWithError(Exception error, Action<ProgressInfo> report)
        {
            LastTaskFailed = true;

            string detail = error?.Message ?? string.Empty;
            ReportError(detail);

            report(new ProgressInfo(0, ProgressStage.Failed));
            MainText = GetDefaultModeText();
            SetResult(EnumText.GetResourceText("Result_Failed", "Failed"), failed: true);
            ReportTaskResult(false, detail);
        }

        private void FailFromCommand(CommandResult result, Action<ProgressInfo> report, bool cancelled)
        {
            LastTaskFailed = true;

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

        public void SetResult(string text, bool failed)
        {
            IsResultFailed = failed;

            if (!SetProperty(ref _resultText, text ?? string.Empty, nameof(ResultText)))
            {
                OnPropertyChanged(nameof(ResultText));
            }
        }


        #endregion
        #region 辅助与收尾
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

        private static int MapSHA256Progress(int index, int count, int percent)
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
                    // 文件被别的程序占用时删不掉，失败清理不该覆盖原本的错误信息。
                }
            }
        }


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

        public void ApplyLanguage()
        {
            if (!LanguageService.IsSupported(CurrentLanguage))
            {
                CurrentLanguage = LanguageService.FallbackLanguage;
            }

            _languageService.SwitchLanguage(CurrentLanguage);

            ShellRegistrationService.SetLanguage(CurrentLanguage);
        }


        #endregion
    }
}
