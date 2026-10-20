using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace Auto7z_WPF.Services
{
    /// <summary>
    /// 右键菜单外壳扩展的注册。
    ///
    /// 注册由命令行触发: Auto7z.exe -r 注册, -ur 注销。
    /// 打包安装程序用这两个参数写 [Run] 与 UninstallRun 即可。
    ///
    /// COM 查 CLSID 只认 HKEY_CLASSES_ROOT, 也就是 HKLM\Software\Classes\CLSID,
    /// 写 HKEY_CURRENT_USER\Software\Classes\CLSID 是不生效的。
    /// 所以注册要管理员权限: 权限不够时用 UAC 提权跑一次 regsvr32。
    ///
    /// 组件 dll 与主程序放在一起, 文件名固定。
    /// </summary>
    public class ShellRegistrationService
    {
        /// <summary>外壳扩展 dll 的文件名。</summary>
        private const string ShellDllName = "Auto7zShell.dll";

        /// <summary>注册时用到的导出函数。</summary>
        private const string SetMainExePathName = "DllSetMainExePath";
        private const string RemoveMainExePathName = "DllRemoveMainExePath";
        private const string RegisterServerName = "DllRegisterServer";
        private const string UnregisterServerName = "DllUnregisterServer";

        /// <summary>注册与注销用的系统工具。</summary>
        private const string RegSvr32Name = "regsvr32.exe";

        /// <summary>把语言告诉组件用的导出函数。</summary>
        private const string SetLanguageName = "DllSetLanguage";


        /// <summary>无参导出函数: HRESULT DllRegisterServer()</summary>
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int RegisterDelegate();

        /// <summary>带一个字符串参数的导出函数: HRESULT DllSetMainExePath(LPCWSTR)</summary>
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int SetPathDelegate([MarshalAs(UnmanagedType.LPWStr)] string path);

        private ShellRegistrationService()
        {
        }

        /// <summary>
        /// 注册右键菜单。权限不够时用 UAC 提权再试一次。
        /// </summary>
        /// <returns>注册成功返回 true。</returns>
        public static bool RegisterWithElevation()
        {
            string dllPath = GetShellDllPath();
            if (dllPath == null)
            {
                Console.WriteLine("找不到 " + ShellDllName + ", 无法注册");
                return false;
            }

            // 已经是管理员就直接注册
            if (IsAdministrator())
            {
                bool ok = RegisterNow(dllPath);
                if (ok) NotifyShellChanged();
                return ok;
            }

            // 权限不够, 用 regsvr32 提权注册。
            // 主程序路径由 dll 在注册时一并写好, 不用再起一个提权进程。
            bool registered = RunElevated(dllPath, true);
            if (registered) NotifyShellChanged();
            return registered;
        }

        /// <summary>
        /// 注销右键菜单。权限不够时用 UAC 提权再试一次。
        /// </summary>
        /// <returns>注销成功返回 true。</returns>
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

        /// <summary>
        /// 用 regsvr32 提权注册或注销。
        ///
        /// 不重新启动自己, 那样会被本程序的 manifest 执行级别压住。
        /// regsvr32 是系统自带的, 用 runas 启动它就能拿到管理员权限。
        /// </summary>
        /// <param name="dllPath">组件 dll 路径。</param>
        /// <param name="register">true 为注册，false 为注销。</param>
        /// <returns>成功返回 true。</returns>
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
                // 用户取消了 UAC 提示
                return false;
            }
        }

        /// <summary>
        /// 把当前语言告诉外壳扩展。
        ///
        /// 右键菜单的文字按这个语言显示, 所以界面语言换了要通知组件一次。
        /// 写在 HKEY_CURRENT_USER 下, 不需要管理员权限。
        /// </summary>
        /// <param name="language">语言代码, 比如 zh-CN。</param>
        public static void SetLanguage(string language)
        {
            if (string.IsNullOrWhiteSpace(language)) return;

            string dllPath = GetShellDllPath();
            if (dllPath == null) return;

            Invoke(dllPath, SetLanguageName, language);
        }

        /// <summary>
        /// 把主程序位置写进注册表。
        ///
        /// 供提权后的进程调用, 写完就退出。
        /// </summary>
        public static void SetMainExePath()
        {
            string dllPath = GetShellDllPath();
            if (dllPath == null) return;

            WriteMainExePath(dllPath);
        }

        /// <summary>
        /// 真正执行注册。当前进程要有管理员权限。
        /// </summary>
        private static bool RegisterNow(string dllPath)
        {
            // 先注销再注册, 保证路径与当前实际位置一致
            Invoke(dllPath, UnregisterServerName);
            if (Invoke(dllPath, RegisterServerName) != 0) return false;

            WriteMainExePath(dllPath);
            return true;
        }

        /// <summary>
        /// 把主程序位置写进注册表。
        ///
        /// 这一步要写 HKEY_CLASSES_ROOT, 跟注册一样需要管理员权限。
        /// 提权那条路上是由 regsvr32 之外的另一个提权进程来做的。
        /// </summary>
        /// <param name="dllPath">组件 dll 路径。</param>
        private static void WriteMainExePath(string dllPath)
        {
            string exePath = GetMainExePath();
            if (exePath != null) Invoke(dllPath, SetMainExePathName, exePath);
        }

        /// <summary>
        /// 真正执行注销。
        /// </summary>
        private static bool UnregisterNow(string dllPath)
        {
            Invoke(dllPath, UnregisterServerName);
            Invoke(dllPath, RemoveMainExePathName);
            return true;
        }

        /// <summary>
        /// 打印命令行用法。给打包程序与用户看。
        /// </summary>
        public static void WriteUsage()
        {
            Console.WriteLine("Auto7z 命令行用法:");
            Console.WriteLine();
            Console.WriteLine("  Auto7z.exe [选项] [文件或目录...]");
            Console.WriteLine();
            Console.WriteLine("选项:");
            Console.WriteLine("  -A            自动模式, 压缩包解压、其余文件压缩 (默认)");
            Console.WriteLine("  -Z            压缩模式");
            Console.WriteLine("  -U            解压模式");
            Console.WriteLine("  -r            注册右键菜单 (需要管理员权限)");
            Console.WriteLine("  -ur           注销右键菜单 (需要管理员权限)");
            Console.WriteLine("  -h            显示本说明");
            Console.WriteLine();
            Console.WriteLine("说明:");
            Console.WriteLine("  不带选项直接给路径时, 按自动模式处理。");
            Console.WriteLine("  命令行模式不弹界面, 进度输出到控制台, 任务做完进程自动退出。");
        }

        /// <summary>
        /// 当前进程是不是管理员。
        /// </summary>
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

        /// <summary>
        /// 取外壳扩展 dll 的完整路径。文件不在时返回 null。
        /// </summary>
        private static string GetShellDllPath()
        {
            string directory = Path.GetDirectoryName(typeof(ShellRegistrationService).Assembly.Location);
            if (string.IsNullOrEmpty(directory)) return null;

            string path = Path.Combine(directory, ShellDllName);
            return File.Exists(path) ? path : null;
        }

        /// <summary>
        /// 取主程序 exe 的完整路径。
        /// </summary>
        private static string GetMainExePath()
        {
            string path = typeof(ShellRegistrationService).Assembly.Location;
            return string.IsNullOrEmpty(path) ? null : path;
        }

        /// <summary>
        /// 调用 dll 里的导出函数。
        /// </summary>
        /// <param name="dllPath">dll 路径。</param>
        /// <param name="functionName">导出函数名。</param>
        /// <param name="argument">可选参数。</param>
        /// <returns>返回值；调用失败返回 -1。</returns>
        private static int Invoke(string dllPath, string functionName, string argument = null)
        {
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

        /// <summary>
        /// 通知资源管理器文件关联变了, 让它重新读一遍外壳扩展列表。
        ///
        /// 不发这个通知的话, 资源管理器会一直用启动时缓存的列表,
        /// 刚注册上的菜单不会出现。
        /// </summary>
        private static void NotifyShellChanged()
        {
            try
            {
                // SHCNE_ASSOCCHANGED, SHCNF_IDLIST
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
    }
}
