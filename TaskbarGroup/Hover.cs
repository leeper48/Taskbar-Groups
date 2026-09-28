using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Automation;

namespace WindowsTaskbarGroup;

/// <summary>A taskbar button as UI Automation reports it. On Windows 11 each app button is a
/// <c>Taskbar.TaskListButtonAutomationPeer</c> whose AutomationId is "Appid: &lt;AppUserModelID&gt;"
/// (checked 2026-09-28: Kurt's pinned groups show as "Appid: WindowsTaskbarGroup.Group.&lt;id&gt;").</summary>
sealed record TaskbarButton(string AppId, string Name, Rectangle Bounds)
{
    public string? GroupId => AppId.StartsWith(Group.AppIdPrefix, StringComparison.Ordinal)
        ? AppId[Group.AppIdPrefix.Length..] : null;
}

static class TaskbarButtons
{
    const string PeerClass = "Taskbar.TaskListButtonAutomationPeer";
    static readonly HashSet<string> TrayClasses = new(StringComparer.Ordinal) { "Shell_TrayWnd", "Shell_SecondaryTrayWnd" };

    /// <summary>The taskbar window (Shell_TrayWnd / Shell_SecondaryTrayWnd) under a point, or zero.</summary>
    static IntPtr TrayAt(Point p)
    {
        IntPtr h = Native.WindowFromPoint(new Native.POINT { X = p.X, Y = p.Y });
        if (h == IntPtr.Zero) return IntPtr.Zero;
        IntPtr root = Native.GetAncestor(h, Native.GA_ROOT);
        return TrayClasses.Contains(Native.ClassOf(root)) ? root : IntPtr.Zero;
    }

    /// <summary>True when the point is over a taskbar (cheap: one WindowFromPoint).</summary>
    public static bool OverTaskbar(Point p) => TrayAt(p) != IntPtr.Zero;

    // The legacy UI Automation client can't hit-test inside the Windows 11 taskbar: FromPoint stops at
    // Shell_TrayWnd (checked 2026-09-28). Listing a taskbar's buttons works, so the buttons of the
    // taskbar under the mouse are listed (at most once a second) and their rectangles hit-tested here.
    static readonly Dictionary<IntPtr, (long Time, List<TaskbarButton> Buttons)> cache = new();
    static readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();

    /// <summary>The app button under a screen point (physical pixels), or null.</summary>
    public static TaskbarButton? At(Point p, int maxAgeMs = 1000)
    {
        IntPtr tray = TrayAt(p);
        if (tray == IntPtr.Zero) return null;
        lock (cache)
        {
            if (!cache.TryGetValue(tray, out var entry) || clock.ElapsedMilliseconds - entry.Time > maxAgeMs)
                cache[tray] = entry = (clock.ElapsedMilliseconds, ButtonsOf(tray));
            return entry.Buttons.FirstOrDefault(b => b.Bounds.Contains(p));
        }
    }

    static List<TaskbarButton> ButtonsOf(IntPtr tray)
    {
        var list = new List<TaskbarButton>();
        try
        {
            var buttons = AutomationElement.FromHandle(tray).FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ClassNameProperty, PeerClass));
            foreach (AutomationElement b in buttons)
                try { if (FromElement(b) is { } tb) list.Add(tb); }
                catch (ElementNotAvailableException) { }
        }
        catch (ElementNotAvailableException) { }
        catch (COMException) { }
        catch (InvalidOperationException) { }
        return list;
    }

    static TaskbarButton? FromElement(AutomationElement el)
    {
        string id = el.Current.AutomationId ?? "";
        if (!id.StartsWith("Appid: ", StringComparison.Ordinal)) return null;
        var r = el.Current.BoundingRectangle;
        return new TaskbarButton(id[7..], el.Current.Name, new Rectangle((int)r.X, (int)r.Y, (int)r.Width, (int)r.Height));
    }

    /// <summary>Every app button on every taskbar (for the self-test).</summary>
    public static List<TaskbarButton> All()
    {
        var list = new List<TaskbarButton>();
        foreach (AutomationElement tray in AutomationElement.RootElement.FindAll(TreeScope.Children, Condition.TrueCondition))
        {
            if (!TrayClasses.Contains(tray.Current.ClassName)) continue;
            var buttons = tray.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ClassNameProperty, PeerClass));
            foreach (AutomationElement b in buttons)
                if (FromElement(b) is { } tb) list.Add(tb);
        }
        return list;
    }
}

/// <summary>
/// The hover helper (<c>TaskbarGroup.exe --watch</c>): a hidden window with a tray icon. A background
/// thread checks the mouse every 50 ms; when it rests on a group's taskbar button for the delay, the
/// group's pop-up opens above the button without taking focus. It also serves clicks: a pinned
/// shortcut's <c>--group</c> process hands its click over (WM_COPYDATA) instead of opening a second
/// pop-up. One helper per data folder (its window title and mutex carry a hash of the folder).
/// </summary>
sealed class Watcher : Form
{
    static string HomeKey => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(Path.GetFullPath(Store.Home).ToLowerInvariant())))[..8];

    public static string WindowTitle => "WindowsTaskbarGroup.Watcher." + HomeKey;
    public static string MutexName => @"Local\WindowsTaskbarGroup.Watcher." + HomeKey;

    readonly NotifyIcon tray;
    readonly Thread thread;
    volatile bool stop;
    volatile int delayMs;
    PopupForm? popup;
    string? popupGroup;

    public Watcher()
    {
        Text = WindowTitle;
        ShowInTaskbar = false;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Location = new Point(-32000, -32000);
        Size = new Size(1, 1);
        delayMs = Store.Load().Hover.DelayMs;

        var menu = new ContextMenuStrip();
        menu.Items.Add("Open Settings", null, (_, _) => OpenSettings());
        menu.Items.Add("Turn Off Hover", null, (_, _) => TurnOff());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit Hover Helper", null, (_, _) => Close());
        tray = new NotifyIcon
        {
            Icon = Icons.AppIcon(),
            Text = "Windows Taskbar Group: hover over a group to open it",
            ContextMenuStrip = menu,
            Visible = true,
        };
        tray.DoubleClick += (_, _) => OpenSettings();

        thread = new Thread(Loop) { IsBackground = true, Name = "hover" };
    }

    /// <summary>Keeps the window hidden while still creating its handle (FindWindow finds it).</summary>
    protected override void SetVisibleCore(bool value)
    {
        if (!IsHandleCreated)
        {
            CreateHandle();
            thread.Start();
        }
        base.SetVisibleCore(false);
    }

    void Loop()
    {
        var clock = Stopwatch.StartNew();
        string? candidate = null;
        long since = 0;
        bool fired = false;

        while (!stop)
        {
            Thread.Sleep(50);
            try
            {
                var p = Cursor.Position;
                long now = clock.ElapsedMilliseconds;
                var b = TaskbarButtons.At(p);

                string? gid = b?.GroupId;
                if (gid != candidate) { candidate = gid; since = now; fired = false; continue; }
                if (gid == null || fired || now - since < delayMs || Native.MouseButtonDown) continue;
                fired = true;
                var bounds = b!.Bounds;
                BeginInvoke(() => HoverOpen(gid, bounds));
            }
            catch { /* keep watching */ }
        }
    }

    bool PopupOpen => popup != null && !popup.IsDisposed;

    void Open(string gid, Point anchor, Rectangle? hoverZone)
    {
        if (PopupOpen) popup!.Close();
        var group = Store.Load().Find(gid);
        if (group == null) return;
        popup = new PopupForm(group, anchor, hoverZone: hoverZone);
        popupGroup = gid;
        var mine = popup;
        popup.FormClosed += (_, _) => { if (popup == mine) { popup = null; popupGroup = null; } };
        popup.Show();
    }

    void HoverOpen(string gid, Rectangle button)
    {
        if (PopupOpen && popupGroup == gid) return;
        Open(gid, new Point(button.X + button.Width / 2, button.Y + button.Height / 2), button);
    }

    /// <summary>A click on a group's taskbar button: pins its hover pop-up open, closes its clicked
    /// pop-up (toggle), or opens it with focus.</summary>
    void ClickGroup(string gid)
    {
        if (PopupOpen && popupGroup == gid)
        {
            if (popup!.IsHover) popup.PinOpen();
            else popup.Close();
            return;
        }
        Open(gid, Cursor.Position, null);
        popup?.Activate();
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_COPYDATA)
        {
            var cds = Marshal.PtrToStructure<Native.COPYDATASTRUCT>(m.LParam);
            string text = Marshal.PtrToStringUni(cds.lpData, cds.cbData / 2);
            m.Result = (IntPtr)1;
            if (text.StartsWith("click ", StringComparison.Ordinal)) ClickGroup(text[6..]);
            else if (text == "reload") delayMs = Store.Load().Hover.DelayMs;
            else if (text == "quit") BeginInvoke(Close);
            return;
        }
        base.WndProc(ref m);
    }

    void OpenSettings() =>
        Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false });

    void TurnOff()
    {
        var store = Store.Load();
        store.Hover.Enabled = false;
        store.Save();
        StartupLink.Remove();
        Close();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        stop = true;
        tray.Visible = false;
        tray.Dispose();
        if (PopupOpen) popup!.Close();
        base.OnFormClosed(e);
    }

    // ---------- Talking to a running helper ----------

    public static bool IsRunning => Native.FindWindow(null, WindowTitle) != IntPtr.Zero;

    /// <summary>Sends a command to the running helper; false if none is running. Passes on the right
    /// to take the foreground, so a click's pop-up can get focus.</summary>
    public static bool Send(string text)
    {
        IntPtr hwnd = Native.FindWindow(null, WindowTitle);
        if (hwnd == IntPtr.Zero) return false;
        Native.GetWindowThreadProcessId(hwnd, out uint pid);
        Native.AllowSetForegroundWindow(pid);
        IntPtr buffer = Marshal.StringToHGlobalUni(text);
        try
        {
            var cds = new Native.COPYDATASTRUCT { dwData = (IntPtr)1, cbData = text.Length * 2, lpData = buffer };
            return Native.SendMessageTimeout(hwnd, Native.WM_COPYDATA, IntPtr.Zero, ref cds,
                0x0002 /* SMTO_ABORTIFHUNG */, 3000, out IntPtr result) != IntPtr.Zero && result == (IntPtr)1;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    /// <summary>Starts the helper if hover is on and it isn't running yet.</summary>
    public static void EnsureRunning()
    {
        if (IsRunning) return;
        Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--watch") { UseShellExecute = false });
    }
}

/// <summary>The Startup-folder shortcut that starts the hover helper at sign-in (skipped in test mode).</summary>
static class StartupLink
{
    static string File => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup),
        "Taskbar Groups Hover Helper.lnk");

    static bool TestMode => Environment.GetEnvironmentVariable("TASKBARGROUP_HOME") is { Length: > 0 };

    public static void Create()
    {
        if (TestMode) return;
        string exe = Environment.ProcessPath!;
        Shortcuts.Write(File, exe, "--watch", Path.GetDirectoryName(exe)!, exe,
            "WindowsTaskbarGroup.HoverHelper", "Opens taskbar groups when the mouse rests on them");
    }

    public static void Remove()
    {
        if (!TestMode && System.IO.File.Exists(File)) System.IO.File.Delete(File);
    }
}
