# Auto7z

<div align="center">

<img src="ScreenShot.png" width="300">

[English](#english) | [中文](#中文)

</div>

---

## English

### What it is

A Windows GUI archiver built on 7-Zip ZS (7-Zip 22.01 ZS v1.5.5 R3, x64).

Drag files onto the window, or right-click them in Explorer and pick **Compress with Auto7z** / **Extract with Auto7z**. It can also be driven from the command line.

- GUI: drag in files or folders, pick format, level, password, split size, MD5, solid mode, encrypted file names.
- Explorer context menu: a native COM shell extension adds two entries to the right-click menu.
- Command line: scriptable, no GUI, progress on stdout, exits when done.
- Portable mode: all data lives next to the exe, nothing left in `%LOCALAPPDATA%`.
- Three languages: English, 简体中文, 繁體中文.

### How to start it

The program ships as two executables built from the same source, with the same
compression engine and the same configuration. Which one you start decides
whether a console is involved at all.

| How you start it | Main window | Progress shown in | Asks you by |
|---|---|---|---|
| Double-click `Auto7zGUI.exe` | yes | the main window | a dialog |
| `Auto7z.exe file...` in a console | **no** | the console | typing in the console |
| Right-click → **Compress/Extract with Auto7z** | **no** | a small progress window | a small dialog |
| Drag files onto `Auto7zGUI.exe` | **no** | a small progress window | a small dialog |

The last two never open the main window: the expectation for a context-menu
action is "click it and walk away", and a full main window popping up is
intrusive. They share a small progress window (`ProcessWindow`) instead, and
ask through small dialogs (`MessageWindow` / `PasswordWindow`).

The command-line mode borrows the console it was started from, so its output
and its questions land in the window you typed the command into.

#### Two executables

The program is built twice from the same source:

| Executable | Subsystem | Used for |
|---|---|---|
| `Auto7z.exe` | console | the command line only — `cmd` waits for it, so `Y/N` answers and passwords typed at the prompt reach the program. With no arguments it prints the usage and exits with code 2; it never opens the main window |
| `Auto7zGUI.exe` | GUI | double-click, Start-menu / desktop shortcut, Explorer context menu, dropping files on it — never creates a console window, not even for a frame. It ignores the console-only switches (`-h` / `-r` / `-ur`), which belong to `Auto7z.exe` |

A single executable cannot do both: the PE subsystem field decides whether
`cmd` waits for the process *and* whether Windows allocates a console at
launch. Use `Auto7z.exe` when typing commands, `Auto7zGUI.exe` for the
graphical entries.

Both share the same single-instance mutex and named pipe, so a second launch
of either one hands its file list to the instance that is already running.

### Requirements

- Windows only, x64. The shell extension is a 64-bit COM DLL and must match the bitness of Explorer.
- .NET Framework 4.7.2, WPF.
- Visual Studio 2022 (or newer) with the .NET desktop workload, to build the C# project.
- Visual Studio Build Tools with the x64 C++ toolchain, to build the shell extension.
- 7-Zip ZS is bundled as an embedded resource and extracted to the data directory at runtime.

### Layout

```
Auto7z Project/
├── Auto7z_WPF/              the WPF application
│   └── Auto7z_WPF/
│       ├── Auto7z_WPF.csproj      console build → Auto7z.exe (command line)
│       ├── Auto7z_WPF.Gui.csproj  GUI build → Auto7zGUI.exe (all graphical entries)
│       ├── App.xaml.cs      startup, run-mode dispatch, single instance
│       ├── MainWindow.xaml  main window
│       ├── ProcessWindow.xaml  progress window (context-menu / drag mode)
│       ├── MessageWindow.xaml   dialogs
│       ├── PasswordWindow.xaml  password prompt
│       ├── Services/        config, 7-Zip driver, shell registration, logging
│       ├── ViewModel/       main view model
│       ├── Models/          enums and small value types
│       └── Resources/       language dictionaries, images, 7-Zip binaries
├── Auto7zShell/             the Explorer context menu extension
│   ├── buildtool/           the build tool (exe) and its source
│   └── src/                 the COM component
└── testtools/               regression and system test scripts
```

Each part has its own README: [`Auto7zShell`](Auto7zShell/README.md),
[`Auto7z_WPF`](Auto7z_WPF/README.md), [`testtools`](testtools/README.md).

### Building

The C# project:

```powershell
& "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe" `
    Auto7z_WPF\Auto7z_WPF.sln /t:Rebuild /p:Configuration=Release /p:Platform="Any CPU"
```

`msbuild` is usually not on `PATH` — use the full path above, or run from a Visual Studio developer prompt.
The solution contains two projects, so this produces **both** `bin\<Configuration>\Auto7z.exe`
(console, command line) and `bin\<Configuration>\Auto7zGUI.exe` (GUI, all graphical entries).

The shell extension, in two steps — the first builds the build tool itself, the second builds the component with it:

```bat
Auto7zShell\buildtool\buildtool.bat
Auto7zShell\buildtool\buildtool.exe --vcvars "C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat"
```

`buildtool.bat` compiles `buildtool.exe` from `BuildTool.cpp`; `buildtool.exe` then compiles the component and leaves it in `Auto7zShell\build\Auto7zShell.dll`. `buildtool.exe` is statically linked and only needs `KERNEL32.dll` and `SHELL32.dll`. It needs the MSVC x64 toolchain; if it can't find one it prints the download steps and opens the browser.

The `VCVARS` path hardcoded in `buildtool.bat` points at a Visual Studio 18 BuildTools location that typically does not exist — on a VS 2022 machine, pass the real `vcvars64.bat` via `--vcvars` as shown above, or edit the `VCVARS` constant in the bat.

Copy the resulting `Auto7zShell.dll` next to `Auto7z.exe` — the application looks for it in its own directory. `Auto7zGUI.exe` belongs in the same directory: the Explorer context menu and drag-and-drop use it, and it has to sit next to `Auto7z.exe`.

### Usage

```
Auto7z.exe [options] [files or folders...]

  -A            automatic mode (default): unpack archives, compress the rest
  -Z            compress
  -U            unpack
  -r            register the Explorer context menu (needs administrator)
  -ur           unregister the Explorer context menu (needs administrator)
  -h            show this help
```

Command line mode never opens a window. Progress goes to the console and the
process exits when the task is done.

With several files, the "merge into one archive?" question is asked on the
console instead of in a dialog. If there is no console to read (a pipe, a
scheduled task), it merges.

**Encrypted archives**: if the archive needs a password that was not given on
the command line, the console asks for it and retries (up to three times):

```
开始解压：...\enc.7z
该压缩包已加密，请输入密码：
```

### Dragging files onto the exe

Dropping files onto the `Auto7zGUI.exe` icon does **not** open the main window —
it behaves like the Explorer context menu: a small progress window shows the
progress and small dialogs ask the questions. The application tells the two
apart by looking at the parent process: Explorer started it, so it is a
drag-and-drop.

### Registering the context menu

```bat
Auto7z.exe -r
Auto7z.exe -ur
```

Registration writes to `HKEY_LOCAL_MACHINE\Software\Classes`, so it needs administrator rights. When the process is not elevated, it runs `regsvr32` with the `runas` verb and the user gets a UAC prompt.

The extension is a traditional `IContextMenu` extension, so on Windows 11 the entries live under **Show more options**.

The menu text follows the interface language. The application records the current language under `HKCU\Software\Auto7z`, and the extension reads it from there. When that key is missing, the extension falls back to the `Language` element in `Auto7z.xml`, and to English after that.

### How it works

The context menu extension is a small COM component (`Auto7zShell.dll`) that implements `IContextMenu` and `IShellExtInit`. It is registered under `*\shellex\ContextMenuHandlers`, `Directory\...`, `Folder\...` and `Directory\Background\...`, which is what makes Explorer hand over the whole selection at once. A `shell\command` registration would start one process per file instead.

The selected paths are passed to the application through a named shared memory block, because the command line cannot hold hundreds of long paths (32767 character limit). The command line only carries a reference to it. That shared-memory form is also how the application recognises "this came from the context menu" and switches to the no-main-window mode.

Only one instance of the application runs at a time. A second launch hands its file list to the running instance over a named pipe and exits; the running instance queues the batch and works through them one at a time.

Portable mode unregisters the context menu when it is switched on and registers it again when it is switched off, so a portable copy does not leave anything behind in the registry.

### External dependencies

No NuGet packages. The only bundled dependency is the 7-Zip ZS build.

**7-Zip ZS**

- Purpose: the actual compression and decompression.
- Distribution: `7z.dll` and `7z.exe` are embedded as resources and extracted to the data directory at runtime.
- Source: 7-Zip ZS by mcmilk, a fork of 7-Zip with Zstandard support.
- License: 7-Zip is LGPL, the ZS fork adds Zstandard bindings under the same terms. Check the license of the exact build you ship.

---

## 中文

### 这是什么

一个基于 7-Zip ZS（7-Zip 22.01 ZS v1.5.5 R3，x64）的 Windows 图形化压缩工具。

把文件拖进窗口，或者在资源管理器里右键选「用 Auto7z 压缩」「用 Auto7z 解压」。也可以从命令行调用。

- 图形界面：拖入文件或文件夹，可选格式、压缩等级、密码、分卷大小、MD5、固实压缩、加密文件名。
- 资源管理器右键菜单：原生 COM 外壳扩展，在右键菜单里加两项。
- 命令行：可脚本化，不弹界面，进度输出到控制台，任务做完就退出。
- 便携模式：数据全放在程序旁边，`%LOCALAPPDATA%` 里不留东西。
- 三种语言：简体中文、繁體中文、English。

### 启动方式

程序编成两个可执行体，共用同一套源码、压缩引擎与配置。启动哪一个，决定这次有没有控制台。

| 启动方式 | 主窗口 | 进度显示在 | 用什么方式问你 |
|---|---|---|---|
| 双击 `Auto7zGUI.exe` | 有 | 主窗口 | 对话框 |
| 在 cmd 里敲 `Auto7z.exe 文件...` | **无** | 控制台 | 在控制台里敲字 |
| 资源管理器右键 →「用 Auto7z 压缩/解压」 | **无** | 小进度窗口 | 小对话框 |
| 把文件拖到 `Auto7zGUI.exe` 图标上 | **无** | 小进度窗口 | 小对话框 |

后两种**永远不打开主窗口** —— 右键操作的预期是「点了就走」，冒出一个完整主窗口很突兀。
它们改用一个小进度窗口（`ProcessWindow`）显示进度，用小对话框（`MessageWindow` /
`PasswordWindow`）问话。

命令行模式会**借用启动它的那个控制台**，所以输出和提问都落在你敲命令的那个窗口里。

#### 两个可执行体

同一份源码编出两份：

| 可执行体 | 子系统 | 用途 |
|---|---|---|
| `Auto7z.exe` | Console | 只做命令行 —— `cmd` 会等它跑完，所以 `Y/N` 与密码输入不会被提示符抢走。不带任何参数时打印用法并以退出码 2 结束，不再拉起主窗口 |
| `Auto7zGUI.exe` | GUI | 双击、开始菜单 / 桌面快捷方式、资源管理器右键菜单、把文件拖到它上面 —— 全程不创建控制台窗口，一帧闪都没有。它不处理 `-h` / `-r` / `-ur` 这类命令行开关，那些归 `Auto7z.exe` |

单文件做不到两者兼得：PE 子系统这一个字段同时决定「`cmd` 等不等它跑完」
和「启动时 Windows 给不给它分配控制台」。敲命令用 `Auto7z.exe`，
图形入口用 `Auto7zGUI.exe`。

两者的单实例互斥名与命名管道是同一套，所以再启动其中任何一个，
都会把文件列表交给已经在跑的那个实例。

### 环境

- 仅限 Windows，x64。外壳扩展是 64 位 COM DLL，位数要跟资源管理器对上。
- .NET Framework 4.7.2，WPF。
- 编译 C# 部分需要 Visual Studio 2022（或更新）加 .NET 桌面开发工作负载。
- 编译右键菜单组件需要 Visual Studio Build Tools 的 x64 C++ 工具链。
- 7-Zip ZS 以嵌入资源的形式打包，运行时释放到数据目录。

### 目录结构

```
Auto7z Project/
├── Auto7z_WPF/              WPF 主程序
│   └── Auto7z_WPF/
│       ├── Auto7z_WPF.csproj      Console 构建 → Auto7z.exe（命令行）
│       ├── Auto7z_WPF.Gui.csproj  GUI 构建 → Auto7zGUI.exe（所有图形入口）
│       ├── App.xaml.cs      启动流程、运行方式分流、单实例
│       ├── MainWindow.xaml  主窗口
│       ├── ProcessWindow.xaml  进度窗口（右键菜单 / 拖拽模式）
│       ├── MessageWindow.xaml   对话框
│       ├── PasswordWindow.xaml  密码输入
│       ├── Services/        配置、7-Zip 调用、注册、日志
│       ├── ViewModel/       主视图模型
│       ├── Models/          枚举与小结构
│       └── Resources/       语言词典、图片、7-Zip 二进制
├── Auto7zShell/             资源管理器右键菜单扩展
│   ├── buildtool/           构建工具（exe）及其源码
│   └── src/                 COM 组件
└── testtools/               回归测试与系统测试脚本
```

各部分另有自己的说明：[`Auto7zShell`](Auto7zShell/README.md)、
[`Auto7z_WPF`](Auto7z_WPF/README.md)、[`testtools`](testtools/README.md)。

### 编译

C# 部分：

```powershell
& "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe" `
    Auto7z_WPF\Auto7z_WPF.sln /t:Rebuild /p:Configuration=Release /p:Platform="Any CPU"
```

`msbuild` 通常不在 `PATH` 上 —— 用上面的完整路径，或从 Visual Studio 开发者命令行里跑。
解决方案里有两个工程，所以这一步会同时产出 `bin\<Configuration>\Auto7z.exe`（Console，命令行）
与 `bin\<Configuration>\Auto7zGUI.exe`（GUI，所有图形入口）。

右键菜单组件，两步走 —— 第一步编译构建工具本身，第二步用它编译组件：

```bat
Auto7zShell\buildtool\buildtool.bat
Auto7zShell\buildtool\buildtool.exe --vcvars "C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat"
```

`buildtool.bat` 用 `BuildTool.cpp` 编出 `buildtool.exe`；`buildtool.exe` 再编组件，产物在 `Auto7zShell\build\Auto7zShell.dll`。`buildtool.exe` 是静态链接的，只依赖 `KERNEL32.dll` 与 `SHELL32.dll`。它需要 MSVC 的 x64 工具链；找不到时会打印下载步骤并打开浏览器。

`buildtool.bat` 里写死的 `VCVARS` 指向 VS 18 BuildTools 的位置，一般不存在 —— VS 2022 的机器上按上面的样子用 `--vcvars` 传入真实的 `vcvars64.bat`，或者改掉 bat 里的 `VCVARS` 常量。

编出来的 `Auto7zShell.dll` 要放在 `Auto7z.exe` 旁边 —— 程序在同目录下找它。

### 用法

```
Auto7z.exe [选项] [文件或目录...]

  -A            自动模式（默认）：压缩包解开、其余的压起来
  -Z            压缩
  -U            解压
  -r            注册右键菜单（需要管理员权限）
  -ur           注销右键菜单（需要管理员权限）
  -h            显示帮助
```

命令行模式不弹窗口。进度输出到控制台，任务做完进程就退出。

多个文件时，「是否合并成一个压缩包」改成在控制台里问。读不到输入（管道、计划任务等场景）就按合并处理。

**加密压缩包**：命令行没给密码时，控制台会问你要，并自动重试（最多 3 次）：

```
开始解压：...\enc.7z
该压缩包已加密，请输入密码：
```

### 把文件拖到 exe 图标上

把文件拖到 `Auto7zGUI.exe` 图标上**不会**打开主窗口 —— 它跟右键菜单一样：
用小进度窗口显示进度，用小对话框问话。
程序靠看父进程来区分：父进程是 explorer，说明是拖拽。

### 注册右键菜单

```bat
Auto7z.exe -r
Auto7z.exe -ur
```

注册写在 `HKEY_LOCAL_MACHINE\Software\Classes`，需要管理员权限。进程没有提权时会用 `runas` 跑一次 `regsvr32`，用户会看到 UAC 提示。

这是传统的 `IContextMenu` 扩展，Windows 11 上两项在「显示更多选项」里。

菜单文字跟着界面语言走。主程序把当前语言记在 `HKCU\Software\Auto7z` 下，组件从那儿读。读不到就回退去读 `Auto7z.xml` 里的 `Language` 元素，再读不到就用英文。

### 大概原理

右键菜单扩展是个小的 COM 组件（`Auto7zShell.dll`），实现 `IContextMenu` 与 `IShellExtInit`。它注册在 `*\shellex\ContextMenuHandlers`、`Directory\...`、`Folder\...` 与 `Directory\Background\...` 下，资源管理器会把整个选区一次性交过来。换成 `shell\command` 那种注册方式，资源管理器会对每个文件各起一个进程。

选中的路径走命名共享内存传给程序，因为命令行装不下几百个长路径（32767 字符上限），命令行里只带一个引用。程序也靠这个共享内存形式认出「这是右键菜单来的」，从而切到无主窗口模式。

程序同一时间只跑一个实例。第二次启动会把文件列表通过命名管道交给已经在跑的那个，自己退出；已有实例把这一批排进队列，一个一个处理。

便携模式在勾上时注销右键菜单，取消勾选时再注册回来，便携程序不会在注册表里留痕迹。

### 外部依赖

没有引入任何 NuGet 包，唯一的内置依赖是 7-Zip ZS。

**7-Zip ZS**

- 用途：实际的压缩与解压。
- 分发：`7z.dll` 与 `7z.exe` 作为嵌入资源打包，运行时释放到数据目录。
- 来源：mcmilk 的 7-Zip ZS，给 7-Zip 加了 Zstandard 支持的分支。
- 许可：7-Zip 是 LGPL，ZS 分支加的 Zstandard 绑定同属该许可。请自行确认并遵守对应许可。
