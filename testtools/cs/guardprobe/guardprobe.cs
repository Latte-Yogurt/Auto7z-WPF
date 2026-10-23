using System;
using System.IO;
using System.Reflection;

// 运行时验证「递归删除的路径边界校验」（B01 / D28）。
//
// 为什么要有这个探针：
//   cs_check.py ⑤ 是**静态**检查 —— 它只看删除点前面 8 行窗口里
//   有没有边界校验的关键字。关键字在，就认为有守卫。
//   但那只证明「代码里写了检查」，不证明「检查真的拦得住」。
//   本探针用反射直接调用产品里的守卫方法，
//   拿真实的越界路径去试，看它到底放不放行。
//
// 这就是 §5 要求的「交叉验证：静态解析 vs 运行时/反射各做一次」。
//
// 关键：探针必须**跟被测 exe 放在同一个目录**运行。
//   AppPaths.GetProgramDirectory() 取的是 Assembly.GetEntryAssembly().Location，
//   入口是哪个 exe，程序目录就算哪个。
//   探针若跑在别的目录，产品算出来的便携根就指到别处去了，
//   守卫当然会把「正确的」路径当成越界路径拦掉 ——
//   那是探针的进程上下文差异，不是产品缺陷。
//   所以本探针会把自己复制到被测 exe 旁边再重启一次。
//
// 用法:
//   guardprobe.exe <Auto7z.exe 路径>
//   退出码 0 = 全部符合预期; 1 = 有不符合
class GuardProbe
{
    const string RELAUNCH_FLAG = "AUTO7Z_GUARDPROBE_RELAUNCHED";

    static int failures = 0;

    static void Check(string name, bool actual, bool expected)
    {
        bool ok = (actual == expected);
        if (!ok) failures++;
        Console.WriteLine("  {0} {1}: 实际={2} 预期={3}",
            ok ? "OK  " : "!!  ", name, actual, expected);
    }

    static int Main(string[] args)
    {
        if (args.Length < 1)
        {
            Console.WriteLine("用法: guardprobe.exe <Auto7z.exe 路径>");
            return 2;
        }

        string asmPath = Path.GetFullPath(args[0]);
        string targetDir = Path.GetDirectoryName(asmPath);
        string selfDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

        // 不在被测 exe 旁边 -> 复制过去再跑一次
        if (!string.Equals(selfDir, targetDir, StringComparison.OrdinalIgnoreCase))
        {
            if (Environment.GetEnvironmentVariable(RELAUNCH_FLAG) == "1")
            {
                Console.WriteLine("!! 重启后仍然不在被测目录旁边，放弃");
                return 1;
            }

            string copy = Path.Combine(targetDir, "guardprobe_run.exe");
            try
            {
                File.Copy(Assembly.GetExecutingAssembly().Location, copy, true);
            }
            catch (Exception e)
            {
                Console.WriteLine("!! 复制探针到被测目录失败: " + e.Message);
                return 1;
            }

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = copy,
                Arguments = "\"" + asmPath + "\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.EnvironmentVariables[RELAUNCH_FLAG] = "1";

            var proc = System.Diagnostics.Process.Start(psi);
            string output = proc.StandardOutput.ReadToEnd();
            proc.StandardError.ReadToEnd();
            proc.WaitForExit();
            Console.Write(output);
            TryDelete(copy);
            return proc.ExitCode;
        }

        return RunProbe(asmPath, targetDir);
    }

    static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception) { }
    }

    static int RunProbe(string asmPath, string programDir)
    {
        var asm = Assembly.LoadFrom(asmPath);
        AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
        {
            string simple = new AssemblyName(e.Name).Name;
            string p = Path.Combine(Path.GetDirectoryName(asmPath), simple + ".exe");
            return File.Exists(p) ? Assembly.LoadFrom(p) : null;
        };

        var vmType = asm.GetType("Auto7z_WPF.Auto7zViewModel");

        // 守卫方法是 private static，用反射照样能拿到。
        var guard = vmType.GetMethod("IsSafeToDeleteRecursively",
            BindingFlags.NonPublic | BindingFlags.Static);

        Console.WriteLine("=== 递归删除守卫的运行时验证 ===");
        Console.WriteLine("被测程序: " + asmPath);

        if (guard == null)
        {
            Console.WriteLine("!! 找不到 IsSafeToDeleteRecursively —— 守卫方法不存在");
            return 1;
        }
        Console.WriteLine("  已找到守卫方法: IsSafeToDeleteRecursively");
        Console.WriteLine("");

        // 现在探针与被测 exe 同目录，产品算出来的程序目录就是对的。
        string portableRoot = Path.Combine(programDir, ".config");
        string localRoot = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        Console.WriteLine("  便携根目录: " + portableRoot);
        Console.WriteLine("  普通根目录: " + localRoot);
        Console.WriteLine("");

        // ---- 应该放行的 ----
        Console.WriteLine("【应当放行】");
        Check("数据目录本身（便携根下）",
            Invoke(guard, Path.Combine(portableRoot, "Auto7z")), true);
        Check("数据目录本身（LOCALAPPDATA 下）",
            Invoke(guard, Path.Combine(localRoot, "Auto7z")), true);
        Check("数据目录的子目录",
            Invoke(guard, Path.Combine(portableRoot, "Auto7z", "Logs")), true);
        Check("数据目录的深层子目录",
            Invoke(guard, Path.Combine(portableRoot, "Auto7z", "7-Zip")), true);

        // ---- 应该拦住的 ----
        Console.WriteLine("");
        Console.WriteLine("【应当拦住】");
        Check("根目录本身（便携 .config）", Invoke(guard, portableRoot), false);
        Check("根目录本身（LOCALAPPDATA）", Invoke(guard, localRoot), false);
        Check("完全无关的路径（C:\\Windows）", Invoke(guard, @"C:\Windows"), false);
        Check("程序的上一级目录", Invoke(guard, programDir), false);
        Check("数据目录的同级目录（名字像但不在根下）",
            Invoke(guard, Path.Combine(localRoot, "Auto7zX")), false);
        Check("空字符串", Invoke(guard, ""), false);
        Check("null", Invoke(guard, null), false);
        Check("用 .. 越出数据目录",
            Invoke(guard, Path.Combine(portableRoot, "Auto7z", "..", "..")), false);

        Console.WriteLine("");
        if (failures == 0)
        {
            Console.WriteLine("全部符合预期 ✓");
            return 0;
        }

        Console.WriteLine("!! 有 " + failures + " 项不符合预期");
        return 1;
    }

    static bool Invoke(MethodInfo m, string path)
    {
        try
        {
            object r = m.Invoke(null, new object[] { path });
            return r is bool && (bool)r;
        }
        catch (TargetInvocationException)
        {
            // 守卫自己抛异常也算「拦住」
            return false;
        }
    }
}
