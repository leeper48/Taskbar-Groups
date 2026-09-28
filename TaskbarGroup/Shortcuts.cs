using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Security.Cryptography;
using System.Text;

namespace WindowsTaskbarGroup;

/// <summary>
/// A group is pinned as a shortcut: <c>TaskbarGroup.exe --group &lt;id&gt;</c> with the group's icon and
/// its own AppUserModelID. The taskbar keys pinned buttons by that ID, so every group is its own
/// button, and the pop-up process sets the same ID (Program) so it belongs to that button.
/// Shortcuts go to <c>data\Shortcuts</c> and to Start Menu\Programs\Taskbar Groups (skipped when
/// TASKBARGROUP_HOME is set, for tests).
/// </summary>
static class Shortcuts
{
    public static string StartMenuDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Taskbar Groups");

    static bool TestMode => Environment.GetEnvironmentVariable("TASKBARGROUP_HOME") is { Length: > 0 };

    public static IEnumerable<string> Folders()
    {
        yield return Store.ShortcutsDir;
        if (!TestMode) yield return StartMenuDir;
    }

    public static string Arguments(Group g) => "--group " + g.Id;

    /// <summary>The icon's file name carries a hash of what it shows, so a changed group gets a new
    /// path and Windows' icon cache can't keep showing the old picture.</summary>
    public static string IconFile(Group g)
    {
        var key = Icons.EffectiveKind(g) switch
        {
            GroupIcon.App => "app\n" + g.Icon.AppPath,
            GroupIcon.Custom => "custom\n" + g.Icon.File + "\n" + File.GetLastWriteTimeUtc(g.Icon.CustomPath!).Ticks,
            _ => g.Name + "\n" + string.Join("\n", g.Items.Take(4).Select(i => i.Path)),
        };
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..8].ToLowerInvariant();
        return Path.Combine(Store.IconsDir, $"{g.Id}-{hash}.ico");
    }

    public static string EnsureIcon(Group g)
    {
        string file = IconFile(g);
        if (!File.Exists(file))
        {
            Icons.WriteGroupIcon(g, file);
            // Old pictures of this group are no longer referenced by its shortcuts.
            foreach (var old in Directory.GetFiles(Store.IconsDir, g.Id + "-*.ico"))
                if (!string.Equals(old, file, StringComparison.OrdinalIgnoreCase))
                    try { File.Delete(old); } catch { /* in use: removed next time */ }
        }
        return file;
    }

    public static string SafeName(string name)
    {
        var bad = Path.GetInvalidFileNameChars();
        var s = new string(name.Select(c => bad.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        return s.Length == 0 ? "Group" : s;
    }

    /// <summary>Writes (or rewrites) the group's shortcuts; returns their paths.</summary>
    public static List<string> Create(Group g)
    {
        string exe = Environment.ProcessPath!;
        string icon = EnsureIcon(g);
        var made = new List<string>();
        foreach (var dir in Folders())
        {
            Directory.CreateDirectory(dir);
            foreach (var old in Find(dir, g)) File.Delete(old);
            string file = Path.Combine(dir, SafeName(g.Name) + ".lnk");
            Write(file, exe, Arguments(g), Path.GetDirectoryName(exe)!, icon, g.AppId, "Open the " + g.Name + " group");
            made.Add(file);
        }
        UpdatePinned(g, icon);
        return made;
    }

    /// <summary>
    /// The taskbar keeps its own copy of a pinned shortcut (in the User Pinned\TaskBar folder), which
    /// still points at the old picture. Rewrites that copy's icon, keeping its target, arguments and
    /// AppUserModelID, then asks the shell to refresh icons. Whether the taskbar redraws at once is
    /// for Kurt to confirm; unpin + pin always works. Skipped in test mode (that folder is real).
    /// </summary>
    static void UpdatePinned(Group g, string icon)
    {
        if (TestMode || !Directory.Exists(AppSources.PinnedDir)) return;
        bool changed = false;
        foreach (var f in Directory.GetFiles(AppSources.PinnedDir, "*.lnk"))
        {
            try
            {
                var info = Read(f);
                if (info.Arguments != Arguments(g) || string.Equals(info.Icon, icon, StringComparison.OrdinalIgnoreCase)) continue;
                Write(f, info.Target, info.Arguments, Path.GetDirectoryName(info.Target)!, icon, g.AppId,
                    "Open the " + g.Name + " group");
                Native.SHChangeNotify(0x00002000 /* SHCNE_UPDATEITEM */, 0x0005 /* SHCNF_PATHW */, f, IntPtr.Zero);
                changed = true;
            }
            catch { /* leave that pin as it is */ }
        }
        if (changed) Native.SHChangeNotify(0x08000000 /* SHCNE_ASSOCCHANGED */, 0, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>Refreshes shortcuts that already exist (after a rename or app change); makes none.</summary>
    public static void Refresh(Group g)
    {
        if (Folders().Any(d => Find(d, g).Any())) Create(g);
    }

    public static void Delete(Group g)
    {
        foreach (var dir in Folders())
            foreach (var f in Find(dir, g)) File.Delete(f);
        if (Directory.Exists(Store.IconsDir))
            foreach (var f in Directory.GetFiles(Store.IconsDir, g.Id + "-*.ico"))
                try { File.Delete(f); } catch { }
        if (Directory.Exists(GroupIcon.CustomDir))
            foreach (var f in Directory.GetFiles(GroupIcon.CustomDir, g.Id + "-*"))
                try { File.Delete(f); } catch { }
    }

    public static IEnumerable<string> Find(string dir, Group g)
    {
        if (!Directory.Exists(dir)) return Array.Empty<string>();
        return Directory.GetFiles(dir, "*.lnk").Where(f =>
        {
            try { return Read(f).Arguments == Arguments(g); } catch { return false; }
        }).ToList();
    }

    public static void Write(string file, string target, string args, string workDir, string icon,
        string appId, string description)
    {
        var link = (Native.IShellLinkW)new Native.CShellLink();
        try
        {
            link.SetPath(target);
            link.SetArguments(args);
            link.SetWorkingDirectory(workDir);
            link.SetIconLocation(icon, 0);
            link.SetDescription(description);

            var store = (Native.IPropertyStore)link;
            var pv = new Native.PROPVARIANT { vt = Native.VT_LPWSTR, p = Marshal.StringToCoTaskMemUni(appId) };
            try
            {
                store.SetValue(ref Native.PKEY_AppUserModel_ID, ref pv);
                store.Commit();
            }
            finally { Marshal.FreeCoTaskMem(pv.p); }

            ((IPersistFile)link).Save(file, true);
        }
        finally { Marshal.ReleaseComObject(link); }
    }

    public record LinkInfo(string Target, string Arguments, string Icon, string? AppId);

    public static LinkInfo Read(string file)
    {
        var link = (Native.IShellLinkW)new Native.CShellLink();
        try
        {
            ((IPersistFile)link).Load(file, 0);
            var target = new StringBuilder(1024);
            link.GetPath(target, target.Capacity, IntPtr.Zero, 0);
            var args = new StringBuilder(1024);
            link.GetArguments(args, args.Capacity);
            var icon = new StringBuilder(1024);
            link.GetIconLocation(icon, icon.Capacity, out _);

            string? appId = null;
            var store = (Native.IPropertyStore)link;
            store.GetValue(ref Native.PKEY_AppUserModel_ID, out var pv);
            try { if (pv.vt == Native.VT_LPWSTR) appId = Marshal.PtrToStringUni(pv.p); }
            finally { Native.PropVariantClear(ref pv); }

            return new LinkInfo(target.ToString(), args.ToString(), icon.ToString(), appId);
        }
        finally { Marshal.ReleaseComObject(link); }
    }
}
