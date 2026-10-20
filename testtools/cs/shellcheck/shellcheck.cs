using System;
using System.Runtime.InteropServices;
using System.Text;

// 外壳扩展自检:
// 走资源管理器加载外壳扩展的那条路, 并把菜单项读出来。
// 用法: shellcheck.exe <dll路径> <CLSID> [文件路径]
class ShellCheck
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

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern uint GetMenuItemID(IntPtr menu, int pos);

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

    // 只提供 CF_HDROP 的极简 IDataObject, 模拟资源管理器给的那个
    [ComVisible(true)]
    class DropDataObject : IDataObject
    {
        private readonly string _file;

        public DropDataObject(string file) { _file = file; }

        public int GetData(ref FORMATETC format, out STGMEDIUM medium)
        {
            medium = new STGMEDIUM();
            if (format.cfFormat != 15) return unchecked((int)0x80040154);

            int chars = _file.Length + 1;
            int total = 20 + chars * 2;
            IntPtr mem = Marshal.AllocHGlobal(total);
            for (int i = 0; i < total; i++) Marshal.WriteByte(mem, i, 0);
            Marshal.WriteInt32(mem, 0, 20);      // pFiles 偏移
            Marshal.WriteInt32(mem, 16, 1);     // fWide
            Marshal.Copy(_file.ToCharArray(), 0, mem + 20, _file.Length);
            Marshal.WriteInt16(mem, 20 + chars * 2 - 2, 0);

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

    static void Main(string[] args)
    {
        string dll = args[0];
        string clsid = args[1];
        string sample = args.Length > 2 ? args[2] : null;

        Console.WriteLine("=== 外壳扩展自检 ===");
        Console.WriteLine("  dll  = " + dll);
        Console.WriteLine("  clsid= " + clsid);

        IntPtr module = LoadLibrary(dll);
        if (module == IntPtr.Zero)
        {
            Console.WriteLine("  !! LoadLibrary 失败, 错误码 " + Marshal.GetLastWin32Error());
            Console.WriteLine("TOTAL PROBLEMS: 1");
            return;
        }
        Console.WriteLine("  LoadLibrary 成功");

        IntPtr proc = GetProcAddress(module, "DllGetClassObject");
        if (proc == IntPtr.Zero)
        {
            Console.WriteLine("  !! 找不到 DllGetClassObject");
            Console.WriteLine("TOTAL PROBLEMS: 1");
            return;
        }

        var getClassObject = (GetClassObjectDelegate)Marshal.GetDelegateForFunctionPointer(
            proc, typeof(GetClassObjectDelegate));

        IntPtr factory;
        int hr = getClassObject(GuidPtr(clsid), GuidPtr("{00000001-0000-0000-C000-000000000046}"), out factory);
        Console.WriteLine("  DllGetClassObject = 0x" + hr.ToString("X8"));
        if (hr != 0) problems++;

        if (hr == 0)
        {
            IntPtr vtable = Marshal.ReadIntPtr(factory);
            var createInstance = (CreateInstanceDelegate)Marshal.GetDelegateForFunctionPointer(
                Marshal.ReadIntPtr(vtable, 3 * IntPtr.Size), typeof(CreateInstanceDelegate));

            IntPtr menuObj;
            hr = createInstance(factory, IntPtr.Zero,
                GuidPtr("{000214E4-0000-0000-C000-000000000046}"), out menuObj);
            Console.WriteLine("  CreateInstance      = 0x" + hr.ToString("X8"));
            if (hr != 0) problems++;

            if (hr == 0)
            {
                // 先 Initialize 才有选中路径, 不然加不出菜单项
                if (sample != null)
                {
                    IntPtr vt = Marshal.ReadIntPtr(menuObj);
                    var queryInterface = (QueryInterfaceDelegate)Marshal.GetDelegateForFunctionPointer(
                        Marshal.ReadIntPtr(vt, 0 * IntPtr.Size), typeof(QueryInterfaceDelegate));

                    IntPtr initPtr;
                    int qiHr = queryInterface(menuObj, GuidPtr("{000214E8-0000-0000-C000-000000000046}"), out initPtr);
                    Console.WriteLine("  QI(IShellExtInit)  = 0x" + qiHr.ToString("X8"));
                    if (qiHr == 0)
                    {
                        IntPtr initVt = Marshal.ReadIntPtr(initPtr);
                        var initialize = (InitializeDelegate)Marshal.GetDelegateForFunctionPointer(
                            Marshal.ReadIntPtr(initVt, 3 * IntPtr.Size), typeof(InitializeDelegate));

                        object dataObject = new DropDataObject(sample);
                        IntPtr dataPtr = Marshal.GetComInterfaceForObject(dataObject, typeof(IDataObject));
                        int initHr = initialize(initPtr, IntPtr.Zero, dataPtr, IntPtr.Zero);
                        Console.WriteLine("  Initialize        = 0x" + initHr.ToString("X8"));
                        if (initHr < 0) problems++;
                    }
                }

                IntPtr menuVtable = Marshal.ReadIntPtr(menuObj);
                var queryContextMenu = (QueryContextMenuDelegate)Marshal.GetDelegateForFunctionPointer(
                    Marshal.ReadIntPtr(menuVtable, 3 * IntPtr.Size), typeof(QueryContextMenuDelegate));

                IntPtr hMenu = CreatePopupMenu();
                // 返回值是加了几个菜单项, 不是 0
                int used = queryContextMenu(menuObj, hMenu, 0, 0x8000, 0x8001, 0);
                Console.WriteLine("  QueryContextMenu   = " + used + " (菜单项数)");
                if (used < 0) problems++;

                int count = GetMenuItemCount(hMenu);
                Console.WriteLine("  菜单项数 = " + count);
                if (count < 2) problems++;
                // 返回值要跟实际插入的项数一致
                if (used != count) problems++;

                for (int i = 0; i < count; i++)
                {
                    StringBuilder text = new StringBuilder(256);
                    GetMenuStringW(hMenu, (uint)i, text, 256, 0x400);
                    uint id = GetMenuItemID(hMenu, i);
                    string label = text.ToString();
                    Console.WriteLine("    [{0}] id=0x{1:X}  '{2}'", i, id, label);
                    if (string.IsNullOrWhiteSpace(label)) problems++;
                }
            }
        }

        Console.WriteLine("");
        Console.WriteLine("TOTAL PROBLEMS: " + problems);
    }
}
