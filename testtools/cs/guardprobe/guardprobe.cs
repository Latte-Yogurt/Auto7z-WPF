using System;
using System.IO;
using System.Reflection;

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

        string portableRoot = Path.Combine(programDir, ".config");
        string localRoot = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        Console.WriteLine("  便携根目录: " + portableRoot);
        Console.WriteLine("  普通根目录: " + localRoot);
        Console.WriteLine("");

        Console.WriteLine("【应当放行】");
        Check("数据目录本身（便携根下）",
            Invoke(guard, Path.Combine(portableRoot, "Auto7z")), true);
        Check("数据目录本身（LOCALAPPDATA 下）",
            Invoke(guard, Path.Combine(localRoot, "Auto7z")), true);
        Check("数据目录的子目录",
            Invoke(guard, Path.Combine(portableRoot, "Auto7z", "Logs")), true);
        Check("数据目录的深层子目录",
            Invoke(guard, Path.Combine(portableRoot, "Auto7z", "7-Zip")), true);

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
            return false;
        }
    }
}
