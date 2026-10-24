# Auto7z

Windows 图形化压缩工具，基于 7-Zip ZS 内核（7-Zip 22.01 ZS v1.5.5 R3，x64）。

- 图形界面：拖文件进来就能压缩或解压，支持密码、分卷、MD5、固实压缩、文件名加密
- 右键菜单：资源管理器里右键文件或文件夹，直接「用 Auto7z 压缩 / 用 Auto7z 解压」
- 命令行：可以脚本化调用，支持自动 / 压缩 / 解压三种模式
- 便携模式：数据全放在程序目录，拔了就能带走
- 多语言：简体中文、繁体中文、English

## 快速开始

### 图形界面

双击 `Auto7zGUI.exe`（图形入口），把文件或文件夹拖进窗口。

- 选中压缩包 → 自动解压
- 选中普通文件 → 自动压缩
- 混合选中 → 逐个判断

「是不是压缩包」是看**文件头魔数**判断的，认得 7z、zip、tar、rar、gz、bz2、xz、zst、cab、wim 这几种，扩展名对不上也没关系。

多个文件会问要不要合并成一个压缩包。

### 右键菜单

在资源管理器里右键文件，右键菜单里会有「用 Auto7z 压缩」和「用 Auto7z 解压」。文字跟着界面语言走。

菜单项要**多选时只开一个进程**，所有选中的文件一次传过去，不会一个文件开一个进程。

### 命令行

```
Auto7z.exe [选项] [文件或目录...]

  -A            自动模式, 压缩包解压、其余文件压缩 (默认)
  -Z            压缩模式
  -U            解压模式
  -r            注册右键菜单 (需要管理员权限)
  -ur           注销右键菜单 (需要管理员权限)
  -h            显示本说明
```

不带选项直接给路径时按自动模式处理。命令行模式**不弹界面**，进度输出到控制台，任务做完进程自动退出。

多文件时「是否合并成一个压缩包」改成在控制台里问。读不到输入（管道、计划任务等场景）就按合并处理。

**不带任何参数**直接跑 `Auto7z.exe` 时只打印用法并以退出码 2 结束 —— 它是命令行入口，
不会再拉起主窗口；图形入口是 `Auto7zGUI.exe`。

把文件拖到 `Auto7zGUI.exe` 图标上，走的也是这条路。

另外还有一个内部用的开关 `--set-main-exe-path`：提权后的进程用它只把主程序路径写进注册表，写完就退出。正常使用碰不到。

## 构建

需要 Visual Studio 2022 或更新版本，带 **.NET Framework 4.7.2** 桌面开发工作负载。

```powershell
& "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe" `
    Auto7z_WPF.sln /t:Rebuild /p:Configuration=Release /p:Platform="Any CPU"
```

`msbuild` 通常不在 `PATH` 上 —— 用完整路径，或从 Visual Studio 开发者命令行里跑。

右键菜单组件是独立的 C++ 项目，见 [`Auto7zShell`](../Auto7zShell/README.md)。要出右键菜单功能，两个都要构建：

```bat
..\Auto7zShell\buildtool\buildtool.bat
..\Auto7zShell\buildtool\buildtool.exe --vcvars "C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat"
```

第一条把 `buildtool.exe` 编译出来，第二条用它把组件编到 `..\Auto7zShell\build\Auto7zShell.dll`（`buildtool.bat` 默认用 VS 2022 Community 的 `vcvars64.bat`，装在别处就用 `--vcvars` 传真实路径）。把那个 dll 拷到主程序的输出目录（`bin\Release\` 或 `bin\Debug\`）里。

构建完的目录里要有：

```
Auto7z.exe
Auto7z.exe.config
Auto7zGUI.exe         ← 图形入口（GUI 子系统）
Auto7zGUI.exe.config
Auto7zShell.dll      ← 右键菜单组件
```

组件跟主程序放同一个目录，它按同目录找主程序。

## 部署

### 安装版（InnoSetup）

```ini
[Files]
Source: "..\Auto7z_WPF\Auto7z_WPF\bin\Release\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Run]
Filename: "{app}\Auto7z.exe"; Parameters: "-r"; Flags: runhidden; Tasks: desktopicon

[UninstallDelete]
Type: filesandordirs; Name: "{app}"

[Code]
// Auto7zShell.dll 被资源管理器加载在自己的进程里；`-ur` 只删注册表，跨进程
// 卸载做不到。不把它释放出来，卸载程序随后删 {app} 时 dll 仍被占用，删不掉、
// 要等重启。

function FindWindowW(ClassName, WindowName: String): Longint;
  external 'FindWindowW@user32.dll stdcall';

// 任务栏或桌面窗口在，就说明资源管理器回来了。
function ExplorerRunning: Boolean;
begin
  Result := (FindWindowW('Shell_TrayWnd', '') <> 0) or
            (FindWindowW('Progman', '') <> 0);
end;

procedure KillExplorerToReleaseShellDll;
var
  ErrorCode: Integer;
begin
  if Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM explorer.exe', '',
          SW_HIDE, ewWaitUntilTerminated, ErrorCode) then
  begin
    Log('taskkill /F /IM explorer.exe returned ' + IntToStr(ErrorCode));
    Sleep(1000);   // 等进程退出、dll 句柄真正释放
  end
  else
    Log('Could not start taskkill, error ' + IntToStr(ErrorCode) + ': ' + SysErrorMessage(ErrorCode));
end;

procedure EnsureExplorerRunning;
var
  ErrorCode: Integer;
  Waited: Integer;
begin
  Waited := 0;
  while (Waited < 10000) and not ExplorerRunning do
  begin
    Sleep(500);
    Waited := Waited + 500;
  end;

  if ExplorerRunning then
    Exit;

  // AutoRestartShell 被关掉的机器上没人拉它，补一次。
  ShellExec('', ExpandConstant('{win}\explorer.exe'), '', '',
            SW_SHOWNORMAL, ewNoWait, ErrorCode);
end;

procedure RunUnregisterShellMenu;
var
  ErrorCode: Integer;
  AppExe: String;
begin
  AppExe := ExpandConstant('{app}\Auto7z.exe');
  if not FileExists(AppExe) then
  begin
    Log('Auto7z.exe not found, skipping context menu cleanup');
    Exit;
  end;

  if ShellExec('runas', AppExe, '-ur', ExpandConstant('{app}'),
               SW_HIDE, ewWaitUntilTerminated, ErrorCode) then
  begin
    Log('Ran Auto7z.exe -ur to unregister the context menu');
  end
  else
  begin
    Log('Elevated cleanup failed, error ' + IntToStr(ErrorCode) + ': ' + SysErrorMessage(ErrorCode));
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ShellDll: String;
begin
  if CurUninstallStep = usUninstall then
  begin
    // 先跑 -ur 清注册表，再杀 explorer：资源管理器重新起来时才不会又把
    // dll 加载回去。这一步在 [UninstallDelete] 之前，dll 才能被真正删掉。
    RunUnregisterShellMenu;

    ShellDll := ExpandConstant('{app}\Auto7zShell.dll');
    if FileExists(ShellDll) then
      KillExplorerToReleaseShellDll;
  end
  else if CurUninstallStep = usPostUninstall then
    EnsureExplorerRunning;
end;
```

`-r` / `-ur` 就是给安装程序用的。注册要管理员权限，`PrivilegesRequired=admin` 时安装程序本身就是管理员，不会多弹 UAC。

卸载走 `[Code]` 而不是 `[UninstallRun]`，是因为第三方卸载工具跑卸载时不一定给管理员权限 —— 那样注册表写不进去，右键菜单会残留，指向一个已经不存在的 dll。

**为什么要杀 explorer**：`Auto7zShell.dll` 是进程内的 COM 组件，资源管理器右键用过一次就把它加载进自己的进程，`-ur` 只能删注册表，没法让别的进程卸载 dll。所以 `usUninstall` 里注销完就 `taskkill` 一次，等句柄释放后 `[UninstallDelete]` 才能把 `{app}` 删干净；`usPostUninstall` 再确认任务栏回来，回来得晚就在这里拉一把。

代价是用户已经打开的文件夹窗口会被关掉，任务栏一般 1～2 秒就恢复（Winlogon 的 `AutoRestartShell` 默认会重启它）。只有在 `Auto7zShell.dll` 确实存在时才动手，没装过外壳扩展的旧版本卸载不会被牵连。

### 便携版

把整个目录拷走就能用。数据写在程序目录下的 `.config\Auto7z\`。

在设置里勾上「便携模式」会：
- 把数据目录搬到程序旁边
- 注销右键菜单（便携程序不该在系统里留痕迹）

取消勾选则搬回来并重新注册。搬完空的 `.config` 目录会一并清掉。

## 配置

配置文件在 `%LOCALAPPDATA%\Auto7z\Auto7z.xml`（便携模式是程序目录下的 `.config\Auto7z\Auto7z.xml`）。

| 键 | 说明 | 默认值 |
|---|---|---|
| `Language` | 界面语言：`zh-CN` / `zh-TW` / `en-US` | 按系统语言推断 |
| `Volume` | 分卷大小（MB），`0` 表示不分卷 | `2000` |
| `Format` | 压缩格式：`7z` / `zip` / `tar` | `7z` |
| `Password` | 压缩密码 | 空 |
| `UnzipPassword` | 解压密码 | 空 |
| `Zstd` | tar 归档是否用 zstd 压缩 | `True` |
| `GenerateMD5` | 是否生成 MD5 校验文件 | `True` |
| `AutoSave` | 是否自动保存设置 | `True` |
| `MaxLevelCompress` | 是否用最高压缩等级 | `False` |
| `IsSolidMode` | 是否固实压缩 | `False` |
| `IsEncFileName` | 是否加密文件名（仅 7z，且需要密码） | `False` |
| `CompressionLevel` | 压缩等级：`Store` / `Normal` / `Ultra` | `Normal` |
| `OutputPath` | 输出目录，空表示源文件旁边 | 空 |
| `IsTopMost` | 窗口是否置顶 | `False` |
| `IsPortable` | 是否便携模式 | `False` |
| `RuntimeMode` | 运行模式：`Low` / `Balanced` / `High` | `Balanced` |
| `SevenZUsageCount` | 7z 使用次数 | `0` |
| `ErrorLevel` | 日志级别：`Info` / `Warning` / `Error`（隐藏开关，界面上没有对应控件） | `Warning` |

`Format` 只支持 `7z` / `zip` / `tar` 三种输出。gz、bz2、xz、zst、rar 等只用于**识别**（判断某个文件是不是压缩包、决定自动模式下解不解），不能作为输出格式。解压不写死格式，由 7-Zip 按文件头自己认，所以 rar / gz / xz / cab / tar.zst 都能解开。

`Volume` 有三条限制：tar 格式不支持分卷（7-Zip 会生成解不开的 `.tar.001`）；输入总大小不超过分卷大小时不切分；输入为空时不切分（否则会产出一个孤零零的 `.7z.001`）。

配置文件坏了会自动重建，缺键也会补齐。早期版本用过的 `ZipLevel`、`Portable`、`MainText` 等旧键在读写时会被剔除。

## 多语言

界面语言由 `Language` 键决定，切换后立刻生效。

右键菜单的文字也跟着换：主程序把当前语言写到 `HKCU\Software\Auto7z\Language`，组件建菜单时从那儿读。写 HKCU 是因为那是普通用户自己就有权限写的键，界面语言随时会变，不需要提权；组件读不到时还会回退去读配置文件里的 `Language` 元素，再读不到就用英文。

三种语言各有一份词典，在 `Resources\Languages\` 下（`zh-CN.xaml` / `zh-TW.xaml` / `en-US.xaml`），键要一一对应。

## 7-Zip 组件

`Resources\Modules\` 下带着 `7z.dll` 和 `7z.exe`（7-Zip 22.01 ZS v1.5.5 R3，x64），**执行第一个压缩/解压任务时**释放到数据目录（不是启动时—— 所以干净环境下首次启动只生成配置文件，`7-Zip\` 目录是空的）。

释放前会核对大小，跟资源对不上说明文件坏了，会重新释放一遍。

## 代码结构

```
Auto7z_WPF/
├── Auto7z_WPF.csproj           Console 构建 → Auto7z.exe（命令行）
├── Auto7z_WPF.Gui.csproj       GUI 构建 → Auto7zGUI.exe（双击 / 开始菜单 / 右键 / 拖拽）
├── App.xaml / App.xaml.cs      启动流程：运行方式分流、单实例
├── App.config                  .NET 运行时与程序集绑定重定向
├── app.manifest                清单（执行级别、兼容性）
├── MainWindow.xaml(.cs)        主窗口：拖放、队列、界面交互
├── ProcessWindow.xaml(.cs)     进度窗口（右键菜单 / 拖拽模式用，不出主窗口）
├── MessageWindow.xaml(.cs)     消息提示（无边框窗口）
├── PasswordWindow.xaml(.cs)    密码输入
├── AboutWindow.xaml(.cs)       关于窗口
├── Models/
│   ├── Enums.cs                枚举定义（含 RunMode 运行方式）
│   ├── PendingFile.cs          待处理文件
│   └── ProgressInfo.cs         进度信息
├── Services/
│   ├── AppConfig.cs            配置项
│   ├── AppPaths.cs             数据目录定位（根目录 + 数据目录名）
│   ├── AppState.cs             运行时状态
│   ├── ArchiveFormatMap.cs     格式 ↔ 7-Zip -t 开关、按魔数识别格式
│   ├── ConfigService.cs        配置读写
│   ├── ConsoleAttach.cs        借用父进程(cmd)的控制台，并重建标准流绑定
│   ├── ConsoleTaskRunner.cs    命令行模式的任务执行
│   ├── EnumText.cs             枚举与界面文案的多语言文本
│   ├── FileSystemHelper.cs     文件系统辅助
│   ├── LanguageService.cs      语言切换
│   ├── LaunchContext.cs        判定启动方式（拖拽 / 双击 / 命令行）
│   ├── LogService.cs           日志
│   ├── Md5Service.cs           MD5 计算
│   ├── MessageService.cs       弹窗
│   ├── SevenZipService.cs      调 7z.exe 的核心逻辑
│   ├── ShellCommandService.cs  命令行解析
│   ├── ShellMenuTaskRunner.cs  右键菜单 / 拖拽模式的任务执行（无主窗口）
│   ├── ShellRegistrationService.cs  右键菜单注册
│   ├── SingleInstanceService.cs    单实例与任务交接
│   └── WindowHelper.cs         窗口辅助
├── ViewModel/
│   ├── Auto7zViewModel.cs      主视图模型
│   ├── ObservableObject.cs     INotifyPropertyChanged 基类
│   └── RelayCommand.cs         ICommand 实现
├── Properties/
│   ├── AssemblyInfo.cs         程序集信息
│   ├── Resources.resx          资源
│   └── Settings.settings       设置
└── Resources/
    ├── Languages/               三语词典
    ├── Images/                  主题图片
    ├── Modules/                 内置 7z 组件
    └── WindowDirectory/         窗口资源字典
```

### 几个关键设计

**同一份源码编两个 exe（图形入口零控制台）**：
`Auto7z.exe` 是 Console 子系统，命令行专用 —— `cmd` 会等它跑完，
`Y/N` 与密码输入才不会被提示符抢走。代价是双击它时 Windows 会先分配一个控制台窗口。
`Auto7zGUI.exe` 是 GUI 子系统，双击 / 开始菜单 / 右键菜单 / 拖拽都用它，
全程不创建控制台，连一帧闪烁都没有。
PE 子系统这一个字段同时决定「cmd 等不等」与「启动时给不给控制台」，单文件无法兼得，
所以编两份；两个工程共用同一份源码（`Auto7z_WPF.Gui.csproj` 只 import 主工程）。

分工是硬性的，不是「看父进程猜」：

- `Auto7z.exe` 不带参数时只打印用法并以退出码 2 退出，**不会**拉起主窗口；
- `Auto7zGUI.exe` 不接控制台、也不处理 `-h` / `-r` / `-ur`，带路径就是右键/拖拽那套小窗口流程，不带路径就是主窗口。

**单实例**：程序只跑一个实例。第二次启动时把文件列表交给已有实例（命名管道），已有实例排队处理。避免多个进程抢同一个 7z 组件。

**三种运行方式**：`RunMode` 枚举区分 `Gui` / `Console` / `ShellMenu`。
三者的差异不只是"有没有界面" —— 信息输出到哪、怎么问用户都不一样，
所以显式区分，不靠"有没有主窗口"去反推。

哪条入口能进哪种模式现在是编译期定死的：`Auto7z.exe` 只走 `Console`，
`Auto7zGUI.exe` 只走 `Gui` 与 `ShellMenu`（`GUI_ENTRY` 编译开关，见 `App.xaml.cs`）。

- `Gui`：双击启动。主窗口在场，进度显示在主窗口提示区，问用户用对话框。
- `Console`：cmd 里敲命令。**借用父 cmd 的控制台**（`ConsoleAttach`），
  进度写控制台，Y/N 和密码也在控制台里输入。
- `ShellMenu`：右键菜单**和拖拽到 exe**。永远不建主窗口，
  进度用 `ProcessWindow` 显示，问用户用小对话框。

**命令行模式不弹界面**：命令行下完全不创建主窗口，做完就退出。
失败时也是往控制台写一行文字，**不弹窗** —— 弹窗会把用户的注意力
从控制台拽走，还会把流程堵住。

**密码框用 TextBox 不是 PasswordBox**：`PasswordBox` 不接受 IME 的组合输入，
中文密码打不进去。压缩包的密码本来就允许中文，能输进去比看不见更重要。
代价是密码在界面上是明文显示的。

**传参走共享内存**：右键菜单选上百个文件时命令行装不下（32767 字符上限），
所以文件列表走命名共享内存，命令行只带一个引用。
程序也靠这个共享内存形式认出"这是右键菜单来的"。
协议见 [`Auto7zShell`](../Auto7zShell/README.md)。

**数据目录分根与数据两层**：根目录普通模式是 `%LOCALAPPDATA%`，
便携模式是程序目录下的 `.config`；数据目录一律叫 `Auto7z`，挂在根目录下面。
目录搬动时按这个结构来，不会留下空壳目录。

**搬移目录的删除有边界校验**：搬移数据要递归删除源目录，
删除前先校验路径 —— 源目录必须位于便携根或 `%LOCALAPPDATA%` 根**之下**、
不等于根本身、目录名必须是 `Auto7z`（见 `Auto7zViewModel.IsSafeToDeleteRecursively`）。
校验不过就不删，宁可搬移失败也不误删别的东西。
这条守卫除了静态检查，还有一个运行时探针直接调用它、
拿真实越界路径（根目录、`C:\Windows`、`..` 逃逸等）验证它真的拦得住 ——
守卫「写了但不生效」也能查出来。

**便携模式会注销右键菜单**：便携程序在别的电脑上跑，不该在系统里留注册表痕迹。
勾选便携模式时注销，取消时注册。

## 测试

测试工具在 [`testtools`](../testtools/README.md)，说明见那里的 README。

一键跑（推荐）：

```powershell
cd ..\testtools
python run_regress.py         # 一键回归
python run_system_test.py     # 一键系统测试
```

单跑主回归：

```powershell
python regress.py --app "..\Auto7z_WPF\Auto7z_WPF\bin\Debug\Auto7z.exe"
```

`regress.py` 里写死的默认路径指向的是旧目录结构，已经对不上了，所以要显式用 `--app` 指定被测程序。

## 已知限制

- 命令行模式的合并询问要读控制台输入，管道环境读不到时按「合并」处理
- 命令行模式解压加密包时，密码也在控制台里问；管道环境读不到会当作「用户取消」
- 右键菜单是传统 `IContextMenu` 扩展，Windows 11 上在「显示更多选项」里
- 右键菜单注册要管理员权限，普通用户运行时会弹 UAC
- 密码框是明文显示的（为了支持中文输入，见「几个关键设计」）
