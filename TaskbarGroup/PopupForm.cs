using System.Diagnostics;
using System.Drawing.Drawing2D;

namespace WindowsTaskbarGroup;

/// <summary>
/// The group's pop-up: a borderless grid of the group's apps, opened next to the taskbar button that
/// was clicked. A click (or Enter, or 1-9) starts an app; clicking elsewhere or Esc closes it.
/// Opened by hover (<c>hoverZone</c> = the taskbar button), it doesn't take focus and closes once the
/// mouse has been outside the button and the pop-up for 400 ms; a click on it keeps it open.
/// </summary>
sealed class PopupForm : Form
{
    readonly Group group;
    readonly bool keepOpen;
    readonly List<Bitmap> icons;
    readonly float s;
    readonly int pad, header, iconPx, cols, rows;
    readonly bool bottomUp;
    readonly Size cell;
    Rectangle gearRect;
    int hover = -1;
    bool gearHover;
    string tipText = "";
    readonly System.Windows.Forms.Timer tipTimer = new() { Interval = 450 };
    Rectangle? hoverZone;
    readonly System.Windows.Forms.Timer leaveTimer = new() { Interval = 50 };
    DateTime lastInside = DateTime.Now;

    public bool IsHover => hoverZone != null;
    readonly AppItem? defaultItem;

    public PopupForm(Group group, Point anchor, bool keepOpen = false, Rectangle? hoverZone = null)
    {
        this.group = group;
        this.keepOpen = keepOpen;
        this.hoverZone = hoverZone;
        defaultItem = group.DefaultItem();
        leaveTimer.Tick += (_, _) => CheckLeave();
        s = Native.DpiAt(anchor) / 96f;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = hoverZone == null;   // hover: WS_EX_TOPMOST in CreateParams; the TopMost property raises Activated on Show
        KeyPreview = true;
        AutoScaleMode = AutoScaleMode.None;
        DoubleBuffered = true;
        BackColor = Ui.Panel;
        ForeColor = Ui.Text;
        Text = group.Name;
        Icon = Icons.AppIcon();
        Font = new Font("Segoe UI", 12f * s, GraphicsUnit.Pixel);

        pad = (int)(8 * s);
        header = (int)(32 * s);
        iconPx = (int)(32 * s);
        cell = new Size((int)(84 * s), (int)(80 * s));
        icons = group.Items.Select(i => Icons.ForPath(i.Path, Math.Max(48, iconPx))).ToList();

        int n = group.Items.Count;
        cols = n == 0 ? 1 : Math.Min(n, Math.Clamp((int)Math.Ceiling(Math.Sqrt(n)), 4, 8));
        rows = n == 0 ? 1 : (n + cols - 1) / cols;
        int w = Math.Max(pad * 2 + cols * cell.Width, (int)(220 * s));
        int h = header + pad + (n == 0 ? (int)(56 * s) : rows * cell.Height) + pad;
        Bounds = Place(new Size(w, h), anchor);
        bottomUp = Bounds.Bottom <= anchor.Y;   // opened above the anchor (a bottom taskbar, or Preview)
        gearRect = new Rectangle(w - header, 0, header, header);

        tipTimer.Tick += (_, _) =>
        {
            tipTimer.Stop();
            if (tipText.Length == 0) return;
            Ui.ShowTip(this, tipText, gearHover ? gearRect : hover >= 0 ? CellRect(hover) : ClientRectangle);
        };
    }

    /// <summary>Opens beside the taskbar the anchor is on (above a bottom taskbar, and so on), centered
    /// on the anchor along the taskbar; away from a taskbar it opens at the anchor. Kept on screen.</summary>
    static Rectangle Place(Size size, Point anchor)
    {
        var screen = Screen.FromPoint(anchor);
        Rectangle b = screen.Bounds, wa = screen.WorkingArea;
        int gap = (int)(8 * Native.DpiAt(anchor) / 96f);
        int near = (int)(64 * Native.DpiAt(anchor) / 96f);  // an auto-hidden taskbar leaves no work-area gap
        int x = anchor.X - size.Width / 2, y = anchor.Y - size.Height / 2;

        if (anchor.Y >= wa.Bottom || (wa.Bottom == b.Bottom && anchor.Y >= b.Bottom - near)) y = Math.Min(wa.Bottom, anchor.Y) - size.Height - gap;
        else if (anchor.Y < wa.Top || (wa.Top == b.Top && anchor.Y < b.Top + near)) y = Math.Max(wa.Top, anchor.Y) + gap;
        else if (anchor.X < wa.Left) { x = wa.Left + gap; }
        else if (anchor.X >= wa.Right) { x = wa.Right - size.Width - gap; }
        else y = anchor.Y - gap - size.Height;

        x = Math.Clamp(x, wa.Left + gap, Math.Max(wa.Left + gap, wa.Right - size.Width - gap));
        y = Math.Clamp(y, wa.Top + gap, Math.Max(wa.Top + gap, wa.Bottom - size.Height - gap));
        return new Rectangle(new Point(x, y), size);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x80;          // WS_EX_TOOLWINDOW: no Alt+Tab entry
            if (IsHover) cp.ExStyle |= 0x8;   // WS_EX_TOPMOST
            cp.ClassStyle |= 0x20000;    // CS_DROPSHADOW
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.DwmInt(Handle, Native.DWMWA_WINDOW_CORNER_PREFERENCE, Native.DWMWCP_ROUND);
        Native.DwmInt(Handle, Native.DWMWA_BORDER_COLOR, Native.ColorRef(Ui.Line));
    }

    protected override bool ShowWithoutActivation => IsHover;

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (IsHover) leaveTimer.Start();
        else Activate();
        if (Environment.GetEnvironmentVariable("TASKBARGROUP_TIMING") is { Length: > 0 } log)
        {
            var started = Process.GetCurrentProcess().StartTime;
            File.AppendAllText(log, $"shown {(DateTime.Now - started).TotalMilliseconds:0} ms after start\n");
        }
    }

    void CheckLeave()
    {
        if (hoverZone is not { } button) return;
        var zone = Rectangle.Union(Bounds, Rectangle.Inflate(button, 4, 4));
        if (zone.Contains(Cursor.Position)) lastInside = DateTime.Now;
        else if ((DateTime.Now - lastInside).TotalMilliseconds > 400) Close();
    }

    /// <summary>Turns a hover pop-up into a clicked one: stays open and takes focus (closes on click-away).</summary>
    public void PinOpen()
    {
        hoverZone = null;
        leaveTimer.Stop();
        Activate();
    }

    /// <summary>A real click on a hover pop-up keeps it open (WinForms' Activated fires on Show too, so
    /// it can't be the signal: that kept every hover pop-up open, 0.4.0).</summary>
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (IsHover) PinOpen();
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        if (!keepOpen && !IsHover) Close();   // a hover pop-up closes by its leave check
    }

    /// <summary>The first apps (the top of the group's list) sit on the row nearest the taskbar, where
    /// the mouse comes from (Kurt, 0.6.4): above a bottom taskbar rows fill from the bottom up.</summary>
    Rectangle CellRect(int i)
    {
        int row = i / cols;
        if (bottomUp) row = rows - 1 - row;
        return new(pad + i % cols * cell.Width, header + pad + row * cell.Height, cell.Width, cell.Height);
    }

    int HitTest(Point p)
    {
        for (int i = 0; i < group.Items.Count; i++)
            if (CellRect(i).Contains(p)) return i;
        return -1;
    }

    /// <summary>Shows the pop-up with an item highlighted (for snapshots).</summary>
    public void Highlight(int index) { hover = index; Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;

        using (var bold = new Font(Font, FontStyle.Bold))
            TextRenderer.DrawText(g, group.Name, bold,
                new Rectangle(pad + (int)(4 * s), 0, gearRect.Left - pad, header), Ui.Text,
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

        if (gearHover)
            using (var path = Icons.Rounded(Rectangle.Inflate(gearRect, -(int)(4 * s), -(int)(4 * s)), 6 * s))
            using (var brush = new SolidBrush(Ui.Hover))
                g.FillPath(brush, path);
        using (var gear = new Font("Segoe UI Symbol", 15f * s, GraphicsUnit.Pixel))
            TextRenderer.DrawText(g, "⚙", gear, gearRect, gearHover ? Ui.Text : Ui.SubText,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

        using (var line = new Pen(Ui.Line))
            g.DrawLine(line, pad, header - 1, Width - pad, header - 1);

        if (group.Items.Count == 0)
        {
            TextRenderer.DrawText(g, "No apps yet. Click ⚙ to add some.", Font,
                new Rectangle(0, header, Width, Height - header), Ui.SubText,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            return;
        }

        for (int i = 0; i < group.Items.Count; i++)
        {
            var r = CellRect(i);
            if (i == hover)
                using (var path = Icons.Rounded(Rectangle.Inflate(r, -(int)(2 * s), -(int)(2 * s)), 8 * s))
                using (var brush = new SolidBrush(Ui.Hover))
                    g.FillPath(brush, path);
            var iconRect = new Rectangle(r.X + (r.Width - iconPx) / 2, r.Y + (int)(10 * s), iconPx, iconPx);
            g.DrawImage(icons[i], iconRect);
            if (ReferenceEquals(group.Items[i], defaultItem))
                using (var star = new Font("Segoe UI Symbol", 11f * s, GraphicsUnit.Pixel))
                    TextRenderer.DrawText(g, "★", star, new Point(r.Right - (int)(18 * s), r.Y + (int)(4 * s)),
                        Color.FromArgb(255, 196, 64));
            var textRect = new Rectangle(r.X + (int)(4 * s), iconRect.Bottom + (int)(4 * s),
                r.Width - (int)(8 * s), r.Bottom - iconRect.Bottom - (int)(6 * s));
            TextRenderer.DrawText(g, group.Items[i].Name, Font, textRect, Ui.Text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis |
                TextFormatFlags.NoPrefix);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int h = HitTest(e.Location);
        bool gh = gearRect.Contains(e.Location);
        if (h != hover || gh != gearHover)
        {
            hover = h;
            gearHover = gh;
            Cursor = h >= 0 || gh ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }
        // One tip per cell, shown after the mouse rests there, next to the cell (Ui.ShowTip).
        string tip = gh ? "Edit this group" : h >= 0 ? Describe(group.Items[h]) : "";
        if (tip != tipText)
        {
            tipText = tip;
            Ui.HideTip(this);
            tipTimer.Stop();
            if (tip.Length > 0) tipTimer.Start();
        }
    }

    static string Describe(AppItem item) =>
        item.Name + "\n" + item.Path + (string.IsNullOrEmpty(item.Arguments) ? "" : " " + item.Arguments);

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        tipTimer.Stop();
        tipText = "";
        Ui.HideTip(this);
        if (hover >= 0 || gearHover) { hover = -1; gearHover = false; Invalidate(); }
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button != MouseButtons.Left) return;
        if (gearRect.Contains(e.Location)) { EditGroup(); return; }
        int i = HitTest(e.Location);
        if (i >= 0) Launch(i);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        int n = group.Items.Count;
        switch (e.KeyCode)
        {
            case Keys.Escape: Close(); return;
            case Keys.Enter: if (hover >= 0) Launch(hover); return;
            case Keys.Left: Move(-1); return;
            case Keys.Right: Move(1); return;
            case Keys.Up: Move(bottomUp ? cols : -cols); return;     // rows run upward when bottomUp
            case Keys.Down: Move(bottomUp ? -cols : cols); return;
        }
        int digit = e.KeyCode >= Keys.D1 && e.KeyCode <= Keys.D9 ? e.KeyCode - Keys.D1
                  : e.KeyCode >= Keys.NumPad1 && e.KeyCode <= Keys.NumPad9 ? e.KeyCode - Keys.NumPad1 : -1;
        if (digit >= 0 && digit < n) Launch(digit);

        void Move(int delta)
        {
            if (n == 0) return;
            hover = hover < 0 ? 0 : Math.Clamp(hover + delta, 0, n - 1);
            Invalidate();
        }
    }

    void Launch(int index)
    {
        var item = group.Items[index];
        Hide();
        item.StartOrReport();
        Close();
    }

    void EditGroup()
    {
        Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--edit " + group.Id) { UseShellExecute = false });
        Close();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { icons.ForEach(i => i.Dispose()); leaveTimer.Dispose(); tipTimer.Dispose(); }
        base.Dispose(disposing);
    }
}
