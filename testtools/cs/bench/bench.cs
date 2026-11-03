using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

class Bench
{
    const string RELAUNCH_FLAG = "AUTO7Z_BENCH_RELAUNCHED";

    static string workDir;
    static string asmPath;
    static Assembly asm;

    static long warmupMs;

    static void Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("用法: bench.exe <Auto7z.exe 路径> <工作目录> [过滤]");
            return;
        }

        asmPath = Path.GetFullPath(args[0]);
        workDir = Path.GetFullPath(args[1]);
        string filter = args.Length > 2 ? args[2] : null;

        string targetDir = Path.GetDirectoryName(asmPath);
        string selfDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

        if (!string.Equals(selfDir, targetDir, StringComparison.OrdinalIgnoreCase))
        {
            if (Environment.GetEnvironmentVariable(RELAUNCH_FLAG) == "1")
            {
                Console.WriteLine("!! 重启后仍然不在被测目录旁边，放弃");
                return;
            }

            string copy = Path.Combine(targetDir, "bench_run.exe");
            File.Copy(Assembly.GetExecutingAssembly().Location, copy, true);

            var psi = new ProcessStartInfo
            {
                FileName = copy,
                Arguments = "\"" + asmPath + "\" \"" + workDir + "\"" +
                            (filter != null ? " " + filter : ""),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.EnvironmentVariables[RELAUNCH_FLAG] = "1";

            var proc = Process.Start(psi);
            string output = proc.StandardOutput.ReadToEnd();
            string err = proc.StandardError.ReadToEnd();
            proc.WaitForExit();
            Console.Write(output);
            if (!string.IsNullOrEmpty(err))
            {
                Console.Write("--- 子进程 stderr ---\n" + err + "\n---\n");
            }
            try { if (File.Exists(copy)) File.Delete(copy); } catch { }
            return;
        }

        try
        {
            Run(filter);
        }
        catch (Exception ex)
        {
            Console.WriteLine("!! 基准执行异常: " + ex);
        }
    }

    static void Run(string filter)
    {
        asm = Assembly.LoadFrom(asmPath);
        AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
        {
            string simple = new AssemblyName(e.Name).Name;
            string p = Path.Combine(Path.GetDirectoryName(asmPath), simple + ".exe");
            return File.Exists(p) ? Assembly.LoadFrom(p) : null;
        };

        Console.WriteLine("=== Auto7z 性能基准 ===");
        Console.WriteLine("被测程序: " + asmPath);
        Console.WriteLine("工作目录: " + workDir);
        Console.WriteLine("");

        if (Directory.Exists(workDir)) Directory.Delete(workDir, true);
        Directory.CreateDirectory(workDir);

        Console.WriteLine("生成测试数据 ...");
        var big = Path.Combine(workDir, "big.bin");
        var bigText = Path.Combine(workDir, "big.txt");
        var folder = Path.Combine(workDir, "folder");
        Directory.CreateDirectory(folder);

        long warmupBytes = 0;
        using (var fs = File.Create(big))
        {
            var rnd = new Random(12345);
            var chunk = new byte[1 << 20];
            for (int i = 0; i < 256; i++)
            {
                rnd.NextBytes(chunk);
                fs.Write(chunk, 0, chunk.Length);
                warmupBytes += chunk.Length;
            }
        }

        using (var fs = File.Create(bigText))
        {
            var text = new StringBuilder();
            for (int i = 0; i < 200000; i++) text.Append("The quick brown fox jumps over the lazy dog.\n");
            var bytes = Encoding.UTF8.GetBytes(text.ToString());
            fs.Write(bytes, 0, bytes.Length);
            warmupBytes += bytes.Length;
        }

        var smallDir = Path.Combine(folder, "many");
        Directory.CreateDirectory(smallDir);
        var smallFiles = new List<string>();
        for (int i = 0; i < 5000; i++)
        {
            string p = Path.Combine(smallDir, "f" + i.ToString("D5") + ".bin");
            File.WriteAllBytes(p, new byte[4096]);
            smallFiles.Add(p);
        }

        Console.WriteLine("数据: " + (warmupBytes / (1024 * 1024.0)).ToString("F1") + " MB, "
                          + smallFiles.Count + " 个小文件");
        Console.WriteLine("");

        var typeSHA256 = asm.GetType("Auto7z_WPF.Services.SHA256Service");
        var typeSeven = asm.GetType("Auto7z_WPF.Services.SevenZipService");
        var typeConfig = asm.GetType("Auto7z_WPF.Services.ConfigService");
        var typeFs = asm.GetType("Auto7z_WPF.Services.FileSystemHelper");

        if (filter == null || filter == "sha256")
        {
            Safe("B1/B2/B3 SHA256", () =>
            {
                BenchSha256(typeSHA256, big);
                BenchSha256Raw(big);
                BenchSha256Text(typeSHA256, bigText);
            });
        }

        if (filter == null || filter == "compress")
        {
            Safe("B4 压缩", () => BenchCompress(typeSeven, bigText));
        }

        if (filter == null || filter == "extract")
        {
            Safe("B5 解压", () => BenchExtract(typeSeven, workDir));
        }

        if (filter == null || filter == "config")
        {
            Safe("B6 配置", () => BenchConfig(typeConfig));
        }

        if (filter == null || filter == "scan")
        {
            Safe("B7/B8 扫描与命名", () =>
            {
                BenchScan(typeFs, smallDir);
                BenchFolderName(typeFs);
            });
        }

        if (filter == null || filter == "receive")
        {
            Safe("B9 接收拖入", () => BenchReceive(asm));
        }

        Console.WriteLine("");
        Console.WriteLine("=== 基准结束 ===");
    }

    static object NewInstance(Type t)
    {
        return Activator.CreateInstance(t);
    }

    static string GetString(object o, string prop)
    {
        return (string)o.GetType().GetProperty(prop).GetValue(o);
    }

    static void BenchSha256(Type sha256Type, string file)
    {
        Console.WriteLine("[B1] SHA256Service.CalculateSHA256 —— 单文件 256MB");
        if (sha256Type == null) { Console.WriteLine("  !! 找不到 SHA256Service"); return; }

        var svc = NewInstance(sha256Type);
        var method = sha256Type.GetMethod("CalculateSHA256");

        for (int i = 0; i < 3; i++)
            method.Invoke(svc, new object[] { new[] { file }, null, CancellationToken.None });

        const int iters = 5;
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < iters; i++)
            method.Invoke(svc, new object[] { new[] { file }, null, CancellationToken.None });
        sw.Stop();

        double mb = (double)new FileInfo(file).Length * iters / (1024.0 * 1024.0);
        Console.WriteLine(Out("sha256.service", mb, sw.Elapsed.TotalSeconds, iters));
    }

    static void BenchSha256Raw(string file)
    {
        Console.WriteLine("[B2] 裸 SHA256.Create() 流式 —— 同一份文件的算法/IO 上限");
        long size = new FileInfo(file).Length;
        byte[] buf = new byte[4 * 1024 * 1024];

        for (int i = 0; i < 2; i++)
        {
            using (var sha256 = SHA256.Create())
            using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096,
                       FileOptions.SequentialScan))
            {
                int r;
                while ((r = fs.Read(buf, 0, buf.Length)) > 0) sha256.TransformBlock(buf, 0, r, buf, 0);
                sha256.TransformFinalBlock(buf, 0, 0);
            }
        }

        const int iters = 5;
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < iters; i++)
        {
            using (var sha256 = SHA256.Create())
            using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096,
                       FileOptions.SequentialScan))
            {
                int r;
                while ((r = fs.Read(buf, 0, buf.Length)) > 0) sha256.TransformBlock(buf, 0, r, buf, 0);
                sha256.TransformFinalBlock(buf, 0, 0);
            }
        }
        sw.Stop();

        double mb = (double)size * iters / (1024 * 1024.0);
        Console.WriteLine(Out("sha256.raw", mb, sw.Elapsed.TotalSeconds, iters));
    }

    static void BenchSha256Text(Type sha256Type, string file)
    {
        Console.WriteLine("[B3] SHA256Service.CalculateSHA256 —— 可压缩文本（接近真实压缩包场景）");
        if (sha256Type == null) return;

        var svc = NewInstance(sha256Type);
        var method = sha256Type.GetMethod("CalculateSHA256");

        for (int i = 0; i < 3; i++)
            method.Invoke(svc, new object[] { new[] { file }, null, CancellationToken.None });

        const int iters = 5;
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < iters; i++)
            method.Invoke(svc, new object[] { new[] { file }, null, CancellationToken.None });
        sw.Stop();

        double mb = (double)new FileInfo(file).Length * iters / (1024.0 * 1024.0);
        Console.WriteLine(Out("sha256.service.text", mb, sw.Elapsed.TotalSeconds, iters));
    }

    static bool ComponentsReady(object sevenSvc, Type sevenType)
    {
        var ensure = sevenType.GetMethod("EnsureComponents", new[] { typeof(Exception).MakeByRefType() });
        object[] args = new object[] { null };
        bool ok = (bool)ensure.Invoke(sevenSvc, args);
        if (ok) return true;
        Console.WriteLine("  !! 7-Zip 组件不可用，跳过这一项：" + (args[0] ?? "(无详情)"));
        return false;
    }

    static void BenchCompress(Type sevenType, string file)
    {
        Console.WriteLine("[B4] SevenZipService 压缩可压缩文本");
        if (sevenType == null) { Console.WriteLine("  !! 找不到 SevenZipService"); return; }

        var svc = NewInstance(sevenType);
        if (!ComponentsReady(svc, sevenType)) return;

        var build = sevenType.GetMethod("BuildCompressCommands");
        var exec = sevenType.GetMethod("ExecuteCommand",
            new[] { typeof(string), typeof(Action<int>), typeof(CancellationToken), typeof(TimeSpan?) });
        var configType = svc.GetType().Assembly.GetType("Auto7z_WPF.Services.AppConfig");
        var config = NewInstance(configType);
        configType.GetProperty("Format").SetValue(config, Enum.Parse(configType.Assembly.GetType("Auto7z_WPF.Models.ArchiveFormat"), "SevenZip"));
        configType.GetProperty("CompressionLevel").SetValue(config, Enum.Parse(configType.Assembly.GetType("Auto7z_WPF.Models.CompressionLevelPreset"), "Normal"));
        configType.GetProperty("RuntimeMode").SetValue(config, Enum.Parse(configType.Assembly.GetType("Auto7z_WPF.Models.RuntimeModePreset"), "Balanced"));
        configType.GetProperty("Volume").SetValue(config, "0");
        configType.GetProperty("Zstd").SetValue(config, false);
        configType.GetProperty("GenerateSHA256").SetValue(config, false);

        string outDir = Path.Combine(workDir, "out");
        Directory.CreateDirectory(outDir);
        string archive = Path.Combine(outDir, "bench.7z");

        var cmds = (IList<string>)build.Invoke(svc, new object[]
        {
            new[] { file }, config, archive
        });

        for (int i = 0; i < 2; i++)
        {
            foreach (string c in cmds)
                exec.Invoke(svc, new object[] { c, null, CancellationToken.None, null });
            try { if (File.Exists(archive)) File.Delete(archive); } catch { }
        }

        const int iters = 3;
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < iters; i++)
        {
            foreach (string c in cmds)
                exec.Invoke(svc, new object[] { c, null, CancellationToken.None, null });
            try { if (File.Exists(archive)) File.Delete(archive); } catch { }
        }
        sw.Stop();

        double mb = (double)new FileInfo(file).Length * iters / (1024.0 * 1024.0);
        Console.WriteLine(Out("compress.text", mb, sw.Elapsed.TotalSeconds, iters));
    }

    static void BenchExtract(Type sevenType, string baseDir)
    {
        Console.WriteLine("[B5] SevenZipService 解压");
        if (sevenType == null) return;

        var svc = NewInstance(sevenType);
        if (!ComponentsReady(svc, sevenType)) return;

        var build = sevenType.GetMethod("BuildExtractCommand");
        var exec = sevenType.GetMethod("ExecuteCommand",
            new[] { typeof(string), typeof(Action<int>), typeof(CancellationToken), typeof(TimeSpan?) });

        string archive = Path.Combine(workDir, "big.bin.7z");
        string src = Path.Combine(workDir, "big.bin");
        var appPathsType = asm.GetType("Auto7z_WPF.Services.AppPaths");
        string sevenZipExe = Path.Combine(
            (string)appPathsType.GetProperty("ComponentsDirectory").GetValue(null), "7z.exe");
        if (!File.Exists(sevenZipExe))
        {
            Console.WriteLine("  !! 找不到 7z.exe（" + sevenZipExe + "），跳过解压基准");
            return;
        }

        var psi = new ProcessStartInfo(sevenZipExe,
            "a -y -aoa -t7z \"" + archive + "\" \"" + src + "\"")
        { UseShellExecute = false };
        using (var p = Process.Start(psi)) { p.WaitForExit(30000); }

        string dest = Path.Combine(workDir, "extracted");
        Directory.CreateDirectory(dest);

        string cmd = (string)build.Invoke(svc, new object[] { archive, dest, null });

        for (int i = 0; i < 2; i++)
        {
            exec.Invoke(svc, new object[] { cmd, null, CancellationToken.None, null });
            Directory.Delete(dest, true);
            Directory.CreateDirectory(dest);
        }

        const int iters = 3;
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < iters; i++)
        {
            exec.Invoke(svc, new object[] { cmd, null, CancellationToken.None, null });
            Directory.Delete(dest, true);
            Directory.CreateDirectory(dest);
        }
        sw.Stop();

        double mb = (double)new FileInfo(src).Length * iters / (1024.0 * 1024.0);
        Console.WriteLine(Out("extract.bin", mb, sw.Elapsed.TotalSeconds, iters));
    }

    static void BenchConfig(Type configType)
    {
        Console.WriteLine("[B6] ConfigService 读/写");
        if (configType == null) { Console.WriteLine("  !! 找不到 ConfigService"); return; }

        var svc = NewInstance(configType);
        var load = configType.GetMethod("Load");
        var save = configType.GetMethod("Save");

        var appConfigType = asm.GetType("Auto7z_WPF.Services.AppConfig");
        if (appConfigType == null) { Console.WriteLine("  !! 找不到 AppConfig"); return; }
        var appConfig = NewInstance(appConfigType);

        for (int i = 0; i < 50; i++) load.Invoke(svc, null);

        const int iters = 200;
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < iters; i++) load.Invoke(svc, null);
        sw.Stop();
        Console.WriteLine(Out("config.load", iters, sw.Elapsed.TotalSeconds, iters));

        sw.Restart();
        for (int i = 0; i < iters; i++) save.Invoke(svc, new object[] { appConfig });
        sw.Stop();
        Console.WriteLine(Out("config.save", iters, sw.Elapsed.TotalSeconds, iters));
    }

    static void BenchScan(Type fsType, string dir)
    {
        Console.WriteLine("[B7] FileSystemHelper.GetSizeInBytes —— " + dir);
        if (fsType == null) { Console.WriteLine("  !! 找不到 FileSystemHelper"); return; }

        var method = fsType.GetMethod("GetSizeInBytes", BindingFlags.Public | BindingFlags.Static);

        for (int i = 0; i < 3; i++) method.Invoke(null, new object[] { dir });

        const int iters = 10;
        var sw = Stopwatch.StartNew();
        long acc = 0;
        for (int i = 0; i < iters; i++) acc += (long)method.Invoke(null, new object[] { dir });
        sw.Stop();

        Console.WriteLine(Out("scan.folder.5000", iters, sw.Elapsed.TotalSeconds, iters));
    }

    static void BenchFolderName(Type fsType)
    {
        Console.WriteLine("[B8] FileSystemHelper.GenerateSafeFolderName");
        if (fsType == null) return;

        var method = fsType.GetMethod("GenerateSafeFolderName", BindingFlags.Public | BindingFlags.Static);
        if (method == null) { Console.WriteLine("  !! 找不到 GenerateSafeFolderName"); return; }

        for (int i = 0; i < 1000; i++) method.Invoke(null, null);

        const int iters = 20000;
        var sw = Stopwatch.StartNew();
        string acc = null;
        for (int i = 0; i < iters; i++) acc = (string)method.Invoke(null, null);
        sw.Stop();

        Console.WriteLine(Out("foldername.gen", iters, sw.Elapsed.TotalSeconds, iters));
    }

    static void BenchReceive(Assembly asm)
    {
        Console.WriteLine("[B9] Auto7zViewModel.SetPendingFiles —— 拖入 5000 个文件的目录");
        var vmType = asm.GetType("Auto7z_WPF.Auto7zViewModel");
        if (vmType == null) { Console.WriteLine("  !! 找不到 Auto7zViewModel"); return; }

        var folder = Path.Combine(workDir, "folder", "many");
        var setPending = vmType.GetMethod("SetPendingFiles", new[] { typeof(string[]) });
        var hasPending = vmType.GetProperty("HasPendingFiles");

        object vm = Activator.CreateInstance(vmType);

        for (int i = 0; i < 5; i++)
            setPending.Invoke(vm, new object[] { new[] { folder } });

        const int iters = 20;
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < iters; i++)
        {
            setPending.Invoke(vm, new object[] { new[] { folder } });
            if (!(bool)hasPending.GetValue(vm)) Console.WriteLine("  !! 批次为空");
        }
        sw.Stop();

        Console.WriteLine(Out("receive.pending.5000", iters, sw.Elapsed.TotalSeconds, iters));
    }

    static string Out(string name, double workUnits, double seconds, int iters)
    {
        double per = seconds / Math.Max(1, iters);
        return "BENCH " + name + " units=" + workUnits.ToString("F2") + " iters=" + iters
               + " seconds=" + seconds.ToString("F4")
               + " per_op_ms=" + (per * 1000.0).ToString("F3")
               + " throughput=" + (workUnits / Math.Max(1e-9, seconds)).ToString("F2");
    }

    static void Safe(string label, Action body)
    {
        try
        {
            body();
        }
        catch (Exception ex)
        {
            Console.WriteLine("  !! " + label + " 失败: " + ex.GetType().Name + " " + ex.Message);
        }
    }
}
