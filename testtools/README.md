# Auto7z 测试工具

只做测试，不改产品代码。

## 一键跑测试

```powershell
cd "<Auto7z Project>\testtools"

python run_cs_probes.py        # 一键跑完 cs\ 下全部 C# 探针（推荐先跑这个）
python run_regress.py          # 一键回归测试
python run_system_test.py      # 一键系统测试
python splitvol_test.py        # 分卷压缩 / 分卷解压专项
python heuristic_probe.py      # 启发式探测（列可疑行为，不判失败）
python run_bench.py            # 一键性能基准（不判失败，只做前后对比）
```

### `run_cs_probes.py` —— C# 探针的统一入口

`cs\` 下那 15 个 exe 各自要一串参数（dll 路径、CLSID、样本文件、输出目录……），
逐个手敲既麻烦又容易传错。这个脚本自动定位被测程序、按各工具真正需要的参数调起来，
最后打印一张表：

```
 工具            结果       说明
--------------------------------------------------------------------------
 langcheck     [通过]     系统语言 → 界面语言的映射
 shellcheck    [通过]     组件自检：菜单项数与谓词名
 menuprobe     [通过]     点击槽位分派：压缩/解压 × 两种调用形态
 ...
--------------------------------------------------------------------------
 合计 15 项：通过/完成 15，失败 0，跳过 0
```

```powershell
python run_cs_probes.py                 # 默认用 bin\Debug
python run_cs_probes.py --config Release
python run_cs_probes.py --app <Auto7z.exe 完整路径>
python run_cs_probes.py --verbose       # 连每个工具的原始输出一起打印
```

退出码 0 = 全通过，1 = 有失败项，2 = 找不到被测程序（脚本会告诉你要先构建什么）。

**单独跑某一个 exe 也可以。** 不带参数时它们都会打印自己的用法并以退出码 2 结束，
不会抛 `IndexOutOfRangeException`：

```powershell
& "cs\shellcheck\shellcheck.exe"
# 用法: shellcheck.exe <Auto7zShell.dll> <CLSID> [样本文件]
#   组件自检：建类工厂、建菜单、读菜单项文字与谓词名。
```

前四个是测试套件，都支持 `--app <exe>` 指定被测程序、`--config Release` 换配置、
`--keep` 保留临时目录（调试用）。退出码 0 = 全过，1 = 有失败项 / 有可疑项，2 = 环境不满足。
`run_bench.py` 是性能基准，参数是 `--app` / `--baseline` / `--keep`，不判失败。

四个套件都带 **`--clear-config`**（开跑前清空配置，让产品从默认值起跑）与
**`--purge-portable`**（删掉 exe 旁的便携数据目录）。
之所以需要后者：只要 exe 旁边存在 `.config` 目录，产品就判定自己是便携版、
**完全无视** `%LOCALAPPDATA%` 那份配置 —— 测试改了半天其实改的是另一份。

### 配置是「链式」的：只在整条链跑完后还原一次

以前每个套件各自「开始时快照、结束时还原」。这个做法有个致命缺陷：**B 的快照取自
A 改完之后的状态** —— 只要有一个套件异常退出没还原，污染就被后一个套件当成"原始配置"
永久固化下来，越跑越偏。实测踩过：连跑几轮之后 `CompressionLevel` 从 `Ultra` 变成 `Store`、
`GenerateSHA256` 从 `True` 变成 `False`，而每个套件都声称"已还原"。

现在改成跨进程的链式会话（`config_guard.ConfigGuard.join_chain` / `leave_chain`）：

| 时机 | 行为 |
|---|---|
| 第一个套件进入 | 把**真正的原始配置**字节（base64）写进 `%TEMP%\Auto7zTest_config_chain.json` |
| 后续套件进入 | 只登记自己的 pid，**绝不覆盖**那份原始快照 |
| 某个套件退出 | 把自己从成员表里摘掉；表里还有人就不还原 |
| 最后一个退出 | 还原原始配置并删掉链文件 |

这样两个常见场景都正确：

- **嵌套**（`run_system_test.py` 内部以子进程调用 `splitvol_test.py`）：子进程退出时不还原，
  由父进程统一收尾。这条已专门验证。
- **并发**：多个套件同时跑时，只有真正最后离开的那个负责还原。

查看链状态：链文件存在说明还有套件在跑；不存在说明已收尾。

### 四个套件共用同一个互斥体

四个套件驱动的是**同一个单实例产品**，所以互斥体必须共用一个名字（`Auto7zTest_shared`）。
名字各不相同等于没有保护 —— 两个套件能同时拿到各自的锁，然后一起抢产品的单实例，
把任务互相交接过去。实测并行跑出过 23 项假失败。

第二个套件被拒绝时会明确打印「已有另一份 Auto7z 测试正在跑」并以退出码 2 结束。

**但共用互斥体带来一个必须处理的副作用：嵌套调用会死锁。**

`run_system_test.py` 会以子进程方式调用 `splitvol_test.py`。父进程持有互斥体时，
子进程去抢同名互斥体必然失败、直接退出 —— 分卷那项就永远跑不成，而且看起来像"环境不满足"。
所以父进程启动子进程时传一个环境变量声明已持有：

```python
subprocess.run([sys.executable, "splitvol_test.py", "--app", app],
               env=child_env_with_inherited_checkout())   # 设 AUTO7Z_TEST_CHECKOUT_INHERITED=1
```

子进程看到该变量就跳过抢锁（`claim_checkout()` 返回占位句柄 `0`，`release_checkout` 对它不做事）。
**这个标记只用于嵌套**，不要拿它绕过并行保护。

三种场景均已实测：

| 场景 | 期望 | 实测 |
|---|---|---|
| 串行（四套依次） | 全部可跑 | 全绿，配置未漂移 |
| 嵌套（父调子） | 子进程能跑 | 分卷项通过，未被误拒 |
| 并行（同时两个） | 第二个被拒 | 退出码 2 + 明确提示 |


- **回归**（`run_regress.py`）：主回归 T01–T22 + 静态检查 + 命名/region/行为/防护检查 + 词典一致性 + 注册契约 + 改名残留 + 删除守卫运行时验证 + 结果颜色契约运行时验证 + **本项目计算服务运行时验证** + 幽灵注册检查 + **死代码审查**（共 14 项）
- **系统**（`run_system_test.py`）：命令行模式、加密包密码交互、多文件 Y/N、分卷压缩/解压、外壳扩展、界面检查（共 18 项）

跑完会自动清理临时目录，删除带白名单校验（见下面的「临时目录与删除边界」）。

## 目录结构

```
testtools/
├── README.md              本文件
├── COVERAGE.md            覆盖维度台账（逐轮追加，见文末）
├── run_cs_probes.py       一键跑完 cs\ 下全部 C# 探针（15 项，带结果表）
├── run_regress.py         一键回归测试（14 项）
├── run_system_test.py     一键系统测试（18 项）
├── run_bench.py           一键性能基准（不判失败）
├── safe_ops.py            临时目录与安全删除（白名单校验）
├── config_guard.py        配置隔离：清空 / 还原真实配置、单实例闸门、便携配置检测
├── splitvol_test.py       分卷压缩 / 分卷解压专项（三种模式对分卷的处理，48 项）
├── heuristic_probe.py     启发式探测（找不符合文档契约的行为，30 项，不判失败只列可疑）
├── cs_check.py            静态检查：命名类型一致性、文档标签、路径边界、空 catch
├── namecheck.py           命名风格一致性：字段/属性/方法/局部变量各自风格
├── regioncheck.py         region 结构：配对、嵌套 ≤1、跨度 ≤150 行
├── behaviorcheck.py       行为约束断言（BC1–BC13）
├── guardcheck.py          破坏性操作防护检查（safe_ops 规则还在不在、拦不拦得住）
├── shellreg_check.py      外壳扩展注册/注销契约：Approved 两边必须指向同一个键
├── renamecheck.py         改名残留：旧名必须清零、新名定义与引用一致
├── regress.py             主回归脚本（T01–T22，22 项）
├── cmdline_test.py        命令行测试（T1–T7）
├── cmd_real_test.py       真实 cmd 语义测试（C1–C4）
├── check_reg.py           右键菜单注册状态检查
├── find_dead2.py          更全面的废键审查（含 C++ 导出函数）
├── deadcode.py            死代码审查（零引用成员 / 只写不读状态 / 废词典键 / 死枚举 / 无人调用的工具）
├── ui_probe.ps1           UI 自动化：主窗口进度、右键模式进度与取消
├── ui_feed_test.ps1       拖拽入窗（接口级，走 Debug 版 --feed）
└── cs/                    需要进程内跑的 C# 检查工具
    ├── build_all.py       编译下面所有工具（二进制放在各自目录里）
    ├── verify/            对话框按钮文字、居中、提示切换
    ├── uiwidth/           UI 文本自检：三语言下查溢出与遮挡
    ├── taborder/          Tab 顺序检查：静态 TabIndex + 运行时可聚焦性
    ├── portable/          便携模式：目录搬迁
    ├── bench/             性能基准：反射直调热点方法（B1–B9），机器可读输出
    ├── guardprobe/        删除守卫的运行时验证：反射直调守卫，拿真实路径试
    ├── resultprobe/       结果颜色契约的运行时验证：反射建 ViewModel、调 SetResult，验通知属性名与颜色依据
    ├── sha256probe/       本项目计算服务的运行时验证：反射直调 SHA256Service，验哈希/格式/无 BOM/递归/进度/取消
    ├── shellproto/        外壳扩展协议：共享内存传参、模式开关、单实例
    ├── shellcheck/        外壳扩展自检：建类工厂、建菜单、读菜单项文字
    ├── scopeprobe/        外壳扩展作用范围：哪些目标该出现菜单项
    ├── dataprobe/         完整链路：Initialize → InvokeCommand → 真的产出压缩包
    ├── menuprobe/         点击槽位分派：ansi / unicode-null 两种调用形态下的模式开关
    ├── iconprobe/         菜单图标：位图在不在、有没有内容
    └── langcheck/         系统语言 → 界面语言的映射
```

这 15 个 exe 都遵守同一条约定：**不带参数时打印用法并返回退出码 2**，不会抛
`IndexOutOfRangeException`。想一次性跑完并看到汇总表，用 `python run_cs_probes.py`；
想单独跑某一个，直接执行它的 exe 就会看到该填哪些参数。

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
| 便携模式测试切模式时注册/注销右键菜单 | 产品加了 `SkipShellMenuSync` 开关（**仅 Debug 构建**），测试置成 true 跳过 |
| `cmdline_test.py` T6 直接跑 `-r` / `-ur` | 改成在**没有 Auto7zShell.dll 的隔离副本**里跑 —— 产品找不到 dll 就不会提权 |

`regsvr32` 要提权写 HKLM，Windows 会弹「用户账户控制」让人点。
以前跑一次完整测试要点 2~3 次，现在 0 次。

真正需要提权的注册/注销是**产品功能**，不在测试范围内：
要用请自己以管理员身份跑 `Auto7z.exe -r` / `Auto7z.exe -ur`。

### 便携模式测试为什么要跳过注册同步

便携模式真正要验的是"数据目录有没有搬对"。注册表只是搬目录的副产物，
跟被测逻辑无关，却要提权、要弹 UAC。跳过之后行为只差"右键菜单没跟着切换"。

`Auto7zViewModel.SkipShellMenuSync` 就是给这件事留的开关（默认 false = 正常行为）。
**它包在 `#if DEBUG` 里，只在 Debug 构建中存在** —— 见下一节。

### 便携状态怎么判定（探针容易在这里做错）

便携与否**不是配置里的键**，而是磁盘事实：`程序目录\.config\` 存在即为便携。
旧版本写在 `Auto7z.json` 里的 `IsPortable` 已不再被读取；文件里残留该键不影响使用，也不需要清理。

`cs\portable\portable.cs` 因此有两条硬性要求，违反任何一条结论都不可信：

| 要求 | 为什么 |
|---|---|
| 便携根要按**入口程序集**（`portable.exe` 自己）算，不能按被测的 `Auto7z.exe` 位置算 | 产品用的是 `Assembly.GetEntryAssembly().Location`。两者位置不同（探针在 `testtools\cs\portable`，被测程序在 `bin\Debug`），按 `asmPath` 算会去检查另一个目录 —— 曾因此误报「切回后便携目录还在」 |
| 断言只看 `IsPortable`、`DataDirectory` 与目录是否存在 | 配置里已经没有这个键，断言它必然变成假失败 |

另外提醒：探针会**清空并重建 `%LOCALAPPDATA%\Auto7z`** 来准备夹具，所以别在它之后立刻跑别的测试 ——
7-Zip 组件会跟着没掉，表现为「找不到 7z 组件」。跑一次产品压缩即可让它自动重新释放。

---

## 重要：测试钩子只在 Debug 构建里

> 给以后维护本项目的人（和 AI）：
> **`Release` 产物里不存在任何测试开关。** 这不是疏漏，是有意为之。

### 规则

产品的测试基础设施一律包在 `#if DEBUG` 里，Release 构建把整段编掉。
目前有两处：

| 位置 | 作用 | Release 中的状态 |
|---|---|---|
| `Services/TestHooks.cs`（整文件）及 `App.xaml.cs` 里的调用点 | `--feed` 注入开关，走拖放之后的 `EnqueueFiles` 路径 | 不编译，类型不存在，命令行不识别 |
| `ViewModel/Auto7zViewModel.cs` 的 `SkipShellMenuSync`（属性 + 那处判断） | 便携模式测试跳过右键菜单注册（否则弹 UAC） | 属性不存在，判断编掉，永远走正常注册流程 |

已实测确认：反射在 Debug 产物里能查到 `SkipShellMenuSync` 与 `TestHooks`，
在 Release 产物里两者都返回 `null`。

### 对测试脚本的要求

1. **用 Debug 构建跑依赖测试钩子的项。** 默认 `--config Debug` 就是为此。
2. **探测钩子在不在，不要假定它一定在。** 拿不到就走"跳过"，不要判失败 ——
   Release 里没有它是正确的。
3. **`cs\portable\portable.cs` 的约定**：找不到 `SkipShellMenuSync` 时打印
   `### RELEASE_NO_TEST_HOOK` 并以**退出码 3** 结束；`regress.py` 的 T22 认这个码，
   报"本项跳过"而不是失败。

### 反射探针为什么不受影响

`guardprobe` / `resultprobe` / `sha256probe` / `bench` 都是**测试进程侧**反射加载产品程序集：

```csharp
var asm = Assembly.LoadFrom(asmPath);
var t = asm.GetType("Auto7z_WPF.Services.SHA256Service");
```

产品侧不需要 `InternalsVisibleTo`、不需要把 `private` 改成 `internal`、
不需要任何"为测试开的口子"。所以这些探针对 Release 同样有效 ——
它们测的是真实交付物里的真实方法。

### 判据

加任何测试便利设施时先问：**这条代码会出现在用户的 `Auto7zGUI.exe` 里吗？**
会，就必须包 `#if DEBUG`，或者改成测试侧反射实现。

## 跑回归测试

单跑主回归（不带一键包装）：

```powershell
cd "<项目根目录>\testtools"

python regress.py --app "..\Auto7z_WPF\Auto7z_WPF\bin\Debug\Auto7z.exe"
```

`regress.py` 的默认被测程序路径已失效（原因见文末「注意事项」），所以**必须**用 `--app` 指定：

```powershell
python regress.py --app "C:\path\to\Auto7z.exe"   # 指定被测程序
python regress.py --config Release                 # 配合同上使用
```

脚本会：

- 在 `%LOCALAPPDATA%\Auto7z\Auto7z.json` 上改配置（标准 JSON，用 Python 的 `json` 读写），**退出时自动还原**
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
| C7 | 文件被独占占用：必须 60 秒内返回（不得挂起），退出码为 1，且有说明性输出 |
| C2 | 多文件 `Y/N`：问了、有产物、进度文本在 `MARK` 之前 |
| C3 | 失败路径退出码必须非 0（退出码语义：0 成功 / 1 失败或取消 / 2 没跑） |
| C4 | 加密包：控制台问密码、错密码重试提示以冒号结尾 |

> **不要用 `.bat` 包一层跑这些用例。** 批处理里 cmd 会等 GUI 程序，
> 那样即使子系统改错也会「通过」。脚本里用的是 `cmd /s /c "… & echo MARK %TIME%"` 单行命令。

## UI 自动化（进度 / 取消）

| 脚本 | 覆盖 | 说明 |
|---|---|---|
| `ui_probe.ps1 -Scenario gui` | 主窗口进度文字 | 用 UI Automation 读 `MainText`，验证压缩过程中数值在变 |
| `ui_probe.ps1 -Scenario shellmenu` | 右键模式进度 + 取消 | 读进度条数值，再用窗口消息关窗，检查 7z 是否被结束、半成品目录是否被删 |
| `ui_feed_test.ps1` | 拖拽入窗（接口级） | 用 Debug 版 `--feed` 把路径交给主窗口，走投放后的 `EnqueueFiles`，再验证 7z 真的起来、进度文本在变 |

```powershell
powershell -ExecutionPolicy Bypass -File ui_probe.ps1 -Scenario gui
powershell -ExecutionPolicy Bypass -File ui_probe.ps1 -Scenario shellmenu
```

**拖拽为什么改成接口级**：主窗口是 `AllowsTransparency` 的无边框透明窗，合成鼠标拖拽进不去；
跨进程调窗口上的 `IDropTarget` 也拿不到（那个指针只在目标进程地址空间里有效）。
所以产品里留了一个 **仅 Debug 构建** 的注入开关 `--feed`，它走的是投放之后的同一条 `EnqueueFiles` 路径；
Release 构建里这段被 `#if DEBUG` 编译掉，最终产品中不存在这个开关。

## 性能基准

`run_bench.py` 跑 `cs\bench\bench.exe`，用反射直调被测程序里的热点方法。

| 编号 | 测什么 | 怎么读 |
|---|---|---|
| B1 | `SHA256Service.CalculateSHA256` 单文件 256MB | 跟 B2 对比：B1 ≈ B2 说明 SHA256 已到算法/IO 上限 |
| B2 | 裸 `SHA256.Create()` 流式 | 上限参照 |
| B3 | `SHA256Service.CalculateSHA256` 可压缩文本 | 接近真实压缩包场景 |
| B4 | `SevenZipService` 压缩 | 吞吐 |
| B5 | `SevenZipService` 解压 | 吞吐 |
| B6 | `ConfigService` 读 / 写 | per_op 毫秒 |
| B7 | `FileSystemHelper.GetSizeInBytes` 5000 文件目录 | 元数据查询开销 |
| B8 | `FileSystemHelper.GenerateSafeFolderName` | 20 万次/秒量级 |
| B9 | `Auto7zViewModel.SetPendingFiles`（拖入接收路径） | **界面线程上那一步**，越小越好 |

**B9 是关键**：拖入一个 5000 文件的目录时，界面线程只做存在性检查，
目录大小统计移到任务线程（`PendingFile.CreateLazy` / `EnsureSizes`）。
改造前这一步会把界面冻住（约 80ms），改造后约 0.04ms。

用法：

```powershell
python run_bench.py                                  # 跑一轮，存 bench/last.json
python run_bench.py --baseline bench/baseline.json   # 跟基线对比
python run_bench.py --app <Auto7z.exe>               # 指定被测程序
```

退出码 0 只代表跑完，**不设硬门槛** —— 性能基准用来观察趋势，不拿来判失败。
结果存在 `testtools\bench\last.json`（每次跑自动写），可以直接看。
要跟历史值比就自己留一份基线，再显式传进去：

```powershell
python run_bench.py --baseline bench\baseline.json
```

> `--baseline` 指到 `last.json` 自己是没有意义的：脚本先写 `last.json` 再读基线，
> 那样比的是本次结果本身，永远显示 `+0.0%`。


## 本项目的计算服务（SHA256）

`Auto7z_WPF\Auto7z_WPF\Services\SHA256Service.cs` 是本项目所有 SHA256 调用的唯一入口
（`SevenZipService.GenerateSHA256` 转手交给它）。调用方不要再内联第二份哈希循环。

源码层面的几条关键约定（改动时别只改一处）：

| 约定 | 含义 |
|---|---|
| `BufferSize = 1024 * 1024` | 1MB 缓冲区，顺序扫描大文件时明显快于小缓冲 |
| `UiRefreshMs = 100` | 进度回调节流 100ms |
| `FileShare.ReadWrite` | 允许读被别的进程占用的文件 |
| `FileOptions.SequentialScan` | 顺序扫描 |
| `"{0} *{1}"` | 输出行格式 `hash *filename` |
| `new UTF8Encoding(false)` | 写 `.sha256` 用 UTF-8 **不带 BOM**（BOM 会让不少校验解析软件把首行哈希当成无效内容） |

静态约定证明不了**跑起来算得对**，所以另有一份运行时验证（回归第 12 项）：

```powershell
python cs\build_all.py sha256probe     # 改了探针才需要重编
```

`cs\sha256probe` 用反射直调产品程序集里的真实方法：

| 断言 | 内容 |
|---|---|
| 单文件 | 返回一行、行格式 `hash *filename`、哈希等于独立计算值 |
| 多文件 | 条数、逐行哈希 |
| 目录 | 递归展开；`CalculateSHA256ToFile` 给目录内每个文件各写一份 `.sha256` |
| 编码 | `.sha256` 是 UTF-8 且**不带 BOM** |
| 坏文件 | 读不到的文件跳过，其余照常产出（不是整体失败） |
| 进度 | 0–100 单调不减，结束时到达 100 |
| 取消 | 已取消的令牌立即中止并抛 `OperationCanceledException` |

## 空 catch 规则：可以吞，但必须写明为什么

`cs_check.py` 会把**空 catch**（只有注释、没有语句的 catch 块）分成两档：

| 形态 | 处理 |
|---|---|
| `catch { /* 删不掉不影响结果 */ }` | 已说明 → 登记，不判失败 |
| `catch { }` | 未说明 → **判失败** |

它做的是**括号配对**，`catch` 与 `}` 分行、中间只有注释的写法也照样查得出来。
说明必须写在**花括号内部**——写在 `catch` 上方不算数。

规则的含义是「异常可以吞，但必须让读代码的人看到你为什么吞」。
工具只能保证**有说明**，判断不了说明是否站得住 —— 语义仍靠评审。

跑法：

```powershell
python cs_check.py "<Auto7z_WPF源码目录>"      # 退出码 1 = 有未说明的空 catch
```

报告里 `TOTAL PROBLEMS` 只统计**未说明**的；已说明的另列在
`--- 已登记（不计失败）---` 下，末尾给 `REGISTERED: N` 计数，
这样「存量说明被删掉」也能被看出来。


## 外壳扩展相关的检查

右键菜单能不能出来，取决于几件事，工具分别查。

**`Auto7zShell.dll` 不在产物目录时，`run_system_test.py` 会自己构建并部署**（见 `ensure_shell_dll`）：
先调 `buildtool.bat` 编出 `buildtool.exe`，再用它编组件（自动探测 VS 2022 的 `vcvars64.bat`），
最后把 `Auto7zShell\build\Auto7zShell.dll` 拷到主程序旁边。

这条能力是补上的，不是本来就有的。原先的写法是「找不到 DLL 就跳过」，
结果菜单项、作用范围、图标、完整链路这四项**长期没有真正执行过** ——
外壳扩展就在工程里，构建它只要一条命令，跳过反而给回归留了盲区。
实测：补齐之后，回归里那项「系统里无右键菜单残留注册」也随之从失败转为通过，
因为注销动作终于有了真实的 DLL 可调。

工具已经编译成二进制放在各自目录里（`cs\shellcheck\shellcheck.exe`），
不用现编。要重新编译就跑 `python cs\build_all.py`。

```powershell
$bin = "..\Auto7z_WPF\Auto7z_WPF\bin\Debug"
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

# 6) 点击槽位分派: 点压缩必须 -z, 点解压必须 -uz
& "cs\menuprobe\menuprobe.exe" --style ansi         "$bin\Auto7zShell.dll" $clsid extract "$env:TEMP\t.txt"
& "cs\menuprobe\menuprobe.exe" --style unicode-null "$bin\Auto7zShell.dll" $clsid extract "$env:TEMP\t.txt"
```

`menuprobe` 末行固定输出 `MODE=<开关>`，便于脚本断言。

**为什么需要两种 `--style`**：资源管理器不保证 `lpVerbW` 有值。它可能置了
`CMIC_MASK_UNICODE`，却只填 ANSI 的 `lpVerb`。组件若不特判 `lpVerbW == NULL`，
`LOWORD(NULL)` 会等于 0，被当成「压缩」槽 —— 于是**点解压却执行压缩**，
而 `InvokeCommand` 照样返回 `S_OK`，日志与子进程全都正常。这种形态 `dataprobe` 测不到，
因为它只构造 `fMask = 0`。

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
| 静态检查（空 catch 必须写明原因） | `cs_check.py` | 命名/类型不一致、文档标签、路径边界、空 catch。**未说明**的空 catch 判失败；已说明的登记计数（会随代码增减，以报告里的 `REGISTERED: N` 为准） |
| 命名风格一致 | `namecheck.py` | 字段/常量/属性/方法/局部变量各自的风格，**判失败** |
| region 结构一致 | `regioncheck.py` | 配对、嵌套 ≤1、跨度 ≤150 行 |
| 行为约束成立 | `behaviorcheck.py` | BC1–BC13，13 条关键行为断言 |
| 破坏性操作防护完好 | `guardcheck.py` | `safe_ops.py` 的四条规则还在不在、拿真实危险输入实测拦不拦得住 |
| 三语词典键集合一致 | `find_dead2.py` | 各语言键数相等且无「多出」的键 |
| 外壳扩展注册/注销契约 | `shellreg_check.py` | 注册写 Approved 的 hive 与注销删的必须是**同一个**（B02 的回归保护） |
| 改名无残留且引用一致 | `renamecheck.py` | 旧名清零、新名在 XAML 有定义且被引用、控件名不带错的类型词（B04 的回归保护） |
| 递归删除守卫运行时验证 | `cs\guardprobe` | **反射直调**守卫方法，拿 12 条真实路径（数据目录本身/子目录应放行；根目录/无关路径/空串/null/`..` 越界应拦住）验证它真的拦得住 —— 守卫「写了但不生效」只有这条能查出来 |
| 结果颜色契约运行时验证 | `cs\resultprobe` | **反射建** `Auto7zViewModel`、调 `SetResult`，在 `PropertyChanged` 回调里查：通知的属性名是不是 `ResultText`（`SetProperty` 靠 `[CallerMemberName]`，从方法里调用会把名字写成方法名）、通知到来时 `IsResultFailed` 是不是已经是新值（决定红字/绿字）、文本没变时是不是照样通知。静态检查报不出这三点 |
| 本项目计算服务运行时验证 | `cs\sha256probe` | **反射直调** `SHA256Service`：哈希对不对、行格式是不是 `hash *name`、`.sha256` 带不带 BOM（应为不带）、目录递不递归、坏文件跳不跳过、进度单不单调、取消响不响应 |
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
| T04 | SHA256 校验文件（比对实际哈希） |
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
| T3 | 模式开关 `-z` / `-uz` / `-a` |
| T4 | 直接给路径（不带共享内存标记） |
| T5 | 单实例：第二个实例交接后自己退出 |

## cmdline_test 覆盖范围

| 编号 | 内容 |
|---|---|
| T1 | `-h` 显示用法，退出码为 0，说明里要列全 `-a` / `-z` / `-uz` / `-r` / `-ur` / `-h` |
| T2 | 三种模式开关：`-z` 与 `-a` 产出压缩包，`-uz` 不产出 |
| T3 | 不带模式开关 → 按自动模式处理 |
| T4 | 拖到 exe 图标（等价于命令行传路径） |
| T5 | 多个文件一次传入 |
| T6 | `-r` / `-ur` 被识别成开关，不被当成文件路径（**不真的执行注册**，在隔离副本里跑） |
| T7 | 未知开关 `-X` 必须报错、退出码 2、且不产出压缩包（原先产品会静默忽略并返回 0） |

## 注意事项

- **部分脚本里的路径是写死的**。`regress.py` 开头还写着一个**旧目录结构**的
  `PROJECT` 常量（指向一个已经不存在的 `Auto7z_WPF\Auto7z_WPF`，注意没有 `\ Project` 段）：
  - `regress.py` 用 `--app` 指定被测程序即可，词典目录会从被测程序位置反推；
  - `find_dead2.py`、`cs_check.py`、`namecheck.py`、`regioncheck.py`、`behaviorcheck.py`、`shellreg_check.py`、`renamecheck.py` 都可以传路径参数，默认指向当前工程。
- **控制台输出的编码跟着控制台代码页走**，不固定是 GBK：
  父 cmd 默认 936(GBK)，用了 `chcp 65001` 就是 UTF-8。
  解码要两种都试（见 `run_system_test.py` 里的 `decode_console`），
  写死一种会在另一种环境下全是乱码。
- **入口分成两个 exe**：`Auto7z.exe` 只做命令行（不带参数时打印用法并以退出码 2 结束），
  `Auto7zGUI.exe` 只做图形（双击 / 开始菜单 / 右键 / 拖拽）。
  `run_regress.py` 与 `shellproto` 已按入口选 exe：需要「启动一次让它重建配置」的用例走图形入口。

  **但带开关参数时两个入口走同一条命令行管道**：图形入口会先 `AttachConsole` 借到父控制台，
  再执行与命令行入口完全相同的 `RunConsolePipeline`。所以测开关行为时两个 exe 可以直接互换——
  这正是「命令行语义只有一处实现」的验证方式（见 `heuristic_probe.py` 里对 `-X` 的双入口对照）。
  借不到控制台时（双击启动）字面输出会落空，但退出码依然正确。
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
- 测试前先清掉 `%LOCALAPPDATA%\Auto7z\7-Zip` 里的假组件：
  假组件会让 7z.exe 报「不支持的 16 位应用程序」。
  应用已能自愈（组件大小与资源不符会自动重新释放），但测试拿到的是失败结论。
- **7-Zip 组件会被别的测试清空**：回归里的便携模式测试会清掉数据目录，
  组件跟着一起没掉。`splitvol_test.py` 现在会先跑一次无足轻重的压缩让它自行重新释放
  （缺失时你会在输出里看到「组件缺失，先跑一次任务」），不再直接报错退出。
  其它脚本如果也依赖组件，请照这个模式加一次预热，别让人误判成产品缺陷。
- `regress.py` 的 `close_gracefully` 会先等程序自己结束；
  还在跑说明是界面模式，才发 WM_CLOSE 让它保存配置再退。

## 自动化做不到的部分

物理鼠标模拟、UIAutomation `Invoke` 都试过，进不去；UAC 提示框也点不了，
所以下面几项**必须人工点**：

1. **资源管理器右键菜单** —— 右键文件看是否出现「用 Auto7z 压缩 / 用 Auto7z 解压」
2. **多选** —— 选中 3 个文件点「用 Auto7z 压缩」，应只开一个进程，合成一个包
3. **UAC 提权** —— 跑 `Auto7z.exe -r` 是否弹出 UAC，确认后注册是否成功
4. **便携模式** —— 勾选便携模式，数据目录应搬到程序旁的 `.config\Auto7z`，右键菜单应消失；取消后应搬回并恢复注册，且 `.config` 被删除（否则下次启动会被判回便携）
5. **交互问答** —— 命令行模式读不到输入时默认合并，确认这符合预期
6. **单实例** —— Auto7z 开着时再右键，文件应交给已有窗口排队处理
7. **右键菜单模式不弹主窗口** —— 右键压缩时只应出现小进度窗口，不应冒出主窗口
8. **命令行模式不弹窗** —— 命令行下失败时应只在控制台出现一行文字，不应弹出错误窗体
9. **自动排队（连续右键）** —— 任务运行中再右键一次，第二批应在第一个任务结束后自动接着跑
