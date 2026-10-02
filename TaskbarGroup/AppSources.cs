using System.Text;

namespace WindowsTaskbarGroup;

/// <summary>An app offered by Add Apps. <paramref name="CopyLink"/>: Path is a shortcut that
/// belongs to someone else (a taskbar pin) and is copied into data\Links when added.</summary>
sealed record Candidate(string Name, string Path, string Detail, bool CopyLink = false, string? PinnedAppId = null);

/// <summary>Where Add Apps finds apps: windows that are open now, and the taskbar's pins.</summary>
static class AppSources
{
    static readonly HashSet<string> ShellClasses = new(StringComparer.Ordinal)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd",
        "Windows.UI.Core.CoreWindow",  // a Store app's inner window; its frame (ApplicationFrameHost) is listed instead
    };

    /// <summary>One entry per app with a visible window on the taskbar, the same set Alt+Tab shows.
    /// Store apps (hosted by ApplicationFrameHost) become <c>shell:AppsFolder\&lt;AppUserModelID&gt;</c>,
    /// which starts the app itself; everything else is its exe.</summary>
    public static List<Candidate> Running()
    {
        var list = new List<Candidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string self = Environment.ProcessPath ?? "";

        Native.EnumWindows((h, _) =>
        {
            try { Consider(h); } catch { /* a window that closed meanwhile */ }
            return true;
        }, IntPtr.Zero);

        void Consider(IntPtr h)
        {
            if (!IsAppWindow(h, out string title)) return;

            Native.GetWindowThreadProcessId(h, out uint pid);
            string? exe = Native.ProcessPath(pid);
            if (exe == null || string.Equals(exe, self, StringComparison.OrdinalIgnoreCase)) return;

            // Store and packaged apps (Windows Terminal, Claude ...) can't be started from their exe
            // under WindowsApps, so they go through their app ID.
            string? appId = Path.GetFileName(exe).Equals("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase)
                ? Native.WindowAppId(h)
                : Native.PackagedAppId(pid);
            Candidate c;
            if (appId is { Length: > 0 })
            {
                string path = @"shell:AppsFolder\" + appId;
                c = new Candidate(Native.ShellDisplayName(path) ?? title, path, "Store app  ·  " + appId);
            }
            else if (Path.GetFileName(exe).Equals("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase))
                return;
            else
                c = new Candidate(SettingsForm.NameFor(exe), exe, exe + "  ·  " + title);

            if (seen.Add(c.Path)) list.Add(c);
        }

        return list.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>A window the taskbar and Alt+Tab show for an app: visible, unowned, not a tool window,
    /// not cloaked (another desktop, a suspended Store app), not the shell's own, with a title.</summary>
    public static bool IsAppWindow(IntPtr h, out string title)
    {
        title = "";
        if (!Native.IsWindowVisible(h) || Native.GetWindow(h, Native.GW_OWNER) != IntPtr.Zero) return false;
        long ex = Native.GetWindowLongPtr(h, Native.GWL_EXSTYLE).ToInt64();
        if ((ex & Native.WS_EX_TOOLWINDOW) != 0 && (ex & Native.WS_EX_APPWINDOW) == 0) return false;
        if (Native.DwmGetWindowAttribute(h, Native.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return false;
        var sb = new StringBuilder(512);
        Native.GetClassName(h, sb, sb.Capacity);
        if (ShellClasses.Contains(sb.ToString())) return false;
        sb.Clear();
        Native.GetWindowText(h, sb, sb.Capacity);
        title = sb.ToString();
        return title.Length > 0;
    }

    public static string PinnedDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar");

    /// <summary>
    /// Everything pinned to the taskbar, without our own group shortcuts:
    /// 1. the shortcuts in the pinned folder (User Pinned\TaskBar);
    /// 2. pinned buttons the taskbar shows that have no shortcut there (0.6.3, Kurt: FreeFileSync was
    ///    missing). Windows keeps those as a reference to another shortcut: FreeFileSync's is in
    ///    User Pinned\ImplicitAppShortcuts, which also holds apps that aren't pinned (4 of Kurt's 5
    ///    Blender versions), so the taskbar's own buttons (UI Automation, "... pinned") say which apps
    ///    are pinned, and each is matched by app ID to a shortcut there or in the Start menu; Store apps
    ///    (Copilot, QuickLook) become shell:AppsFolder\&lt;app id&gt;.
    /// </summary>
    public static List<Candidate> Pinned()
    {
        var list = new List<Candidate>();
        var covered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // app ids, targets and names of listed pins
        if (Directory.Exists(PinnedDir))
            foreach (var file in Directory.GetFiles(PinnedDir, "*.lnk"))
            {
                string detail;
                try
                {
                    var info = Shortcuts.Read(file);
                    if (IsOurGroup(info)) continue;
                    detail = info.Target.Length > 0
                        ? info.Target + (info.Arguments.Length > 0 ? " " + info.Arguments : "")
                        : Path.GetFileName(file);
                    if (info.AppId != null) covered.Add(info.AppId);
                    if (info.Target.Length > 0) covered.Add(info.Target);
                }
                catch { detail = Path.GetFileName(file); }
                covered.Add(Path.GetFileNameWithoutExtension(file));
                list.Add(new Candidate(Path.GetFileNameWithoutExtension(file), file, detail, CopyLink: true));
            }

        List<TaskbarButton> buttons;
        try { buttons = TaskbarButtons.All(); } catch { buttons = new(); }
        Dictionary<string, string>? linksByAppId = null;
        foreach (var b in buttons.Where(IsPinned).GroupBy(b => b.AppId, StringComparer.OrdinalIgnoreCase).Select(g => g.First()))
        {
            string name = ButtonName(b);
            if (b.GroupId != null || covered.Contains(b.AppId) || covered.Contains(name)) continue;
            linksByAppId ??= LinksByAppId();
            Candidate? c = null;
            if (linksByAppId.TryGetValue(b.AppId, out var lnk))
                c = new Candidate(name, lnk, "Pinned  ·  " + lnk, CopyLink: true, PinnedAppId: b.AppId);
            else if (File.Exists(b.AppId))
                c = new Candidate(name, b.AppId, "Pinned  ·  " + b.AppId, PinnedAppId: b.AppId);
            else if (Native.ShellDisplayName(@"shell:AppsFolder\" + b.AppId) is { } display)
                c = new Candidate(display, @"shell:AppsFolder\" + b.AppId, "Pinned Store app  ·  " + b.AppId, PinnedAppId: b.AppId);
            if (c == null) continue;
            covered.Add(b.AppId);
            list.Add(c);
        }
        return list.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    static bool IsOurGroup(Shortcuts.LinkInfo info) =>
        info.Arguments.StartsWith("--group ", StringComparison.Ordinal) &&
        Path.GetFileName(info.Target).Equals("TaskbarGroup.exe", StringComparison.OrdinalIgnoreCase);

    /// <summary>The taskbar names a pinned button "&lt;app&gt; pinned" or "&lt;app&gt; - 2 running windows pinned".</summary>
    static bool IsPinned(TaskbarButton b) => b.Name.EndsWith(" pinned", StringComparison.OrdinalIgnoreCase);

    static string ButtonName(TaskbarButton b) =>
        System.Text.RegularExpressions.Regex.Replace(b.Name, @"( - \d+ running windows?)?( pinned)?$", "").Trim();

    /// <summary>Shortcuts that carry an explicit app ID: ImplicitAppShortcuts and both Start menus.</summary>
    static Dictionary<string, string> LinksByAppId()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var dirs = new[]
        {
            Path.Combine(Path.GetDirectoryName(PinnedDir)!, "ImplicitAppShortcuts"),
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
        };
        foreach (var dir in dirs.Where(Directory.Exists))
            foreach (var f in SafeFiles(dir))
                try
                {
                    if (Shortcuts.Read(f).AppId is { Length: > 0 } id && !map.ContainsKey(id)) map[id] = f;
                }
                catch { /* unreadable shortcut */ }
        return map;
    }

    static IEnumerable<string> SafeFiles(string dir)
    {
        try { return Directory.EnumerateFiles(dir, "*.lnk", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }).ToList(); }
        catch { return Array.Empty<string>(); }
    }

    public static string LinksDir => Path.Combine(Store.Home, "Links");

    /// <summary>Turns a chosen candidate into a group item. A taskbar pin is copied into data\Links
    /// first (keeping its target, arguments, icon and run-as settings), so the item keeps working
    /// after the app is unpinned.</summary>
    public static AppItem ToItem(Candidate c)
    {
        string path = c.Path;
        if (c.CopyLink)
        {
            Directory.CreateDirectory(LinksDir);
            string name = Shortcuts.SafeName(c.Name);
            string dest = Path.Combine(LinksDir, name + ".lnk");
            for (int n = 2; File.Exists(dest) && !SameFile(dest, c.Path); n++)
                dest = Path.Combine(LinksDir, $"{name} ({n}).lnk");
            File.Copy(c.Path, dest, overwrite: true);
            path = dest;
        }
        return new AppItem { Name = c.Name, Path = path };
    }

    /// <summary>
    /// Unpins a taskbar pin with the shell's own "Unpin from taskbar" command (there's no official API;
    /// deleting the .lnk would leave a dead button). Tries the language-independent verb name first,
    /// then the menu entry by its English caption. Success = Windows removed the pin's .lnk from the
    /// pinned folder within 3 s.
    /// </summary>
    public static bool Unpin(Candidate c)
    {
        if (c.PinnedAppId == null)
        {
            if (!File.Exists(c.Path)) return true;
            return InvokeUnpin(Path.GetDirectoryName(c.Path)!, Path.GetFileName(c.Path), () => !File.Exists(c.Path));
        }
        // A pin kept outside the pinned folder: the verb on its shortcut (or on the Start menu's app
        // entry); done when the taskbar no longer shows the app as pinned.
        bool Unpinned() => !TaskbarButtons.All().Any(b => IsPinned(b) &&
            string.Equals(b.AppId, c.PinnedAppId, StringComparison.OrdinalIgnoreCase));
        return c.Path.StartsWith(@"shell:AppsFolder\", StringComparison.OrdinalIgnoreCase)
            ? InvokeUnpin("shell:AppsFolder", c.PinnedAppId, Unpinned)
            : InvokeUnpin(Path.GetDirectoryName(c.Path)!, Path.GetFileName(c.Path), Unpinned);
    }

    static bool InvokeUnpin(string folder, string itemName, Func<bool> done)
    {
        var type = Type.GetTypeFromProgID("Shell.Application");
        if (type == null) return false;
        dynamic shell = Activator.CreateInstance(type)!;
        try
        {
            dynamic? item = shell.Namespace(folder)?.ParseName(itemName);
            if (item == null) return false;
            try { item.InvokeVerb("taskbarunpin"); } catch { /* try the menu entry */ }
            if (WaitFor(done)) return true;
            foreach (dynamic verb in item.Verbs())
            {
                string name = ((string)verb.Name).Replace("&", "");
                if (name.Equals("Unpin from taskbar", StringComparison.OrdinalIgnoreCase))
                {
                    verb.DoIt();
                    return WaitFor(done);
                }
            }
            return false;
        }
        catch { return false; }
        finally { System.Runtime.InteropServices.Marshal.ReleaseComObject(shell); }
    }

    static bool WaitFor(Func<bool> done)
    {
        for (int i = 0; i < 30; i++)
        {
            if (done()) return true;
            Thread.Sleep(100);
        }
        return done();
    }

    public static bool InGroup(Candidate c, Group g) => g.Items.Any(i =>
        string.Equals(i.Path, c.Path, StringComparison.OrdinalIgnoreCase) ||
        (c.CopyLink && i.Path.StartsWith(LinksDir, StringComparison.OrdinalIgnoreCase) && File.Exists(i.Path) && SameFile(i.Path, c.Path)));

    static bool SameFile(string a, string b) =>
        new FileInfo(a).Length == new FileInfo(b).Length && File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b));
}
