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

        // 写盘路径边界校验：FileSystemHelper.IsUnderDirectory（配置写入与日志清理共用这一条判据）。
        var fsType = asm.GetType("Auto7z_WPF.Services.FileSystemHelper");

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
        // 守卫按产品的数据根目录判断边界，而该根可能被 AUTO7Z_DATA_ROOT 改道 ——
        // 不跟着改的话，这里拿 %LOCALAPPDATA% 当"根"，被测守则拿的是临时目录，
        // 每一项都会得出与实现相反的结论。
        string localRoot = DataRoot();
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
        Console.WriteLine("【写盘路径边界 IsUnderDirectory】");
        var under = fsType.GetMethod("IsUnderDirectory", BindingFlags.Public | BindingFlags.Static);
        if (under == null)
        {
            Console.WriteLine("!! 找不到 FileSystemHelper.IsUnderDirectory —— 写盘边界校验不存在");
            failures++;
        }
        else
        {
            string root = Path.Combine(Path.GetTempPath(), "Auto7zBoundaryRoot");
            Check("目录本身", UnderInvoke(under, root, root), true);
            Check("直接子文件", UnderInvoke(under, Path.Combine(root, "a.json"), root), true);
            Check("深层子文件", UnderInvoke(under, Path.Combine(root, "x", "y", "a.json"), root), true);
            Check("带 .. 但仍在根下", UnderInvoke(under, Path.Combine(root, "x", "..", "a.json"), root), true);
            Check("大小写不同（Windows 路径不区分大小写）",
                UnderInvoke(under, root.ToUpperInvariant() + "\\a.json", root), true);

            Check("同级前缀相同的目录（根\\X 不能吃到 根X）",
                UnderInvoke(under, root + "X\\a.json", root), false);
            Check("带 .. 越出根", UnderInvoke(under, Path.Combine(root, "..", "a.json"), root), false);
            Check("完全无关的路径", UnderInvoke(under, @"C:\Windows\notepad.exe", root), false);
            Check("空路径", UnderInvoke(under, "", root), false);
            Check("null 路径", UnderInvoke(under, null, root), false);
            Check("空的根", UnderInvoke(under, Path.Combine(root, "a.json"), ""), false);
        }

        Console.WriteLine("");
        Console.WriteLine("【体积统计：读不全必须计数】");
        var sizeDetailed = fsType.GetMethod("GetSizeInBytesDetailed", BindingFlags.Public | BindingFlags.Static);
        if (sizeDetailed == null)
        {
            Console.WriteLine("!! 找不到 FileSystemHelper.GetSizeInBytesDetailed —— 体积统计无法回报漏读");
            failures++;
        }
        else
        {
            string probeRoot = Path.Combine(Path.GetTempPath(), "Auto7zSizeProbe");
            try { if (Directory.Exists(probeRoot)) Directory.Delete(probeRoot, true); } catch { }
            Directory.CreateDirectory(probeRoot);

            // 两个文件共 6 字节 + 一个空子目录。
            File.WriteAllText(Path.Combine(probeRoot, "a.txt"), "abc");
            File.WriteAllText(Path.Combine(probeRoot, "b.txt"), "xyz");
            Directory.CreateDirectory(Path.Combine(probeRoot, "empty"));

            CheckSize("正常目录：字节数正确", sizeDetailed, probeRoot, 6, 0);
            CheckSize("正常目录：不漏读", sizeDetailed, probeRoot, 6, 0, expectSkips: 0);

            // 不存在的路径：字节数 -1（调用方据此计为「整条读不出来」）。
            CheckSize("不存在的路径：返回 -1", sizeDetailed,
                Path.Combine(probeRoot, "does-not-exist"), -1, -1);

            try { Directory.Delete(probeRoot, true); } catch { }
        }

        Console.WriteLine("");
        if (failures == 0)
        {
            Console.WriteLine("全部符合预期 ✓");
            return 0;
        }

        Console.WriteLine("!! 有 " + failures + " 项不符合预期");
        return 1;
    }

    static void CheckSize(string name, MethodInfo m, string path, long expectedBytes, int expectedSkips,
        int? expectSkips = null)
    {
        try
        {
            object[] args = { path, 0 };
            object bytes = m.Invoke(null, args);
            int skips = (int)args[1];
            long actual = bytes is long ? (long)bytes : -999;

            bool okBytes = actual == expectedBytes;
            bool okSkips = expectSkips == null || skips == expectedSkips;

            if (!okBytes || !okSkips) failures++;
            Console.WriteLine("  {0} {1}: 字节={2} 预期={3} 跳过子树={4}{5}",
                okBytes && okSkips ? "OK  " : "!!  ", name, actual, expectedBytes, skips,
                expectSkips == null ? "" : " 预期=" + expectedSkips);
        }
        catch (TargetInvocationException)
        {
            failures++;
            Console.WriteLine("  !!  " + name + ": 调用抛异常");
        }
    }

    static bool UnderInvoke(MethodInfo m, string path, string root)
    {
        try
        {
            object r = m.Invoke(null, new object[] { path, root });
            return r is bool && (bool)r;
        }
        catch (TargetInvocationException)
        {
            return false;
        }
    }

    /// <summary>
    /// 产品的数据根目录。被测程序支持 AUTO7Z_DATA_ROOT 改道（见 AppPaths.DataRootVariable），
    /// 测试用它把配置、日志、7-Zip 组件统一写进 %TEMP% 下的受控目录。
    /// </summary>
    static string DataRoot()
    {
        string redirect = Environment.GetEnvironmentVariable("AUTO7Z_DATA_ROOT");
        if (!string.IsNullOrEmpty(redirect) && Path.IsPathRooted(redirect)) return redirect;

        return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
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
