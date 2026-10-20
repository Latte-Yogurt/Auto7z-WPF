using System;
using System.Runtime.InteropServices;
using System.Text;

// 查菜单项有没有图标:
// GetMenuItemInfo 读 hbmpItem, 有位图就说明图标挂上了。
class IconProbe
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr LoadLibrary(string file);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    static extern IntPtr GetProcAddress(IntPtr module, string name);

    [DllImport("user32.dll")]
    static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool GetMenuItemInfoW(IntPtr menu, uint item, bool byPosition,
                                       IntPtr buffer, uint size);

    [DllImport("user32.dll")]
    static extern int GetMenuItemCount(IntPtr menu);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetMenuStringW(IntPtr menu, uint id, StringBuilder text, int max, uint flag);

    [DllImport("gdi32.dll")]
    static extern int GetObject(IntPtr handle, int size, IntPtr buffer);

    [DllImport("gdi32.dll")]
    static extern int GetBitmapBits(IntPtr bitmap, uint count, IntPtr buffer);

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
            Marshal.WriteInt32(mem, 0, 20);
            Marshal.WriteInt32(mem, 16, 1);
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

    // MENUITEMINFOW
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct MENUITEMINFO
    {
        public uint cbSize;
        public uint fMask;
        public uint fType;
        public uint fState;
        public uint wID;
        public IntPtr hSubMenu;
        public IntPtr hbmpChecked;
        public IntPtr hbmpUnchecked;
        public IntPtr dwItemData;
        public IntPtr dwTypeData;
        public uint cch;
        public IntPtr hbmpItem;
    }

    const uint MIIM_BITMAP = 0x00000080;

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

        Console.WriteLine("=== 菜单图标检查 ===");

        IntPtr module = LoadLibrary(dll);
        var getClassObject = (GetClassObjectDelegate)Marshal.GetDelegateForFunctionPointer(
            GetProcAddress(module, "DllGetClassObject"), typeof(GetClassObjectDelegate));

        IntPtr factory;
        getClassObject(GuidPtr(clsid), GuidPtr("{00000001-0000-0000-C000-000000000046}"), out factory);

        IntPtr vtable = Marshal.ReadIntPtr(factory);
        var createInstance = (CreateInstanceDelegate)Marshal.GetDelegateForFunctionPointer(
            Marshal.ReadIntPtr(vtable, 3 * IntPtr.Size), typeof(CreateInstanceDelegate));

        IntPtr menuObj;
        createInstance(factory, IntPtr.Zero, GuidPtr("{000214E4-0000-0000-C000-000000000046}"), out menuObj);

        // 先 Initialize 才有路径, 不然加不出菜单项
        string sample = args.Length > 2 ? args[2] : null;
        if (sample != null)
        {
            IntPtr vt = Marshal.ReadIntPtr(menuObj);
            var queryInterface = (QueryInterfaceDelegate)Marshal.GetDelegateForFunctionPointer(
                Marshal.ReadIntPtr(vt, 0 * IntPtr.Size), typeof(QueryInterfaceDelegate));

            IntPtr initPtr;
            if (queryInterface(menuObj, GuidPtr("{000214E8-0000-0000-C000-000000000046}"), out initPtr) == 0)
            {
                IntPtr initVt = Marshal.ReadIntPtr(initPtr);
                var initialize = (InitializeDelegate)Marshal.GetDelegateForFunctionPointer(
                    Marshal.ReadIntPtr(initVt, 3 * IntPtr.Size), typeof(InitializeDelegate));

                object dataObject = new DropDataObject(sample);
                IntPtr dataPtr = Marshal.GetComInterfaceForObject(dataObject, typeof(IDataObject));
                initialize(initPtr, IntPtr.Zero, dataPtr, IntPtr.Zero);
            }
        }

        IntPtr menuVt = Marshal.ReadIntPtr(menuObj);
        var query = (QueryContextMenuDelegate)Marshal.GetDelegateForFunctionPointer(
            Marshal.ReadIntPtr(menuVt, 3 * IntPtr.Size), typeof(QueryContextMenuDelegate));

        IntPtr hMenu = CreatePopupMenu();
        query(menuObj, hMenu, 0, 0x8000, 0x8001, 0);

        int count = GetMenuItemCount(hMenu);
        int problems = 0;

        for (int i = 0; i < count; i++)
        {
            StringBuilder label = new StringBuilder(256);
            GetMenuStringW(hMenu, (uint)i, label, 256, 0x400);

            int size = Marshal.SizeOf(typeof(MENUITEMINFO));
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                MENUITEMINFO info = new MENUITEMINFO();
                info.cbSize = (uint)size;
                info.fMask = MIIM_BITMAP;
                Marshal.StructureToPtr(info, buffer, false);

                if (!GetMenuItemInfoW(hMenu, (uint)i, true, buffer, (uint)size))
                {
                    Console.WriteLine("  [{0}] '{1}'  读不到信息", i, label);
                    problems++;
                    continue;
                }

                MENUITEMINFO read = (MENUITEMINFO)Marshal.PtrToStructure(buffer, typeof(MENUITEMINFO));
                bool hasBitmap = read.hbmpItem != IntPtr.Zero;

                Console.Write("  [{0}] '{1}'  位图={2}", i, label, hasBitmap ? "有" : "无");
                if (hasBitmap)
                {
                    // 看看位图里有没有非空像素, 全透明等于没图标
                    IntPtr bits = Marshal.AllocHGlobal(32 * 32 * 4);
                    try
                    {
                        int got = GetBitmapBits(read.hbmpItem, 32 * 32 * 4, bits);
                        bool hasContent = false;
                        for (int b = 0; b < got; b += 4)
                        {
                            if (Marshal.ReadByte(bits, b) != 0 ||
                                Marshal.ReadByte(bits, b + 1) != 0 ||
                                Marshal.ReadByte(bits, b + 2) != 0)
                            {
                                hasContent = true;
                                break;
                            }
                        }
                        Console.Write("  内容={0}", hasContent ? "有内容" : "全空");
                        if (!hasContent) problems++;
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(bits);
                    }
                }
                else
                {
                    problems++;
                }
                Console.WriteLine("");
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        Console.WriteLine("");
        Console.WriteLine("TOTAL PROBLEMS: " + problems);
    }
}
