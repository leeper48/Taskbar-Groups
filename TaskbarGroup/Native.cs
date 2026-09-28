using System.Runtime.InteropServices;
using System.Text;

namespace WindowsTaskbarGroup;

static class Native
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [DllImport("user32.dll")] public static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
    [DllImport("shcore.dll")] public static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);

    /// <summary>Effective DPI of the monitor under a screen point (96 if unknown).</summary>
    public static int DpiAt(Point p)
    {
        IntPtr mon = MonitorFromPoint(new POINT { X = p.X, Y = p.Y }, 2 /* MONITOR_DEFAULTTONEAREST */);
        return GetDpiForMonitor(mon, 0, out uint x, out _) == 0 ? (int)x : 96;
    }

    // --- Top-level windows (running apps) ---
    public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] public static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);
    public const int DWMWA_CLOAKED = 14;
    public const uint GW_OWNER = 4;
    public const int GWL_EXSTYLE = -20;
    public const long WS_EX_TOOLWINDOW = 0x80, WS_EX_APPWINDOW = 0x40000;

    [DllImport("kernel32.dll")] public static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref int size);
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("shell32.dll")]
    public static extern int SHGetPropertyStoreForWindow(IntPtr hwnd, ref Guid iid,
        [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);

    public static string? ProcessPath(uint pid)
    {
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally { CloseHandle(h); }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern int GetApplicationUserModelId(IntPtr process, ref int length, StringBuilder id);

    /// <summary>The app ID of a packaged (Store / MSIX) process, or null for ordinary desktop apps.</summary>
    public static string? PackagedAppId(uint pid)
    {
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(512);
            int len = sb.Capacity;
            return GetApplicationUserModelId(h, ref len, sb) == 0 && sb.Length > 0 ? sb.ToString() : null;
        }
        finally { CloseHandle(h); }
    }

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IShellItem
    {
        void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        void GetParent(out IShellItem parent);
        [PreserveSig] int GetDisplayName(uint sigdn, out IntPtr name);
        void GetAttributes(uint mask, out uint attributes);
        void Compare(IShellItem other, uint hint, out int order);
    }

    /// <summary>The name Explorer / the Start menu shows for a path (e.g. a shell:AppsFolder app), or null.</summary>
    public static string? ShellDisplayName(string path)
    {
        var iid = typeof(IShellItem).GUID;
        if (SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out object obj) != 0) return null;
        var item = (IShellItem)obj;
        try
        {
            if (item.GetDisplayName(0 /* SIGDN_NORMALDISPLAY */, out IntPtr p) != 0) return null;
            try { return Marshal.PtrToStringUni(p); }
            finally { Marshal.FreeCoTaskMem(p); }
        }
        finally { Marshal.ReleaseComObject(item); }
    }

    /// <summary>The window's explicit AppUserModelID (Store apps, and apps that set one), or null.</summary>
    public static string? WindowAppId(IntPtr hwnd)
    {
        var iid = typeof(IPropertyStore).GUID;
        if (SHGetPropertyStoreForWindow(hwnd, ref iid, out var store) != 0 || store == null) return null;
        try
        {
            store.GetValue(ref PKEY_AppUserModel_ID, out var pv);
            try { return pv.vt == VT_LPWSTR ? Marshal.PtrToStringUni(pv.p) : null; }
            finally { PropVariantClear(ref pv); }
        }
        catch { return null; }
        finally { Marshal.ReleaseComObject(store); }
    }

    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hwnd);

    // --- DWM (Windows 11 rounded corners, dark frame, border color) ---
    public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    public const int DWMWA_BORDER_COLOR = 34;
    public const int DWMWCP_ROUND = 2;

    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public static void DwmInt(IntPtr hwnd, int attr, int value) =>
        DwmSetWindowAttribute(hwnd, attr, ref value, sizeof(int));

    /// <summary>COLORREF is 0x00BBGGRR.</summary>
    public static int ColorRef(Color c) => c.R | (c.G << 8) | (c.B << 16);

    // --- Shell images ---
    [StructLayout(LayoutKind.Sequential)]
    public struct SIZE { public int cx, cy; }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr phbm);
    }

    public const int SIIGBF_BIGGERSIZEOK = 0x1;
    public const int SIIGBF_ICONONLY = 0x4;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern int SHCreateItemFromParsingName(string path, IntPtr pbc, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out object ppv);

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAP
    {
        public int bmType, bmWidth, bmHeight, bmWidthBytes;
        public ushort bmPlanes, bmBitsPixel;
        public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public ushort biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    [DllImport("gdi32.dll")] public static extern int GetObject(IntPtr h, int size, out BITMAP bm);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr h);
    [DllImport("gdi32.dll")]
    public static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint lines, byte[] bits,
        ref BITMAPINFOHEADER bmi, uint usage);
    [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    // --- Shortcuts (.lnk) with an AppUserModelID ---
    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    public class CShellLink { }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int cch, IntPtr fd, uint flags);
        void GetIDList(out IntPtr pidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int cmd);
        void SetShowCmd(int cmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int cch, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(IntPtr hwnd, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PROPERTYKEY { public Guid fmtid; public uint pid; }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    public struct PROPVARIANT
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public IntPtr p;
    }

    public const ushort VT_LPWSTR = 31;

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IPropertyStore
    {
        void GetCount(out uint count);
        void GetAt(uint index, out PROPERTYKEY key);
        void GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
        void SetValue(ref PROPERTYKEY key, ref PROPVARIANT value);
        void Commit();
    }

    [DllImport("ole32.dll")] public static extern int PropVariantClear(ref PROPVARIANT pv);

    /// <summary>System.AppUserModel.ID</summary>
    public static PROPERTYKEY PKEY_AppUserModel_ID = new()
    { fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), pid = 5 };
}
