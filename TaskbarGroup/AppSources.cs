using System.Text;

namespace WindowsTaskbarGroup;

/// <summary>An app offered by Add Apps. <paramref name="CopyLink"/>: Path is a shortcut that
/// belongs to someone else (a taskbar pin) and is copied into data\Links when added.</summary>
sealed record Candidate(string Name, string Path, string Detail, bool CopyLink = false);

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
            if (!Native.IsWindowVisible(h) || Native.GetWindow(h, Native.GW_OWNER) != IntPtr.Zero) return;
            long ex = Native.GetWindowLongPtr(h, Native.GWL_EXSTYLE).ToInt64();
            if ((ex & Native.WS_EX_TOOLWINDOW) != 0 && (ex & Native.WS_EX_APPWINDOW) == 0) return;
            if (Native.DwmGetWindowAttribute(h, Native.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return;
            var sb = new StringBuilder(512);
            Native.GetClassName(h, sb, sb.Capacity);
            if (ShellClasses.Contains(sb.ToString())) return;
            sb.Clear();
            Native.GetWindowText(h, sb, sb.Capacity);
            string title = sb.ToString();
            if (title.Length == 0) return;

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

    public static string PinnedDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar");

    /// <summary>The taskbar's pinned shortcuts (the folder Windows keeps them in), without our own
    /// group shortcuts. Store apps pinned to the taskbar aren't kept there; they show up under
    /// Running Apps while they're open.</summary>
    public static List<Candidate> Pinned()
    {
        var list = new List<Candidate>();
        if (!Directory.Exists(PinnedDir)) return list;
        foreach (var file in Directory.GetFiles(PinnedDir, "*.lnk"))
        {
            string detail;
            try
            {
                var info = Shortcuts.Read(file);
                if (info.Arguments.StartsWith("--group ", StringComparison.Ordinal) &&
                    Path.GetFileName(info.Target).Equals("TaskbarGroup.exe", StringComparison.OrdinalIgnoreCase)) continue;
                detail = info.Target.Length > 0
                    ? info.Target + (info.Arguments.Length > 0 ? " " + info.Arguments : "")
                    : Path.GetFileName(file);
            }
            catch { detail = Path.GetFileName(file); }
            list.Add(new Candidate(Path.GetFileNameWithoutExtension(file), file, detail, CopyLink: true));
        }
        return list.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
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
    public static bool Unpin(string pinnedLink)
    {
        if (!File.Exists(pinnedLink)) return true;
        var type = Type.GetTypeFromProgID("Shell.Application");
        if (type == null) return false;
        dynamic shell = Activator.CreateInstance(type)!;
        try
        {
            dynamic? item = shell.Namespace(Path.GetDirectoryName(pinnedLink)).ParseName(Path.GetFileName(pinnedLink));
            if (item == null) return false;
            try { item.InvokeVerb("taskbarunpin"); } catch { /* try the menu entry */ }
            if (WaitGone(pinnedLink)) return true;
            foreach (dynamic verb in item.Verbs())
            {
                string name = ((string)verb.Name).Replace("&", "");
                if (name.Equals("Unpin from taskbar", StringComparison.OrdinalIgnoreCase))
                {
                    verb.DoIt();
                    return WaitGone(pinnedLink);
                }
            }
            return false;
        }
        catch { return false; }
        finally { System.Runtime.InteropServices.Marshal.ReleaseComObject(shell); }
    }

    static bool WaitGone(string file)
    {
        for (int i = 0; i < 30; i++)
        {
            if (!File.Exists(file)) return true;
            Thread.Sleep(100);
        }
        return !File.Exists(file);
    }

    public static bool InGroup(Candidate c, Group g) => g.Items.Any(i =>
        string.Equals(i.Path, c.Path, StringComparison.OrdinalIgnoreCase) ||
        (c.CopyLink && i.Path.StartsWith(LinksDir, StringComparison.OrdinalIgnoreCase) && File.Exists(i.Path) && SameFile(i.Path, c.Path)));

    static bool SameFile(string a, string b) =>
        new FileInfo(a).Length == new FileInfo(b).Length && File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b));
}
