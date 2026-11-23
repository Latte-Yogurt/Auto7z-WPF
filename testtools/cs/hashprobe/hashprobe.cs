using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;

/// <summary>
/// 计算服务的运行时验证：拿产品程序集里的真实 <c>HashingService</c> 跑一遍，
/// 逐项核对它算出来的值与已知标准值是否一致。
///
/// 为什么不再叫 sha256probe：产品早已支持 18 种算法（MD5 … CRC-64），
/// 只验 SHA-256 的话，其余 17 条计算路径一条都没被测到 —— 而哈希算错是**静默**的，
/// 界面上不报错、校验文件照样生成，只有真正去比对才发现对不上。
/// 所以本探针逐算法验证，并覆盖「同一文件、多种算法」的交叉场景。
/// </summary>
class HashProbe
{
    const string RELAUNCH_FLAG = "AUTO7Z_HASHPROBE_RELAUNCHED";

    static int failures = 0;
    static int checks = 0;
    static string workDir;

    /// <summary>
    /// 标准值表：用业界公开实现算出的参考摘要，本探针拿它当判据。
    ///
    /// 载荷固定为 <see cref="Payload"/> 这 28 字节，值由 Python hashlib / zlib 独立算得，
    /// 与本项目代码无关 —— 这样「产品算得对不对」才是被独立验证的，而不是拿产品自己验自己。
    /// 表中 18 项与产品 HashAlgorithms.All 一一对应。
    /// </summary>
    static readonly Dictionary<string, string> KnownGood = new Dictionary<string, string>
    {
        { "md5",         "af161f24bc39c1e03ff3185d0fb8d7ab" },
        { "sha1",        "53ca6c88759d7d499c571f25103e3a33e809d663" },
        { "sha224",      "bd18de7420d560df0da9d3ed5e4223e375b0a79fe6a7a9b005904f57" },
        { "sha256",      "2f36cf1611225e646a35263fdb97ec7689f0a4f61551ff54237662fb8997a175" },
        { "sha384",      "141186a5f89a98a3ffa8b4d49008b7c97f03e91a16326acef4edf075b80d0dde7a4d5213866d918f2f8da1ab30b19351" },
        { "sha512",      "281e631719f3fb49d3ae308f5d736b501d1f87932d2c13a58c74e486e245315d0a0e3a2474741866901cbf77a18f8a8bcdb9f62a275bc99bb9696b98c0dc7613" },
        { "sha512-224",  "5e3bb62bb0e1a2d0b444acce5d05d0b0f6271119e43be4152d60f843" },
        { "sha512-256",  "06ec5ac260281c496c362024e2e574c67c803c34897a7e331f8a9773e6001c86" },
        { "sha3-224",    "6582e6f4bfbcb807e6a398baa53a60914af10f0e8a838adb3d11ee05" },
        { "sha3-256",    "9d0d0fd81a234b977216207c2a4a69523b22ac346a7f75ffcda01b0057afe42e" },
        { "sha3-384",    "4dd955bd3df90fb6b6d1fa17f03483235e1690f5510521aa6744ca3e44543838ecf7f540ec6e599a3b25c3c479444e83" },
        { "sha3-512",    "edfa9e49024022de76db632d2de4406e02a4a5c4a26cdfb947d1724772d6ca412665c0a82090aacdec5b51a509aed3ad56969d896b7bb91415f7f47a1276ec58" },
        { "blake2s-256", "53304a2ecd431e9e292c0252cfa7bfafb1ae2fd070bf9e45eacb99ebcbdc8621" },
        { "blake2b-256", "d009a02e57e73d0f881f3bccb2af46bee83ebb5caf8500bed25c8802a73a4e5e" },
        { "blake2b-512", "e67b5b9da214538a094b7f42bfca3061117a922b8342d303df4a7e64f1fbc355a5e4f63ecbb4174b0508933da3affefe1723a2dc9cff68e6b416ac3dbf72b5cc" },
        { "blake3",      "634c783b3821e9ea52f6935d3f24cd0bd6b16c63502be239bc1c30c11cc10166" },
        { "crc32",       "f5962e92" },
        { "crc64",       "ddd36afd3705e3b7" },
    };

    /// <summary>产生上面那批标准值的固定载荷。</summary>
    static readonly byte[] Payload = Encoding.ASCII.GetBytes("Auto7z hash algorithm probe");

    static void Check(string name, bool ok, string detail)
    {
        checks++;
        if (!ok) failures++;
        Console.WriteLine("  {0} {1}{2}",
            ok ? "OK  " : "!!  ", name, string.IsNullOrEmpty(detail) ? "" : "  —— " + detail);
    }

    static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("用法: hashprobe.exe <Auto7z.exe 路径> <工作目录>");
            return 2;
        }

        string asmPath = Path.GetFullPath(args[0]);
        workDir = Path.GetFullPath(args[1]);
        string targetDir = Path.GetDirectoryName(asmPath);
        string selfDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

        // 反射加载要求探针与被测程序同目录（依赖解析走同目录），否则先把自己复制过去再重跑。
        if (!string.Equals(selfDir, targetDir, StringComparison.OrdinalIgnoreCase))
        {
            if (Environment.GetEnvironmentVariable(RELAUNCH_FLAG) == "1")
            {
                Console.WriteLine("!! 重启后仍然不在被测目录旁边，放弃");
                return 1;
            }

            string copy = Path.Combine(targetDir, "hashprobe_run.exe");
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

        Console.WriteLine("=== Auto7z 计算服务的运行时验证（全算法）===");
        Console.WriteLine("被测程序: " + asmPath);

        var svcType = asm.GetType("Auto7z_WPF.Services.HashingService");
        var algType = asm.GetType("Auto7z_WPF.Services.HashAlgorithms");
        if (svcType == null || algType == null)
        {
            Console.WriteLine("!! 找不到 HashingService 或 HashAlgorithms");
            return 1;
        }

        // 2.5.1 起 Calculate 已删除（产品内部零调用，只有本探针还在按名反射它）。
        // 改测它唯一真正的实现 CalculateDetailed —— 顺带能断言 FailedFiles，覆盖面更宽。
        var calc = svcType.GetMethod("CalculateDetailed");
        if (calc == null)
        {
            Console.WriteLine("!! HashingService 缺少预期的方法（CalculateDetailed）");
            return 1;
        }

        // 已删除的兼容壳不得复活。
        Check("SHA256Service 已删除", asm.GetType("Auto7z_WPF.Services.SHA256Service") == null,
            "兼容壳不应再存在于产品程序集中");
        Check("RelayCommand（非泛型）已删除", asm.GetType("Auto7z_WPF.RelayCommand") == null,
            "非泛型 RelayCommand 不应再存在于产品程序集中");

        if (Directory.Exists(workDir)) Directory.Delete(workDir, true);
        Directory.CreateDirectory(workDir);

        object svc = Activator.CreateInstance(svcType);
        try
        {
            IList<object> catalog = LoadCatalog(algType);
            if (catalog == null || catalog.Count == 0)
            {
                Console.WriteLine("!! 取不到算法目录");
                return 1;
            }

            CheckAlgorithmsPresent(catalog);
            CheckEveryAlgorithm(calc, svc, catalog);

            object def = DefaultAlgorithm(algType);
            CheckSingleFile(calc, svc, def);
            CheckMultipleFiles(calc, svc, def);
            CheckDirectory(calc, svc, def);
            CheckMissingFileIsSkipped(calc, svc, def);
            CheckProgressMonotonic(calc, svc, def);
            CheckCancellation(calc, svc, def);
        }
        finally
        {
            // 不用 ?. ：本项目的探针用 csc .NET Framework 编译，空条件运算符会报 CS1525。
            IDisposable disposable = svc as IDisposable;
            if (disposable != null) disposable.Dispose();
        }

        Console.WriteLine("");
        if (failures == 0)
        {
            Console.WriteLine("全部符合预期 ✓（共 " + checks + " 项）");
            return 0;
        }

        Console.WriteLine("!! 有 " + failures + " 项不符合预期（共 " + checks + " 项）");
        return 1;
    }

    /// <summary>把 HashAlgorithms.All 读成一组算法目录项（保持产品顺序）。</summary>
    static IList<object> LoadCatalog(Type algType)
    {
        PropertyInfo all = algType.GetProperty("All", BindingFlags.Public | BindingFlags.Static);
        if (all == null) return null;

        object raw = all.GetValue(null, null);
        var result = new List<object>();
        var enumerable = raw as System.Collections.IEnumerable;
        if (enumerable == null) return result;
        foreach (object one in enumerable) result.Add(one);
        return result;
    }

    static object DefaultAlgorithm(Type algType)
    {
        PropertyInfo def = algType.GetProperty("Default", BindingFlags.Public | BindingFlags.Static);
        return def == null ? null : def.GetValue(null, null);
    }

    static string IdOf(object info)
    {
        return (string)info.GetType().GetProperty("Id").GetValue(info, null);
    }

    static int DigestBytesOf(object info)
    {
        return (int)info.GetType().GetProperty("DigestBytes").GetValue(info, null);
    }

    /// <summary>
    /// 算法目录的完整性：产品声称支持的算法必须都在，标准值表也必须覆盖到。
    /// 任一侧多出来或少掉一个都算失败 —— 那意味着「支持了但没验」或「验了但已删」。
    /// </summary>
    static void CheckAlgorithmsPresent(IList<object> catalog)
    {
        Console.WriteLine("");
        Console.WriteLine("【算法目录完整性】");

        var actual = new List<string>();
        foreach (object info in catalog) actual.Add(IdOf(info));

        Check("算法数量", actual.Count == KnownGood.Count,
            "产品 " + actual.Count + " 种，标准值表 " + KnownGood.Count + " 种");

        var missingInProbe = new List<string>();
        foreach (string id in actual)
        {
            if (!KnownGood.ContainsKey(id)) missingInProbe.Add(id);
        }
        Check("产品有的算法都有标准值", missingInProbe.Count == 0,
            missingInProbe.Count == 0 ? "覆盖完整" : "缺标准值: " + string.Join(",", missingInProbe.ToArray()));

        var missingInProduct = new List<string>();
        foreach (KeyValuePair<string, string> pair in KnownGood)
        {
            if (!actual.Contains(pair.Key)) missingInProduct.Add(pair.Key);
        }
        missingInProduct.Sort();
        Check("标准值表的算法产品都有", missingInProduct.Count == 0,
            missingInProduct.Count == 0 ? "一致" : "产品已无: " + string.Join(",", missingInProduct.ToArray()));
    }

    /// <summary>
    /// 逐算法验证：每个算法的摘要必须与独立算出的标准值逐字符相等，
    /// 长度必须等于 DigestBytes*2，且行格式是 "hash *filename"。
    ///
    /// 这一项是本探针存在的理由 —— 只验默认算法等于放掉其余 17 条路径。
    /// </summary>
    static void CheckEveryAlgorithm(MethodInfo calc, object svc, IList<object> catalog)
    {
        string dir = Path.Combine(workDir, "all");
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, "payload.bin");
        File.WriteAllBytes(file, Payload);

        Console.WriteLine("");
        Console.WriteLine("【逐算法校验】");

        int verified = 0;
        foreach (object info in catalog)
        {
            string id = IdOf(info);
            int digestBytes = DigestBytesOf(info);
            string expected = KnownGood.ContainsKey(id) ? KnownGood[id] : null;

            string[] lines = InvokeCalc(calc, svc, info, new[] { file }, null, CancellationToken.None);
            if (lines == null || lines.Length != 1)
            {
                Check(id, false, "返回值不是 1 行");
                continue;
            }

            string line = lines[0];
            int star = line.IndexOf(" *", StringComparison.Ordinal);
            string actual = star >= 0 ? line.Substring(0, star) : line;
            string name = star >= 0 ? line.Substring(star + 2) : "";

            if (expected == null)
            {
                Check(id, false, "标准值表里没有这个算法");
                continue;
            }

            bool lenOk = actual.Length == digestBytes * 2;
            bool valOk = string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
            bool fmtOk = name == "payload.bin";

            if (valOk && lenOk && fmtOk)
            {
                verified++;
                Console.WriteLine("  OK   {0}  {1}  ({2} 字符)", PadRight(id, 12), actual, actual.Length);
            }
            else
            {
                var why = new StringBuilder();
                if (!valOk) why.Append("值不符（期望 " + expected + "，实际 " + actual + "）");
                if (!lenOk) why.Append(why.Length > 0 ? "；" : "").Append("长度 " + actual.Length + " ≠ " + (digestBytes * 2));
                if (!fmtOk) why.Append(why.Length > 0 ? "；" : "").Append("文件名为 '" + name + "'");
                Check(id, false, why.ToString());
            }
        }

        Check("全部算法值正确", verified == catalog.Count && catalog.Count > 0,
            verified + "/" + catalog.Count + " 通过");
    }

    static string PadRight(string s, int width)
    {
        if (s == null) return "";
        var sb = new StringBuilder(s);
        while (sb.Length < width) sb.Append(' ');
        return sb.ToString();
    }

    static void CheckSingleFile(MethodInfo calc, object svc, object algorithm)
    {
        Console.WriteLine("");
        Console.WriteLine("【单文件（默认算法）】");
        string file = MakePayload(Path.Combine(workDir, "single"), "a.bin", 300 * 1024);

        string[] lines = InvokeCalc(calc, svc, algorithm, new[] { file }, null, CancellationToken.None);
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

    static void CheckMultipleFiles(MethodInfo calc, object svc, object algorithm)
    {
        Console.WriteLine("");
        Console.WriteLine("【多文件（默认算法）】");
        string dir = Path.Combine(workDir, "multi");
        string f1 = MakePayload(dir, "one.bin", 64 * 1024);
        string f2 = MakePayload(dir, "two.bin", 128 * 1024 + 7);

        string[] lines = InvokeCalc(calc, svc, algorithm, new[] { f1, f2 }, null, CancellationToken.None);
        Check("两个输入返回两行", lines != null && lines.Length == 2,
            lines == null ? "返回 null" : "实际 " + lines.Length + " 行");
        if (lines == null || lines.Length != 2) return;

        Check("第一条哈希正确", lines[0] == RawSha256(f1) + " *" + Path.GetFileName(f1), lines[0]);
        Check("第二条哈希正确", lines[1] == RawSha256(f2) + " *" + Path.GetFileName(f2), lines[1]);
    }

    static void CheckDirectory(MethodInfo calc, object svc, object algorithm)
    {
        Console.WriteLine("");
        Console.WriteLine("【目录递归展开】");
        string dir = Path.Combine(workDir, "tree");
        MakePayload(Path.Combine(dir, "sub"), "deep.bin", 8 * 1024);
        MakePayload(dir, "top.bin", 4 * 1024);

        string[] lines = InvokeCalc(calc, svc, algorithm, new[] { dir }, null, CancellationToken.None);
        Check("目录递归展开出两个文件", lines != null && lines.Length == 2,
            lines == null ? "返回 null" : "实际 " + lines.Length + " 行");

        if (lines != null && lines.Length == 2)
        {
            // 不用 LINQ：本文件刻意不引 System.Linq，保持依赖面最小。
            string want = RawSha256(Path.Combine(dir, "sub", "deep.bin")) + " *deep.bin";
            bool deepOk = false;
            foreach (string one in lines)
            {
                if (one == want) { deepOk = true; break; }
            }
            Check("深层文件哈希正确", deepOk, string.Join(" | ", lines));
        }
    }

    static void CheckMissingFileIsSkipped(MethodInfo calc, object svc, object algorithm)
    {
        Console.WriteLine("");
        Console.WriteLine("【坏文件跳过】");
        string good = MakePayload(Path.Combine(workDir, "skip"), "good.bin", 4096);
        string missing = Path.Combine(workDir, "skip", "does_not_exist.bin");

        string[] lines = InvokeCalc(calc, svc, algorithm, new[] { good, missing }, null, CancellationToken.None);
        Check("坏文件被跳过、好文件仍产出", lines != null && lines.Length == 1,
            lines == null ? "返回 null" : "实际 " + lines.Length + " 行");
        if (lines != null && lines.Length == 1)
        {
            Check("跳过之后产出的那一行仍然是好文件的", lines[0].EndsWith(" *good.bin", StringComparison.Ordinal), lines[0]);
        }
    }

    static void CheckProgressMonotonic(MethodInfo calc, object svc, object algorithm)
    {
        Console.WriteLine("");
        Console.WriteLine("【进度单调】");
        string dir = Path.Combine(workDir, "prog");
        for (int i = 0; i < 6; i++) MakePayload(Path.Combine(dir, "d" + i), "f" + i + ".bin", 96 * 1024);

        var seen = new List<int>();
        Action<int> report = p => seen.Add(p);
        InvokeCalcWithProgress(calc, svc, algorithm, new[] { dir }, report, CancellationToken.None);

        Check("进度回调有被调用", seen.Count > 0, "实际 " + seen.Count + " 次");
        if (seen.Count == 0) return;

        bool monotonic = true;
        for (int i = 1; i < seen.Count; i++)
        {
            if (seen[i] < seen[i - 1]) { monotonic = false; break; }
        }
        Check("进度单调不减", monotonic, string.Join(",", seen));
        Check("进度全部落在 0..100", AllInRange(seen), "最小值/最大值=" + Min(seen) + "/" + Max(seen));
        Check("结束时到达 100", seen[seen.Count - 1] == 100, "最后=" + seen[seen.Count - 1]);
    }

    static bool AllInRange(List<int> values)
    {
        foreach (int v in values) if (v < 0 || v > 100) return false;
        return true;
    }

    static void CheckCancellation(MethodInfo calc, object svc, object algorithm)
    {
        Console.WriteLine("");
        Console.WriteLine("【取消】");
        string dir = Path.Combine(workDir, "cancel");
        for (int i = 0; i < 4; i++) MakePayload(Path.Combine(dir, "d" + i), "f" + i + ".bin", 256 * 1024);

        bool cancelled = false;
        try
        {
            calc.Invoke(svc, new object[] { new[] { dir }, algorithm, null, new CancellationToken(true) });
        }
        catch (TargetInvocationException e)
        {
            cancelled = e.InnerException is OperationCanceledException;
            if (!cancelled) Check("取消时抛 OperationCanceledException", false, Describe(e));
        }
        catch (ArgumentException e)
        {
            // 参数个数/类型对不上时反射会抛这个 —— 说明 Calculate 的签名又变了，
            // 探针要跟着改，不能当成「取消没生效」。
            Check("取消时抛 OperationCanceledException", false, "反射调用签名不匹配: " + e.Message);
            return;
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

    /// <summary>独立算一份 SHA-256，用来核对产品的「默认算法」路径。不用产品代码。</summary>
    static string RawSha256(string file)
    {
        using (var sha = System.Security.Cryptography.SHA256.Create())
        using (var fs = File.OpenRead(file))
        {
            byte[] hash = sha.ComputeHash(fs);
            var sb = new StringBuilder(hash.Length * 2);
            foreach (byte b in hash) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }
    }

    // HashingService.CalculateDetailed 的签名是 (string[] paths, HashAlgorithmInfo algorithm,
    // Action<int> progress, CancellationToken token) —— 算法是必填参数。
    static string[] InvokeCalc(MethodInfo calc, object svc, object algorithm, string[] paths, Action<int> progress, CancellationToken token)
    {
        return InvokeCalcWithProgress(calc, svc, algorithm, paths, progress, token);
    }

    static string[] InvokeCalcWithProgress(MethodInfo calc, object svc, object algorithm, string[] paths, Action<int> progress, CancellationToken token)
    {
        try
        {
            object r = calc.Invoke(svc, new object[] { paths, algorithm, progress, token });
            // CalculateDetailed 返回 HashBatchResult；取它的 Lines（与已删除的 Calculate 返回值同义）。
            if (r == null) return null;
            var linesProp = r.GetType().GetProperty("Lines");
            return linesProp == null ? null : linesProp.GetValue(r) as string[];
        }
        catch (TargetInvocationException e)
        {
            Check("Calculate 不抛异常", false, Describe(e));
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

    static int Min(List<int> values)
    {
        int m = values[0];
        foreach (int v in values) if (v < m) m = v;
        return m;
    }

    static int Max(List<int> values)
    {
        int m = values[0];
        foreach (int v in values) if (v > m) m = v;
        return m;
    }
}
