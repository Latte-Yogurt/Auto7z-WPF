using System;
using System.Collections.Generic;
using System.Reflection;

// 验证 HashAlgorithmInfo 的 Aliases / Extensions 加固：
// 把公开可写属性设为 null 之后，读取端仍应拿到空集合而不是 null，
// Extension 属性仍应返回 ".checksum" 兜底。
//
// 为什么必须实证：这两个属性是 public { get; set; }，
// 「外部可能写 null」是接口层面的真实可能，不是假想 ——
// 而 BuildCatalog 的静态构造里就有 foreach (info.Aliases)，
// 静态构造抛异常 → TypeInitializationException → 进程内再也用不了 HashAlgorithms。
class NullHardeningProbe
{
    static int problems = 0;

    static void Check(string n, bool ok, string d)
    {
        Console.WriteLine((ok ? "  OK   " : "  !!   ") + n + (d.Length > 0 ? "  --- " + d : ""));
        if (!ok) problems++;
    }

    static int Main(string[] a)
    {
        if (a.Length < 1) { Console.WriteLine("用法: nullhard.exe <Auto7z.exe>"); return 2; }

        AppDomain.CurrentDomain.AssemblyResolve += delegate(object s, ResolveEventArgs e)
        {
            string simple = new AssemblyName(e.Name).Name;
            string p = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(a[0]), simple + ".exe");
            return System.IO.File.Exists(p) ? Assembly.LoadFrom(p) : null;
        };

        Assembly asm = Assembly.LoadFrom(a[0]);
        Type infoType = asm.GetType("Auto7z_WPF.Services.HashAlgorithmInfo")
                     ?? asm.GetType("Auto7z_WPF.HashAlgorithmInfo");
        if (infoType == null)
        {
            // 可能嵌在 HashAlgorithms 里
            foreach (Type t in asm.GetTypes())
            {
                if (t.Name == "HashAlgorithmInfo") { infoType = t; break; }
            }
        }
        if (infoType == null) { Console.WriteLine("!! 找不到 HashAlgorithmInfo"); return 1; }

        Console.WriteLine("=== Aliases / Extensions 的 null 加固 ===");
        Console.WriteLine("被测类型: " + infoType.FullName);
        Console.WriteLine("");

        PropertyInfo extProp = infoType.GetProperty("Extensions");
        PropertyInfo aliProp = infoType.GetProperty("Aliases");
        PropertyInfo ext0Prop = infoType.GetProperty("Extension");

        Check("Extensions 属性存在", extProp != null, "");
        Check("Aliases 属性存在", aliProp != null, "");
        Check("Extension（单数）属性存在", ext0Prop != null, "");
        if (extProp == null || aliProp == null || ext0Prop == null) return 1;

        // 1) 新建对象：默认不该是 null
        object info = Activator.CreateInstance(infoType);
        object e1 = extProp.GetValue(info, null);
        object a1 = aliProp.GetValue(info, null);
        Check("新建对象 Extensions 非 null", e1 != null, e1 == null ? "null" : "有值");
        Check("新建对象 Aliases 非 null", a1 != null, a1 == null ? "null" : "有值");

        // 2) 显式设为 null：读取端必须仍非 null
        extProp.SetValue(info, null, null);
        aliProp.SetValue(info, null, null);
        object e2 = extProp.GetValue(info, null);
        object a2 = aliProp.GetValue(info, null);
        Check("设 null 后 Extensions 仍非 null", e2 != null,
              e2 == null ? "!! 返回了 null，foreach 会 NRE" : "已兜底为空集合");
        Check("设 null 后 Aliases 仍非 null", a2 != null,
              a2 == null ? "!! 返回了 null，foreach 会 NRE" : "已兜底为空集合");

        // 3) Extension（单数）必须给出兜底后缀
        string ext = (string)ext0Prop.GetValue(info, null);
        Check("空集合时 Extension 返回 .checksum", ext == ".checksum", "实际=" + (ext ?? "null"));

        // 4) 能安全遍历（这是静态构造里真正做的事）
        int aliasCount = 0;
        foreach (object _ in (System.Collections.IEnumerable)a2) aliasCount++;
        Check("空 Aliases 可安全遍历", aliasCount == 0, "遍历到 " + aliasCount + " 项");

        int extCount = 0;
        foreach (object _ in (System.Collections.IEnumerable)e2) extCount++;
        Check("空 Extensions 可安全遍历", extCount == 0, "遍历到 " + extCount + " 项");

        // 5) 正常赋值仍要生效（不能因为加固把功能弄坏）
        extProp.SetValue(info, new List<string> { ".sha256", ".sha256sum" }, null);
        string ext2 = (string)ext0Prop.GetValue(info, null);
        Check("正常赋值后 Extension 取首项", ext2 == ".sha256", "实际=" + (ext2 ?? "null"));

        // 6) 算法目录整体仍可用
        Type algType = asm.GetType("Auto7z_WPF.Services.HashAlgorithms")
                    ?? asm.GetType("Auto7z_WPF.HashAlgorithms");
        if (algType != null)
        {
            PropertyInfo allProp = algType.GetProperty("All", BindingFlags.Public | BindingFlags.Static);
            object all = allProp != null ? allProp.GetValue(null, null) : null;
            int n = 0;
            if (all is System.Collections.IEnumerable) foreach (object _ in (System.Collections.IEnumerable)all) n++;
            Check("HashAlgorithms.All 可枚举且非空", n >= 18, "算法数=" + n);

            MethodInfo byId = algType.GetMethod("ById", BindingFlags.Public | BindingFlags.Static);
            if (byId != null)
            {
                object found = byId.Invoke(null, new object[] { "sha256" });
                Check("ById(\"sha256\") 命中", found != null, "");
            }
        }

        Console.WriteLine("");
        Console.WriteLine(problems == 0 ? "PASS" : "TOTAL PROBLEMS: " + problems);
        return problems == 0 ? 0 : 1;
    }
}
