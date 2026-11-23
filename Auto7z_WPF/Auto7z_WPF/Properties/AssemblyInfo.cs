using System.Reflection;
using System.Resources;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;

// 版本号必须是四段 2.1.0.0：AssemblyVersion 只认这种形式，
// 写成 2.1.0 会在编译期报错，而发行说明里那个 v2.1.0 只是显示用的短写法。
[assembly: AssemblyTitle("Auto7z_WPF")]
[assembly: AssemblyDescription("")]
[assembly: AssemblyConfiguration("")]
[assembly: AssemblyCompany("")]
[assembly: AssemblyProduct("Auto7z_WPF")]
[assembly: AssemblyCopyright("Copyright ©  2024 LatteYogurt , All rights reserved.")]
[assembly: AssemblyTrademark("")]
[assembly: AssemblyCulture("")]

// 关掉 ComVisible：本程序集不导出任何 COM 类型，
// 真正被资源管理器加载的是独立的 Auto7zShell.dll。
[assembly: ComVisible(false)]

// ThemeInfo 指向本程序集自己的资源字典，WPF 才能在找不到窗体资源时回收
// 到 UIResource.xaml 里的模板；写成 None 会让侧栏与标题栏退回系统默认外观。
[assembly: ThemeInfo(
    ResourceDictionaryLocation.None,
    ResourceDictionaryLocation.SourceAssembly
)]


// 2.1.1 → 2.2.0：功能性变化（四个 P0 缺陷修复 + 三个 P0 热修 + 便携判定重构 +
// 外壳扩展测试补全）。2.2.0 尚未发布，处在开发者内测阶段，热修直接并入本版，
// 不另立修订号 —— 未发布的版本没有向后兼容包袱，把缺陷记在同一个版本里更清楚。
//
// 2.2.0 → 2.3.0：本轮又把「配置废弃键清理失效」「外壳扩展 HKCU 路径劫持」
// 「目录背景右键永不出现」这几条功能性缺陷修掉，并重写了安装脚本，
// 属于功能变化，按惯例进位到次版本号。
//
// 2.3.0 → 2.3.1：修复「强制结束进程留下分卷残片」（崩溃残留清理），
// 移除无意义的文件夹空白处右键菜单与废弃键清理逻辑，属于功能变化。
//
// 2.3.1 → 2.4.0 / 2.4.0 → 2.5.0：这两版的改动都是「把静默错误变成可见错误」这条主线下的
// 功能性修复，按惯例各进一次次版本号。2.5.0 的内容见 Auto7z_ReleaseFix.md。
//
// 2.5.0 → 2.5.1：本轮只修交互缺陷，没有新增功能 —— Ctrl+S 是把「关窗时才保存」这条已有
// 能力的入口补上（SaveConfig 早就存在、只是按需调不到），回车确认与 Tab 顺序都是修补
// 既有的键盘行为。因此按修复进位到修订号。内容见 Auto7z_ReleaseFix_2.5.1.md。
[assembly: AssemblyVersion("2.5.1.0")]
[assembly: AssemblyFileVersion("2.5.1.0")]
[assembly: Guid("F5D3B688-37B2-4259-B9CE-C8B4B0E2E8CD")]
