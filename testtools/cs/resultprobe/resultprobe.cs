using System;
using System.ComponentModel;
using System.IO;
using System.Reflection;

class ResultProbe
{
    const string RELAUNCH_FLAG = "AUTO7Z_RESULTPROBE_RELAUNCHED";

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
            Console.WriteLine("用法: resultprobe.exe <Auto7z.exe 路径>");
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

            string copy = Path.Combine(targetDir, "resultprobe_run.exe");
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

        return RunProbe(asmPath);
    }

    static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception) { }
    }

    static int RunProbe(string asmPath)
    {
        var asm = Assembly.LoadFrom(asmPath);
        AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
        {
            string simple = new AssemblyName(e.Name).Name;
            string p = Path.Combine(Path.GetDirectoryName(asmPath), simple + ".exe");
            return File.Exists(p) ? Assembly.LoadFrom(p) : null;
        };

        var vmType = asm.GetType("Auto7z_WPF.Auto7zViewModel");
        Console.WriteLine("=== 任务结果的颜色契约（运行时）===");
        Console.WriteLine("被测程序: " + asmPath);
        Console.WriteLine("");

        if (vmType == null)
        {
            Console.WriteLine("!! 找不到 Auto7zViewModel");
            return 1;
        }

        var setResult = vmType.GetMethod("SetResult",
            BindingFlags.Public | BindingFlags.Instance);
        if (setResult == null)
        {
            Console.WriteLine("!! 找不到 SetResult");
            return 1;
        }

        var failedProp = vmType.GetProperty("IsResultFailed");
        var textProp = vmType.GetProperty("ResultText");
        if (failedProp == null || textProp == null)
        {
            Console.WriteLine("!! 找不到 IsResultFailed / ResultText");
            return 1;
        }

        object vm;
        try
        {
            vm = Activator.CreateInstance(vmType);
        }
        catch (Exception e)
        {
            Console.WriteLine("!! 构造 Auto7zViewModel 失败: " + e);
            return 1;
        }

        var events = (INotifyPropertyChanged)vm;

        int notifyCount = 0;
        int volumeNotify = 0;
        bool successNotified = false;
        bool successNotifiedStale = false;
        bool failureNotified = false;
        bool failureNotifiedStale = false;
        bool sameTextNotified = false;
        bool sameTextStale = false;
        string firstText = null;

        events.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == "Volume")
            {
                volumeNotify++;
                return;
            }
            if (e.PropertyName != "ResultText") return;

            notifyCount++;
            bool failedNow = (bool)failedProp.GetValue(vm);
            string textNow = (string)textProp.GetValue(vm);

            if (notifyCount == 1)
            {
                firstText = textNow;
                if (failedNow) successNotifiedStale = true;
                else successNotified = true;
            }
            else if (notifyCount == 2)
            {
                if (!failedNow) failureNotifiedStale = true;
                else failureNotified = true;
            }
            else if (notifyCount == 3)
            {
                if (string.Equals(firstText, textNow, StringComparison.Ordinal))
                {
                    if (failedNow) sameTextStale = true;
                    else sameTextNotified = true;
                }
            }
        };

        try
        {
            vmType.GetProperty("Volume").SetValue(vm, "999");
        }
        catch (Exception e)
        {
            Console.WriteLine("  !! 对照属性 Volume 设置失败: " + e.Message);
        }

        try
        {
            setResult.Invoke(vm, new object[] { "成功文本", false });
            setResult.Invoke(vm, new object[] { "失败文本", true });
            setResult.Invoke(vm, new object[] { "成功文本", false });
        }
        catch (TargetInvocationException ex)
        {
            Console.WriteLine("!! 调用 SetResult 抛异常: " + (ex.InnerException != null ? ex.InnerException.Message : "?"));
            return 1;
        }

        Console.WriteLine("  ResultText 通知次数 = " + notifyCount);
        Console.WriteLine("");
        Console.WriteLine("【应当成立】");
        Check("订阅机制本身通（对照属性 Volume 的通知能收到）", volumeNotify >= 1, true);
        Check("通知的属性名是 ResultText（不是 SetResult）", notifyCount >= 3, true);
        Check("成功那次：通知到来时失败标记已是 false（绿字）", successNotified && !successNotifiedStale, true);
        Check("失败那次：通知到来时失败标记已是 true（红字）", failureNotified && !failureNotifiedStale, true);
        Check("文本没变时仍然发了通知（第二次相同结果也要显示）", sameTextNotified && !sameTextStale, true);

        Console.WriteLine("");
        if (failures == 0)
        {
            Console.WriteLine("全部符合预期 ✓");
            return 0;
        }

        Console.WriteLine("!! 有 " + failures + " 项不符合预期");
        return 1;
    }
}
