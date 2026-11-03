using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

class Sha256Probe
{
    const string RELAUNCH_FLAG = "AUTO7Z_SHA256PROBE_RELAUNCHED";

    static int failures = 0;
    static string workDir;

    static void Check(string name, bool ok, string detail)
    {
        if (!ok) failures++;
        Console.WriteLine("  {0} {1}{2}",
            ok ? "OK  " : "!!  ", name, string.IsNullOrEmpty(detail) ? "" : "  —— " + detail);
    }

    static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("用法: sha256probe.exe <Auto7z.exe 路径> <工作目录>");
            return 2;
        }

        string asmPath = Path.GetFullPath(args[0]);
        workDir = Path.GetFullPath(args[1]);
        string targetDir = Path.GetDirectoryName(asmPath);
        string selfDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

        if (!string.Equals(selfDir, targetDir, StringComparison.OrdinalIgnoreCase))
        {
            if (Environment.GetEnvironmentVariable(RELAUNCH_FLAG) == "1")
            {
                Console.WriteLine("!! 重启后仍然不在被测目录旁边，放弃");
                return 1;
            }

            string copy = Path.Combine(targetDir, "sha256probe_run.exe");
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
                Arguments = "\"" + asmPath + "\" \"" + workDir + "\"",
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

        var svcType = asm.GetType("Auto7z_WPF.Services.SHA256Service");
        Console.WriteLine("=== Auto7z 计算服务的运行时验证 ===");
        Console.WriteLine("被测程序: " + asmPath);
        if (svcType == null)
        {
            Console.WriteLine("!! 找不到 Auto7z_WPF.Services.SHA256Service");
            return 1;
        }

        var calc = svcType.GetMethod("CalculateSHA256");
        var calcToFile = svcType.GetMethod("CalculateSHA256ToFile");
        var writeFile = svcType.GetMethod("WriteSHA256File");
        if (calc == null || calcToFile == null || writeFile == null)
        {
            Console.WriteLine("!! 计算服务缺少预期的方法（CalculateSHA256 / CalculateSHA256ToFile / WriteSHA256File）");
            return 1;
        }

        if (Directory.Exists(workDir)) Directory.Delete(workDir, true);
        Directory.CreateDirectory(workDir);

        object svc = Activator.CreateInstance(svcType);

        CheckSingleFile(calc, svc);
        CheckMultipleFiles(calc, svc);
        CheckDirectory(calc, svc, calcToFile);
        CheckMissingFileIsSkipped(calc, svc);
        CheckProgressMonotonic(calc, svc);
        CheckCancellation(calc, svc);

        Console.WriteLine("");
        if (failures == 0)
        {
            Console.WriteLine("全部符合预期 ✓");
            return 0;
        }

        Console.WriteLine("!! 有 " + failures + " 项不符合预期");
        return 1;
    }

    static void CheckSingleFile(MethodInfo calc, object svc)
    {
        Console.WriteLine("");
        Console.WriteLine("【单文件】");
        string file = MakePayload(Path.Combine(workDir, "single"), "a.bin", 300 * 1024);

        string[] lines = InvokeCalc(calc, svc, new[] { file }, null, CancellationToken.None);
        Check("返回一行", lines != null && lines.Length == 1,
            lines == null ? "返回 null" : "实际 " + lines.Length + " 行");
        if (lines == null || lines.Length == 0) return;

        string expectedHash = RawSha256(file);
        string name = Path.GetFileName(file);
        Check("行格式为 'hash *filename'",
            lines[0] == expectedHash + " *" + name, "实际=\"" + lines[0] + "\"");
        Check("哈希值与独立计算结果一致",
            lines[0].StartsWith(expectedHash, StringComparison.Ordinal), "期望前缀=" + expectedHash);
    }

    static void CheckMultipleFiles(MethodInfo calc, object svc)
    {
        Console.WriteLine("");
        Console.WriteLine("【多文件】");
        string dir = Path.Combine(workDir, "multi");
        string f1 = MakePayload(dir, "one.bin", 64 * 1024);
        string f2 = MakePayload(dir, "two.bin", 128 * 1024 + 7);

        string[] lines = InvokeCalc(calc, svc, new[] { f1, f2 }, null, CancellationToken.None);
        Check("两个输入返回两行", lines != null && lines.Length == 2,
            lines == null ? "返回 null" : "实际 " + lines.Length + " 行");
        if (lines == null || lines.Length != 2) return;

        Check("第一条哈希正确", lines[0] == RawSha256(f1) + " *" + Path.GetFileName(f1), lines[0]);
        Check("第二条哈希正确", lines[1] == RawSha256(f2) + " *" + Path.GetFileName(f2), lines[1]);
    }

    static void CheckDirectory(MethodInfo calc, object svc, MethodInfo calcToFile)
    {
        Console.WriteLine("");
        Console.WriteLine("【目录递归与 .sha256 产物】");
        string dir = Path.Combine(workDir, "tree");
        MakePayload(Path.Combine(dir, "sub"), "deep.bin", 8 * 1024);
        MakePayload(dir, "top.bin", 4 * 1024);

        string[] lines = InvokeCalc(calc, svc, new[] { dir }, null, CancellationToken.None);
        Check("目录递归展开出两个文件", lines != null && lines.Length == 2,
            lines == null ? "返回 null" : "实际 " + lines.Length + " 行");

        try
        {
            calcToFile.Invoke(svc, new object[] { dir, null, CancellationToken.None });
        }
        catch (TargetInvocationException e)
        {
            Check("CalculateSHA256ToFile 不抛异常", false, Describe(e));
            return;
        }

        string topSum = Path.Combine(dir, "top.bin.sha256");
        Check("目录内每个文件各生成一份 .sha256", File.Exists(topSum),
            "期望存在 " + topSum);

        if (File.Exists(topSum))
        {
            byte[] raw = File.ReadAllBytes(topSum);
            bool bom = raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF;
            Check(".sha256 是 UTF-8 且不写 BOM", !bom, "前三字节=" + BitConverter.ToString(raw, 0, Math.Min(3, raw.Length)));

            string text = File.ReadAllText(topSum, Encoding.UTF8).Trim();
            Check(".sha256 内容与独立计算一致",
                text == RawSha256(Path.Combine(dir, "top.bin")) + " *top.bin", text);
        }
    }

    static void CheckMissingFileIsSkipped(MethodInfo calc, object svc)
    {
        Console.WriteLine("");
        Console.WriteLine("【坏文件跳过】");
        string good = MakePayload(Path.Combine(workDir, "skip"), "good.bin", 4096);
        string missing = Path.Combine(workDir, "skip", "does_not_exist.bin");

        string[] lines = InvokeCalc(calc, svc, new[] { good, missing }, null, CancellationToken.None);
        Check("坏文件被跳过、好文件仍产出", lines != null && lines.Length == 1,
            lines == null ? "返回 null" : "实际 " + lines.Length + " 行");
        if (lines != null && lines.Length == 1)
        {
            Check("跳过之后产出的那一行仍然是好文件的", lines[0].EndsWith(" *good.bin", StringComparison.Ordinal), lines[0]);
        }
    }

    static void CheckProgressMonotonic(MethodInfo calc, object svc)
    {
        Console.WriteLine("");
        Console.WriteLine("【进度单调】");
        string dir = Path.Combine(workDir, "prog");
        for (int i = 0; i < 6; i++) MakePayload(Path.Combine(dir, "d" + i), "f" + i + ".bin", 96 * 1024);

        var seen = new List<int>();
        Action<int> report = p => seen.Add(p);
        InvokeCalcWithProgress(calc, svc, new[] { dir }, report, CancellationToken.None);

        Check("进度回调有被调用", seen.Count > 0, "实际 " + seen.Count + " 次");
        if (seen.Count == 0) return;

        bool monotonic = true;
        for (int i = 1; i < seen.Count; i++)
        {
            if (seen[i] < seen[i - 1]) { monotonic = false; break; }
        }
        Check("进度单调不减", monotonic, string.Join(",", seen));
        Check("进度全部落在 0..100", seen.TrueForAll(p => p >= 0 && p <= 100), "最小值/最大值=" + Min(seen) + "/" + Max(seen));
        Check("结束时到达 100", seen[seen.Count - 1] == 100, "最后=" + seen[seen.Count - 1]);
    }

    static void CheckCancellation(MethodInfo calc, object svc)
    {
        Console.WriteLine("");
        Console.WriteLine("【取消】");
        string dir = Path.Combine(workDir, "cancel");
        for (int i = 0; i < 4; i++) MakePayload(Path.Combine(dir, "d" + i), "f" + i + ".bin", 256 * 1024);

        bool cancelled = false;
        try
        {
            calc.Invoke(svc, new object[] { new[] { dir }, null, new CancellationToken(true) });
        }
        catch (TargetInvocationException e)
        {
            cancelled = e.InnerException is OperationCanceledException;
            if (!cancelled) Check("取消时抛 OperationCanceledException", false, Describe(e));
        }
        Check("已取消的令牌立即中止", cancelled, "未抛 OperationCanceledException");
    }


    static string MakePayload(string dir, string name, int size)
    {
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, name);
        byte[] data = new byte[size];
        for (int i = 0; i < size; i++) data[i] = (byte)((i * 7 + 11) & 0xFF);
        File.WriteAllBytes(path, data);
        return path;
    }

    static string RawSha256(string path)
    {
        using (var sha = SHA256.Create())
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.SequentialScan))
        {
            return ToHex(sha.ComputeHash(fs));
        }
    }

    static string ToHex(byte[] bytes)
    {
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (byte b in bytes) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }

    static string[] InvokeCalc(MethodInfo calc, object svc, string[] paths, Action<int> progress, CancellationToken token)
    {
        return InvokeCalcWithProgress(calc, svc, paths, progress, token);
    }

    static string[] InvokeCalcWithProgress(MethodInfo calc, object svc, string[] paths, Action<int> progress, CancellationToken token)
    {
        try
        {
            object r = calc.Invoke(svc, new object[] { paths, progress, token });
            return r as string[];
        }
        catch (TargetInvocationException e)
        {
            Check("CalculateSHA256 不抛异常", false, Describe(e));
            return null;
        }
    }

    static string Describe(TargetInvocationException e)
    {
        if (e == null) return "(无异常信息)";
        Exception inner = e.InnerException;
        if (inner == null) return e.Message;
        return inner.GetType().Name + ": " + inner.Message;
    }

    static int Min(List<int> v) { int m = v[0]; foreach (int x in v) if (x < m) m = x; return m; }
    static int Max(List<int> v) { int m = v[0]; foreach (int x in v) if (x > m) m = x; return m; }
}
