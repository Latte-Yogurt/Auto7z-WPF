# Auto7z 测试工具

只做测试，不改产品代码。

```
testtools/
├── README.md              本文件
├── regress.py             主回归脚本（T01–T22，22 项）
├── cmdline_test.py        命令行测试（T1–T6）
├── check_reg.py           右键菜单注册状态检查
├── diff_reg.py            把 7-Zip 与 Auto7z 的注册表结构列出来对比
├── find_dead_keys.py      查词典与配置里的废键、没人调用的方法
├── find_dead2.py          更全面的废键审查（含 C++ 导出函数）
├── add_keys.py            往三语词典批量加键
├── add_portable_keys.py   追加便携模式相关的词典键
├── add_tabindex.py        给主窗口控件按视觉顺序加 TabIndex
├── remove_delete_temp_keys.py  移除已废弃的词典键
└── cs/                    需要进程内跑的 C# 检查工具
    ├── verify/            对话框按钮文字、居中、提示切换
    ├── uiwidth/           UI 文本自检：三语言下查溢出与遮挡
    ├── taborder/          Tab 顺序检查：静态 TabIndex + 运行时可聚焦性
    ├── portable/          便携模式：目录搬迁
    ├── shellproto/        外壳扩展协议：共享内存传参、模式开关、单实例
    ├── shellcheck/        外壳扩展自检：建类工厂、建菜单、读菜单项文字
    ├── scopeprobe/        外壳扩展作用范围：哪些目标该出现菜单项
    ├── dataprobe/         完整链路：Initialize → InvokeCommand → 真的产出压缩包
    ├── iconprobe/         菜单图标：位图在不在、有没有内容
    ├── measure2/          文本宽度测量
    └── langcheck/         系统语言 → 界面语言的映射
```

## 跑回归测试

```powershell
cd "C:\Users\Mocha\Desktop\VSProject\Auto7z Project\testtools"

python regress.py --app "..\Auto7z_WPF\Auto7z_WPF\bin\Debug\Auto7z.exe"
```

`regress.py` 里写死的默认路径（`C:\Users\Mocha\Desktop\VSProject\Auto7z_WPF\Auto7z_WPF`）是旧目录结构，现在已经不存在了，所以**必须**用 `--app` 指定被测程序：

```powershell
python regress.py --app "C:\path\to\Auto7z.exe"   # 指定被测程序
python regress.py --config Release                 # 配合同上使用
```

脚本会：

- 在 `%LOCALAPPDATA%\Auto7z\Auto7z.xml` 上改配置，**退出时自动还原**
- 产物写到工作区 `regress/` 目录，报告写到 `regress/report.txt`
- 每项测试前清掉残留的 Auto7z 进程（单实例机制下残留会干扰后面的测试）
- **结束时重新注册一次右键菜单** —— T22 会测便携模式，那会把菜单注销掉

回归脚本还会顺带编译 `cs\portable\portable.cs` 跑便携模式测试，需要 `csc.exe`；没有就跳过那一项。

## 跑命令行测试

```powershell
python cmdline_test.py "C:\path\to\Auto7z.exe" "C:\temp\cmdline_test"
```

覆盖：`-h` 用法说明、三种模式开关、不带模式开关走自动模式、拖到 exe 图标（命令行传路径）、多文件一次传入、`-r` / `-ur` 参数识别（只验证参数没被当成路径，不真的注册）。

## 外壳扩展相关的检查

右键菜单能不能出来，取决于几件事，工具分别查：

```powershell
$bin = "C:\Users\Mocha\Desktop\VSProject\Auto7z Project\Auto7z_WPF\Auto7z_WPF\bin\Debug"
$csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$refs = @(
  "C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.7.2\PresentationFramework.dll",
  "C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.7.2\PresentationCore.dll",
  "C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.7.2\WindowsBase.dll",
  "C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.7.2\System.Xaml.dll")

function Build-Tool($name) {
  $a = @("/nologo", "/out:$bin\$name.exe")
  foreach ($r in $refs) { $a += "/r:$r" }
  $a += "cs\$name\$name.cs"
  & $csc @a
}

# 1) 注册表: 菜单挂载点、COM 类、Approved 列表
python check_reg.py

# 2) 组件能不能建出菜单, 菜单项文字对不对
Build-Tool "shellcheck"
& "$bin\shellcheck.exe" "$bin\Auto7zShell.dll" "{6C3F8A21-4B7D-4E5A-9A2C-3D710E5B8841}"

# 3) 作用范围: 哪些地方该出现菜单项
Build-Tool "scopeprobe"
& "$bin\scopeprobe.exe" "$bin\Auto7zShell.dll" "{6C3F8A21-4B7D-4E5A-9A2C-3D710E5B8841}" $env:TEMP

# 4) 完整链路: Initialize → InvokeCommand → 真的产出压缩包
Build-Tool "dataprobe"
& "$bin\dataprobe.exe" "$bin\Auto7zShell.dll" "{6C3F8A21-4B7D-4E5A-9A2C-3D710E5B8841}" "$env:TEMP\t.txt"

# 5) 菜单图标
Build-Tool "iconprobe"
& "$bin\iconprobe.exe" "$bin\Auto7zShell.dll" "{6C3F8A21-4B7D-4E5A-9A2C-3D710E5B8841}" "$env:TEMP\t.txt"
```

这些工具都要 `Auto7zShell.dll` 跟 `Auto7z.exe` 在同一个目录（`$bin`），菜单图标要从主程序取。

**作用范围的期望值**（scopeprobe）：

| 目标 | 菜单项数 |
|---|---|
| 普通文件 | 2 |
| 普通文件夹 | 2 |
| C 盘 / D 盘根目录 | 0 |
| 回收站里的项目 | 0 |
| 虚拟路径 | 0 |

## 回归覆盖范围

| 编号 | 内容 |
|---|---|
| T01 | 压缩格式 7z / zip / tar |
| T02 | 压缩等级 Store / Normal / Ultra（用可压缩文本比较） |
| T03 | 分卷 0 / 1 / 2 MB |
| T04 | MD5 校验文件（比对实际哈希） |
| T05 | 密码加密：正确密码可解、错误密码正确失败 |
| T06 | tar + zstd 两步流程（查中间 .tar 残留） |
| T07 | 解压往返 |
| T08 | 空文件夹（产物名不应是 .001） |
| T09 | 特殊字符文件名 |
| T10 | 深层嵌套目录 |
| T11 | 合并询问（命令行模式在控制台提示） |
| T12 | 输出路径：无效路径回退、有效路径写入 |
| T13 | 自动模式混合输入（压缩包 + 普通文件） |
| T14 | 配置废键清理 |
| T15 | 非法格式值是否给出警告 |
| T16 | 三语词典键数、重复、互相一致 |
| T17 | 日志分流、过期问题日志清理 |
| T18 | 损坏压缩包、空文件 |
| T19 | 非法分卷大小（负数 / 非数字 / 溢出） |
| T20 | 配置文件损坏后能否重建 |
| T21 | 7z 进程残留 |
| T22 | 便携模式：目录搬迁（勾选 / 取消） |

## shellproto 覆盖范围

| 编号 | 内容 |
|---|---|
| T1 | 共享内存传多个文件（`#映射名:字节数:事件名`） |
| T2 | 200 个文件 —— 命令行放不下的场景 |
| T3 | 模式开关 `-Z` / `-U` / `-A` |
| T4 | 直接给路径（拖到 exe 上） |
| T5 | 单实例：第二个实例交接后自己退出 |

## cmdline_test 覆盖范围

| 编号 | 内容 |
|---|---|
| T1 | `-h` 显示用法，退出码为 0，说明里要列全 `-A` / `-Z` / `-U` / `-r` / `-ur` / `-h` |
| T2 | 三种模式开关：`-Z` 与 `-A` 产出压缩包，`-U` 不产出 |
| T3 | 不带模式开关 → 按自动模式处理 |
| T4 | 拖到 exe 图标（等价于命令行传路径） |
| T5 | 多个文件一次传入 |
| T6 | `-r` / `-ur` 被识别成开关，不被当成文件路径 |

## 注意事项

- **脚本里的路径是写死的**。`regress.py`、`find_dead_keys.py`、`find_dead2.py`、
  `add_keys.py`、`add_portable_keys.py`、`add_tabindex.py`、`remove_delete_temp_keys.py`
  里都硬编码了旧的工程路径 `C:\Users\Mocha\Desktop\VSProject\Auto7z_WPF\Auto7z_WPF`，
  现在已经不存在了。这些脚本要么用 `--app` 之类的参数指定路径，要么把开头那行常量
  改成现在的 `C:\Users\Mocha\Desktop\VSProject\Auto7z Project\Auto7z_WPF\Auto7z_WPF`。
  `regress.py` 只有被测程序路径受 `--app` 影响，词典目录（`lang_dir`）仍然是写死的。
- **控制台输出是 GBK**。Python 里要用 `decode("gbk")` 解码，用 UTF-8 会全是乱码。
- **命令行模式不弹界面**：多文件压缩的「是否合并」改成在控制台里问，
  读不到输入时按「合并」处理，不会卡住。回归脚本用 `stdin=DEVNULL` 断开输入。
- **右键菜单注册要管理员权限**。COM 只认 `HKCR\CLSID`，写 `HKCU` 不生效。
  用 `Auto7z.exe -r` 注册、`-ur` 注销，权限不够会弹 UAC 提权。
- **Approved 列表必须写 HKLM**。写在 HKCU 的话资源管理器会静默禁用这个扩展，
  右键菜单完全不出现，而且没有任何错误提示。
- **菜单挂载点要写 `HKLM\Software\Classes`**，不能写 `HKEY_CLASSES_ROOT` 的 `*` 下面，
  那样写会被系统拒绝（错误 5）。
- **注册完要通知资源管理器**，不然它还拿着启动时缓存的列表。程序里已经调了
  `SHChangeNotify(SHCNE_ASSOCCHANGED)`；手工改注册表后要自己发这个通知。
- **`QueryContextMenu` 的返回值是加进去的菜单项个数**，不是 `S_OK`。
  返回 0 等于告诉资源管理器「没有菜单项」。
- **C++ 多重继承下 `QueryInterface` 要按接口分别返回指针**。
  `IContextMenu` 和 `IShellExtInit` 在对象里的偏移不同，
  一律返回 `IContextMenu` 的地址会让资源管理器调错函数、静默禁用扩展。
- 部署 dll 后如果菜单没出现，先重启一下资源管理器。
- 测试前先清掉 `%LOCALAPPDATA%\Auto7z\7-Zip` 里的假组件 ——
  曾经写过 stub 文件，会让 7z.exe 报「不支持的 16 位应用程序」。
  现在应用已能自愈（组件大小与资源不符会自动重新释放）。
- `regress.py` 的 `close_gracefully` 会先等程序自己结束；
  还在跑说明是界面模式，才发 WM_CLOSE 让它保存配置再退。

## 自动化做不到的部分

WPF 的 `AllowsTransparency` 窗口收不到合成的鼠标事件（AHK v1.1、Win32 `PostMessage`、
物理鼠标模拟、UIAutomation `Invoke` 都试过），UAC 提示框也点不了，
所以下面几项**必须人工点**：

1. **资源管理器右键菜单** —— 右键文件看是否出现「用 Auto7z 压缩 / 用 Auto7z 解压」
2. **多选** —— 选中 3 个文件点「用 Auto7z 压缩」，应只开一个进程，合成一个包
3. **UAC 提权** —— 跑 `Auto7z.exe -r` 是否弹出 UAC，确认后注册是否成功
4. **便携模式** —— 勾选便携模式，右键菜单应消失；取消后应恢复
5. **交互问答** —— 命令行模式读不到输入时默认合并，确认这符合预期
6. **单实例** —— Auto7z 开着时再右键，文件应交给已有窗口排队处理
