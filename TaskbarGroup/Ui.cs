namespace WindowsTaskbarGroup;

/// <summary>Dark look shared by the settings window and the pop-up.</summary>
static class Ui
{
    public static readonly Color Back = Color.FromArgb(30, 30, 36);
    public static readonly Color Panel = Color.FromArgb(42, 42, 50);
    public static readonly Color Field = Color.FromArgb(52, 52, 62);
    public static readonly Color Hover = Color.FromArgb(62, 62, 76);
    public static readonly Color Line = Color.FromArgb(70, 70, 84);
    public static readonly Color Text = Color.FromArgb(232, 232, 238);
    public static readonly Color SubText = Color.FromArgb(160, 160, 172);
    public static readonly Color Accent = Color.FromArgb(124, 92, 255);
    public static readonly Color AccentHover = Color.FromArgb(146, 120, 255);

    // ---------- Tooltips ----------
    // Placed by hand, never under the mouse (0.6.2, Kurt: "the tool tips flicker a lot"). With the
    // standard automatic tooltip, a tip that doesn't fit below the mouse (buttons in the bottom bar,
    // the pop-up by the taskbar) is moved up over the mouse; the control then sees the mouse leave,
    // the tip hides, the mouse is back, the tip shows: a loop. Here a tip goes below its control when
    // there's room, else above it, and is shown once per hover.

    static readonly ToolTip Tips = new() { UseAnimation = false, UseFading = false, ShowAlways = true };
    static readonly Dictionary<Control, string> tipTexts = new();
    static readonly System.Windows.Forms.Timer tipTimer = new() { Interval = 450 };
    static Control? tipPending, tipShowing;

    static Ui()
    {
        tipTimer.Tick += (_, _) =>
        {
            tipTimer.Stop();
            if (tipPending is not { IsDisposed: false } c || !tipTexts.TryGetValue(c, out var text) || text.Length == 0) return;
            var local = c.PointToClient(Cursor.Position);
            if (!c.ClientRectangle.Contains(local)) return;
            // Tall controls (lists): anchor at the mouse rather than at the whole control.
            ShowTip(c, text, c.Height < 80 ? c.ClientRectangle : new Rectangle(local.X - 10, local.Y - 12, 20, 24));
        };
    }

    /// <summary>Gives a control a tooltip (or changes its text).</summary>
    public static T Tip<T>(T control, string text) where T : Control
    {
        if (!tipTexts.ContainsKey(control))
        {
            control.MouseEnter += (_, _) => { tipPending = control; tipTimer.Stop(); tipTimer.Start(); };
            control.MouseLeave += (_, _) => { if (tipPending == control) tipPending = null; HideTip(control); };
            control.MouseDown += (_, _) => { tipPending = null; HideTip(control); };
            control.Disposed += (_, _) => { tipTexts.Remove(control); if (tipPending == control) tipPending = null; };
        }
        tipTexts[control] = text;
        return control;
    }

    /// <summary>Shows a tip for part of a control (<paramref name="anchor"/> in its client coordinates):
    /// below it if it fits on the screen, else above it, so it never covers the mouse.</summary>
    public static void ShowTip(Control owner, string text, Rectangle anchor)
    {
        var size = TextRenderer.MeasureText(text, SystemFonts.StatusFont ?? Control.DefaultFont) + new Size(16, 12);
        var screen = Screen.FromControl(owner).WorkingArea;
        int gap = 6;
        var p = new Point(anchor.Left, anchor.Bottom + gap);
        if (owner.PointToScreen(p).Y + size.Height > screen.Bottom) p = new Point(anchor.Left, anchor.Top - size.Height - gap);
        var s = owner.PointToScreen(p);
        if (s.X + size.Width > screen.Right) p.X -= s.X + size.Width - screen.Right;
        if (s.X < screen.Left) p.X += screen.Left - s.X;
        if (tipShowing != null && tipShowing != owner && !tipShowing.IsDisposed) Tips.Hide(tipShowing);
        Tips.Show(text, owner, p, 15000);
        tipShowing = owner;
    }

    public static void HideTip(Control owner)
    {
        if (tipShowing != owner) return;
        if (!owner.IsDisposed) Tips.Hide(owner);
        tipShowing = null;
    }

    public static Button FlatButton(string text, Action onClick, string tip) =>
        MakeButton(text, onClick, tip, Field, Hover);

    public static Button AccentButton(string text, Action onClick, string tip)
    {
        var b = MakeButton(text, onClick, tip, Accent, AccentHover);
        b.ForeColor = Color.White;
        b.Font = new Font(b.Font, FontStyle.Bold);
        return b;
    }

    static Button MakeButton(string text, Action onClick, string tip, Color back, Color hover)
    {
        var b = new Button
        {
            Text = text,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlatStyle = FlatStyle.Flat,
            BackColor = back,
            ForeColor = Text,
            Padding = new Padding(10, 4, 10, 4),
            Margin = new Padding(0, 0, 6, 6),
            UseVisualStyleBackColor = false,
            Cursor = Cursors.Hand,
        };
        b.FlatAppearance.BorderSize = 0;
        b.FlatAppearance.MouseOverBackColor = hover;
        b.FlatAppearance.MouseDownBackColor = hover;
        b.Click += (_, _) => onClick();
        Tip(b, tip);
        return b;
    }

    public static Label Heading(string text) => new()
    {
        Text = text.ToUpperInvariant(),
        AutoSize = true,
        ForeColor = SubText,
        Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
        Margin = new Padding(0, 4, 0, 6),
    };

    /// <summary>Dark title bar and rounded corners (Windows 11; ignored elsewhere).</summary>
    public static void DarkFrame(Form f)
    {
        f.HandleCreated += (_, _) =>
        {
            Native.DwmInt(f.Handle, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, 1);
            Native.DwmInt(f.Handle, Native.DWMWA_WINDOW_CORNER_PREFERENCE, Native.DWMWCP_ROUND);
        };
    }

    /// <summary>A small dark text prompt. Returns null on Cancel.</summary>
    public static string? Prompt(IWin32Window? owner, string title, string label, string value)
    {
        using var f = new Form
        {
            Text = title,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false,
            StartPosition = FormStartPosition.CenterParent,
            BackColor = Back, ForeColor = Text,
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(14),
            Font = new Font("Segoe UI", 9.5f),
            Icon = Icons.AppIcon(),
        };
        DarkFrame(f);
        var box = new TextBox
        {
            Text = value, Width = 360, BackColor = Field, ForeColor = Text,
            BorderStyle = BorderStyle.FixedSingle, Margin = new Padding(0, 0, 0, 12),
        };
        var ok = AccentButton("OK", () => f.DialogResult = DialogResult.OK, "Save (Enter)");
        var cancel = FlatButton("Cancel", () => f.DialogResult = DialogResult.Cancel, "Close without saving (Esc)");
        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, Margin = Padding.Empty };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        var table = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Dock = DockStyle.Fill };
        table.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(0, 0, 0, 6) });
        table.Controls.Add(box);
        table.Controls.Add(buttons);
        f.Controls.Add(table);
        f.AcceptButton = ok;
        f.CancelButton = cancel;
        f.Shown += (_, _) => { box.Focus(); box.SelectAll(); };
        return f.ShowDialog(owner) == DialogResult.OK ? box.Text.Trim() : null;
    }
}
