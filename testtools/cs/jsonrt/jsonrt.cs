using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

class Case
{
    public string Key; public string Val; public bool IsBool;
    public Case(string k, string v, bool b) { Key = k; Val = v; IsBool = b; }
}

class JsonRoundTripProbe
{
    static int problems = 0;
    static void Check(string name, bool ok, string detail)
    {
        Console.WriteLine((ok ? "  OK   " : "  !!   ") + name + (detail.Length > 0 ? "  --- " + detail : ""));
        if (!ok) problems++;
    }

    static int Main(string[] a)
    {
        if (a.Length < 2) { Console.WriteLine("usage: jsonrt.exe <Auto7z.exe> <workdir>"); return 2; }
        AppDomain.CurrentDomain.AssemblyResolve += delegate(object s, ResolveEventArgs e)
        {
            string simple = new AssemblyName(e.Name).Name;
            string p = Path.Combine(Path.GetDirectoryName(a[0]), simple + ".exe");
            return File.Exists(p) ? Assembly.LoadFrom(p) : null;
        };
        Assembly asm = Assembly.LoadFrom(a[0]);
        Type t = asm.GetType("Auto7z_WPF.Services.JsonConfig");
        MethodInfo build = t.GetMethod("Build");
        MethodInfo parse = t.GetMethod("Parse");
        MethodInfo writeFile = t.GetMethod("WriteFile");

        Console.WriteLine("=== Build -> Parse round trip ===");
        List<Case> cases = new List<Case>();
        cases.Add(new Case("Plain", "hello", false));
        cases.Add(new Case("Chinese", "\u4e2d\u6587\u503c", false));
        cases.Add(new Case("Quote", "a\"b", false));
        cases.Add(new Case("Backslash", "C:\\path\\to\\file", false));
        cases.Add(new Case("Newline", "line1\nline2", false));
        cases.Add(new Case("Tab", "a\tb", false));
        cases.Add(new Case("Number", "2000", false));
        cases.Add(new Case("NegNumber", "-42", false));
        cases.Add(new Case("BoolTrue", "True", true));
        cases.Add(new Case("BoolFalse", "False", true));
        cases.Add(new Case("Empty", "", false));
        cases.Add(new Case("Spaces", "  padded  ", false));

        List<KeyValuePair<string, string>> entries = new List<KeyValuePair<string, string>>();
        List<bool> bools = new List<bool>();
        foreach (Case c in cases)
        {
            entries.Add(new KeyValuePair<string, string>(c.Key, c.Val));
            bools.Add(c.IsBool);
        }

        string json = (string)build.Invoke(null, new object[] { entries, bools });
        Console.WriteLine("--- generated JSON ---");
        Console.WriteLine(json);

        Dictionary<string, string> back = parse.Invoke(null, new object[] { json }) as Dictionary<string, string>;
        Check("Parse can read back what Build wrote", back != null, "");

        if (back != null)
        {
            foreach (Case c in cases)
            {
                string got;
                if (!back.TryGetValue(c.Key, out got)) got = "(missing)";
                if (c.IsBool)
                {
                    string expect = string.Equals(c.Val, "true", StringComparison.OrdinalIgnoreCase) ? "true" : "false";
                    Check("bool " + c.Key, string.Equals(got, expect, StringComparison.OrdinalIgnoreCase), "expect " + expect + " got " + got);
                }
                else if (c.Key == "Number" || c.Key == "NegNumber")
                {
                    Check("number " + c.Key + " stays unquoted", got == c.Val, "expect " + c.Val + " got " + got);
                }
                else
                {
                    Check("string " + c.Key, got == c.Val, got == c.Val ? "" : "expect [" + c.Val + "] got [" + got + "]");
                }
            }
        }

        Console.WriteLine("");
        Console.WriteLine("=== WriteFile boundary ===");
        string work = a[1];
        Directory.CreateDirectory(work);
        string allowed = Path.Combine(work, "allowed");
        Directory.CreateDirectory(allowed);

        string good = Path.Combine(allowed, "ok.json");
        try { writeFile.Invoke(null, new object[] { good, "{}", allowed }); Check("inside root writable", File.Exists(good), ""); }
        catch (Exception ex) { Exception inner = ex is TargetInvocationException ? ex.InnerException : ex; Check("inside root writable", false, "threw: " + inner.Message); }

        string bad = Path.Combine(work, "outside.json");
        bool refused = false;
        try { writeFile.Invoke(null, new object[] { bad, "{}", allowed }); }
        catch (TargetInvocationException tie) { refused = tie.InnerException is UnauthorizedAccessException; }
        catch { refused = true; }
        Check("outside root refused", refused, refused ? "" : "no exception");
        Check("outside root wrote nothing", !File.Exists(bad), "");

        bool nullRefused = false;
        try { writeFile.Invoke(null, new object[] { Path.Combine(work, "nullroot.json"), "{}", null }); }
        catch (TargetInvocationException tie) { nullRefused = tie.InnerException is UnauthorizedAccessException; }
        catch { nullRefused = true; }
        Check("allowedRoot=null refused", nullRefused, "");

        Console.WriteLine("");
        Console.WriteLine(problems == 0 ? "PASS" : "TOTAL PROBLEMS: " + problems);
        return problems == 0 ? 0 : 1;
    }
}