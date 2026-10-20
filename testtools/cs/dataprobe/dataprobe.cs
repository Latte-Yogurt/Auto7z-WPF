using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

// 自造一个只提供 CF_HDROP 的 IDataObject, 模拟资源管理器传给外壳扩展的那个。
// 有了它就能真正调一次 IShellExtInit::Initialize, 看组件能不能取到选中的路径。
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

    // 只支持 CF_HDROP 的极简实现
    [ComVisible(true)]
    class DropDataObject : IDataObject
    {
        private readonly List<string> _files;

        public DropDataObject(List<string> files) { _files = files; }

        public int GetData(ref FORMATETC format, out STGMEDIUM medium)
        {
            medium = new STGMEDIUM();
            if (format.cfFormat != 15)   // CF_HDROP
                return unchecked((int)0x80040154);   // E_INVALIDARG

            // 组一个 DROPFILES 结构
            int chars = 0;
            foreach (string f in _files) chars += f.Length + 1;
            int total = 20 + (chars + 1) * 2;
            IntPtr mem = Marshal.AllocHGlobal(total);
            try
            {
                for (int i = 0; i < total; i++) Marshal.WriteByte(mem, i, 0);
                Marshal.WriteInt32(mem, 0, 20);      // pFiles 偏移
                Marshal.WriteInt32(mem, 4, 0);       // pt.x
                Marshal.WriteInt32(mem, 8, 0);       // pt.y
                Marshal.WriteInt32(mem, 12, 0);      // fNC
                Marshal.WriteInt32(mem, 16, 1);      // fWide = TRUE
                int offset = 20;
                foreach (string f in _files)
                {
                    Marshal.Copy(f.ToCharArray(), 0, mem + offset, f.Length);
                    offset += (f.Length + 1) * 2;
                }
                Marshal.WriteInt16(mem, offset, 0);

                medium.tymed = 1;                   // TYMED_HGLOBAL
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

    static void Main(string[] args)
    {
        string dll = args[0];
        string clsid = args[1];
        string file = args[2];

        Console.WriteLine("=== Initialize 调用测试 ===");
        Console.WriteLine("  dll   = " + dll);
        Console.WriteLine("  文件  = " + file);

        IntPtr module = LoadLibrary(dll);
        if (module == IntPtr.Zero) { Console.WriteLine("  !! LoadLibrary 失败"); return; }

        var getClassObject = (GetClassObjectDelegate)Marshal.GetDelegateForFunctionPointer(
            GetProcAddress(module, "DllGetClassObject"), typeof(GetClassObjectDelegate));

        IntPtr factory;
        int hr = getClassObject(GuidPtr(clsid), GuidPtr("{00000001-0000-0000-C000-000000000046}"), out factory);
        Console.WriteLine("  DllGetClassObject = 0x" + hr.ToString("X8"));
        if (hr != 0) return;

        IntPtr vtable = Marshal.ReadIntPtr(factory);
        var createInstance = (CreateInstanceDelegate)Marshal.GetDelegateForFunctionPointer(
            Marshal.ReadIntPtr(vtable, 3 * IntPtr.Size), typeof(CreateInstanceDelegate));

        IntPtr menuObj;
        hr = createInstance(factory, IntPtr.Zero, GuidPtr("{000214E4-0000-0000-C000-000000000046}"), out menuObj);
        Console.WriteLine("  CreateInstance      = 0x" + hr.ToString("X8"));
        if (hr != 0) return;

        // 造 IDataObject
        object dataObject = new DropDataObject(new List<string> { file });
        IntPtr dataPtr = Marshal.GetComInterfaceForObject(dataObject, typeof(IDataObject));
        Console.WriteLine("  IDataObject = " + dataPtr);

        // 用真正的 QueryInterface 拿 IShellExtInit 指针
        IntPtr initVtable = Marshal.ReadIntPtr(menuObj);
        var queryInterface = (QueryInterfaceDelegate)Marshal.GetDelegateForFunctionPointer(
            Marshal.ReadIntPtr(initVtable, 0 * IntPtr.Size), typeof(QueryInterfaceDelegate));

        IntPtr initPtr = IntPtr.Zero;
        hr = queryInterface(menuObj, GuidPtr("{000214E8-0000-0000-C000-000000000046}"), out initPtr);
        Console.WriteLine("  QI(IShellExtInit)   = 0x" + hr.ToString("X8") + " 指针=" + initPtr);
        if (hr != 0) { Console.WriteLine("  !! 拿不到 IShellExtInit"); return; }

        // IShellExtInit 的 vtable: IUnknown 3 个, Initialize 是第 4 个
        IntPtr initVt = Marshal.ReadIntPtr(initPtr);
        var initialize = (InitializeDelegate)Marshal.GetDelegateForFunctionPointer(
            Marshal.ReadIntPtr(initVt, 3 * IntPtr.Size), typeof(InitializeDelegate));

        hr = initialize(initPtr, IntPtr.Zero, dataPtr, IntPtr.Zero);
        Console.WriteLine("  Initialize         = 0x" + hr.ToString("X8"));
        Console.WriteLine(hr == 0 ? "  -> Initialize 成功 (S_OK)" : "  -> Initialize 返回 " + hr.ToString("X8"));

        // 调 InvokeCommand, 看能不能真的把 Auto7z 拉起来
        IntPtr menuVt2 = Marshal.ReadIntPtr(menuObj);
        var invokeCommand = (InvokeCommandDelegate)Marshal.GetDelegateForFunctionPointer(
            Marshal.ReadIntPtr(menuVt2, 4 * IntPtr.Size), typeof(InvokeCommandDelegate));

        // CMINVOKECOMMANDINFO: cbSize, fMask, hwnd, lpVerb, lpParameters, lpDirectory, nShow
        int structSize = 24;
        IntPtr info = Marshal.AllocHGlobal(structSize);
        for (int i = 0; i < structSize; i++) Marshal.WriteByte(info, i, 0);
        Marshal.WriteInt32(info, 0, structSize);   // cbSize
        Marshal.WriteInt32(info, 4, 0);            // fMask = 0
        // lpVerb 指向动词字符串
        IntPtr verb = Marshal.StringToHGlobalUni("Auto7z.Compress");
        Marshal.WriteIntPtr(info, 8, verb);

        hr = invokeCommand(menuObj, info);
        Console.WriteLine("  InvokeCommand      = 0x" + hr.ToString("X8"));
        Console.WriteLine(hr == 0 ? "  -> InvokeCommand 成功" : "  -> InvokeCommand 返回 " + hr.ToString("X8"));
    }
}
