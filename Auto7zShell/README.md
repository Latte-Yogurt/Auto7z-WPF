# Auto7zShell —— 右键菜单外壳扩展

Windows 资源管理器右键菜单扩展。右键文件或文件夹时提供「用 Auto7z 压缩」「用 Auto7z 解压」两项，文字跟着主程序的界面语言走。

用 C++ 写成独立的 COM 组件（`Auto7zShell.dll`），由主程序 `Auto7z.exe` 在命令行下注册。

## 目录结构

```
Auto7zShell/
├── buildtool/
│   ├── BuildTool.cpp      构建工具源码
│   ├── buildtool.bat      编译构建工具用
│   └── buildtool.exe      构建工具
├── README.md              本文件
├── src/
│   ├── ShellExtension.cpp 组件实现
│   ├── ShellExtension.def 导出表
│   ├── ShellProtocol.h    与主程序之间的传参约定
│   ├── ShellResource.rc   菜单文字，三种语言各一套
│   └── ShellExtension.obj 编译中间产物（由 cl 生成）
└── build/                 构建产物
    ├── Auto7zShell.dll
    └── ShellResource.res
```

`build\` 目录由构建工具创建，不在版本库里。`src\ShellExtension.obj` 是 `cl` 生成的中间文件，下次构建会被覆盖。

## 构建

```bat
buildtool\buildtool.bat
buildtool\buildtool.exe
```

第一个用 `BuildTool.cpp` 编译构建工具本身，第二个构建组件。产物在 `build\Auto7zShell.dll`，要拷到主程序旁边 —— 组件按同目录找主程序。

组件的 `buildtool.exe` 支持 `--clean` 先清空 `build\` 目录再构建。

### 构建工具

| 参数 | 作用 |
|---|---|
| 无 | 构建 |
| `--clean` | 先清空产物目录再构建 |
| `--vcvars <路径>` | 指定 `vcvars64.bat` 的位置 |
| `--help` | 显示用法 |

`buildtool.exe` 是静态链接的，只依赖 `KERNEL32.dll` 与 `SHELL32.dll`，拷到任何 Windows 机器上都能跑。

用 bat 而不是直接改构建步骤，是因为 bat 是明文脚本，谁都能改；编译成 exe 之后要改就得先看懂 C++。

不过要说清楚 —— **exe 也不等于防篡改**。反编译工具照样能改。真要防篡改得做 **Authenticode 代码签名**，改了签名就失效。

### 缺少编译环境时

构建工具找不到 MSVC 工具链时，会打印下载步骤并**直接打开浏览器**：

```
找不到 MSVC 的 x64 工具链
需要 Visual Studio Build Tools 的 x64 工具链才能编译。
下载地址:
  https://visualstudio.microsoft.com/downloads/#build-tools-for-visual-studio-2022
装的时候勾选「使用 C++ 的桌面开发」, 右边的工作负载选「MSVC 生成工具」,
组件里保留「适用于 Windows 的 C++ MSVC 生成工具」与「Windows 11 SDK」。
装完重开命令行再跑一次就行。
已经装在别的位置的话, 用 --vcvars 指定 vcvars64.bat。
正在打开下载页...
```

装 Build Tools 时要勾的工作负载：

- 「使用 C++ 的桌面开发」
- 右侧工作负载选「MSVC 生成工具」
- 组件里保留「适用于 Windows 的 C++ MSVC 生成工具」与「Windows 11 SDK」

工具链默认在几个常见位置找（按顺序）：VS 18 的 BuildTools、VS 2022 的 BuildTools / Community / Professional / Enterprise，每个都试 `Program Files (x86)` 与 `Program Files` 两个路径。装在别处就用 `--vcvars` 指定 `vcvars64.bat` 的完整路径。

`buildtool.bat` 里的路径是写死的（`VS 18\BuildTools`），不在那儿就把那一行改掉，或者直接用 `--vcvars`。

### 构建工具内部做了什么

三步放在**同一个 cmd 会话**里一次做完：

```
call vcvars64.bat && cl ... && rc ... && link ...
```

之所以不把工具链的环境变量搬回自己的进程：那样要读 `set` 的输出、处理编码、还容易在变量名大小写上出岔子（`Path` 与 `PATH` 是同一项，删一次会把刚设好的也删掉）。让同一个 cmd 先跑 vcvars 再跑 cl 与 link，环境变量自然是对的。

构建失败时会把编译器的输出显示出来，不会只丢一句「构建失败」。

### 编译选项

| 选项 | 原因 |
|---|---|
| `/utf-8` | 源码是 UTF-8，不加这个编译器会按本地代码页（GBK）读，中文注释和字符串会乱 |
| `/MT` | 静态链接 CRT，dll 不依赖运行库，部署到别的机器不用带 VC++ redistributable |
| `/O2` | 优化 |
| `/W3` | 警告级别 |
| `/DUNICODE /D_UNICODE` | 全部用宽字符 API |
| `/EHsc` | 启用 C++ 异常 |

链接的库：`ole32.lib`（COM）、`shell32.lib`（外壳 API）、`advapi32.lib`（注册表）、`shlwapi.lib`、`user32.lib`（菜单与图标）、`gdi32.lib`（把图标画成位图）。

### 文件编码

| 文件 | 编码 | 原因 |
|---|---|---|
| `ShellExtension.cpp` | UTF-8 无 BOM | 靠 `/utf-8` 让编译器按 UTF-8 读 |
| `ShellProtocol.h` | UTF-8 无 BOM | 同上 |
| `ShellExtension.def` | ASCII | 链接器只认这个 |
| **`ShellResource.rc`** | **UTF-16LE 带 BOM** | **`rc` 工具按 ANSI 读 `.rc`，UTF-8 的中文会被截断，菜单项就成了空白** |
| `BuildTool.cpp` | UTF-8 无 BOM | 同上，`buildtool.bat` 编译它时也带 `/utf-8` |
| `buildtool.bat` | ASCII | cmd 按本地代码页读，UTF-8 会解析出错 |

改 `ShellResource.rc` 时必须存成 **UTF-16LE 带 BOM**（Visual Studio 默认就是）。

改 `ShellExtension.cpp` / `BuildTool.cpp` 时反过来，存成 **UTF-8 无 BOM** —— 这两个文件靠编译器的 `/utf-8` 开关识别，带 BOM 也能编，但源码里的中文注释会依赖编译器版本的处理方式。

## 组件接口

```
IContextMenu   菜单项、命令执行
IShellExtInit  接收资源管理器传来的选中项
```

类 ID：`{6C3F8A21-4B7D-4E5A-9A2C-3D710E5B8841}`

导出函数：

| 导出 | 作用 |
|---|---|
| `DllGetClassObject` | COM 入口，返回类工厂 |
| `DllCanUnloadNow` | COM 入口 |
| `DllRegisterServer` | 写注册表：CLSID、InprocServer32、Approved、四个菜单挂载点，顺带写主程序路径 |
| `DllUnregisterServer` | 删掉上面那些 |
| `DllSetMainExePath` | 记录主程序位置，供菜单图标与启动时使用 |
| `DllRemoveMainExePath` | 清掉上面那个 |
| `DllSetLanguage` | 记下主程序当前用的语言，写在 `HKCU\Software\Auto7z` |

## 注册表

COM 类走 `HKEY_CLASSES_ROOT`（等价于 `HKLM\Software\Classes`），菜单挂载点**显式**写 `HKLM\Software\Classes`，Approved 列表写 `HKLM`（这三处都需要管理员权限）：

```
HKLM\Software\Classes\CLSID\{6C3F8A21-...}
    (默认)                = Auto7z Shell Extension
    InprocServer32
        (默认)            = <dll 路径>
        ThreadingModel    = Apartment
    Auto7z
        MainExePath       = <Auto7z.exe 路径>

HKLM\Software\Classes\*\shellex\ContextMenuHandlers\Auto7z
HKLM\Software\Classes\Directory\shellex\ContextMenuHandlers\Auto7z
HKLM\Software\Classes\Folder\shellex\ContextMenuHandlers\Auto7z
HKLM\Software\Classes\Directory\Background\shellex\ContextMenuHandlers\Auto7z
    (默认)                = {6C3F8A21-...}

HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Shell Extensions\Approved
    {6C3F8A21-...}       = Auto7z Shell Extension
```

语言在另一个位置，普通用户就能写：

```
HKCU\Software\Auto7z
    Language              = zh-CN
```

注意 `DllUnregisterServer` 删 Approved 时删的是 `HKEY_CURRENT_USER` 那一份，而注册时写的是 `HKLM` 那一份 —— 所以卸载后 `HKLM` 下的 Approved 条目会留下。它不指向任何已注册的 dll，Explorer 会忽略，不影响使用。

### 四个容易踩的坑

**一、菜单挂载点要写 `HKLM\Software\Classes`**

写 `HKEY_CLASSES_ROOT\*\...` 会被系统拒掉（错误 5）—— HKCR 的 `*` 只认 HKLM 那一份，合并视图不给写。

**二、Approved 列表必须写 HKLM**

写在 `HKEY_CURRENT_USER` 下资源管理器**不认**，扩展会被**静默禁用** —— 右键菜单完全不出现，而且没有任何错误提示。这是最难查的一个。

**三、注册完要通知资源管理器**

不然它还拿着启动时缓存的处理器列表。主程序里已经调了 `SHChangeNotify(SHCNE_ASSOCCHANGED)`；手工改注册表后要自己发这个通知。

**四、语言要走 HKCU，不要走注册表的 CLSID 段**

语言跟着界面走，界面语言随时会变。写在 `HKCU\Software\Auto7z` 下，主程序改语言时写一次就行，用户自己就有权限，不用提权。放进 `CLSID\...\Auto7z` 里就写不进去了 —— 那要管理员权限。

## 菜单项

| 位置 | 文字 |
|---|---|
| 0 | 用 Auto7z 压缩 / 用 Auto7z 壓縮 / Compress with Auto7z |
| 1 | 用 Auto7z 解压 / 用 Auto7z 解壓 / Extract with Auto7z |

文字在 `ShellResource.rc` 的字符串表里，三种语言各一套，id 的百位区分：

| id | 语言 |
|---|---|
| 101 / 102 | 简体中文 |
| 201 / 202 | 繁體中文 |
| 301 / 302 | English |

跟 `ShellExtension.cpp` 里的 `kStringCompressZhCN` 等常量对应。读不到语言时用英文那套。

图标取主程序的图标：组件从注册表 `CLSID\...\Auto7z\MainExePath` 找到 `Auto7z.exe`，用 `ExtractIconEx` 取小图标（菜单用 16 像素那个），再画成位图给菜单项。取不到就菜单项不带图标，不影响用。

注册表里没有 `MainExePath` 时，组件回退到「dll 同目录下的 `Auto7z.exe`」来找主程序。

## 传参方式

选中的文件列表**不走命令行**，走命名共享内存：

```
Auto7z.exe #<映射名>:<字节数>:<事件名> [-A|-Z|-U]
```

映射名与事件名由组件随机生成，主程序按名字打开共享内存，从里面读出以 `\0` 分隔的 UTF-16 路径列表。共享内存第一个 `wchar_t` 是 0，表示后面按 UTF-16 读。

用共享内存是为了绕开命令行的长度上限（32767 字符）—— 一次选中上百个长路径时命令行装不下。

句柄的生命周期：组件写完数据后**保持句柄打开**，等主程序读完才关。命名共享内存最后一个句柄关闭时对象就销毁了，提前关掉主程序就读不到了。

主程序读完会 `SetEvent` 通知组件，组件 `WaitForSingleObject` 等这个信号，最多等 30 秒；超时就直接放掉，不让句柄一直占着。

## 作用范围

菜单项只对**磁盘上真实存在的文件或文件夹**显示：

| 目标 | 有菜单项 |
|---|---|
| 普通文件 | 有 |
| 普通文件夹 | 有 |
| 盘符根目录（`C:\`、`D:\`） | 无 |
| 回收站里的项目 | 无 |
| 虚拟路径（此电脑等） | 无 |

判断靠 `GetFileAttributesW` 加 `GetVolumePathNameW`。回收站的内部文件在 `$Recycle.Bin` 下，压缩它们没有意义。

## 几个实现上的要点

**`QueryInterface` 要按接口分别返回指针**

`IContextMenu` 和 `IShellExtInit` 在对象里的偏移不一样（多重继承），一律返回 `IContextMenu` 的地址，资源管理器拿 `IShellExtInit` 去用就会调错函数，**静默禁用扩展**。

**GUID 比较用 `IsEqualIID`，不要用 `==`**

C++ 里 GUID 没有重载 `operator==`，比较的是地址，永远不相等。

**`QueryContextMenu` 的返回值是加进去的菜单项个数**

返回 `S_OK`（也就是 0）等于告诉资源管理器「没有菜单项」。

**`Initialize` 始终返回 S_OK**

取不到路径时返回失败会让资源管理器静默禁用整个扩展。取不到就当没有，到 `InvokeCommand` 再说。

**菜单项位图不能在插入后删掉**

`MIIM_BITMAP` 不会复制位图，菜单项一直引用那个句柄。删掉之后菜单里就是空白图标。

**`InsertMenuItemW` 两次要用不同的位置**

用同一个 `indexMenu` 的话第二项会插到第一项前面，顺序就反了。第二次要传 `indexMenu + 1`。

## 检查组件是否正常

工具在 `..\testtools\`，说明见那里的 README。下面的命令在 `Auto7zShell\` 目录下执行。

```powershell
$bin = "..\Auto7z_WPF\Auto7z_WPF\bin\Debug"
$csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$refs = @(
  "C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.7.2\PresentationFramework.dll",
  "C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.7.2\PresentationCore.dll",
  "C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.7.2\WindowsBase.dll",
  "C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.7.2\System.Xaml.dll")

function Build-Tool($name) {
  $a = @("/nologo", "/out:$bin\$name.exe")
  foreach ($r in $refs) { $a += "/r:$r" }
  $a += "..\testtools\cs\$name\$name.cs"
  & $csc @a
}

$clsid = "{6C3F8A21-4B7D-4E5A-9A2C-3D710E5B8841}"

# 组件能不能建出菜单, 菜单项文字对不对
Build-Tool "shellcheck"
& "$bin\shellcheck.exe" "$bin\Auto7zShell.dll" $clsid "$env:TEMP\t.txt"

# 哪些地方该出现菜单项
Build-Tool "scopeprobe"
& "$bin\scopeprobe.exe" "$bin\Auto7zShell.dll" $clsid $env:TEMP

# 完整链路: Initialize → InvokeCommand → 真的产出压缩包
Build-Tool "dataprobe"
& "$bin\dataprobe.exe" "$bin\Auto7zShell.dll" $clsid "$env:TEMP\t.txt"

# 菜单图标
Build-Tool "iconprobe"
& "$bin\iconprobe.exe" "$bin\Auto7zShell.dll" $clsid "$env:TEMP\t.txt"
```

## 排查清单

右键菜单不出现时，按这个顺序查：

1. `reg query "HKLM\Software\Classes\*\shellex\ContextMenuHandlers\Auto7z"` —— 挂载点在不在
2. `reg query "HKLM\Software\Classes\CLSID\{6C3F8A21-...}\InprocServer32"` —— dll 路径对不对、`ThreadingModel` 是不是 `Apartment`
3. `reg query "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Shell Extensions\Approved" /v "{6C3F8A21-...}"` —— 在不在批准名单里
4. `reg query "HKCU\Software\Auto7z" /v Language` —— 语言记了没有，记的是不是界面语言
5. dll 位数对不对 —— 64 位 Explorer 需要 64 位 dll，`regsvr32` 要用 `System32` 里那个
6. 重启资源管理器，排除缓存
7. 跑一遍 `shellcheck` 工具，看组件本身能不能建出菜单
