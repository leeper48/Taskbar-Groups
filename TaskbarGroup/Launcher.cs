namespace WindowsTaskbarGroup;

/// <summary>
/// Starting a group's app: if it's already running, its window comes to the front instead of a second
/// copy starting (Kurt, 0.7.0). Shift+click in the pop-up starts a new copy anyway.
/// An item counts as running when an app window (as Alt+Tab shows them) belongs to:
/// - its exe (an .exe item, or a shortcut's target exe), only when no arguments are passed, so a
///   "cmd /c script.bat" shortcut never grabs an unrelated console window;
/// - its app ID: a Store app (shell:AppsFolder\&lt;id&gt;), a packaged app, or a shortcut that carries one
///   (Discord's pin: com.squirrel.Discord.Discord), matched against the window's own app ID.
/// Folders and documents always open normally.
/// </summary>
static class Launcher
{
    public static bool StartOrActivate(AppItem item, bool forceNew = false)
    {
        if (!forceNew)
            try
            {
                if (FindWindow(item) is { } hwnd && hwnd != IntPtr.Zero)
                {
                    if (Native.IsIconic(hwnd)) Native.ShowWindow(hwnd, 9 /* SW_RESTORE */);
                    Native.SetForegroundWindow(hwnd);
                    return true;
                }
            }
            catch { /* fall back to starting it */ }
        return item.StartOrReport();
    }

    sealed record Keys(HashSet<string> Exes, HashSet<string> AppIds)
    {
        public bool Empty => Exes.Count == 0 && AppIds.Count == 0;
    }

    static Keys KeysFor(AppItem item)
    {
        var keys = new Keys(new(StringComparer.OrdinalIgnoreCase), new(StringComparer.OrdinalIgnoreCase));
        string path = Environment.ExpandEnvironmentVariables(item.Path);
        bool noArgs = string.IsNullOrWhiteSpace(item.Arguments);
        if (path.StartsWith(@"shell:AppsFolder\", StringComparison.OrdinalIgnoreCase))
            keys.AppIds.Add(path[@"shell:AppsFolder\".Length..]);
        else if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) && File.Exists(path))
        {
            var info = Shortcuts.Read(path);
            if (info.AppId is { Length: > 0 } id) keys.AppIds.Add(id);
            string target = Environment.ExpandEnvironmentVariables(info.Target);
            if (noArgs && info.Arguments.Length == 0 && target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(target))
                keys.Exes.Add(Path.GetFullPath(target));
        }
        else if (noArgs && path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(path))
            keys.Exes.Add(Path.GetFullPath(path));
        return keys;
    }

    /// <summary>The item's frontmost app window (windows are listed in Z order, top first), or null.</summary>
    public static IntPtr? FindWindow(AppItem item)
    {
        var keys = KeysFor(item);
        if (keys.Empty) return null;
        IntPtr found = IntPtr.Zero;
        Native.EnumWindows((h, lParam) =>
        {
            try
            {
                if (!AppSources.IsAppWindow(h, out string _)) return true;
                Native.GetWindowThreadProcessId(h, out uint pid);
                if (keys.AppIds.Count > 0 &&
                    ((Native.WindowAppId(h) is { } wid && keys.AppIds.Contains(wid)) ||
                     (Native.PackagedAppId(pid) is { } pidId && keys.AppIds.Contains(pidId))))
                {
                    found = h;
                    return false;
                }
                if (keys.Exes.Count > 0 && Native.ProcessPath(pid) is { } exe && keys.Exes.Contains(exe))
                {
                    found = h;
                    return false;
                }
            }
            catch { /* a window that closed meanwhile */ }
            return true;
        }, IntPtr.Zero);
        return found == IntPtr.Zero ? null : found;
    }
}
