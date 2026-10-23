# Auto7z 测试工具

只做测试，不改产品代码。

## 一键跑测试

```powershell
cd "C:\Users\Mocha\Desktop\VSProject\Auto7z Project\testtools"

python run_regress.py         # 一键回归测试
python run_system_test.py     # 一键系统测试
```

两个脚本都支持 `--app <exe>` 指定被测程序、`--config Release` 换配置、
`--keep` 保留临时目录（调试用）。退出码 0 = 全过，1 = 有失败项，2 = 环境不满足。

- **回归**（`run_regress.py`）：主回归 T01–T22 + 静态检查 + 命名/region/行为/防护检查 + 词典一致性 + 注册契约 + 改名残留 + 删除守卫运行时验证 + 幽灵注册检查（共 11 项）
- **系统**（`run_system_test.py`）：命令行模式、加密包密码交互、多文件 Y/N、外壳扩展、界面检查（共 15 项）

跑完会自动清理临时目录，删除带白名单校验（见下面的「临时目录与删除边界」）。

## 目录结构

```
testtools/
├── README.md              本文件
├── run_regress.py         一键回归测试（11 项）
├── run_system_test.py     一键系统测试（15 项）
├── safe_ops.py            临时目录与安全删除（白名单校验）
├── cs_check.py            静态检查：命名类型一致性、文档标签、路径边界、死分支
├── namecheck.py           命名风格一致性：字段/属性/方法/局部变量各自风格
├── regioncheck.py         region 结构：配对、嵌套 ≤1、跨度 ≤150 行
├── behaviorcheck.py       行为约束断言（BC1–BC10）
├── guardcheck.py          破坏性操作防护检查（safe_ops 规则还在不在、拦不拦得住）
├── shellreg_check.py      外壳扩展注册/注销契约：Approved 两边必须指向同一个键
├── renamecheck.py         改名残留：旧名必须清零、新名定义与引用一致
├── regress.py             主回归脚本（T01–T22，22 项）
├── cmdline_test.py        命令行测试（T1–T6）
├── cmd_real_test.py       真实 cmd 语义测试（#13 回归保护，C1–C4）
├── check_reg.py           右键菜单注册状态检查
├── diff_reg.py            把 7-Zip 与 Auto7z 的注册表结构列出来对比
├── find_dead_keys.py      查词典与配置里的废键、没人调用的方法
├── find_dead2.py          更全面的废键审查（含 C++ 导出函数）
├── add_keys.py            往三语词典批量加键
├── add_portable_keys.py   追加便携模式相关的词典键
├── add_tabindex.py        给主窗口控件按视觉顺序加 TabIndex
├── remove_delete_temp_keys.py  移除已废弃的词典键
├── COVERAGE.md            覆盖维度台账（D01 起，逐轮追加不改写历史行；测试资产，可能随轮次归档移动）
└── cs/                    需要进程内跑的 C# 检查工具
    ├── build_all.py       编译下面所有工具（二进制放在各自目录里）
    ├── verify/            对话框按钮文字、居中、提示切换
    ├── uiwidth/           UI 文本自检：三语言下查溢出与遮挡
    ├── taborder/          Tab 顺序检查：静态 TabIndex + 运行时可聚焦性
    ├── portable/          便携模式：目录搬迁
    ├── guardprobe/        删除守卫的运行时验证：反射直调守卫，拿真实路径试
    ├── shellproto/        外壳扩展协议：共享内存传参、模式开关、单实例
    ├── shellcheck/        外壳扩展自检：建类工厂、建菜单、读菜单项文字
    ├── scopeprobe/        外壳扩展作用范围：哪些目标该出现菜单项
    ├── dataprobe/         完整链路：Initialize → InvokeCommand → 真的产出压缩包
    ├── iconprobe/         菜单图标：位图在不在、有没有内容
    ├── measure2/          文本宽度测量
    └── langcheck/         系统语言 → 界面语言的映射
```

## 编译 C# 检查工具

二进制放在**各自的目录里**（`cs\verify\verify.exe` 这种），不往别处扔：

```powershell
python cs\build_all.py              # 编译全部
python cs\build_all.py shellcheck   # 只编译指定的
python cs\build_all.py --clean      # 先删旧的再编译
```

一键测试脚本发现工具没编译时会自动调它。

### 命名风格检查

```powershell
python namecheck.py                          # 查被测程序的源码
python namecheck.py "<源码目录>"              # 查任意一份（变异测试用）
```

规则（按工程实测多数派定的）：

| 成员 | 风格 |
|---|---|
| 私有实例字段 | `_camelCase` |
| 私有静态字段（含 `static readonly`） | `_camelCase` |
| 私有常量 `const` | PascalCase |
| 属性（含 `=>` 表达式体） | PascalCase |
| 方法 | PascalCase |
| 局部变量 | camelCase |

退出码 0 = 全部一致，1 = 有不一致（会逐条列出文件、成员、当前名、期望风格）。
**注意**：`private static string X => ...` 是属性不是字段，按 PascalCase 算。

## 临时目录与删除边界

这个项目出过一次 P0 事故：测试脚本传参错误 + 删除边界模糊，
把测试工具的源码全删了，测试全线崩溃。

所以删除一律走 `safe_ops.py` 的白名单校验，四条硬性要求：

1. 目标必须在受控根目录之内（默认 `%TEMP%`）
2. 目标路径里必须含标记串 `Auto7zTest`
3. 不能是受控根本身
4. 目录里如果有源码类文件（`.cs`/`.py`/`.xaml`/`.csproj`…）直接拒绝

任何一条不满足就报错拒绝，不删。临时目录统一在**所有测试跑完之后**清理，
不边测边清 —— 中途清理一旦边界判断出错，删掉的是还在用的目录。

**变异测试的源码副本**也走这套白名单。副本里含 `.cs`/`.xaml` 源文件，
规则 4 会拒绝删除 —— 所以清理**前**要先移除副本里的源码类文件
（或改用人工确认），再调 `safe_ops` 删目录。
不要为了绕过规则把 `safe_ops.py` 的源码扩展名清单改掉 ——
`guardcheck.py` 会盯这条规则，改掉它等于拆掉护栏。

## 右键菜单注册

测试跑完的程序不是要投产的那一份。**收尾一律注销**右键菜单，
不在系统里留指向测试产物的注册（那才是真正的"幽灵注册"）。

### 跑测试会不会弹 UAC

**正常情况下不会。** UAC 只在"要写 HKLM"时才会弹，而测试已经绕开了所有这类动作：

| 原本会弹 UAC 的地方 | 现在怎么处理 |
|---|---|
| `regress.py` 收尾无条件 `Auto7z.exe -ur` | 改成**先查再动**：系统里没有注册就什么都不做 |
| 便携模式测试切模式时注册/注销右键菜单 | 产品加了 `SkipShellMenuSync` 开关，测试置成 true 跳过 |
| `cmdline_test.py` T6 直接跑 `-r` / `-ur` | 改成在**没有 Auto7zShell.dll 的隔离副本**里跑 —— 产品找不到 dll 就不会提权 |

`regsvr32` 要提权写 HKLM，Windows 会弹「用户账户控制」让人点。
以前跑一次完整测试要点 2~3 次，现在 0 次。

真正需要提权的注册/注销是**产品功能**，不在测试范围内：
要用请自己以管理员身份跑 `Auto7z.exe -r` / `Auto7z.exe -ur`。

### 便携模式测试为什么要跳过注册同步

便携模式真正要验的是"数据目录有没有搬对"。注册表只是搬目录的副产物，
跟被测逻辑无关，却要提权、要弹 UAC。跳过之后行为只差"右键菜单没跟着切换"。

`Auto7zViewModel.SkipShellMenuSync` 就是给这件事留的开关（默认 false = 正常行为）。

## 跑回归测试

单跑主回归（不带一键包装）：

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
- 报告写到 `regress/report.txt`（留在工程里方便查）；
  **测试产物**写到受控临时目录，跑完统一清
- 每项测试前清掉残留的 Auto7z 进程（单实例机制下残留会干扰后面的测试）
- **结束时检查右键菜单注册状态**：有残留才注销，没有就什么都不做
  （注销要提权写 HKLM，会弹 UAC；没有残留就不该打扰用户）

回归脚本还会顺带编译 `cs\portable\portable.cs` 跑便携模式测试，需要 `csc.exe`；没有就跳过那一项。

## 跑命令行测试

```powershell
python cmdline_test.py "C:\path\to\Auto7z.exe" "C:\temp\cmdline_test"
```

覆盖：`-h` 用法说明、三种模式开关、不带模式开关走自动模式、拖到 exe 图标（命令行传路径）、多文件一次传入、`-r` / `-ur` 参数识别。

> `-r` / `-ur` **不真的执行注册** —— 那要提权写 HKLM，会弹 UAC 让人点。
> 测试把它们放在一个**没有 Auto7zShell.dll 的隔离副本**里跑：
> 产品找不到 dll 就不会提权，参数解析照常验证。

## 跑真实 cmd 测试

`cmdline_test.py` 用 `subprocess` 直接起 exe，天然会等它结束 —— **测不出** cmd 自己的等待语义
（#13 的症状正是「cmd 立刻还回提示符」）。这一项单独由 `cmd_real_test.py` 覆盖：

```powershell
python cmd_real_test.py              # 默认测 bin\Debug\Auto7z.exe
python cmd_real_test.py --app "..\..\Auto7z.exe"
```

| 用例 | 内容 |
|---|---|
| C1 | `-h`：有用法输出；`MARK` 排在用法之后（说明 cmd 等到了输出结束） |
| C2 | 多文件 `Y/N`：问了、有产物、进度文本在 `MARK` 之前 |
| C3 | 失败路径退出码必须非 0（对应 releasefix #3） |
| C4 | 加密包：控制台问密码、错密码重试提示以冒号结尾 |

> **不要用 `.bat` 包一层跑这些用例。** 批处理里 cmd 会等 GUI 程序，
> 那样即使子系统改错也会「通过」。脚本里用的是 `cmd /s /c "… & echo MARK %TIME%"` 单行命令。

## 外壳扩展相关的检查

右键菜单能不能出来，取决于几件事，工具分别查。

工具已经编译成二进制放在各自目录里（`cs\shellcheck\shellcheck.exe`），
不用现编。要重新编译就跑 `python cs\build_all.py`。

```powershell
$bin = "C:\Users\Mocha\Desktop\VSProject\Auto7z Project\Auto7z_WPF\Auto7z_WPF\bin\Debug"
$clsid = "{6C3F8A21-4B7D-4E5A-9A2C-3D710E5B8841}"

# 1) 注册表: 菜单挂载点、COM 类、Approved 列表
python check_reg.py

# 2) 组件能不能建出菜单, 菜单项文字对不对
& "cs\shellcheck\shellcheck.exe" "$bin\Auto7zShell.dll" $clsid "$env:TEMP\t.txt"

# 3) 作用范围: 哪些地方该出现菜单项
& "cs\scopeprobe\scopeprobe.exe" "$bin\Auto7zShell.dll" $clsid $env:TEMP

# 4) 完整链路: Initialize → InvokeCommand → 真的产出压缩包
& "cs\dataprobe\dataprobe.exe" "$bin\Auto7zShell.dll" $clsid "$env:TEMP\t.txt"

# 5) 菜单图标
& "cs\iconprobe\iconprobe.exe" "$bin\Auto7zShell.dll" $clsid "$env:TEMP\t.txt"
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

一键回归除了主回归 T01–T22，还跑下面这些项：

| 项 | 工具 | 查什么 |
|---|---|---|
| 静态检查（已知问题计数） | `cs_check.py` | 命名/类型不一致、死分支、文档标签、路径边界。报出来的是**已知现存问题**（8 处空 catch），不判失败 |
| 命名风格一致 | `namecheck.py` | 字段/常量/属性/方法/局部变量各自的风格，**判失败** |
| region 结构一致 | `regioncheck.py` | 配对、嵌套 ≤1、跨度 ≤150 行 |
| 行为约束成立 | `behaviorcheck.py` | BC1–BC10，10 条关键行为断言 |
| 破坏性操作防护完好 | `guardcheck.py` | `safe_ops.py` 的四条规则还在不在、拿真实危险输入实测拦不拦得住 |
| 三语词典键集合一致 | `find_dead2.py` | 各语言键数相等且无「多出」的键 |
| 外壳扩展注册/注销契约 | `shellreg_check.py` | 注册写 Approved 的 hive 与注销删的必须是**同一个**（B02 的回归保护） |
| 改名无残留且引用一致 | `renamecheck.py` | 旧名清零、新名在 XAML 有定义且被引用、控件名不带错的类型词（B04 的回归保护） |
| 递归删除守卫运行时验证 | `cs\guardprobe` | **反射直调**守卫方法，拿 12 条真实路径（数据目录本身/子目录应放行；根目录/无关路径/空串/null/`..` 越界应拦住）验证它真的拦得住 —— 守卫「写了但不生效」只有这条能查出来 |
| 系统里无右键菜单残留注册 | 注册表查询 | 测试不往系统里留注册 |

静态检查（`cs_check.py ⑤`）只看删除点前有没有写边界校验的**关键字**，
守卫被改成「永远放行」或「永远拒绝」时它**报不出来** ——
这就是为什么还要有 `guardprobe` 这个运行时探针。

主回归编号：

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
| T4 | 直接给路径（不带共享内存标记） |
| T5 | 单实例：第二个实例交接后自己退出 |

## cmdline_test 覆盖范围

| 编号 | 内容 |
|---|---|
| T1 | `-h` 显示用法，退出码为 0，说明里要列全 `-A` / `-Z` / `-U` / `-r` / `-ur` / `-h` |
| T2 | 三种模式开关：`-Z` 与 `-A` 产出压缩包，`-U` 不产出 |
| T3 | 不带模式开关 → 按自动模式处理 |
| T4 | 拖到 exe 图标（等价于命令行传路径） |
| T5 | 多个文件一次传入 |
| T6 | `-r` / `-ur` 被识别成开关，不被当成文件路径（**不真的执行注册**，在隔离副本里跑） |

## 注意事项

- **部分脚本里的路径是写死的**。`regress.py`、`find_dead_keys.py`、
  `add_keys.py`、`add_portable_keys.py`、`add_tabindex.py`、`remove_delete_temp_keys.py`
  里还硬编码着旧工程路径 `C:\Users\Mocha\Desktop\VSProject\Auto7z_WPF\Auto7z_WPF`（**注意没有 `\ Project` 段**），该目录已不存在：
  - `regress.py` 用 `--app` 指定被测程序即可，词典目录会从被测程序位置反推；
  - `find_dead2.py`、`cs_check.py`、`namecheck.py`、`regioncheck.py`、`behaviorcheck.py`、`shellreg_check.py`、`renamecheck.py` 都可以传路径参数，默认指向当前工程；
  - `add_*`/`remove_*` 这几个是**一次性维护脚本**（往词典加键、删废弃键），现在用不到；真要用先把开头那行常量改成 `C:\Users\Mocha\Desktop\VSProject\Auto7z Project\Auto7z_WPF\Auto7z_WPF`。
- **控制台输出的编码跟着控制台代码页走**，不固定是 GBK：
  父 cmd 默认 936(GBK)，用了 `chcp 65001` 就是 UTF-8。
  解码要两种都试（见 `run_system_test.py` 里的 `decode_console`），
  写死一种会在另一种环境下全是乱码。
- **入口分成两个 exe**：`Auto7z.exe` 只做命令行（不带参数时打印用法并以退出码 2 结束），
  `Auto7zGUI.exe` 只做图形（双击 / 开始菜单 / 右键 / 拖拽）。
  `run_regress.py` 与 `shellproto` 已按入口选 exe：需要「启动一次让它重建配置」的用例走图形入口。
- **命令行模式不弹界面**：多文件压缩的「是否合并」改成在控制台里问，
  读不到输入时按「合并」处理，不会卡住。回归脚本用 `stdin=DEVNULL` 断开输入。
  同理，命令行模式**失败时也只往控制台写一行文字，不弹窗**。
- **右键菜单注册要管理员权限**。COM 只认 `HKCR\CLSID`，写 `HKCU` 不生效。
  用 `Auto7z.exe -r` 注册、`-ur` 注销，权限不够会弹 UAC 提权。
  测试**不碰**这两个动作（见上面「跑测试会不会弹 UAC」）。
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
7. **右键菜单模式不弹主窗口** —— 右键压缩时只应出现小进度窗口，不应冒出主窗口
8. **命令行模式不弹窗** —— 命令行下失败时应只在控制台出现一行文字，不应弹出错误窗体
