using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

class DataProbe
{
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

    // CMINVOKECOMMANDINFOEX 的 x64 真实布局（72 字节）。
    // 字段顺序必须与 SDK 一致，hwnd 占 8 字节，所以 lpVerb 落在偏移 16 而不是 8。
    [StructLayout(LayoutKind.Sequential)]
    struct CMINVOKECOMMANDINFOEX
    {
        public int cbSize;
        public int fMask;
        public IntPtr hwnd;
        public UIntPtr lpVerb;         // MAKEINTRESOURCE 或指向 ANSI 字符串
        public IntPtr lpParameters;
        public IntPtr lpDirectory;
        public int nShow;
        public int dwHotKey;
        public IntPtr hIcon;
        public IntPtr lpTitle;
        public IntPtr lpVerbW;         // MAKEINTRESOURCE 或指向 Unicode 字符串
        public IntPtr lpParametersW;
        public IntPtr lpDirectoryW;
        public IntPtr lpTitleW;
        public int ptInvokeX;
        public int ptInvokeY;
    }

    [ComVisible(true)]
    class DropDataObject : IDataObject
    {
        private readonly List<string> _files;

        public DropDataObject(List<string> files) { _files = files; }

        public int GetData(ref FORMATETC format, out STGMEDIUM medium)
        {
            medium = new STGMEDIUM();
            if (format.cfFormat != 15)
                return unchecked((int)0x80040154);

            int chars = 0;
            foreach (string f in _files) chars += f.Length + 1;
            int total = 20 + (chars + 1) * 2;
            IntPtr mem = Marshal.AllocHGlobal(total);
            try
            {
                for (int i = 0; i < total; i++) Marshal.WriteByte(mem, i, 0);
                Marshal.WriteInt32(mem, 0, 20);
                Marshal.WriteInt32(mem, 4, 0);
                Marshal.WriteInt32(mem, 8, 0);
                Marshal.WriteInt32(mem, 12, 0);
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
                medium.hUnkForRelease = IntPtr.Zero;
                return 0;
            }
            catch
            {
                Marshal.FreeHGlobal(mem);
                medium = new STGMEDIUM();
                return unchecked((int)0x80004005);
            }
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

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr LoadLibrary(string file);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    static extern IntPtr GetProcAddress(IntPtr module, string name);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int GetClassObjectDelegate(IntPtr clsid, IntPtr iid, out IntPtr obj);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int CreateInstanceDelegate(IntPtr self, IntPtr outer, IntPtr iid, out IntPtr obj);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int InitializeDelegate(IntPtr self, IntPtr pidlFolder, IntPtr dataObject, IntPtr hkeyProgId);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int QueryInterfaceDelegate(IntPtr self, IntPtr iid, out IntPtr obj);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int InvokeCommandDelegate(IntPtr self, IntPtr info);

    static IntPtr GuidPtr(string text)
    {
        IntPtr p = Marshal.AllocHGlobal(16);
        Marshal.StructureToPtr(new Guid(text), p, false);
        return p;
    }

    static int Main(string[] args)
    {
        if (args.Length < 3)
        {
            Console.WriteLine("用法: dataprobe.exe <Auto7zShell.dll> <CLSID> <样本文件>");
            Console.WriteLine("");
            Console.WriteLine("  模拟资源管理器调用链：Initialize -> QueryContextMenu -> GetCommandString");
            Console.WriteLine("  -> InvokeCommand，验证组件能走通并真的产出压缩包。");
            Console.WriteLine("");
            Console.WriteLine("  例: dataprobe.exe Auto7zShell.dll {6C3F8A21-4B7D-4E5A-9A2C-3D710E5B8841} sample.txt");
            return 2;
        }

        string dll = args[0];
        string clsid = args[1];
        string file = args[2];

        Console.WriteLine("=== Initialize 调用测试 ===");
        Console.WriteLine("  dll   = " + dll);
        Console.WriteLine("  文件  = " + file);

        IntPtr module = LoadLibrary(dll);
        if (module == IntPtr.Zero) { Console.WriteLine("  !! LoadLibrary 失败"); return 3; }

        var getClassObject = (GetClassObjectDelegate)Marshal.GetDelegateForFunctionPointer(
            GetProcAddress(module, "DllGetClassObject"), typeof(GetClassObjectDelegate));

        IntPtr factory;
        int hr = getClassObject(GuidPtr(clsid), GuidPtr("{00000001-0000-0000-C000-000000000046}"), out factory);
        Console.WriteLine("  DllGetClassObject = 0x" + hr.ToString("X8"));
        if (hr != 0) { Console.WriteLine("  !! HRESULT = 0x" + hr.ToString("X8")); return 3; }

        IntPtr vtable = Marshal.ReadIntPtr(factory);
        var createInstance = (CreateInstanceDelegate)Marshal.GetDelegateForFunctionPointer(
            Marshal.ReadIntPtr(vtable, 3 * IntPtr.Size), typeof(CreateInstanceDelegate));

        IntPtr menuObj;
        hr = createInstance(factory, IntPtr.Zero, GuidPtr("{000214E4-0000-0000-C000-000000000046}"), out menuObj);
        Console.WriteLine("  CreateInstance      = 0x" + hr.ToString("X8"));
        if (hr != 0) { Console.WriteLine("  !! HRESULT = 0x" + hr.ToString("X8")); return 3; }

        object dataObject = new DropDataObject(new List<string> { file });
        IntPtr dataPtr = Marshal.GetComInterfaceForObject(dataObject, typeof(IDataObject));
        Console.WriteLine("  IDataObject = " + dataPtr);

        IntPtr initVtable = Marshal.ReadIntPtr(menuObj);
        var queryInterface = (QueryInterfaceDelegate)Marshal.GetDelegateForFunctionPointer(
            Marshal.ReadIntPtr(initVtable, 0 * IntPtr.Size), typeof(QueryInterfaceDelegate));

        IntPtr initPtr = IntPtr.Zero;
        hr = queryInterface(menuObj, GuidPtr("{000214E8-0000-0000-C000-000000000046}"), out initPtr);
        Console.WriteLine("  QI(IShellExtInit)   = 0x" + hr.ToString("X8") + " 指针=" + initPtr);
        if (hr != 0) { Console.WriteLine("  !! 拿不到 IShellExtInit"); return 3; }

        IntPtr initVt = Marshal.ReadIntPtr(initPtr);
        var initialize = (InitializeDelegate)Marshal.GetDelegateForFunctionPointer(
            Marshal.ReadIntPtr(initVt, 3 * IntPtr.Size), typeof(InitializeDelegate));

        hr = initialize(initPtr, IntPtr.Zero, dataPtr, IntPtr.Zero);
        Console.WriteLine("  Initialize         = 0x" + hr.ToString("X8"));
        Console.WriteLine(hr == 0 ? "  -> Initialize 成功 (S_OK)" : "  -> Initialize 返回 " + hr.ToString("X8"));

        IntPtr menuVt2 = Marshal.ReadIntPtr(menuObj);
        var invokeCommand = (InvokeCommandDelegate)Marshal.GetDelegateForFunctionPointer(
            Marshal.ReadIntPtr(menuVt2, 4 * IntPtr.Size), typeof(InvokeCommandDelegate));

        // 原来这里是「24 字节结构体、谓词写到偏移 8」——那是 32 位的布局。
        // x64 下 hwnd 占 8 字节，真实结构是 72 字节、lpVerb 在偏移 16。
        // 按旧写法，谓词被写进 hwnd 字段，lpVerb 恒为 NULL，
        // 组件里 LOWORD(NULL)==0 → 永远落到压缩槽。于是它「通过」，
        // 却从头到尾没有验证过解压。
        //
        // 现在用真实结构体 + MAKEINTRESOURCE(偏移)，与资源管理器点击菜单时一致。
        string which = args.Length > 3 ? args[3] : "compress";
        bool extract = which == "extract";
        UIntPtr offset = extract ? new UIntPtr(1) : new UIntPtr(0);

        var info = new CMINVOKECOMMANDINFOEX();
        info.cbSize = Marshal.SizeOf(typeof(CMINVOKECOMMANDINFOEX));
        info.fMask = 0;
        info.lpVerb = offset;          // MAKEINTRESOURCE
        info.nShow = 1;

        IntPtr packed = Marshal.AllocHGlobal(info.cbSize);
        Marshal.StructureToPtr(info, packed, false);
        hr = invokeCommand(menuObj, packed);
        Marshal.FreeHGlobal(packed);

        Console.WriteLine("  谓词               = " + which
                          + " (MAKEINTRESOURCE(" + (extract ? 1 : 0) + "))");
        Console.WriteLine("  期望开关           = " + (extract ? "-uz" : "-z"));
        Console.WriteLine("  InvokeCommand      = 0x" + hr.ToString("X8"));
        Console.WriteLine(hr == 0 ? "  -> InvokeCommand 成功" : "  -> InvokeCommand 返回 " + hr.ToString("X8"));
        return 0;
    }
}
