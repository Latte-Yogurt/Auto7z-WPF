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
[assembly: AssemblyVersion("2.2.0.0")]
[assembly: AssemblyFileVersion("2.2.0.0")]
[assembly: Guid("F5D3B688-37B2-4259-B9CE-C8B4B0E2E8CD")]
