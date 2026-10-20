using System;
using System.Runtime.InteropServices;
using System.Text;

// 外壳扩展的作用范围自检:
// 走一遍资源管理器的调用流程, 看各类目标下会不会出现菜单项。
//   普通文件 / 文件夹  -> 应该出现 2 项
//   回收站 / 虚拟路径  -> 不应该出现菜单项
class ScopeProbe
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr LoadLibrary(string file);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    static extern IntPtr GetProcAddress(IntPtr module, string name);

    [DllImport("user32.dll")]
    static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll")]
    static extern int GetMenuItemCount(IntPtr menu);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetMenuStringW(IntPtr menu, uint id, StringBuilder text, int max, uint flag);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int GetClassObjectDelegate(IntPtr clsid, IntPtr iid, out IntPtr obj);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int CreateInstanceDelegate(IntPtr self, IntPtr outer, IntPtr iid, out IntPtr obj);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int QueryContextMenuDelegate(IntPtr self, IntPtr hMenu, uint index,
                                          uint idFirst, uint idLast, uint flags);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int QueryInterfaceDelegate(IntPtr self, IntPtr iid, out IntPtr obj);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int InitializeDelegate(IntPtr self, IntPtr pidlFolder, IntPtr dataObject, IntPtr hkeyProgId);

    [ComImport, Guid("0000010e-0000-0000-C000-000000000046"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDataObject
    {
        [PreserveSig] int GetData(ref FORMATETC format, out STGMEDIUM medium);
        [PreserveSig] int GetDataHere(ref FORMATETC format, out STGMEDIUM medium);
        [PreserveSig] int QueryGetData(ref FORMATETC format);
        [PreserveSig] int GetCanonicalFormatEtc(ref FORMATETC format, out FORMATETC result);
        [PreserveSig] int SetData(ref FORMATETC format, ref STGMEDIUM medium, int release);
        [PreserveSig] int EnumFormatEtc(uint reserved, out IntPtr enumerator);
        [PreserveSig] int DAdvise(ref FORMATETC format, int advise, IntPtr sink, out uint connection);
        [PreserveSig] int DUnadvise(uint connection);
        [PreserveSig] int EnumDAdvise(out IntPtr enumerator);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct FORMATETC
    {
        public short cfFormat;
        public IntPtr dwAspect;
        public int lindex;
        public int tymed;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct STGMEDIUM
    {
        public int tymed;
        public IntPtr hGlobal;
        public IntPtr hUnkForRelease;
    }

    [ComVisible(true)]
    class DropDataObject : IDataObject
    {
        private readonly string[] _files;

        public DropDataObject(string[] files) { _files = files; }

        public int GetData(ref FORMATETC format, out STGMEDIUM medium)
        {
            medium = new STGMEDIUM();
            if (format.cfFormat != 15) return unchecked((int)0x80040154);

            int chars = 0;
            foreach (string f in _files) chars += f.Length + 1;
            int total = 20 + (chars + 1) * 2;
            IntPtr mem = Marshal.AllocHGlobal(total);
            for (int i = 0; i < total; i++) Marshal.WriteByte(mem, i, 0);
            Marshal.WriteInt32(mem, 0, 20);
            Marshal.WriteInt32(mem, 16, 1);
            int offset = 20;
            foreach (string f in _files)
            {
                Marshal.Copy(f.ToCharArray(), 0, mem + offset, f.Length);
                offset += (f.Length + 1) * 2;
            }
            Marshal.WriteInt16(mem, offset, 0);

            medium.tymed = 1;
            medium.hGlobal = mem;
            return 0;
        }

        public int GetDataHere(ref FORMATETC f, out STGMEDIUM m) { m = new STGMEDIUM(); return unchecked((int)0x80004001); }
        public int QueryGetData(ref FORMATETC f) { return f.cfFormat == 15 ? 0 : unchecked((int)0x80040154); }
        public int GetCanonicalFormatEtc(ref FORMATETC f, out FORMATETC r) { r = f; return 0; }
        public int SetData(ref FORMATETC f, ref STGMEDIUM m, int release) { return 0; }
        public int EnumFormatEtc(uint reserved, out IntPtr e) { e = IntPtr.Zero; return 0; }
        public int DAdvise(ref FORMATETC f, int a, IntPtr s, out uint c) { c = 0; return 0; }
        public int DUnadvise(uint c) { return 0; }
        public int EnumDAdvise(out IntPtr e) { e = IntPtr.Zero; return 0; }
    }

    static IntPtr GuidPtr(string text)
    {
        IntPtr p = Marshal.AllocHGlobal(16);
        Marshal.StructureToPtr(new Guid(text), p, false);
        return p;
    }

    static int problems = 0;

    static void Check(string label, string[] files, int expected, IntPtr module, string clsid)
    {
        var getClassObject = (GetClassObjectDelegate)Marshal.GetDelegateForFunctionPointer(
            GetProcAddress(module, "DllGetClassObject"), typeof(GetClassObjectDelegate));

        IntPtr factory;
        int hr = getClassObject(GuidPtr(clsid), GuidPtr("{00000001-0000-0000-C000-000000000046}"), out factory);
        if (hr != 0) { Console.WriteLine("  !! " + label + " 拿不到类工厂"); problems++; return; }

        IntPtr vtable = Marshal.ReadIntPtr(factory);
        var createInstance = (CreateInstanceDelegate)Marshal.GetDelegateForFunctionPointer(
            Marshal.ReadIntPtr(vtable, 3 * IntPtr.Size), typeof(CreateInstanceDelegate));

        IntPtr menuObj;
        hr = createInstance(factory, IntPtr.Zero, GuidPtr("{000214E4-0000-0000-C000-000000000046}"), out menuObj);
        if (hr != 0) { Console.WriteLine("  !! " + label + " 造不出对象"); problems++; return; }

        IntPtr vt = Marshal.ReadIntPtr(menuObj);
        var queryInterface = (QueryInterfaceDelegate)Marshal.GetDelegateForFunctionPointer(
            Marshal.ReadIntPtr(vt, 0 * IntPtr.Size), typeof(QueryInterfaceDelegate));

        IntPtr initPtr;
        if (queryInterface(menuObj, GuidPtr("{000214E8-0000-0000-C000-000000000046}"), out initPtr) != 0)
        {
            Console.WriteLine("  !! " + label + " 拿不到 IShellExtInit");
            problems++;
            return;
        }

        IntPtr initVt = Marshal.ReadIntPtr(initPtr);
        var initialize = (InitializeDelegate)Marshal.GetDelegateForFunctionPointer(
            Marshal.ReadIntPtr(initVt, 3 * IntPtr.Size), typeof(InitializeDelegate));

        object dataObject = new DropDataObject(files);
        IntPtr dataPtr = Marshal.GetComInterfaceForObject(dataObject, typeof(IDataObject));
        initialize(initPtr, IntPtr.Zero, dataPtr, IntPtr.Zero);

        IntPtr menuVt = Marshal.ReadIntPtr(menuObj);
        var queryContextMenu = (QueryContextMenuDelegate)Marshal.GetDelegateForFunctionPointer(
            Marshal.ReadIntPtr(menuVt, 3 * IntPtr.Size), typeof(QueryContextMenuDelegate));

        IntPtr hMenu = CreatePopupMenu();
        int used = queryContextMenu(menuObj, hMenu, 0, 0x8000, 0x8001, 0);
        int count = GetMenuItemCount(hMenu);

        Console.Write("  {0,-28} 菜单项={1}", label, count);
        for (int i = 0; i < count; i++)
        {
            StringBuilder text = new StringBuilder(256);
            GetMenuStringW(hMenu, (uint)i, text, 256, 0x400);
            Console.Write("  '" + text + "'");
        }
        Console.WriteLine("");

        if (count != expected)
        {
            Console.WriteLine("      !! 期望 " + expected + " 项, 实际 " + count + " 项");
            problems++;
        }
    }

    static void Main(string[] args)
    {
        string dll = args[0];
        string clsid = args[1];
        string temp = args.Length > 2 ? args[2] : System.IO.Path.GetTempPath();

        IntPtr module = LoadLibrary(dll);
        if (module == IntPtr.Zero) { Console.WriteLine("LoadLibrary 失败"); return; }

        string realFile = System.IO.Path.Combine(temp, "scope_probe.txt");
        System.IO.File.WriteAllText(realFile, "测试");

        string recycleFile = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System).Substring(0, 3),
            "$Recycle.Bin\\S-1-5-21-test\\$R1234.txt");

        Console.WriteLine("=== 作用范围自检 ===");
        Check("普通文件", new[] { realFile }, 2, module, clsid);
        Check("普通文件夹", new[] { temp.TrimEnd('\\') }, 2, module, clsid);
        Check("C 盘根目录", new[] { "C:\\" }, 0, module, clsid);
        Check("D 盘根目录", new[] { "D:\\" }, 0, module, clsid);
        Check("回收站里的项目", new[] { recycleFile }, 0, module, clsid);
        Check("虚拟路径", new[] { "::{645FF040-5081-101B-9F08-00AA002F954E}" }, 0, module, clsid);

        Console.WriteLine("");
        Console.WriteLine("TOTAL PROBLEMS: " + problems);
    }
}
