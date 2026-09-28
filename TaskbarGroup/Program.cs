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
    /// --watch: the hover helper (tray icon; opens groups when the mouse rests on their buttons).
    /// --selftest &lt;scratch dir&gt;: shortcut, icon and rendering checks; report in selftest.txt.
    /// </summary>
    [STAThread]
    static int Main(string[] args)
    {
        string? Arg(int i) => args.Length > i ? args[i] : null;

        // Before any window: the pop-up belongs to the pinned button with the same ID.
        if (Arg(0) == "--group" && Arg(1) is { } gid)
        {
            // A group with a default app: a click starts it (this process has the click's right to the
            // foreground, which the app inherits). Shift+click opens the pop-up instead.
            bool shift = (Native.GetAsyncKeyState(0x10 /* VK_SHIFT */) & 0x8000) != 0;
            if (!shift && Store.Load().Find(gid)?.DefaultItem() is { } defaultApp)
            {
                Watcher.Send("close " + gid);   // a hover pop-up of this group is no longer needed
                return defaultApp.StartOrReport() ? 0 : 1;
            }
            // The hover helper already has everything loaded: hand the click over to it.
            if (Watcher.Send("click " + gid)) return 0;
            Native.SetCurrentProcessExplicitAppUserModelID(new Group { Id = gid }.AppId);
        }

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
            case "--watch":
            {
                using var mutex = new Mutex(true, Watcher.MutexName, out bool first);
                if (!first || !Store.Load().Hover.Enabled) return 0;
                Application.Run(new Watcher());
                return 0;
            }
            case "--dump-icons":
            {
                // Diagnostic: each item's icon as the app loads it (48 and 256 px) plus its shortcut details.
                var group = Store.Load().Groups.FirstOrDefault(g => g.Name == Arg(1) || g.Id == Arg(1));
                string outDir = Arg(2) ?? throw new ArgumentException("--dump-icons <group> <dir>");
                if (group == null) return 1;
                Directory.CreateDirectory(outDir);
                var report = new StringBuilder();
                for (int i = 0; i < group.Items.Count; i++)
                {
                    var item = group.Items[i];
                    foreach (int size in new[] { 48, 256 })
                        using (var bmp = Icons.ForPath(item.Path, size))
                        {
                            bmp.Save(Path.Combine(outDir, $"{i}_{size}.png"), ImageFormat.Png);
                            report.AppendLine($"{i} {item.Name} @{size}: {bmp.Width}x{bmp.Height} {bmp.PixelFormat}");
                        }
                    if (item.Path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                        try
                        {
                            var l = Shortcuts.Read(item.Path);
                            report.AppendLine($"   target {l.Target} | icon {l.Icon} | appid {l.AppId}");
                        }
                        catch (Exception ex) { report.AppendLine("   (unreadable: " + ex.Message + ")"); }
                }
                File.WriteAllText(Path.Combine(outDir, "icons.txt"), report.ToString());
                return 0;
            }
            case "--quit-helper":
                // Stops this data folder's hover helper (e.g. before a rebuild, which can't replace a running exe).
                return Watcher.Send("quit") ? 0 : 1;
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
            tools.DefaultApp = tools.Items[2].Path;   // shows the default badge in the snapshots
            var empty = new Group { Name = "Games" };
            store.Groups.Add(tools);
            store.Groups.Add(empty);
            store.Save();

            var loaded = Store.Load();
            Check(loaded.Groups[0].DefaultItem()?.Path == tools.Items[2].Path && loaded.Groups[1].DefaultItem() == null,
                "default app saved and found: " + loaded.Groups[0].DefaultItem()?.Name);
            var gone = new Group { Items = tools.Items.Take(2).ToList(), DefaultApp = tools.DefaultApp };
            Check(gone.DefaultItem() == null, "a default app that left the group means no default (a click opens the pop-up)");
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

            // Group icons: grid (default), one app, custom picture; every change gives a new icon file.
            {
                string gridIcon = Shortcuts.IconFile(tools);
                tools.Icon = new GroupIcon { Kind = GroupIcon.App, AppPath = tools.Items[1].Path };
                string appIconFile = Shortcuts.IconFile(tools);
                Check(Icons.EffectiveKind(tools) == GroupIcon.App && appIconFile != gridIcon, "app icon choice gets its own icon file");
                var without = new Group { Id = tools.Id, Name = tools.Name, Items = tools.Items.Skip(2).ToList(), Icon = tools.Icon };
                Check(Icons.EffectiveKind(without) == GroupIcon.Grid, "a chosen app that left the group falls back to the grid");

                string picture = Path.Combine(dir, "tile.png");
                using (var picker = new IconPickerForm(tools))
                {
                    picker.StartPosition = FormStartPosition.Manual;
                    picker.Location = Screen.PrimaryScreen.WorkingArea.Location;
                    picker.Show();
                    picker.Preview(0, picture);
                    Application.DoEvents();
                    Snap(picker, Path.Combine(dir, "icon_picker.png"));
                    picker.Apply();
                    picker.Close();
                }
                Check(Icons.EffectiveKind(tools) == GroupIcon.Custom && tools.Icon.File is { } f && !Path.IsPathRooted(f) &&
                      File.ReadAllBytes(tools.Icon.CustomPath!).AsSpan().SequenceEqual(File.ReadAllBytes(picture)),
                    "custom picture copied into data\\icons\\custom: " + tools.Icon.File);
                store.Save();
                Shortcuts.Refresh(tools);
                var customLink = Shortcuts.Read(Directory.GetFiles(Store.ShortcutsDir, "*.lnk").Single());
                Check(customLink.Icon == Shortcuts.IconFile(tools) && File.Exists(customLink.Icon) && customLink.Icon != gridIcon,
                    "the shortcut uses the custom icon: " + Path.GetFileName(customLink.Icon));
                using (var customTile = Icons.GroupPicture(tools, 256, p => Icons.ForPath(p, 256)))
                    customTile.Save(Path.Combine(dir, "tile_custom.png"), ImageFormat.Png);

                using (var picker = new IconPickerForm(tools))
                {
                    picker.Preview(0);   // back to the grid
                    picker.Apply();
                }
                Check(tools.Icon.Kind == GroupIcon.Grid && Directory.GetFiles(GroupIcon.CustomDir).Length == 0,
                    "back to the grid removes the unused custom picture");
                Check(Shortcuts.IconFile(tools) == gridIcon, "and gives the grid's icon file again");
                store.Save();
                Shortcuts.Refresh(tools);

                // An out-of-date picture (as after a drawing change) is redrawn when settings opens.
                File.Delete(Shortcuts.IconFile(tools));
                using (new SettingsForm(Store.Load())) { }
                var refreshed = Shortcuts.Read(Directory.GetFiles(Store.ShortcutsDir, "*.lnk").Single());
                Check(File.Exists(Shortcuts.IconFile(tools)) && refreshed.Icon == Shortcuts.IconFile(tools),
                    "a missing group picture is redrawn when settings opens");
            }

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
            Check(pinned.All(c => (c.CopyLink && File.Exists(c.Path)) || c.PinnedAppId != null),
                $"taskbar pins: {pinned.Count} ({pinned.Count(c => c.PinnedAppId != null)} kept outside the pinned folder: " +
                string.Join(", ", pinned.Where(c => c.PinnedAppId != null).Select(c => c.Name)) + ")");
            Check(pinned.Select(c => c.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() == pinned.Count,
                "each pinned app listed once");
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

            // Hover: find group buttons on the real taskbar (read-only) and hit-test their centers.
            var buttons = TaskbarButtons.All();
            var groupButtons = buttons.Where(b => b.GroupId != null).ToList();
            Check(buttons.Count > 0, $"taskbar buttons found: {buttons.Count}, groups among them: " +
                (groupButtons.Count == 0 ? "none pinned" : string.Join(", ", groupButtons.Select(b => b.Name))));
            var clock = Stopwatch.StartNew();
            foreach (var b in groupButtons.Count > 0 ? groupButtons : buttons.Take(3).ToList())
            {
                var center = new Point(b.Bounds.X + b.Bounds.Width / 2, b.Bounds.Y + b.Bounds.Height / 2);
                var hit = TaskbarButtons.At(center);
                bool over = TaskbarButtons.OverTaskbar(center);
                IntPtr under = Native.WindowFromPoint(new Native.POINT { X = center.X, Y = center.Y });
                Check(over && hit?.AppId == b.AppId,
                    $"hit test at {center} finds \"{b.Name}\" ({b.AppId}); over taskbar {over}, " +
                    $"window {Native.ClassOf(under)} / root {Native.ClassOf(Native.GetAncestor(under, Native.GA_ROOT))}, hit {hit?.AppId ?? "none"}");
            }
            log.AppendLine($"      hit test average: {clock.ElapsedMilliseconds / Math.Max(1, Math.Max(groupButtons.Count, Math.Min(3, buttons.Count)))} ms");

            // A hover pop-up must not take focus, and must close once the mouse is away from it and its button.
            {
                var wa = Screen.PrimaryScreen.WorkingArea;
                var fakeButton = new Rectangle(wa.Right - 60, wa.Bottom - 4, 56, 4);   // bottom-right corner, away from the mouse
                using var hp = new PopupForm(tools, new Point(fakeButton.X + 28, fakeButton.Bottom), hoverZone: fakeButton);
                hp.Show();
                for (int i = 0; i < 5; i++) { Application.DoEvents(); Thread.Sleep(20); }
                IntPtr fg = Native.GetForegroundWindow();
                Check(fg != hp.Handle, "hover pop-up doesn't take focus");
                Check(hp.IsHover, "hover pop-up is still in hover mode after showing");
                bool inZone = Rectangle.Union(hp.Bounds, fakeButton).Contains(Cursor.Position);
                var until = DateTime.Now.AddSeconds(2);
                while (!hp.IsDisposed && hp.Visible && DateTime.Now < until) { Application.DoEvents(); Thread.Sleep(20); }
                Check(inZone || hp.IsDisposed || !hp.Visible, inZone
                    ? "hover pop-up leave check skipped (the mouse is over the test pop-up)"
                    : "hover pop-up closes by itself when the mouse is elsewhere");
            }

            // Tooltips never cover their control: a button at the bottom of the screen gets its tip above it.
            using (var tipForm = new Form { FormBorderStyle = FormBorderStyle.None, ShowInTaskbar = false, StartPosition = FormStartPosition.Manual })
            {
                var wa = Screen.PrimaryScreen.WorkingArea;
                tipForm.Bounds = new Rectangle(wa.Left + 20, wa.Bottom - 40, 200, 40);
                var tipButton = new Button { Text = "Bottom", Dock = DockStyle.Fill };
                tipForm.Controls.Add(tipButton);
                tipForm.Show();
                Application.DoEvents();
                Ui.ShowTip(tipButton, "A tooltip for a button at the bottom of the screen\nsecond line", tipButton.ClientRectangle);
                for (int i = 0; i < 5; i++) { Application.DoEvents(); Thread.Sleep(20); }
                var buttonRect = tipButton.RectangleToScreen(tipButton.ClientRectangle);
                var tipRect = Rectangle.Empty;
                uint me = (uint)Environment.ProcessId;
                Native.EnumWindows((h, _) =>
                {
                    Native.GetWindowThreadProcessId(h, out uint pid);
                    if (pid == me && Native.IsWindowVisible(h) && Native.ClassOf(h).Contains("tooltips_class32"))
                        tipRect = Native.WindowRect(h);
                    return true;
                }, IntPtr.Zero);
                Check(!tipRect.IsEmpty && !tipRect.IntersectsWith(buttonRect) && tipRect.Bottom <= buttonRect.Top,
                    $"tooltip of a bottom-edge button sits above it (tip {tipRect}, button {buttonRect})");
                Ui.HideTip(tipButton);
            }

            // Hover helper: one per data folder, starts, answers, refuses a second copy, quits.
            var hs = Store.Load();
            hs.Hover.Enabled = true;
            hs.Save();
            Check(!Watcher.IsRunning, "no helper running for the scratch data folder yet");
            using (var helper = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--watch") { UseShellExecute = false })!)
            {
                for (int i = 0; i < 100 && !Watcher.IsRunning; i++) Thread.Sleep(50);
                Check(Watcher.IsRunning, "helper starts (window " + Watcher.WindowTitle + ")");
                using (var second = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--watch") { UseShellExecute = false })!)
                    Check(second.WaitForExit(5000) && second.ExitCode == 0, "a second helper exits at once");
                Check(Watcher.Send("reload"), "helper answers");
                Watcher.Send("quit");
                Check(helper.WaitForExit(5000), "helper quits when asked");
            }
            Check(!Watcher.Send("reload"), "nothing answers after it quit");

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
