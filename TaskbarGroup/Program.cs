using System.Diagnostics;
using System.Drawing.Imaging;
using System.Reflection;
using System.Text;

namespace WindowsTaskbarGroup;

static class Program
{
    public static string Version =>
        typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            .Split('+')[0] ?? "?";

    /// <summary>
    /// No arguments: the settings window.
    /// --group &lt;id&gt;: the group's pop-up (what a pinned shortcut runs).
    /// --edit &lt;id&gt;: the settings window with that group selected.
    /// --selftest &lt;scratch dir&gt;: shortcut, icon and rendering checks; report in selftest.txt.
    /// </summary>
    [STAThread]
    static int Main(string[] args)
    {
        string? Arg(int i) => args.Length > i ? args[i] : null;

        // Before any window: the pop-up belongs to the pinned button with the same ID.
        if (Arg(0) == "--group" && Arg(1) is { } gid)
            Native.SetCurrentProcessExplicitAppUserModelID(new Group { Id = gid }.AppId);

        ApplicationConfiguration.Initialize();

        switch (Arg(0))
        {
            case "--group":
            {
                var group = Store.Load().Find(Arg(1) ?? "");
                if (group == null)
                {
                    MessageBox.Show("This group no longer exists. Unpin it from the taskbar, or open Windows Taskbar Group to make a new one.",
                        "Windows Taskbar Group", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return 1;
                }
                Application.Run(new PopupForm(group, Cursor.Position));
                return 0;
            }
            case "--selftest":
                return SelfTest.Run(Arg(1) ?? throw new ArgumentException("--selftest <scratch dir>"));
            default:
                if (BringExistingToFront()) return 0;
                Application.Run(new SettingsForm(Store.Load(), Arg(0) == "--edit" ? Arg(1) : null));
                return 0;
        }
    }

    /// <summary>One settings window at a time, so two can't overwrite each other's saves.</summary>
    static bool BringExistingToFront()
    {
        using var me = Process.GetCurrentProcess();
        foreach (var p in Process.GetProcessesByName(me.ProcessName))
        {
            using (p)
            {
                if (p.Id == me.Id || p.MainWindowHandle == IntPtr.Zero) continue;
                if (!p.MainWindowTitle.StartsWith("Windows Taskbar Group v")) continue;
                if (Native.IsIconic(p.MainWindowHandle)) Native.ShowWindow(p.MainWindowHandle, 9 /* SW_RESTORE */);
                Native.SetForegroundWindow(p.MainWindowHandle);
                return true;
            }
        }
        return false;
    }
}

/// <summary>Checks on a scratch data folder: shortcut target, arguments and AppUserModelID read back,
/// the group icon is a valid .ico, and the settings window and pop-up render (PNG files).</summary>
static class SelfTest
{
    public static int Run(string dir)
    {
        Directory.CreateDirectory(dir);
        Environment.SetEnvironmentVariable("TASKBARGROUP_HOME", Path.Combine(dir, "data"));
        var log = new StringBuilder();
        int failed = 0;
        void Check(bool ok, string what)
        {
            log.AppendLine((ok ? "ok    " : "FAIL  ") + what);
            if (!ok) failed++;
        }
        log.AppendLine("Windows Taskbar Group v" + Program.Version + " self-test");

        try
        {
            string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string sys = Environment.SystemDirectory;
            var store = new Store();
            var tools = new Group { Name = "Tools" };
            foreach (var p in new[] { Path.Combine(sys, "notepad.exe"), Path.Combine(sys, "calc.exe"),
                         Path.Combine(sys, "mspaint.exe"), Path.Combine(win, "explorer.exe"),
                         Path.Combine(sys, "cmd.exe"), win, Path.Combine(sys, "taskmgr.exe") })
                tools.Items.Add(new AppItem { Name = SettingsForm.NameFor(p), Path = p });
            var empty = new Group { Name = "Games" };
            store.Groups.Add(tools);
            store.Groups.Add(empty);
            store.Save();

            var loaded = Store.Load();
            Check(loaded.Groups.Count == 2 && loaded.Groups[0].Items.Count == tools.Items.Count, "groups.json saves and loads");

            var made = Shortcuts.Create(tools);
            Check(made.Count == 1 && File.Exists(made[0]), "shortcut written (test mode: data\\Shortcuts only)");
            var info = Shortcuts.Read(made[0]);
            Check(string.Equals(info.Target, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase), "target = this exe: " + info.Target);
            Check(info.Arguments == "--group " + tools.Id, "arguments: " + info.Arguments);
            Check(info.AppId == tools.AppId, "AppUserModelID: " + info.AppId);
            Check(File.Exists(info.Icon), "icon file: " + Path.GetFileName(info.Icon));
            using (var ico = new Icon(info.Icon, 32, 32)) Check(ico.Width == 32, "icon loads at 32 px");
            using (var ico = new Icon(info.Icon, 48, 48)) Check(ico.Width == 48, "icon loads at 48 px");
            // System.Drawing never picks a PNG frame, so read the 256 entry from the directory.
            using (var r = new BinaryReader(File.OpenRead(info.Icon)))
            {
                r.ReadUInt16(); r.ReadUInt16();
                int count = r.ReadUInt16();
                var sizes = new List<int>();
                bool png256 = false;
                for (int i = 0; i < count; i++)
                {
                    int w = r.ReadByte(); r.ReadBytes(7); r.ReadInt32(); int off = r.ReadInt32();
                    sizes.Add(w == 0 ? 256 : w);
                    if (w == 0)
                    {
                        long back = r.BaseStream.Position;
                        r.BaseStream.Position = off;
                        png256 = r.ReadUInt32() == 0x474E5089;  // \x89PNG
                        r.BaseStream.Position = back;
                    }
                }
                Check(sizes.SequenceEqual(new[] { 16, 20, 24, 32, 40, 48, 64, 256 }) && png256,
                    "icon frames " + string.Join(",", sizes) + (png256 ? " (256 as PNG)" : ""));
            }

            string oldIcon = info.Icon;
            tools.Name = "Tools and More";
            store.Save();
            Shortcuts.Refresh(tools);
            var files = Directory.GetFiles(Store.ShortcutsDir, "*.lnk");
            Check(files.Length == 1 && Path.GetFileName(files[0]) == "Tools and More.lnk", "rename replaces the shortcut: " + string.Join(", ", files.Select(Path.GetFileName)));
            string newIcon = Shortcuts.Read(files[0]).Icon;
            Check(newIcon != oldIcon && File.Exists(newIcon) && !File.Exists(oldIcon), "rename gives a new icon file and removes the old");

            Shortcuts.Refresh(empty);
            Check(!Shortcuts.Find(Store.ShortcutsDir, empty).Any(), "refresh makes no shortcut for a group without one");

            // Pictures
            using (var tile = Icons.GroupTile(tools.Items.Take(4).Select(i => Icons.ForPath(i.Path, 256)).ToList(), tools.Name, 256))
                tile.Save(Path.Combine(dir, "tile.png"), ImageFormat.Png);

            var anchor = new Point(Screen.PrimaryScreen!.Bounds.Width / 2, Screen.PrimaryScreen.Bounds.Bottom - 10);
            foreach (var (g, name) in new[] { (tools, "popup.png"), (empty, "popup_empty.png") })
            {
                using var popup = new PopupForm(g, anchor, keepOpen: true);
                popup.Highlight(1);
                Check(popup.Bottom <= Screen.PrimaryScreen.WorkingArea.Bottom, $"{name}: sits above the taskbar ({popup.Bounds})");
                Snap(popup, Path.Combine(dir, name));
            }

            using (var form = new SettingsForm(Store.Load()))
            {
                form.StartPosition = FormStartPosition.Manual;
                form.Location = Screen.PrimaryScreen.WorkingArea.Location;  // off-screen would take another monitor's scaling
                form.Show();
                form.Select(0, 2);
                Application.DoEvents();
                Snap(form, Path.Combine(dir, "settings.png"));
                form.Close();
            }
            Check(File.Exists(Path.Combine(dir, "settings.png")), "settings window renders");

            // Add Apps sources
            var running = AppSources.Running();
            var pinned = AppSources.Pinned();
            var sources = new StringBuilder();
            sources.AppendLine("RUNNING");
            foreach (var c in running) sources.AppendLine($"  {c.Name} | {c.Path} | {c.Detail}");
            sources.AppendLine("PINNED (" + AppSources.PinnedDir + ")");
            foreach (var c in pinned) sources.AppendLine($"  {c.Name} | {c.Detail}");
            File.WriteAllText(Path.Combine(dir, "sources.txt"), sources.ToString());
            Check(running.Count > 0 && running.All(c => c.Path != Environment.ProcessPath), $"running apps: {running.Count} (not this app)");
            Check(running.Select(c => c.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() == running.Count, "running apps listed once each");
            var packaged = running.Where(c => c.Path.StartsWith(@"shell:AppsFolder\")).ToList();
            Check(packaged.All(c => Native.ShellDisplayName(c.Path) != null) &&
                  running.All(c => !c.Path.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase)),
                $"packaged apps go through their app ID ({packaged.Count}: {string.Join(", ", packaged.Select(c => c.Name))})");
            Check(pinned.All(c => c.CopyLink && File.Exists(c.Path)), $"taskbar pins: {pinned.Count}");
            if (pinned.Count > 0)
            {
                var item = AppSources.ToItem(pinned[0]);
                Check(item.Path.StartsWith(AppSources.LinksDir) && File.Exists(item.Path) &&
                      File.ReadAllBytes(item.Path).AsSpan().SequenceEqual(File.ReadAllBytes(pinned[0].Path)),
                    "a pin is copied into data\\Links: " + Path.GetFileName(item.Path));
                tools.Items.Add(item);
                Check(AppSources.InGroup(pinned[0], tools), "the copied pin counts as already in the group");
                Check(!AppSources.InGroup(pinned[^1], tools) || pinned.Count == 1, "other pins don't");
                tools.Items.Remove(item);
            }

            foreach (var (taskbar, name) in new[] { (false, "add_running.png"), (true, "add_taskbar.png") })
            {
                using var add = new AddAppsForm(tools);
                add.StartPosition = FormStartPosition.Manual;
                add.Location = Screen.PrimaryScreen.WorkingArea.Location;
                add.Show();
                add.Preview(taskbar, 2);
                Application.DoEvents();
                Snap(add, Path.Combine(dir, name));
                add.Close();
            }
        }
        catch (Exception ex)
        {
            Check(false, "exception: " + ex);
        }

        log.AppendLine(failed == 0 ? "ALL PASSED" : failed + " FAILED");
        File.WriteAllText(Path.Combine(dir, "selftest.txt"), log.ToString());
        return failed == 0 ? 0 : 1;
    }

    static void Snap(Control c, string file)
    {
        using var bmp = new Bitmap(c.Width, c.Height);
        c.DrawToBitmap(bmp, new Rectangle(Point.Empty, c.Size));
        bmp.Save(file, ImageFormat.Png);
    }
}
