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

    public static readonly ToolTip Tips = new()
    {
        InitialDelay = 450, ReshowDelay = 100, AutoPopDelay = 15000, ShowAlways = true,
    };

    public static T Tip<T>(T control, string text) where T : Control
    {
        Tips.SetToolTip(control, text);
        return control;
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
