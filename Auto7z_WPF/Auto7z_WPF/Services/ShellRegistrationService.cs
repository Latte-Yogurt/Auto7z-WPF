using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Windows;

namespace Auto7z_WPF.Services
{
    /// <summary>
    /// 右键菜单的注册与注销入口，实际注册动作由 Auto7zShell.dll 里的 DllRegisterServer 完成。
    /// </summary>
    public class ShellRegistrationService
    {
        #region 常量与导出函数签名
        private const string ShellDllName = "Auto7zShell.dll";

        private const string SetMainExePathName = "DllSetMainExePath";
        private const string RemoveMainExePathName = "DllRemoveMainExePath";
        private const string RegisterServerName = "DllRegisterServer";
        private const string UnregisterServerName = "DllUnregisterServer";

        private const string RegSvr32Name = "regsvr32.exe";

        private const string SetLanguageName = "DllSetLanguage";


        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int RegisterDelegate();

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int SetPathDelegate([MarshalAs(UnmanagedType.LPWStr)] string path);

        private ShellRegistrationService()
        {
        }
        #endregion

        #region 注册与注销入口
        /// <summary>登记右键菜单，未提权时自行拉起提权进程。</summary>
        /// <returns>注册是否成功。</returns>
        public static bool RegisterWithElevation()
        {
            string dllPath = GetShellDllPath();
            if (dllPath == null)
            {
                WriteShellDllMissing();
                return false;
            }

            if (IsAdministrator())
            {
                bool ok = RegisterNow(dllPath);
                if (ok) NotifyShellChanged();
                return ok;
            }

            // 未提权时在本进程内调用只是白跑一趟：DllRegisterServer 里写 HKLM 的每一步都会被拒，
            // 但它照样返回 S_OK，看起来像注册成功。所以必须换成提权进程执行。
            bool registered = RunElevated(dllPath, true);
            if (registered) NotifyShellChanged();
            return registered;
        }

        /// <summary>撤销右键菜单；找不到 Auto7zShell.dll 时按已注销处理。</summary>
        /// <returns>注销是否成功。</returns>
        public static bool UnregisterWithElevation()
        {
            string dllPath = GetShellDllPath();
            if (dllPath == null) return true;

            if (IsAdministrator())
            {
                bool ok = UnregisterNow(dllPath);
                NotifyShellChanged();
                return ok;
            }

            bool unregistered = RunElevated(dllPath, false);
            NotifyShellChanged();
            return unregistered;
        }

        private static bool RunElevated(string dllPath, bool register)
        {
            try
            {
                string system = Environment.GetFolderPath(Environment.SpecialFolder.System);
                string tool = Path.Combine(system, RegSvr32Name);
                if (!File.Exists(tool)) return false;

                string options = register ? "/s " : "/u /s ";
                ProcessStartInfo startInfo = new ProcessStartInfo(tool, options + "\"" + dllPath + "\"")
                {
                    UseShellExecute = true,
                    Verb = "runas"
                };

                using (Process process = Process.Start(startInfo))
                {
                    if (process == null) return false;
                    process.WaitForExit(60000);
                    return process.ExitCode == 0;
                }
            }
            catch (Exception)
            {
                // 用户在 UAC 提示上点了取消会抛 Win32Exception，这里按注册失败上报即可。
                return false;
            }
        }

        /// <summary>把界面语言写进注册表，右键菜单据此显示对应语言的文字。</summary>
        /// <param name="language">语言代码，例如 zh-CN。</param>
        public static void SetLanguage(string language)
        {
            if (string.IsNullOrWhiteSpace(language)) return;

            string dllPath = GetShellDllPath();
            if (dllPath == null) return;

            Invoke(dllPath, SetLanguageName, language);
        }

        private static bool RegisterNow(string dllPath)
        {
            // 先注销一遍：旧版本可能残留着指向别处路径的 CLSID 与 Approved 项，
            // 直接覆盖只会让没人引用的旧键一直留着。
            Invoke(dllPath, UnregisterServerName);
            // 返回值必须检查：DllRegisterServer 逐项写注册表，任何一项被拒都返回失败但不抛异常，
            // 漏检就会把「一条都没写进去」当成注册成功。
            if (Invoke(dllPath, RegisterServerName) != 0) return false;

            WriteMainExePath(dllPath);
            return true;
        }

        private static void WriteMainExePath(string dllPath)
        {
            string exePath = GetMainExePath();
            if (exePath != null) Invoke(dllPath, SetMainExePathName, exePath);
        }

        private static bool UnregisterNow(string dllPath)
        {
            Invoke(dllPath, UnregisterServerName);
            Invoke(dllPath, RemoveMainExePathName);
            return true;
        }
        #endregion

        #region 控制台输出
        /// <summary>向控制台打印命令行用法，语言取自配置文件。</summary>
        public static void WriteUsage()
        {
            Console.WriteLine(BuildUsage(ResolveConsoleLanguage()));
        }

        private static string BuildUsage(string language)
        {
            // 控制台模式不使用 Application 对象，EnumText 的缓存和资源查找都靠不住，
            // 所以三套用法文案直接内联在代码里。
            if (string.Equals(language, "zh-CN", StringComparison.OrdinalIgnoreCase))
            {
                return string.Join(Environment.NewLine, new[]
                {
                    "Auto7z 命令行用法:",
                    "",
                    "  Auto7z.exe [选项] [文件或目录...]",
                    "",
                    "选项:",
                    "  -a            自动模式, 压缩包解压、其余文件压缩 (默认)",
                    "  -z            压缩模式",
                    "  -uz           解压模式",
                    "  -r            注册右键菜单 (需要管理员权限)",
                    "  -ur           注销右键菜单 (需要管理员权限)",
                    "  -h            显示本说明",
                    "",
                    "说明:",
                    "  不带选项直接给路径时, 按自动模式处理。",
                    "  命令行开关不区分大小写（-H 与 -h 等价）。",
                    "  命令行模式不弹界面, 进度输出到控制台, 任务做完进程自动退出。"
                });
            }

            if (string.Equals(language, "zh-TW", StringComparison.OrdinalIgnoreCase))
            {
                return string.Join(Environment.NewLine, new[]
                {
                    "Auto7z 命令列用法:",
                    "",
                    "  Auto7z.exe [選項] [檔案或目錄...]",
                    "",
                    "選項:",
                    "  -a            自動模式, 壓縮檔解壓、其餘檔案壓縮 (預設)",
                    "  -z            壓縮模式",
                    "  -uz           解壓模式",
                    "  -r            註冊右鍵選單 (需要系統管理員權限)",
                    "  -ur           取消註冊右鍵選單 (需要系統管理員權限)",
                    "  -h            顯示本說明",
                    "",
                    "說明:",
                    "  不帶選項直接給路徑時, 按自動模式處理。",
                    "  命令列開關不區分大小寫（-H 與 -h 等價）。",
                    "  命令列模式不彈介面, 進度輸出到主控台, 工作做完程式自動結束。"
                });
            }

            return string.Join(Environment.NewLine, new[]
            {
                "Auto7z command line usage:",
                "",
                "  Auto7z.exe [options] [files or folders...]",
                "",
                "Options:",
                "  -a            automatic mode: unpack archives, compress the rest (default)",
                "  -z            compress",
                "  -uz           unpack",
                "  -r            register the Explorer context menu (needs administrator)",
                "  -ur           unregister the Explorer context menu (needs administrator)",
                "  -h            show this help",
                "",
                "Notes:",
                "  With no option, the given paths are handled in automatic mode.",
                "  Switches are case-insensitive (-H is the same as -h).",
                "  Command line mode opens no window: progress goes to the console and the process exits when the task is done."
            });
        }

        /// <summary>打印注册或注销的结果，资源缺失时退回内置中文。</summary>
        /// <param name="success">操作是否成功。</param>
        /// <param name="registering">true 表示本次是注册，false 表示注销。</param>
        public static void WriteRegistrationResult(bool success, bool registering)
        {
            string key;
            string fallback;
            if (registering)
            {
                key = success ? "Console_RegisterOk" : "Console_RegisterFailed";
                fallback = success ? "右键菜单注册成功" : "右键菜单注册失败，请以管理员身份运行";
            }
            else
            {
                key = success ? "Console_UnregisterOk" : "Console_UnregisterFailed";
                fallback = success ? "右键菜单已注销" : "右键菜单注销失败";
            }

            Console.WriteLine(GetConsoleText(key, fallback));
        }

        /// <summary>缺少 Auto7zShell.dll 时的统一提示。</summary>
        public static void WriteShellDllMissing()
        {
            Console.WriteLine(GetConsoleText("Console_ShellDllMissing",
                "找不到 " + ShellDllName + "，无法注册"));
        }

        private static string GetConsoleText(string key, string fallback)
        {
            // 没有 Application 就没有资源合并，这里按语言代码直接加载对应的 ResourceDictionary。
            string language = ResolveConsoleLanguage();
            try
            {
                var dictionary = new ResourceDictionary
                {
                    Source = new Uri($"Resources/Languages/{language}.xaml", UriKind.Relative)
                };

                var text = dictionary[key] as string;
                if (!string.IsNullOrEmpty(text)) return text;
            }
            catch
            {
                // 词典取不到时用中文兜底，控制台输出不该因为本地化失败而消失
            }

            return fallback;
        }

        private static string ResolveConsoleLanguage()
        {
            try
            {
                string path = AppPaths.ConfigFilePath;
                if (File.Exists(path))
                {
                    // 控制台模式下没有 Application 对象，不能走 ConfigService 那套（它会触发日志与语言服务），
                    // 这里只把文件读出来取一个键。
                    var values = JsonConfig.Parse(File.ReadAllText(path, new System.Text.UTF8Encoding(false)));
                    string language = values != null && values.TryGetValue("Language", out string raw) ? raw.Trim() : null;
                    if (LanguageService.IsSupported(language)) return language;
                }
            }
            catch
            {
                // 配置损坏时退回系统语言
            }

            return LanguageService.ResolveSystemLanguage();
        }
        #endregion

        #region DLL 调用与系统交互
        private static bool IsAdministrator()
        {
            try
            {
                using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                {
                    WindowsPrincipal principal = new WindowsPrincipal(identity);
                    return principal.IsInRole(WindowsBuiltInRole.Administrator);
                }
            }
            catch
            {
                return false;
            }
        }

        private static string GetShellDllPath()
        {
            string directory = Path.GetDirectoryName(typeof(ShellRegistrationService).Assembly.Location);
            if (string.IsNullOrEmpty(directory)) return null;

            string path = Path.Combine(directory, ShellDllName);
            return File.Exists(path) ? path : null;
        }

        private static string GetMainExePath()
        {
            string path = typeof(ShellRegistrationService).Assembly.Location;
            return string.IsNullOrEmpty(path) ? null : path;
        }

        private static int Invoke(string dllPath, string functionName, string argument = null)
        {
            // 不以 P/Invoke 静态声明这些导出函数，而在这里运行时取地址：
            // Auto7zShell.dll 只在注册注销时用得上，不该变成加载期的硬依赖。
            IntPtr module = LoadLibrary(dllPath);
            if (module == IntPtr.Zero) return -1;

            try
            {
                IntPtr function = GetProcAddress(module, functionName);
                if (function == IntPtr.Zero) return -1;

                if (argument == null)
                {
                    var entry = (RegisterDelegate)Marshal.GetDelegateForFunctionPointer(
                        function, typeof(RegisterDelegate));
                    return entry();
                }

                var entryWithArg = (SetPathDelegate)Marshal.GetDelegateForFunctionPointer(
                    function, typeof(SetPathDelegate));
                return entryWithArg(argument);
            }
            catch (Exception)
            {
                return -1;
            }
            finally
            {
                FreeLibrary(module);
            }
        }

        private static void NotifyShellChanged()
        {
            try
            {
                // 只写注册表不会让已经开着的资源管理器重新枚举处理程序，
                // 不广播的话用户得重启 explorer.exe 才能看见菜单。
                SHChangeNotify(0x08000000, 0x0000, null, IntPtr.Zero);
            }
            catch (Exception)
            {
                // 通知失败不影响注册结果
            }
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern void SHChangeNotify(int eventId, uint flags, string item1, IntPtr item2);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibrary(string fileName);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool FreeLibrary(IntPtr module);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
        private static extern IntPtr GetProcAddress(IntPtr module, string procedureName);
        #endregion
    }
}
