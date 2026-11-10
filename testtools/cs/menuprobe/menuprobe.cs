using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;

// 右键菜单点击链路探针。
//
// 存在的理由：项目里原有的 dataprobe 只构造 fMask=0 的 CMINVOKECOMMANDINFOEX，
// 从不进入 FindCommandSlot 的 Unicode 分支，因此「点解压却执行压缩」这类缺陷
// 完全是盲区。本工具补上 --style unicode-null 形态：
//   fMask 带 CMIC_MASK_UNICODE，但 lpVerbW = NULL，索引只放在 ANSI 的 lpVerb 里。
// 真实环境下资源管理器确实会用这种形态调用组件。
//
// 用法：
//   menuprobe.exe <Auto7zShell.dll> <CLSID> <compress|extract> <样本路径...>
//   menuprobe.exe --style unicode-null <dll> <clsid> <槽位> <样本...>
//   menuprobe.exe --style ansi <dll> <clsid> <槽位> <样本...>
//
// 输出最后一行固定为：
//   MODE=<模式开关>   或   MODE=<none>
// 便于上层脚本断言。
public static class MenuProbe
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr LoadLibrary(string path);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    static extern IntPtr GetProcAddress(IntPtr module, string name);
    [DllImport("user32.dll")]
    static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll")]
    static extern int GetMenuItemCount(IntPtr menu);
    [DllImport("user32.dll")]
    static extern uint GetMenuItemID(IntPtr menu, int position);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetMenuStringW(IntPtr menu, uint item, StringBuilder text, int max, uint flags);

    delegate int GetClassObjectDelegate(IntPtr clsid, IntPtr iid, out IntPtr obj);
    delegate int CreateInstanceDelegate(IntPtr self, IntPtr outer, IntPtr iid, out IntPtr obj);
    delegate int QueryInterfaceDelegate(IntPtr self, IntPtr iid, out IntPtr obj);
    delegate int InitializeDelegate(IntPtr self, IntPtr pidlFolder, IntPtr dataObject, IntPtr hkeyProgId);
    delegate int QueryContextMenuDelegate(IntPtr self, IntPtr hMenu, uint index, uint idFirst, uint idLast, uint flags);
    delegate int GetCommandStringDelegate(IntPtr self, UIntPtr id, uint type, IntPtr reserved, IntPtr buffer, uint bufferChars);
    delegate int InvokeCommandDelegate(IntPtr self, IntPtr info);

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

            string joined = string.Join("\0", _files) + "\0\0";
            int total = 20 + joined.Length * 2;
            IntPtr mem = Marshal.AllocHGlobal(total);
            for (int i = 0; i < total; i++) Marshal.WriteByte(mem, i, 0);
            Marshal.WriteInt32(mem, 0, 20);
            Marshal.WriteInt32(mem, 16, 1);
            Marshal.Copy(joined.ToCharArray(), 0, mem + 20, joined.Length);

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

    // CMINVOKECOMMANDINFOEX 的 x64 布局：72 字节，lpVerb 在偏移 16（hwnd 是 8 字节）。
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct INVOKEEX
    {
        public int cbSize;
        public int fMask;
        public IntPtr hwnd;
        public IntPtr lpVerb;
        public IntPtr lpParameters;
        public IntPtr lpDirectory;
        public int nShow;
        public int dwHotKey;
        public IntPtr hIcon;
        public IntPtr lpTitle;
        public IntPtr lpVerbW;
        public IntPtr lpParametersW;
        public IntPtr lpDirectoryW;
        public IntPtr lpTitleW;
        public int ptInvokeX;
        public int ptInvokeY;
    }

    const uint CMIC_MASK_UNICODE = 0x4000;

    static IntPtr GuidPtr(string text)
    {
        IntPtr p = Marshal.AllocHGlobal(16);
        Marshal.StructureToPtr(new Guid(text), p, false);
        return p;
    }

    static object keepAlive;

    static List<string> ChildCommands(int parentPid)
    {
        var found = new List<string>();
        try
        {
            using (var searcher = new ManagementObjectSearcher(
                "SELECT Name, CommandLine FROM Win32_Process WHERE ParentProcessId=" + parentPid))
            {
                foreach (ManagementObject item in searcher.Get())
                    found.Add(Convert.ToString(item["Name"]) + "|" + Convert.ToString(item["CommandLine"]));
            }
        }
        catch (Exception) { }
        return found;
    }

    public static int Main(string[] args)
    {
        // 参数解析：可选的 --style ansi|unicode-null、--wait <秒>
        string style = "ansi";
        int waitSeconds = 0;
        var rest = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--style" && i + 1 < args.Length) { style = args[++i]; continue; }
            if (args[i] == "--wait" && i + 1 < args.Length) { waitSeconds = int.Parse(args[++i]); continue; }
            rest.Add(args[i]);
        }
        if (rest.Count < 3)
        {
            Console.WriteLine("用法: menuprobe.exe [--style ansi|unicode-null] <dll> <clsid> <compress|extract> <样本路径...>");
            return 2;
        }

        string dll = rest[0];
        string clsid = rest[1];
        string which = rest[2];
        var files = rest.GetRange(3, rest.Count - 3);
        int slot = (which == "extract") ? 1 : 0;

        Console.WriteLine("=== 右键菜单点击链路 ===");
        Console.WriteLine("  dll    = " + dll);
        Console.WriteLine("  style  = " + style);
        Console.WriteLine("  槽位   = " + which + " (" + slot + ")");
        Console.WriteLine("  样本   = " + string.Join(" ; ", files));

        IntPtr module = LoadLibrary(dll);
        if (module == IntPtr.Zero)
        {
            Console.WriteLine("  !! LoadLibrary 失败 err=" + Marshal.GetLastWin32Error());
            Console.WriteLine("MODE=none");
            return 3;
        }

        var gco = (GetClassObjectDelegate)Marshal.GetDelegateForFunctionPointer(
            GetProcAddress(module, "DllGetClassObject"), typeof(GetClassObjectDelegate));

        IntPtr factory;
        int hr = gco(GuidPtr(clsid), GuidPtr("{00000001-0000-0000-C000-000000000046}"), out factory);
        if (hr != 0) { Console.WriteLine("  !! DllGetClassObject 0x" + hr.ToString("X8")); Console.WriteLine("MODE=none"); return 3; }

        var create = (CreateInstanceDelegate)Marshal.GetDelegateForFunctionPointer(
            Marshal.ReadIntPtr(Marshal.ReadIntPtr(factory), 3 * IntPtr.Size), typeof(CreateInstanceDelegate));

        IntPtr menuObj;
        hr = create(factory, IntPtr.Zero, GuidPtr("{000214E4-0000-0000-C000-000000000046}"), out menuObj);
        if (hr != 0) { Console.WriteLine("  !! CreateInstance 0x" + hr.ToString("X8")); Console.WriteLine("MODE=none"); return 3; }

        var dataObject = new DropDataObject(files.ToArray());
        keepAlive = dataObject;
        IntPtr dataPtr = Marshal.GetComInterfaceForObject(dataObject, typeof(IDataObject));

        var qi = (QueryInterfaceDelegate)Marshal.GetDelegateForFunctionPointer(
            Marshal.ReadIntPtr(Marshal.ReadIntPtr(menuObj), 0), typeof(QueryInterfaceDelegate));

        IntPtr initPtr;
        hr = qi(menuObj, GuidPtr("{000214E8-0000-0000-C000-000000000046}"), out initPtr);
        if (hr != 0) { Console.WriteLine("  !! QI(IShellExtInit) 0x" + hr.ToString("X8")); Console.WriteLine("MODE=none"); return 3; }

        var initialize = (InitializeDelegate)Marshal.GetDelegateForFunctionPointer(
            Marshal.ReadIntPtr(Marshal.ReadIntPtr(initPtr), 3 * IntPtr.Size), typeof(InitializeDelegate));
        hr = initialize(initPtr, IntPtr.Zero, dataPtr, IntPtr.Zero);
        Console.WriteLine("  Initialize = 0x" + hr.ToString("X8"));

        IntPtr menuVtable = Marshal.ReadIntPtr(menuObj);
        var queryContextMenu = (QueryContextMenuDelegate)Marshal.GetDelegateForFunctionPointer(
            Marshal.ReadIntPtr(menuVtable, 3 * IntPtr.Size), typeof(QueryContextMenuDelegate));

        IntPtr hMenu = CreatePopupMenu();
        int used = queryContextMenu(menuObj, hMenu, 0, 0x8000, 0x8001, 0);
        Console.WriteLine("  QueryContextMenu = " + used + "  菜单项数 = " + GetMenuItemCount(hMenu));

        for (int i = 0; i < GetMenuItemCount(hMenu); i++)
        {
            var text = new StringBuilder(256);
            GetMenuStringW(hMenu, (uint)i, text, 256, 0x400);
            Console.WriteLine("    [" + i + "] id=0x" + GetMenuItemID(hMenu, i).ToString("X") + "  '" + text + "'");
        }

        var getCommandString = (GetCommandStringDelegate)Marshal.GetDelegateForFunctionPointer(
            Marshal.ReadIntPtr(menuVtable, 5 * IntPtr.Size), typeof(GetCommandStringDelegate));

        foreach (uint rawId in new uint[] { 0x8000, 0x8001 })
        {
            IntPtr buf = Marshal.AllocHGlobal(512);
            for (int i = 0; i < 512; i++) Marshal.WriteByte(buf, i, 0);
            int vhr = getCommandString(menuObj, (UIntPtr)rawId, 4, IntPtr.Zero, buf, 256);
            Console.WriteLine("  GetCommandString(0x" + rawId.ToString("X") + ") = 0x" + vhr.ToString("X8")
                + " '" + Marshal.PtrToStringUni(buf) + "'");
            Marshal.FreeHGlobal(buf);
        }

        var invoke = (InvokeCommandDelegate)Marshal.GetDelegateForFunctionPointer(
            Marshal.ReadIntPtr(menuVtable, 4 * IntPtr.Size), typeof(InvokeCommandDelegate));

        var info = new INVOKEEX();
        info.cbSize = Marshal.SizeOf(typeof(INVOKEEX));
        info.lpVerb = (IntPtr)slot;      // 索引始终放在 ANSI 的 lpVerb
        info.nShow = 1;
        if (style == "unicode-null")
        {
            // 关键形态：置了 Unicode 位，但 lpVerbW 留空。
            info.fMask = (int)CMIC_MASK_UNICODE;
            info.lpVerbW = IntPtr.Zero;
        }
        else
        {
            info.fMask = 0;
        }

        int pid = Process.GetCurrentProcess().Id;
        IntPtr packed = Marshal.AllocHGlobal(info.cbSize);
        Marshal.StructureToPtr(info, packed, false);
        hr = invoke(menuObj, packed);
        Marshal.FreeHGlobal(packed);
        Console.WriteLine("  InvokeCommand = 0x" + hr.ToString("X8"));

        var observed = new List<string>();
        for (int attempt = 0; attempt < 60; attempt++)
        {
            foreach (string line in ChildCommands(pid))
                if (!observed.Contains(line)) observed.Add(line);
            if (observed.Count > 0) break;
            System.Threading.Thread.Sleep(60);
        }

        string mode = "none";
        foreach (string line in observed)
        {
            Console.WriteLine("  子进程: " + line);
            if (!line.StartsWith("Auto7z", StringComparison.OrdinalIgnoreCase)) continue;
            int hash = line.IndexOf('#');
            if (hash < 0) continue;
            string tail = line.Substring(hash);
            int lastSpace = tail.LastIndexOf(' ');
            if (lastSpace >= 0) mode = tail.Substring(lastSpace + 1).Trim();
        }

        if (waitSeconds > 0)
        {
            // 端到端模式：让子进程把任务跑完，观察真实产物。
            Console.WriteLine("  等待任务完成（最多 " + waitSeconds + " 秒）...");
            var deadline = DateTime.UtcNow.AddSeconds(waitSeconds);
            while (DateTime.UtcNow < deadline)
            {
                var alive = Process.GetProcessesByName("Auto7zGUI");
                var sevenZip = Process.GetProcessesByName("7z");
                if (alive.Length == 0 && sevenZip.Length == 0) break;
                System.Threading.Thread.Sleep(300);
            }
            // 给文件系统一点落盘时间。
            System.Threading.Thread.Sleep(1500);
            Console.WriteLine("  任务结束");
        }
        else
        {
            // 快速模式：只取模式开关，立刻回收子进程。
            foreach (string name in new[] { "Auto7zGUI", "Auto7z" })
                foreach (var leftover in Process.GetProcessesByName(name))
                    try { leftover.Kill(); } catch (Exception) { }
        }

        Console.WriteLine("MODE=" + mode);
        return 0;
    }
}
