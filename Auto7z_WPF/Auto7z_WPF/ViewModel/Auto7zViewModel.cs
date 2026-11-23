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

        // 校验文件用的算法。存的是算法目录里的标识（sha256、sha3-256、blake3 …），
        // 而不是枚举：算法目录是数据驱动的，加一个算法不该改配置契约。
        //
        // 初值取 HashAlgorithms.Default.Id，不写死字符串：默认算法只在 HashAlgorithms 定义一次。
        private string _checksumAlgorithmId = HashAlgorithms.Default.Id;

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

        // 批量解压时外层进度条已到达的百分比：只前进不回退，免得每套归档都从 0% 重来。
        private int _batchReportedPercent;

        // 进入「当前这一项」之前，同一批里是否已经有失败项。
        //
        // 为什么不直接用 LastTaskFailed：它是全 ViewModel 共享的标志，压缩部分成功
        // 也会把它置 true。若解压侧直接读它，就会出现
        // 「上一轮压缩有警告 → 本轮干净解压被误判成部分失败」这种跨任务污染。
        // 这个字段由 StartExtractAll 在每项开始前刷新，作用域严格限定在「本次解压批次内」。
        private bool _batchHadFailureBeforeCurrentItem;

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
        private bool _isResultWarning;


        public Auto7zViewModel()
        {
            // 便携与否由 AppPaths 按磁盘事实决定，不读配置里的键：配置可能自相矛盾，
            // 而「数据目录在哪」只有一个答案。旧版本留下的 IsPortable 键既不读也不写，忽略即可。
            AppPaths.IsPortable = AppPaths.DetectPortableFromDisk();

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
            // 手工保存：只有主窗口绑了 Ctrl+S，其余窗口拿到同一个 ViewModel 也能执行，
            // 但它们不会显示这个快捷键 —— 命令本身不做「当前窗口」判断，判断留给 XAML。
            SaveConfigCommand = new RelayCommand<object>(_ => SaveConfigWithFeedback());

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
            ChecksumAlgorithmId = config.ChecksumAlgorithm;
            IsTopMost = config.IsTopMost;
            // 便携状态以目录事实为准（构造函数已按磁盘探测过），不再从配置里读。
            _isPortable = AppPaths.IsPortable;
            RuntimeMode = config.RuntimeMode;
            OutputPath = config.OutputPath;
            IsSolidMode = config.IsSolidMode;
            IsEncFileName = config.IsEncFileName;
            CompressionLevel = config.CompressionLevel;
            UnzipPassword = config.UnzipPassword;
            CurrentLanguage = config.Language;

            ErrorLevel = config.ErrorLevel;

            _mainText = GetDefaultModeText();
        }


        public bool SetPortableMode(bool portable)
        {
            if (portable == _isPortable) return true;

            // **任务运行期间禁止切换模式。**（P1-1，RC 阻断项）
            //
            // 为什么这条必须挡在最前面，而不是「靠闸门兜住」：
            //
            //   1. 任务 active 时产物目录里有 `.auto7z-owner`，job.lock 也在
            //   2. 搬迁先复制数据目录，再删源；复制期间任务可能刚好结束
            //   3. 任务结束后，产物目录会被用户当作「已经生成好的压缩包」
            //   4. 若此时状态与锁被搬成一份「active + 有效锁 + PID 已退出」的组合，
            //      下次启动就会按「上次崩溃」递归删除那个目录 —— 删的是成功产物
            //
            // 第 4 步是本轮报告里指出的数据丢失路径。`MarkTaskFinished` 那边
            // 已经改成「拿不到闸就整体不动」，但两道防线都要有：
            // 光是让收尾可被安全跳过，仍会留下一个「任务在搬家中结束」的状态，
            // 而用户在任务跑着的时候本来也不该能切模式。
            //
            // 返回 false 让调用方把复选框翻回去，界面不会显示一个没生效的开关。
            if (IsBusy)
            {
                _logService.WriteLog(
                    EnumText.GetErrorText("Log_PortableBlockedByTask",
                        "有任务正在执行，不能切换便携模式；请等任务结束后再试。"),
                    LogService.ErrorLevel.Warning);
                return false;
            }

            // 这两个值在闸门内产生、在闸门外使用：
            //   blockedSave        —— 闸门内那次配置保存是否被写入闸挡下（需要在开闸后补写）
            //   portableRootNotEmpty —— 取消便携后 .config 里仍有别人的文件（需要告警）
            // 它们都是「闸门内观察到的事实」，必须先在闸门内取出来，
            // 否则开闸后再去查，看到的是已经被后台线程改动过的状态。
            bool blockedSave = false;
            string portableRootNotEmpty = null;

            // **搬迁与模式切换必须是一个事务，闸门要罩住整段**（BUG-03）。
            //
            // 早先闸门只包在 DataDirectoryMover 内部：文件搬完、源目录删掉，
            // 闸门当场就开了，而「改 _isPortable / AppPaths.IsPortable / 重置日志路径」
            // 还在后面几行。中间这个窗口里，后台线程会按**旧模式**算日志路径：
            //
            //   1. 数据迁移成功，原 .config/Auto7z 被删除
            //   2. 闸门自动打开
            //   3. AppPaths.IsPortable 仍然是 true
            //   4. 后台日志线程写日志 → 按旧模式重新创建 .config/Auto7z/Logs
            //   5. ViewModel 才执行 AppPaths.IsPortable = false
            //
            // 而便携判定看的是 `.config` 目录是否存在（AppPaths.DetectPortableFromDisk）。
            // 第 4 步把旧目录又建出来了，于是**下次启动又被判成便携模式** ——
            // 用户点了「取消便携」，当前运行期间看着成功，重启却回到便携。
            //
            // 现在由调用方在更外层持闸，把「搬文件」与「提交模式」放进同一个隔离区。
            // MigrationGate 是可重入的：搬迁类内部再持一次只是加深度，
            // 真正开闸发生在下面这个 using 结束处 —— 即模式已经提交完之后。
            using (MigrationGate.SuspendWrites())
            {
                // 先搬数据目录，再改状态：搬失败时磁盘与内存都还停在原模式，不会出现
                // 「两边都有配置、程序却认为自己只在其中一边」的中间态。
                bool moved = portable ? DataDirectoryMover.MoveToPortable()
                                          : DataDirectoryMover.MoveToLocal();

                if (!moved)
                {
                    ReportError(
                        EnumText.GetErrorText("Err_PortableMoveFailed", "切换{0}失败：数据目录没能移动", portable ? "便携模式" : "普通模式"));
                    return false;
                }

                // 搬运已经在闸门内完成，下面几步必须**也在闸门内**做完，
                // 否则又会打开上面那个「旧模式写回旧目录」的窗口。
                _isPortable = portable;
                // 先设模式（它会作废缓存的数据目录），再重置日志句柄 ——
                // LogService 的日志路径是从 AppPaths.DataDirectory 现算的，顺序反了就会
                // 把句柄重置到旧目录上。这两个动作以前被塞在 AppPaths.IsPortable 的 setter 里，
                // 造成 AppPaths ↔ LogService 循环依赖，现在由调用方显式串起来。
                AppPaths.IsPortable = portable;
                LogService.ResetLogFiles();

                // **补写搬迁期间被闸门挡下的那一次配置保存**。
                //
                // 闸门关闭期间配置写入一律放弃，否则会有内容落在
                // 「已复制、未删除」的窗口里，搬完后两边都不完整。
                // 这里要把「切换模式」这个状态本身落盘，免得下次启动读到的是搬迁前的旧配置。
                //
                // 注意：本线程正持着搬迁闸，WriteConfigAtomically 里的 TryEnterWrite
                // 会因「本线程正在搬迁」而返回 false。这是**有意的** ——
                // 真正的补写发生在闸门打开之后（见下面 blockedSave 的处理），
                // 这样配置写入仍然走「先取写入闸、再落盘」的统一路径，
                // 不需要给「搬迁线程自己」开一个特权分支。
                blockedSave = !SaveConfig();

                // 取消便携之后要确认 .config 真的没了：便携判定看的就是这个目录，
                // 它还留着的话下次启动会被判回便携、把数据又搬回来，等于取消不掉。
                // 目录里还有别的文件时不能强删（不是本程序的东西），只能如实告警。
                //
                // 这一步放在闸门内是刻意的：它决定「下次启动算不算便携」，
                // 属于模式提交的一部分，不该在闸门外被别人抢着把目录建回来。
                if (!portable && Directory.Exists(AppPaths.GetPortableRootDirectory()))
                {
                    portableRootNotEmpty = AppPaths.GetPortableRootDirectory();
                }
            }

            // 走到这里闸门已经打开：下面的日志与配置写入都是正常写入路径。

            // 把「闸门期间被挡下的那次配置保存」补上。
            // 失败不阻断切换：搬到新位置这件事已经成功了，
            // 配置稍后还会被 AutoSave 或用户手动作再写一次。
            if (blockedSave && !SaveConfig())
            {
                _logService.WriteLog(
                    EnumText.GetErrorText("Err_ConfigSaveFailed", "保存配置失败，详情见日志。"),
                    LogService.ErrorLevel.Warning);
            }

            _logService.WriteLog(
                EnumText.GetErrorText("Log_PortableSwitched", "已切换到{0}，数据目录：{1}",
                    portable ? "便携模式" : "普通模式", AppPaths.DataDirectory),
                LogService.ErrorLevel.Info);

            if (portableRootNotEmpty != null)
            {
                _logService.WriteLog(
                    EnumText.GetErrorText("Log_PortableRootNotEmpty",
                        "已切到普通模式，但 {0} 里还有其它文件，未删除；下次启动仍会被判为便携模式",
                        portableRootNotEmpty),
                    LogService.ErrorLevel.Warning);
            }

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
        // 搬迁的六步实现已提取到 Services.DataDirectoryMover：
        // 那块逻辑全是 static、不碰实例状态，只依赖 AppPaths 与文件系统，
        // 属于「文件系统搬迁」这个独立职责（核心风险是删到哪里，而不是界面怎么显示）。
        // 本类只负责「该不该搬」的判定与副作用（日志、外壳注册）。
        #endregion
        #region 配置保存与关闭收尾
        /// <summary>
        /// 把当前设置写进配置文件。返回是否真的写成功 ——
        /// 手工保存（Ctrl+S）要靠这个结果区分「已保存」与「保存失败」，
        /// 静默失败会让用户以为配置已经落盘。
        /// </summary>
        public bool SaveConfig()
        {
            var config = new AppConfig
            {
                Language = CurrentLanguage,
                Volume = Volume,
                Format = Format,
                Password = Password,
                Zstd = Zstd,
                GenerateSHA256 = GenerateSHA256,
                ChecksumAlgorithm = ChecksumAlgorithmId,
                AutoSave = AutoSave,
                IsTopMost = IsTopMost,
                RuntimeMode = RuntimeMode,
                OutputPath = OutputPath,
                IsSolidMode = IsSolidMode,
                IsEncFileName = IsEncFileName,
                CompressionLevel = CompressionLevel,
                UnzipPassword = UnzipPassword,
                ErrorLevel = ErrorLevel
            };

            return _configService.Save(config);
        }

        public void SaveAutoSaveSetting(bool autoSave)
        {
            // 返回值必须检查：写不进去时用户下次启动看到的是旧的开关，
            // 而界面上显示的是他刚选的那个值 —— 不留日志就完全无从追查。
            if (!_configService.Update("AutoSave", autoSave.ToString()))
            {
                _logService.WriteLog(
                    EnumText.GetErrorText("Err_ConfigSaveFailed", "保存配置失败，详情见日志。"),
                    LogService.ErrorLevel.Warning);
            }
        }

        /// <summary>
        /// 手工保存（Ctrl+S）。与「关闭时自动保存」的区别只在反馈：
        /// 这里无论成功与否都要给用户一个结论，自动保存则只有日志。
        ///
        /// 成功走单按键提示框（标题「提示」、正文「已保存配置」），
        /// 失败走错误提示框 —— 「已保存」这句话一旦说错，用户会以为配置已经落盘，
        /// 下次启动却发现设置回到了旧值。
        /// </summary>
        public void SaveConfigWithFeedback()
        {
            bool saved = SaveConfig();

            if (saved)
            {
                // 正文与日志共用同一句文案：界面说什么，日志里就是什么，事后对照不会对不上。
                string message = EnumText.GetResourceText("ConfigSaved", "配置已保存");
                _messageService.ShowInfo(message, out string log);
                _logService.WriteLog(log, LogService.ErrorLevel.Info);
                return;
            }

            ReportError(EnumText.GetErrorText("Err_ConfigSaveFailed", "保存配置失败，详情见日志。"));
        }

        public void OnClosing()
        {
            CancelCurrentTask();

            _sevenZipService.KillRunningProcesses();

            // 关闭路径上不能弹模态框（窗口正在消失，没有宿主），但也绝不能默默吞掉失败：
            // 用户下次启动看到旧设置、却没有任何痕迹可查，是最难排查的那类问题。
            if (AutoSave)
            {
                if (!SaveConfig())
                {
                    _logService.WriteLog(
                        EnumText.GetErrorText("Err_ConfigSaveFailed", "保存配置失败，详情见日志。"),
                        LogService.ErrorLevel.Error);
                }
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

        /// <summary>
        /// 结果行是否应显示为「成功但有警告」（黄色）。
        /// 与 <see cref="IsResultFailed"/> 互斥：警告不算失败（退出码仍是 1，但界面不该涂成红色）。
        /// </summary>
        public bool IsResultWarning
        {
            get => _isResultWarning;
            set => SetProperty(ref _isResultWarning, value);
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

        /// <summary>
        /// 校验算法标识。界面上是下拉框里的显示名，配置里存这个标识：
        /// 显示名会跟语言走，标识不会，所以配置契约必须落在标识上。
        /// </summary>
        public string ChecksumAlgorithmId
        {
            get => _checksumAlgorithmId;
            set
            {
                // 未知标识一律回落到默认算法：配置被手改成乱码时不该让校验功能失效。
                string normalized = HashAlgorithms.ById(value)?.Id ?? HashAlgorithms.Default.Id;
                if (normalized == _checksumAlgorithmId) return;

                if (SetProperty(ref _checksumAlgorithmId, normalized))
                {
                    OnPropertyChanged(nameof(ChecksumAlgorithm));
                }
            }
        }

        /// <summary>当前校验算法。取不到（标识非法）时退回默认算法，调用方不必判空。</summary>
        public HashAlgorithmInfo ChecksumAlgorithm
        {
            get => HashAlgorithms.ById(_checksumAlgorithmId) ?? HashAlgorithms.Default;
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
        public ICommand SaveConfigCommand { get; }

        public bool LastTaskFailed { get; private set; }

        public bool HasPendingFiles => _pendingBatch != null && !_pendingBatch.IsEmpty;

        /// <summary>
        /// 上一次 <see cref="SetPendingFiles"/> 丢弃了多少个输入路径（不存在、不可访问或按当前模式不接受）。
        ///
        /// 为什么要把它暴露出来：混合批里丢掉一部分输入时，归档照样产出、任务照样「成功」，
        /// 而调用方只能看到退出码 0 —— 脚本与计划任务无从察觉少处理了东西。
        /// 按 README 退出码契约「1 = 任务跑了但失败、被取消，或有文件被跳过」，
        /// 这种情况必须让调用方看得见，而不是只写一条默认级别看不到的 WARNING 日志。
        /// </summary>
        public int LastRejectedInputCount { get; private set; }

        public void SetPendingFiles(IEnumerable<string> files)
        {
            LastRejectedInputCount = 0;

            if (files == null)
            {
                _pendingBatch = new PendingFileBatch(null);
                return;
            }

            // 一次算出「本批要不要归一化续卷」，循环里不再反复问。
            // 判据只看当前操作模式，与路径无关，属于循环不变量。
            bool adjustForExtract = ShouldNormalizeSplitVolume();

            var items = new List<PendingFile>();
            int rejected = 0;
            foreach (var path in files)
            {
                if (string.IsNullOrWhiteSpace(path)) continue;

                PendingFile entry = ClassifyInputPath(path, adjustForExtract);
                if (entry == null)
                {
                    rejected++;
                }
                else
                {
                    items.Add(entry);
                }
            }

            if (rejected > 0)
            {
                _logService.WriteLog(
                    EnumText.GetErrorText("Log_InputRejected", "{0} 个输入路径不存在或不可访问，已跳过", rejected),
                    LogService.ErrorLevel.Warning);
            }

            // 记住丢弃数，供调用方把它反映到控制台提示与退出码上。
            LastRejectedInputCount = rejected;

            _pendingBatch = new PendingFileBatch(items);
        }

        /// <summary>
        /// 把一条输入路径归类成待处理项；无法接受时返回 null（由调用方计为「被丢弃」）。
        ///
        /// 单独提出来（原先是 SetPendingFiles 里 50 行的内联分支）的原因：
        /// 这段是**纯判定** —— 输入是「一个路径 + 是否解压语义」，输出是「待处理项或不接受」，
        /// 不碰任何实例状态、不写盘、不记日志。把它与「遍历 + 计数 + 写日志 + 换批次」
        /// 混在一个方法里，会让那条最容易出错的分卷归一化规则淹没在流程代码中。
        ///
        /// 分卷归一化**只对解压语义成立**：
        ///   - 解压：7-Zip 只认首卷，传 .021 进来必须换回 .001，否则一定失败、
        ///     或同一套被重复解压。
        ///   - 压缩：用户选中 .021 点「压缩」，要压的就是 .021 本身。若在入口改写成 .001，
        ///     压出来的会变成首卷内容 —— 表现是「不管选哪一卷，最后都压了 .001」。
        ///     压缩流程自己会按分卷归类（见 KeepWholeSplitSets），入口不该替它决定。
        /// </summary>
        /// <param name="path">原始输入路径。</param>
        /// <param name="adjustForExtract">是否处于「续卷应归一化回首卷」的语义下。</param>
        /// <returns>待处理项；路径不可接受时返回 null。</returns>
        private static PendingFile ClassifyInputPath(string path, bool adjustForExtract)
        {
            string full = NormalizeInputPath(path);

            try
            {
                if (File.Exists(full))
                {
                    string entry = (adjustForExtract && SevenZipService.IsSplitArchive(full))
                        ? SevenZipService.ResolveFirstVolumePath(full)
                        : full;

                    return PendingFile.CreateLazy(entry, false);
                }

                if (Directory.Exists(full))
                {
                    return PendingFile.CreateLazy(full, true);
                }

                // 逻辑名（x.7z）在磁盘上并不存在，只有 x.7z.001 落地。
                // 这条只在解压语义下才有意义：压缩一个不存在的路径没有意义。
                // 只认 File.Exists 会把「界面上选中逻辑名」的输入整个挡掉，
                // 而 SevenZipService 里明明写了配套的 .001 回退。
                if (SevenZipService.IsSplitArchive(full))
                {
                    return adjustForExtract
                        ? PendingFile.CreateLazy(SevenZipService.ResolveFirstVolumePath(full), false)
                        : null;
                }

                return null;
            }
            catch
            {
                // 非法路径（含通配符、超长、盘符不存在）一律判为不可接受。
                return null;
            }
        }

        /// <summary>
        /// 当前操作是否应当把「续卷」归一化回首卷。
        ///
        /// 只有解压语义成立：7-Zip 解压只认 .001。
        ///   - UnZip：明确解压，必须归一化。
        ///   - Auto ：分卷会被判为归档走解压分支，入口先归一化可以让「多选整套分卷」
        ///            只解一次；压缩分支不依赖这个归一化（它有自己的分卷归类）。
        ///   - Zip  ：**绝不归一化**。用户选 .021 点压缩，压的就该是 .021。
        /// 判断依据是侧栏的单选模式，与任务真正执行时的分派保持一致。
        /// </summary>
        private bool ShouldNormalizeSplitVolume()
        {
            return CurrentOperationMode != OperationMode.Zip;
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

            BeginTask();

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
                // 错误前缀必须走资源键：Console 模式下用户直接看这行，硬编码中文会在英文环境里露馅。
                WriteConsoleLine(EnumText.GetResourceText("ErrorMessage", "Error: ") + ex.Message);
                ReportTaskResult(false, ex.Message);
                SetResult(EnumText.GetResourceText("Result_Failed", "Failed"), ResultSeverity.Failed);
            }
            finally
            {
                _cancellation?.Dispose();
                _cancellation = null;
                IsBusy = false;
                // 任务无论成功、失败还是取消，这里都要清掉进行中状态：
                // 只有进程被强杀才会让 IsRunning=true 残留到下次启动。
                TaskStateService.MarkTaskFinished();
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
            RunTaskAndWait(StartExtractAll, externalReport);
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

            SplitArchiveAndOther(batch.Files, out List<PendingFile> archives, out List<PendingFile> others);

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

            // 压缩这一步会把剩下的重新打包：只剩一卷（缺卷）的分卷套留着也没用，
            // 压进去只是一堆解不开的碎片，先剔掉再交给压缩分支。
            var compressible = KeepWholeSplitSets(others);
            if (compressible.Count == 0)
            {
                CompleteWithoutWork(report);
                return;
            }

            CompressFiles(compressible, report);
        }

        /// <summary>
        /// 把一批待处理项分成「要解压的归档」与「要压缩的其余项」。
        ///
        /// 单独提出来（原先是 StartAutoDispatch 里 30 行的内联循环）的原因：
        /// 这是**纯归类** —— 输入一批项，输出两个列表，不写盘、不记日志、不启动任务。
        /// 它与「按归类结果分派」是两件事，混在一个方法里会让那段最绕的分卷规则
        /// 淹没在流程控制里。
        ///
        /// 三条规则都不直观，各自都来自真实缺陷：
        ///
        /// 1. **目录一律算「其余」**：压缩目录有意义，解压一个目录没有。
        /// 2. **分卷按「套」判断，不能按单卷看**：续卷的魔数是压缩数据流，
        ///    DetectByMagic 认不出 7z 头。只看单卷的话，同一套里的 .002…
        ///    会掉进压缩分支被重新压一遍 —— 既解不开原包，又多出一堆垃圾产物。
        ///    所以先按逻辑名去重，再把整卷补成首卷（7-Zip 只认 .001）。
        /// 3. **一套分卷只在解压分支出现一次**：全选 7 卷时每一卷都满足
        ///    IsSplitArchive，不去重就会把同一套包解 7 遍。键用逻辑名、大小写不敏感。
        /// </summary>
        /// <param name="files">待分类的项。</param>
        /// <param name="archives">要解压的归档（已补成首卷、已按套去重）。</param>
        /// <param name="others">要压缩的其余项。</param>
        private void SplitArchiveAndOther(IReadOnlyList<PendingFile> files,
            out List<PendingFile> archives, out List<PendingFile> others)
        {
            archives = new List<PendingFile>();
            others = new List<PendingFile>();

            var seenSplitSets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var f in files)
            {
                if (f.IsDirectory)
                {
                    others.Add(f);
                    continue;
                }

                if (SevenZipService.IsSplitArchive(f.Path))
                {
                    string logical = SevenZipService.GetLogicalArchiveName(f.Path);
                    if (!seenSplitSets.Add(logical)) continue;

                    // 统一补成首卷：7-Zip 只认 .001，给它任意一卷都会失败。
                    archives.Add(new PendingFile(
                        SevenZipService.ResolveFirstVolumePath(f.Path), 0, false));
                    continue;
                }

                if (_sevenZipService.IsArchiveFile(f.Path)) archives.Add(f);
                else others.Add(f);
            }
        }

        #endregion

        #region 分卷套归并与残缺判定

        /// <summary>
        /// 从「不属于压缩包」那一组里剔掉不完整的分卷套，返回真正可以压缩的项。
        ///
        /// 走到这里的分卷说明首卷不在（否则它已经被归到解压分支了）。
        /// 判据不能再用 IsSplitArchive：它要求首卷存在，残卷根本进不了那个分支。
        /// 所以只看「是不是带卷号的文件」—— 是卷号却没有可用的首卷，就是残缺的一套。
        /// 这种碎片既解不开，压起来也只是垃圾，单独报出来比静默压碎更清楚。
        /// </summary>
        private List<PendingFile> KeepWholeSplitSets(List<PendingFile> files)
        {
            var kept = new List<PendingFile>();
            var warnedSets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var f in files)
            {
                if (f.IsDirectory)
                {
                    kept.Add(f);
                    continue;
                }

                string logical = SevenZipService.GetLogicalArchiveName(f.Path);
                bool looksLikeVolume = !string.Equals(logical, f.Path, StringComparison.OrdinalIgnoreCase);

                if (!looksLikeVolume || SevenZipService.IsSplitArchive(f.Path))
                {
                    kept.Add(f);
                    continue;
                }

                // 是卷号形式，但首卷不在：残缺的一套，整遍只报一次。
                if (warnedSets.Add(logical))
                {
                    _logService.WriteLog(
                        EnumText.GetErrorText("Log_SplitSetIncomplete", "分卷不完整，首卷缺失，已跳过：{0}", logical),
                        LogService.ErrorLevel.Warning);
                    ReportWarningText(EnumText.GetErrorText(
                        "Warn_SplitSetIncomplete", "分卷缺少首卷，已跳过：{0}", logical));
                }
            }

            return kept;
        }

        #endregion

        #region 按项分派与警告输出

        private void ExtractEach(List<PendingFile> files, Action<ProgressInfo> report)
        {
            foreach (var f in files)
            {
                if (CurrentToken.IsCancellationRequested) return;

                // 按项直接解压：这里已经由 StartAutoDispatch 归过类，
                // 不能再走 StartExtract 的「只取第一项」入口。
                ExtractOne(report, f.Path);
            }
        }

        private void ReportWarningText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;

            // 与「按件跳过」的警告共用同一条规则：命令行只能写控制台，
            // ShowWarning 会推 DispatcherFrame 等用户点确定，命令行没有消息循环，弹了就挂死。
            if (CurrentRunMode == RunMode.Console)
            {
                WriteConsoleText(text, true);
                _logService.WriteLog(text, LogService.ErrorLevel.Warning);
                return;
            }

            _messageService.ShowWarning(text, out string log);
            _logService.WriteLog(log, LogService.ErrorLevel.Warning);
        }

        #endregion

        #region 后台调度与排队

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

        /// <summary>
        /// 任务开始前的统一初始化。**两条任务入口都必须先调它。**
        ///
        /// 为什么单独提出来：早先 RunTaskAndWait（命令行）与 RunTaskAsync（GUI）
        /// 各自写了一遍初始化，而两边的「要重置哪些字段」渐渐分叉了 ——
        /// RunTaskAsync 漏了 <c>_batchReportedPercent = 0</c>。
        ///
        /// 后果是真实的：<see cref="OutputProgress"/> 会把进度钳制在
        /// <c>_batchReportedPercent</c> 上且只增不减，于是 GUI 里上一批跑到 100%
        /// 之后，下一批的进度条**一直停在 100% 不动**（命令行路径没这个问题，
        /// 因为那条路径恰好重置了它）。实测确认：GUI 路径起任务后该字段仍是 100。
        ///
        /// 现在两条入口只调这一个方法，将来再加需要重置的状态时
        /// 不可能只改一边。
        /// </summary>
        private void BeginTask()
        {
            IsBusy = true;

            // 新任务必须从「上次没失败」起跑。漏掉会让一次失败/部分成功永久污染后续任务
            // （详见 RunTaskAsync 原有注释里的那个偶发缺陷）。
            LastTaskFailed = false;

            // 批量进度条不能继承上一任的百分比。
            _batchReportedPercent = 0;

            _cancellation = new CancellationTokenSource();
        }

        private async Task RunTaskAsync(Action<Action<ProgressInfo>> task)
        {
            if (IsBusy)
            {
                return;
            }

            BeginTask();

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
                SetResult(EnumText.GetResourceText("Result_Failed", "Failed"), ResultSeverity.Failed);
            }
            finally
            {
                _cancellation?.Dispose();
                _cancellation = null;
                IsBusy = false;
                // 任务无论成功、失败还是取消，这里都要清掉进行中状态：
                // 只有进程被强杀才会让 IsRunning=true 残留到下次启动。
                TaskStateService.MarkTaskFinished();
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

        /// <summary>
        /// 把进度送到外层通道。
        ///
        /// 批量解压时多套归档共用一个通道，而每次解压都是从 0% 重新开始 ——
        /// 界面与右键模式的进度条会一遍遍被打回零。这里只要进度不是往下走就放行，
        /// 让进度条在整个批次里单调向前。界面文字（阶段与百分比）仍由调用方刷新。
        /// </summary>
        private void OutputProgress(Action<ProgressInfo> report, ProgressInfo info)
        {
            if (report == null || info == null) return;

            // 外层通道自己会把进度交给 UpdateProgress 与控制台，
            // 这里只做「不回退」这一件事，避免同一条进度被输出两次。
            if (info.Percent > _batchReportedPercent)
            {
                _batchReportedPercent = info.Percent;
            }

            report(new ProgressInfo(_batchReportedPercent, info.Stage));
        }


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

        /// <summary>
        /// 按分卷配置的解析结果决定「要不要扫体积」，并对每种状态留痕。
        ///
        /// 单独提出来（原先是 StartCompress 里 35 行的 switch）的原因：
        /// 这是**一个完整的判定 + 留痕单元**，三种状态各有各的语义，
        /// 与「拼命令、跑 7z」没有关系。混在压缩主流程里会让这三条语义差别
        /// 被流程代码包住，而它们恰恰是最容易被误解的部分：
        ///
        ///   - Valid          ：要扫体积（分卷判断靠它）。但体积读失败不中断压缩 ——
        ///                      只是分卷判断会偏保守，必须留痕让用户能从日志看出原因。
        ///   - ExplicitlyOff  ：填 0 是**用户的明确选择**，不是错误，记 Info 即可。
        ///                      硬报 Warning 会让用户以为配置写坏了。
        ///   - Invalid        ：值非法。不阻断任务（按不分卷继续），但必须让用户看到配置错了。
        ///
        /// 留痕放在任务入口而不是 CanSplitVolume，是因为后者被界面反复调用，
        /// 每次判定都写日志会把问题日志刷满，用户真正失败的场景反而找不到。
        /// </summary>
        /// <param name="batch">本批待压缩项（Valid 时会就地补齐体积）。</param>
        /// <param name="volumeState">分卷配置的解析结果。</param>
        private void ApplyVolumeStateAndMeasure(PendingFileBatch batch, SevenZipService.VolumeSpecState volumeState)
        {
            switch (volumeState)
            {
                case SevenZipService.VolumeSpecState.Valid:
                    // 体积只用于判断要不要分卷，读失败不该中断压缩 —— 但「没读全」必须留痕，
                    // 否则用户遇到「该分卷却没分」时日志里一个字都没有，只能靠猜。
                    // 两种不完整都计数：整条读不出来，以及读到了但有子树被跳过。
                    batch.EnsureSizes(path =>
                    {
                        long bytes = _sevenZipService.GetSizeInBytesDetailed(path, out int skipped);
                        return new SizeProbeResult { Bytes = bytes, SkippedDirectories = skipped };
                    }, out int sizeMisses);

                    if (sizeMisses > 0)
                    {
                        _logService.WriteLog(
                            EnumText.GetErrorText("Log_SizeEstimateIncomplete",
                                "体积统计未能读全：{0} 个输入路径读取失败，分卷判断可能偏保守（压缩本身不受影响）",
                                sizeMisses),
                            LogService.ErrorLevel.Warning);
                    }
                    break;

                case SevenZipService.VolumeSpecState.ExplicitlyOff:
                    // 填 0 是用户的明确选择，不是错误：记一条 Info，让「这次为什么没分卷」在日志里可查。
                    _logService.WriteLog(
                        EnumText.GetErrorText("Log_VolumeSplitDisabled", "分卷大小设为 0，本次不进行分卷（该值是合法的禁用开关）"),
                        LogService.ErrorLevel.Info);
                    break;

                case SevenZipService.VolumeSpecState.Invalid:
                    // 非法值不阻断任务：按不分卷继续压缩，但必须让用户从日志里看到配置写错了。
                    _logService.WriteLog(
                        EnumText.GetErrorText("Log_VolumeInvalid", "分卷大小无效（{0}），已按不分卷处理", Volume),
                        LogService.ErrorLevel.Warning);
                    break;
            }
        }

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

            if (!_sevenZipService.TryPrepareCompressOutput(validatedOutputPath, batch.Files, out string outputDirectory, out Exception pathError))
            {
                FailWithError(pathError, report);
                return;
            }

            // 产物目录已就位，从这里开始真正写文件：记下目录供崩溃后清理残片。
            TaskStateService.MarkTaskStarted(outputDirectory);

            string baseName = BuildArchiveBaseName(batch);

            // 分卷大小只解析一次：既决定要不要扫体积，也决定要不要留痕。
            // 留痕放在任务入口而不是 CanSplitVolume，是因为后者被界面反复调用，
            // 每次判定都写日志会把问题日志刷满，而用户真正失败的场景反而找不到。
            SevenZipService.VolumeSpecState volumeState =
                SevenZipService.ResolveVolumeSpec(Format, Volume, out long volumeBytes);

            ApplyVolumeStateAndMeasure(batch, volumeState);

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

            report(new ProgressInfo(100, ProgressStage.Finished));
            string doneText = EnumText.GetResourceText("Result_CompressDone", "压缩完成");
            // 有文件被跳过时不能报「完成」：归档虽然产出，但内容缺了一块，
            // 结果行按「成功但有警告」着黄色、退出码给 1，界面与脚本拿到的是同一个结论。
            // 这里不能用 ResultSeverity.Failed —— 那会把一次「归档产出且只是少了几个条目」涂成红色失败。
            bool partial = LastTaskFailed;
            SetResult(doneText, partial ? ResultSeverity.Warning : ResultSeverity.Success);
            ReportTaskResult(!partial, doneText, partial: partial);
        }

        #endregion
        #region 压缩参数与命令执行
        private AppConfig BuildCompressConfig(PendingFileBatch batch)
        {
            return new AppConfig
            {
                Format = Format,
                Volume = Volume,
                Password = Password,
                Zstd = Zstd,
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

                // 读 Outcome 而不是自己去拼 Succeeded + HasWarning：
                // 「部分成功」是一个独立结局，不该由调用方每次记得额外看一眼 HasWarning。
                if (result.Outcome == CommandOutcome.Partial)
                {
                    hasWarning = true;
                    LogSevenZipWarning(result);
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
                    // GUI 模式：**不再弹模态窗**。
                    //
                    // 原先这里是 ShowWarning，会推一个 DispatcherFrame 把任务线程停住等人点。
                    // 后果有两个，都真实发生过：
                    //   ① 自动化里没人点，整个任务永久挂起（本轮审查就踩了一整晚）；
                    //   ② 用户切走窗口去做别的事，压缩早就完成了，程序却还挂着一个等待框，
                    //      看起来像"卡死"。
                    //
                    // 语义上也不该弹：这不是错误，只是"完成但有跳过"。结果行已经会用黄字
                    // 表达它，日志里也记了明细，再拦一个模态框属于重复打扰。
                    // 保留写入结果行 + 日志，与解压侧 FinishExtract 的处理方式对齐。
                    _logService.WriteLog(warnText, LogService.ErrorLevel.Warning);
                    SetResult(warnText, ResultSeverity.Warning);
                }

                // 归档已产出但确有文件被跳过：按退出码契约属「任务跑了但未完全成功」，不能报 0。
                // 调用方拿 0 会把一次丢文件的操作当成成功，脚本无从察觉。
                LastTaskFailed = true;
            }

            return true;
        }

        #endregion

        #region 压缩产物与校验文件

        private void WriteChecksumFiles(string archivePath, string outputDirectory, Action<ProgressInfo> report)
        {
            var produced = _sevenZipService.CollectProducedFiles(archivePath);
            if (produced.Count == 0) return;

            // 校验文件是「一次任务一份」，把这套产物全部列在里面。
            // 分卷尤其明显：7 个卷各写一份只含一行的清单是没法用的，
            // 校验工具需要的是「一份清单覆盖整套包」。清单路径取逻辑名（归档名 + 算法后缀），
            // 而不是挂到某个分卷上，否则用户面对 7 个后缀各异的文件无从下手。
            HashAlgorithmInfo algorithm = ChecksumAlgorithm;
            string checksumPath = GetChecksumFilePath(archivePath, algorithm);

            try
            {
                using (var hashing = new HashingService())
                {
                    HashBatchResult batch = hashing.WriteChecksumManifest(produced, checksumPath, algorithm,
                        p => OutputProgress(report, new ProgressInfo(p, ProgressStage.CalculatingSHA256)),
                        CurrentToken);

                    // 清单可能不完整：某个分卷读不出来时，那份 .sha256 只会列出剩下的几个。
                    // 用户拿它去校验整套包必然对不上，而原因在界面上完全不可见 ——
                    // 所以必须写进日志（Warning 级，默认阈值就能看到）并计入任务失败。
                    if (batch != null && !batch.IsComplete)
                    {
                        ReportIncompleteChecksum(batch, checksumPath);
                    }

                    // 行都算出来了，但其中某些文件在读取期间被别的进程改动过：
                    // 那个摘要对应的是「读取过程中的某一瞬间」，未必等于磁盘现状。
                    // 单独报一条 —— 这与「读不出来」是两回事，混在同一句里会让用户按错误的思路排查。
                    if (batch != null && batch.ChangedDuringReadFiles.Count > 0)
                    {
                        _logService.WriteLog(
                            EnumText.GetErrorText("Log_ChecksumChangedDuringRead",
                                "校验清单中 {0} 个文件在计算期间被修改，对应的摘要可能已与磁盘内容不一致：{1}",
                                batch.ChangedDuringReadFiles.Count,
                                string.Join("; ", batch.ChangedDuringReadFiles)),
                            LogService.ErrorLevel.Warning);

                        // 与清单漏项同性质：清单在，但其中部分行已经不可信。
                        LastTaskFailed = true;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                CleanupProducedFiles(produced);
                DeleteQuietly(checksumPath);
                _sevenZipService.CleanupUnfinished(outputDirectory);
                FailFromCommand(null, report, cancelled: true);
            }
            catch (Exception ex)
            {
                _logService.WriteLog(
                    EnumText.GetErrorText("Log_SHA256Failed", "生成校验文件失败：{0}", ex.Message),
                    LogService.ErrorLevel.Warning);
            }
        }

        /// <summary>
        /// 校验清单不完整时的收尾：写日志、必要时把「旧清单怎么了」也说清楚、并计入任务失败。
        ///
        /// 单独提出来（原先是 WriteChecksumFiles 里 30 行的内联块）的原因：
        /// 它是一段**完整的现场还原逻辑**，包含三层判断（漏了几个 / 旧清单在不在 / 要不要上控制台），
        /// 与「算校验值」本身是两件事。混在主流程里会让这段最难懂的判断被流程代码包住。
        ///
        /// 为什么必须报：用户拿一份「只有 4 行」的清单去校验一整套 5 个分卷，
        /// 必然对不上，而界面上完全看不出少了一项。
        ///
        /// 为什么「旧清单」要单独说：一个文件都没算出来时，新清单根本没写出去。
        /// 此时磁盘上若还留着上一轮的同名文件，它会被当成本次产物的校验文件 ——
        /// 「已清除」与「删不掉、请手动检查」是两种完全不同的现场，合并成一句
        /// 会让用户按错误的思路排查。
        /// </summary>
        /// <param name="batch">本次计算结果（已确认 IsComplete 为 false）。</param>
        /// <param name="checksumPath">校验清单路径。</param>
        private void ReportIncompleteChecksum(HashBatchResult batch, string checksumPath)
        {
            _logService.WriteLog(
                EnumText.GetErrorText("Log_ChecksumIncomplete",
                    "校验清单不完整：{0} 个文件中有 {1} 个无法读取，清单只包含其余的 {2} 项。缺失：{3}",
                    batch.TotalFiles, batch.FailedFiles.Count, batch.Lines.Length,
                    string.Join("; ", batch.FailedFiles)),
                LogService.ErrorLevel.Warning);

            // 一个都没算出来时清单根本没写出去。此时旧清单是否还在，
            // 直接决定用户要不要去磁盘上手动处理 —— 两种现场必须分开报。
            if (batch.Lines.Length == 0 && batch.StaleManifest != StaleManifestOutcome.NotPresent)
            {
                bool removed = batch.StaleManifest == StaleManifestOutcome.Removed;
                string staleText = EnumText.GetErrorText(
                    removed ? "Log_ChecksumManifestCleared" : "Log_ChecksumManifestStaleKept",
                    removed
                        ? "本次没有任何文件能算出校验值，未生成新的校验清单；上一轮遗留的同名校验文件已清除：{0}"
                        : "本次没有任何文件能算出校验值，且上一轮的校验文件未能确认已清除（可能仍存在），它可能与本次产物无关，请手动检查：{0}",
                    checksumPath);

                _logService.WriteLog(staleText, LogService.ErrorLevel.Warning);

                if (CurrentRunMode == RunMode.Console)
                {
                    WriteConsoleText(staleText, true);
                }
            }

            // 清单漏项与压缩时「有文件被跳过」同一性质：产物在，但内容缺了一块。
            LastTaskFailed = true;
        }

        /// <summary>
        /// 校验文件落在归档旁边，文件名取逻辑名 + 算法首选后缀：分卷 x.7z.001… 对应 x.7z.sha256。
        ///
        /// 不分卷时逻辑名就是归档本身，得到 x.7z.sha256 —— 与旧行为一致，
        /// 已有脚本按这个路径取文件不会受影响。换了算法时后缀跟着换（x.7z.sha3-256），
        /// 这正是校验工具认算法的唯一线索，写错后缀会被按另一个算法读回去。
        /// </summary>
        private static string GetChecksumFilePath(string archivePath, HashAlgorithmInfo algorithm)
        {
            if (string.IsNullOrWhiteSpace(archivePath)) return archivePath;

            string logical = SevenZipService.GetLogicalArchiveName(archivePath);
            string extension = algorithm != null ? algorithm.Extension : HashAlgorithms.Default.Extension;
            return logical + extension;
        }

        private static void DeleteQuietly(string path)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) File.Delete(path);
            }
            catch
            {
                // 删不掉不影响原本要报的错误。
            }
        }

        #endregion

        #region 归档命名辅助

        private static string BuildArchiveBaseName(PendingFileBatch batch)
        {
            if (batch == null || batch.IsEmpty) return "Archive";

            var first = batch.Files[0];
            string baseName = first.IsDirectory
                ? first.Name
                : GetCompressSourceBaseName(first.Path);

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

        /// <summary>
        /// 压缩单个文件时的产物基名。
        ///
        /// 普通文件沿用 `xx.txt` → `xx.7z` 的老规矩（剥掉扩展名）。
        /// 但分卷例外：`ASCIIPlayer.7z.021` 若按 GetFileNameWithoutExtension 剥一层，
        /// 得到 `ASCIIPlayer.7z`，再接上 `.7z` 就成了 `ASCIIPlayer.7z.7z` ——
        /// 这个名字看起来像「压缩了 .7z 分卷」，用户根本分不清自己压的是哪一卷，
        /// 而且和真实的分卷产物 `.7z.001` 形式撞在一起，极易被误读成压缩了首卷。
        ///
        /// 所以分卷保留完整的卷名：`ASCIIPlayer.7z.021` → `ASCIIPlayer.7z.021.7z`，
        /// 一眼就能看出压的是第 021 卷。
        /// </summary>
        private static string GetCompressSourceBaseName(string path)
        {
            string fileName = Path.GetFileName(path);
            if (string.IsNullOrWhiteSpace(fileName)) return fileName;

            // 只对「卷名」形态（点 + 至少三位纯数字）保留全名。
            string logical = SevenZipService.GetLogicalArchiveName(path);
            if (!string.Equals(logical, path, StringComparison.OrdinalIgnoreCase))
            {
                return fileName;
            }

            return Path.GetFileNameWithoutExtension(path);
        }

        #endregion

        #region 解压启动

        /// <summary>
        /// 解压「解压模式」这一批输入：分卷整套归并成首卷，逐个归档各解各的。
        /// </summary>
        private void StartExtractAll(Action<ProgressInfo> report)
        {
            var batch = _pendingBatch;
            if (batch.IsEmpty)
            {
                CompleteWithoutWork(report);
                return;
            }

            // 原来的实现只取 batch.Files[0]：多选时第 2 个及以后的归档全被静默丢弃，
            // 而多选整套分卷时「哪个排在第一个」完全取决于资源管理器的枚举顺序 ——
            // 首卷不在第一位就一定失败。这里按项逐个解，并先把分卷归并成一套。
            var distinct = SevenZipService.CollapseSplitVolumes(batch.Files.Select(f => f.Path));

            var targets = new List<string>();
            var missing = new List<string>();
            foreach (string path in distinct)
            {
                bool isSplit = SevenZipService.IsSplitArchive(path);
                if (File.Exists(path) || isSplit) targets.Add(path);
                else missing.Add(path);
            }

            // 缺失的归档要**先全部收集、再一次性报告**，不能在循环里逐个 FailWithError：
            //
            // FailWithError 会 SetResult(..., ResultSeverity.Failed) 并报告一次失败，但它并不中断循环。
            // 早先这里在枚举过程中直接调用它，于是排在缺失项之后的每一个正常归档解压完成时
            // 都会走 FinishExtract → SetResult(doneText, ResultSeverity.Success)，把红色失败结论覆盖成
            // 「解压完成」，退出码却仍然是 1（LastTaskFailed 没被重置）——
            // 界面说成功、脚本收到失败，两边对不上。
            //
            // 收成一次报告还顺带解决了「一次多选里丢了 5 个归档就弹 5 次同样错误」的问题。
            if (missing.Count > 0)
            {
                LastTaskFailed = true;
                string detail = EnumText.GetErrorText("Err_ArchiveNotFound", "压缩文件不存在") + "：" +
                    string.Join("; ", missing);
                _logService.WriteLog(EnumText.GetErrorText("Log_ArchivesMissing",
                    "{0} 个归档不存在，已跳过：{1}", missing.Count, detail), LogService.ErrorLevel.Warning);
                ReportWarningText(detail);
            }

            if (targets.Count == 0)
            {
                // 一个可解压的目标都没有：此时必须给出明确的失败结论，
                // 而不是让流程落到「无工作可做」的正常收尾上。
                if (missing.Count > 0)
                {
                    FailWithError(new FileNotFoundException(
                        EnumText.GetErrorText("Err_ArchiveNotFound", "压缩文件不存在"),
                        string.Join("; ", missing)), report);
                }
                else
                {
                    CompleteWithoutWork(report);
                }
                return;
            }

            foreach (string archive in targets)
            {
                if (CurrentToken.IsCancellationRequested) return;

                // 记录「进入本项之前，这一批里是否已经有过失败」。
                // FinishExtract 用它判断要不要把当前项收尾成「未完全成功」，
                // 而**不再**读全局的 LastTaskFailed —— 后者会被压缩等其它任务置位，
                // 造成「上一轮压缩有警告 → 本轮干净解压被误判成部分失败」。
                _batchHadFailureBeforeCurrentItem = LastTaskFailed;
                ExtractOne(report, archive);
            }
        }

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

            string archive = !string.IsNullOrWhiteSpace(archiveOverride)
                ? archiveOverride
                : batch.Files[0].Path;

            ExtractOne(report, archive);
        }

        /// <summary>
        /// 解压单个归档。批量解压与单发解压共用这一段，避免两条路径悄悄分叉。
        /// </summary>
        private void ExtractOne(Action<ProgressInfo> report, string archive)
        {
            if (!EnsureComponents(report)) return;

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

            // 目标目录已就位，从这里开始真正写文件：记下目录供崩溃后清理残片。
            TaskStateService.MarkTaskStarted(destinationDirectory);

            var runtimeConfig = new AppConfig
            {
                Format = DetectArchiveFormat(archive),
                Password = UnzipPassword
            };

            // 分卷统一指回首卷：用户送进来的可能是 .002，7-Zip 只认 .001。
            string sourceArchive = isSplit ? SevenZipService.ResolveFirstVolumePath(archive) : archive;

            RunExtractWithPasswordRetry(sourceArchive, destinationDirectory, runtimeConfig, report);
        }


        #endregion
        #region 密码重试
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

                // 读 Outcome 而不是自己去拼 Succeeded + HasWarning（见压缩侧同样的说明）。
                bool partialExtract = result.Outcome == CommandOutcome.Partial;

                if (partialExtract)
                {
                    LogSevenZipWarning(result);
                }

                if (result.Succeeded)
                {
                    // 解压真的成功了 ⇒ 手里的这个密码已被证明是对的，立刻写回磁盘。
                    //
                    // 为什么必须在这里、而不是等 OnClosing 的自动保存：
                    //   - 右键菜单模式没有主窗口，压根不会走 OnClosing；
                    //   - 命令行模式跑完就退出，也不会走那条路径；
                    //   - 即使用户关掉了自动保存（AutoSave=false），一个已被验证通过的
                    //     密码也没有理由不记住 —— 它下一次还能用。
                    AdoptVerifiedPassword(runtimeConfig.Password);

                    // 退出码 1 = 归档解开了但有条目被跳过，不能报成完全成功。
                    FinishExtract(report, destinationDirectory, sourceArchive, partialExtract);
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

            // 循环跑满仍未收尾：当前不可达（每轮要么 return、要么 continue 到下一轮，
            // 而最后一轮会在 AskPasswordAndRetry 里因 attempt >= maxAttempts 报错并返回 false），
            // **但那是隐式契约** —— 它依赖 AskPasswordAndRetry 内部的那个判断。
            // 将来若把「密码重试次数」改成可配置、或让 AskPasswordAndRetry 在最后一轮
            // 返回 true（比如改成「再问一次」语义），这里就会静默走完循环：
            // 既不报失败、也不写结果，界面停在上一轮的状态 —— 那种「什么都没发生」最难查。
            // 显式兜底的成本是一行日志与一次报错，值得。
            _sevenZipService.CleanupUnfinished(destinationDirectory);
            FailWithError(new InvalidOperationException(
                EnumText.GetErrorText("Err_PasswordRetries", "连续多次密码错误，已放弃解压")), report);
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
            // 只把密码交给本次 7-Zip 命令用，**不要**在这里改 UnzipPassword。
            //
            // 早先这里还有一行 UnzipPassword = password; 造成真实缺陷：
            // UnzipPassword 是纯内存属性（setter 只发通知、不写盘），提前赋值会让
            // AdoptVerifiedPassword 里「值已一致就跳过写盘」的优化误判为已保存，
            // 于是配置文件永远不会被更新 —— 内存是新密码、磁盘是旧密码，
            // 下次启动又拿旧密码去试。实测复现：解压成功 rc=0，
            // 但配置里仍是 STALE_WRONG_PW。
            //
            // 正确顺序：解压成功 → AdoptVerifiedPassword 发现内存与磁盘都还是旧值
            // → 真正写盘。密码的「记住」只在被证明正确之后发生一次。
            return true;
        }

        /// <summary>
        /// 把一个**已被验证通过**的解压密码写回配置。
        ///
        /// 这是三种入口（GUI 主窗口 / 右键菜单 / 命令行）唯一的密码回写点：
        /// 三者只在「怎么拿到密码字符串」上有差别，拿到之后全部汇合到
        /// <see cref="AskPasswordAndRetry"/>，再经这里落盘。
        ///
        /// 调用时机必须严格是「解压成功之后」。早期实现是「用户按下回车就写」，
        /// 结果连续输错几次时反而把**错的**密码存了下来，下次启动还会拿它去试 ——
        /// 那比不存更糟。现在只放在 <c>result.Succeeded</c> 分支里。
        ///
        /// 只写 <c>UnzipPassword</c>，不动压缩侧的 <c>Password</c>：用户证明的是
        /// 「这个密码能解开这个包」，把它抄到压缩侧等于悄悄换掉上一个包用的密码，
        /// 那是另一份语义完全不同的状态。
        /// </summary>
        /// <param name="password">已验证成功的密码；空值直接忽略。</param>
        private void AdoptVerifiedPassword(string password)
        {
            if (string.IsNullOrEmpty(password)) return;

            // 已经一致就不必再写盘：每次解压都刷一遍配置文件既慢又会推高磁盘写。
            if (string.Equals(UnzipPassword, password, StringComparison.Ordinal)) return;

            RememberUnzipPassword(password);
        }

        /// <summary>
        /// 真正执行写盘的单元（可被测试单独驱动）。
        /// 写失败只记日志：密码没记住是个可恢复的小问题，不该让本次解压显示为失败。
        /// </summary>
        /// <param name="password">已验证成功的密码。</param>
        private void RememberUnzipPassword(string password)
        {
            if (string.IsNullOrEmpty(password)) return;

            UnzipPassword = password;

            if (!_configService.Update("UnzipPassword", password))
            {
                _logService.WriteLog(
                    EnumText.GetErrorText("Err_ConfigSaveFailed", "保存配置失败，详情见日志。"),
                    LogService.ErrorLevel.Warning);
            }
        }

        #endregion
        #region 解压收尾

        /// <summary>
        /// 解压收尾。
        ///
        /// <paramref name="partial"/> 为 true 表示 7-Zip 退出码是 1（有警告）：
        /// 归档确实解开了，但其中有些条目没能处理。这种情况**不能报成完全成功** ——
        /// 界面必须显示为未完全成功、退出码给 1，与压缩侧对 exit=1 的处理保持一致。
        /// </summary>
        private void FinishExtract(Action<ProgressInfo> report, string destinationDirectory,
            string sourceArchive, bool partial)
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

            report(new ProgressInfo(100, ProgressStage.Finished));

            // 本次解压自身有警告（7z exit=1）**或者**同一批（batch）里更早的项已经失败。
            //
            // 后一半的初衷是：批量解压里前面某个归档失败后，后面成功的那一个若按「干净成功」
            // 收尾，会把这个红色结论覆盖成「解压完成」，而退出码仍是 1 —— 界面与脚本对不上。
            //
            // **但早期实现写成了 `partial || LastTaskFailed`，语义过宽**：
            // LastTaskFailed 是全 ViewModel 共享的一个标志，压缩部分成功也会把它置 true。
            // 于是「先压缩一个被占用的文件（exit=1）→ 再解压一个完全干净的包」时，
            // 第二次解压会读到上一轮压缩留下的 true，被误判成
            // 「解压已完成，但部分条目被跳过」—— 明明什么都没跳过，日志里也查不到原因
            // （这条路径落在成功分支，按设计不写错误日志）。
            //
            // 现在改用**批次内**的失败计数：只有「本次任务这一批里，排在我前面的项失败过」
            // 才继承警告结论。跨任务、跨类型的残留不再影响解压结果。
            bool partialResult = partial || _batchHadFailureBeforeCurrentItem;

            if (partialResult)
            {
                // 归档解开了但有条目被跳过：结果行按「成功但有警告」着黄色，退出码给 1。
                //
                // 文案必须是解压侧的。这里原先复用压缩侧的 Result_PartialWarning，
                // 于是解压完成的提示会写「**压缩**已完成，但部分文件被跳过」——
                // 用户看到的是解压却被告知压缩完成，等于自相矛盾的结论。
                string partialText = EnumText.GetResourceText("Result_ExtractPartialWarning",
                    "解压已完成，但部分条目被跳过，详情见日志。");

                // 关键：把「为什么判成 partial」写进日志 —— 用户抱怨"原因不明、log 没记录"
                // 正是因为这里原先什么都不写。
                _logService.WriteLog(EnumText.GetErrorText("Log_ExtractPartialReason",
                    "解压判为未完全成功：本次 7z 有警告={0}，同批前序失败={1}",
                    partial, _batchHadFailureBeforeCurrentItem), LogService.ErrorLevel.Warning);

                SetResult(partialText, ResultSeverity.Warning);
                ReportTaskResult(true, partialText, partial: true);
                return;
            }

            string doneText = EnumText.GetResourceText("Result_ExtractDone", "解压完成");
            SetResult(doneText, ResultSeverity.Success);
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

#if DEBUG
            // 自动化：密码框也是模态阻塞，没人点就会一直等下去。
            // 交给共用驱动器填值并点击 —— 与用户"输入密码然后点确定"走同一条路。
            // 注意这里**不直接返回密码**：那样会跳过窗口创建与点击，
            // 就测不到「密码框能不能正确把值交回来」这条真实链路。
#endif

            PasswordWindow window = null;
            var completed = new ManualResetEventSlim(false);

            // 弹窗动作在界面线程上执行，而本方法跑在任务线程上并阻塞等待它关闭。
            // 这里**必须**保证无论弹窗成功与否，completed 最终一定会被置位 ——
            // 否则任务线程会永久停在下面的 Wait() 上。
            //
            // 早先只在外层包了 try/catch，而 BeginInvoke 的委托是在界面线程上执行的：
            // 委托内抛出的异常不会回到这里的 catch，而是交给 Dispatcher 的未处理异常路径
            // （表现为整个进程崩溃），同时 completed.Set() 永远不执行。结果是
            // 「用户看到一个报错，程序随后卡死」或者「进程直接没了，任务线程无从收尾」。
            // 现在把 try/catch 放进委托内部：失败时置位事件，由下面的返回 null 走取消路径。
            Action showDialog = () =>
            {
                try
                {
                    window = CreatePasswordWindow(completed, prompt);
                }
                catch (Exception ex)
                {
                    _logService.WriteLog(
                        EnumText.GetErrorText("Log_PasswordDialogFailed", "弹出密码输入框失败：{0}", ex.Message),
                        LogService.ErrorLevel.Error);
                }
                finally
                {
                    // 窗口没建起来 = 用户不可能给出密码。窗口不会关闭，所以建失败时由这里补一次置位。
                    if (window == null) completed.Set();
                }
            };

            try
            {
                if (app.Dispatcher.CheckAccess())
                {
                    showDialog();
                }
                else
                {
                    app.Dispatcher.BeginInvoke(showDialog);
                }
            }
            catch (Exception ex)
            {
                _logService.WriteLog(EnumText.GetErrorText("Log_PasswordDialogFailed", "弹出密码输入框失败：{0}", ex.Message), LogService.ErrorLevel.Error);
                completed.Dispose();
                return null;
            }

            try
            {
                completed.Wait();
            }
            catch (ObjectDisposedException)
            {
                // 事件已被释放（极少见：与上面的失败路径竞争），按「没拿到密码」处理。
                return null;
            }

            string password = window != null && window.Confirmed ? window.Password : null;
            completed.Dispose();

            _logService.WriteLog(
                password == null
                    ? EnumText.GetErrorText("Log_PasswordCancelled", "用户取消了密码输入")
                    : EnumText.GetErrorText("Log_PasswordGot", "已获取密码"),
                LogService.ErrorLevel.Info);
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
#if DEBUG
            // 与 MessageService 走同一个驱动器：填值 + 点确认，路径与真人一致。
            DebugAutoDriver.Attach(window);
#endif
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
                _logService.WriteLog(
                    EnumText.GetErrorText("Log_InnerTarFailed",
                        "解开中间 tar 失败：{0}", innerResult.GetFailureDetail()),
                    LogService.ErrorLevel.Error);
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


        /// <summary>
        /// 记录 7-Zip 的「有警告但已完成」提示。
        ///
        /// 压缩与解压两条路径都会遇到这种情况，文案与级别必须一致 ——
        /// 早先两处各自写了一遍完整的 WriteLog 调用（而且都是 170 字符的超长行），
        /// 将来改文案时漏改一处，用户会在压缩里看到一套说法、解压里看到另一套。
        /// 提取之后只有一个出处。
        /// </summary>
        /// <param name="result">7-Zip 的执行结果。</param>
        private void LogSevenZipWarning(CommandResult result)
        {
            _logService.WriteLog(
                EnumText.GetErrorText("Log_SevenZipWarning",
                    "7-Zip 提示（有警告但已完成）：{0}", result.GetFailureDetail()),
                LogService.ErrorLevel.Warning);
        }

        private void FailWithError(Exception error, Action<ProgressInfo> report)
        {
            LastTaskFailed = true;

            string detail = error?.Message ?? string.Empty;
            ReportError(detail);

            report(new ProgressInfo(0, ProgressStage.Failed));
            MainText = GetDefaultModeText();
            SetResult(EnumText.GetResourceText("Result_Failed", "Failed"), ResultSeverity.Failed);
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
                SetResult(EnumText.GetResourceText("Result_Cancelled", EnumText.GetErrorText("Log_TaskCancelled", "任务已取消")), ResultSeverity.Failed);
                ReportTaskResult(false, null, cancelled: true);
                return;
            }

            string detail = result?.GetFailureDetail();
            if (string.IsNullOrWhiteSpace(detail))
            {
                detail = EnumText.GetErrorText("Err_UnexpectedExitCode", "7-Zip 返回非 0 退出码：{0}",
                    result?.ExitCode.ToString(CultureInfo.InvariantCulture) ?? "未知");
            }

            // 输出排空超时时，上面的 detail 是从**可能被截断**的输出里解析出来的 ——
            // 真正的错误行可能就在丢掉的那部分里。必须让用户知道这一点，
            // 否则他会拿着一个不知从哪来的原因去排查。
            if (result != null && result.OutputTruncated)
            {
                _logService.WriteLog(
                    EnumText.GetErrorText("Log_OutputTruncated",
                        "7-Zip 输出未能完整读取（排空超时），下面的失败原因可能不完整：{0}", detail),
                    LogService.ErrorLevel.Warning);

                detail = EnumText.GetErrorText("Err_OutputTruncated",
                    "（输出未读全，原因可能不准确）{0}", detail);
            }

            ReportError(detail);

            report(new ProgressInfo(0, ProgressStage.Failed));
            MainText = GetDefaultModeText();
            SetResult(EnumText.GetResourceText("Result_Failed", "Failed"), ResultSeverity.Failed);
            ReportTaskResult(false, detail);
        }

        public void SetResult(string text, ResultSeverity severity)
        {
            // 三态：成功绿、失败红、成功但有警告黄。
            // IsResultFailed 保留为「是否算失败」（Warning 不算失败），
            // 颜色由 IsResultWarning 单独承载 —— 只留一个 bool 时黄色永远没机会出现。
            IsResultWarning = severity == ResultSeverity.Warning;
            IsResultFailed = severity == ResultSeverity.Failed;

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
            // 归一化到白名单里的规范写法，再往下传。
            //
            // 不能只判 IsSupported 就原样使用：IsSupported 内部会 Trim + 忽略大小写地比较，
            // 所以 " ZH-cn " 是「支持」的，但它的字面量会被原样写进注册表 ——
            // ShellRegistrationService.SetLanguage 只挡空串，之后直接把值交给外壳扩展。
            // 结果是 dll 收到 " ZH-cn "，按语言名找资源失败、菜单退回默认语言，
            // 而程序这边认为已经设成中文了。
            //
            // SwitchLanguage 内部自己会归一（见 LanguageService.Normalize），
            // 但 SetLanguage 走的是另一条路、拿的是这里的字面量，所以必须在本方法归一。
            string language = LanguageService.IsSupported(CurrentLanguage)
                ? CurrentLanguage.Trim()
                : LanguageService.FallbackLanguage;

            // 大小写也对齐到白名单里的那份：LocalApplicationData 之外，
            // 资源文件名 zh-CN.xaml 在部分宿主里区分大小写。
            foreach (string supported in LanguageService.GetSupportedLanguages())
            {
                if (string.Equals(supported, language, StringComparison.OrdinalIgnoreCase))
                {
                    language = supported;
                    break;
                }
            }

            // 属性本身也要回到规范值：它是公开状态、界面与后续调用都读它。
            if (!string.Equals(CurrentLanguage, language, StringComparison.Ordinal))
            {
                CurrentLanguage = language;
            }

            _languageService.SwitchLanguage(language);

            ShellRegistrationService.SetLanguage(language);
        }


        #endregion
    }
}
