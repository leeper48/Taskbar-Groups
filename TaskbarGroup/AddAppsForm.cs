namespace WindowsTaskbarGroup;

/// <summary>Add Apps: tick apps from what's running now or what's pinned to the taskbar, or browse
/// for files. <see cref="Result"/> holds the new items when it closes with OK.</summary>
sealed class AddAppsForm : Form
{
    enum Source { Running, Taskbar }

    readonly Group group;
    readonly float scale;
    readonly ListBox list = new()
    {
        Dock = DockStyle.Fill, DrawMode = DrawMode.OwnerDrawFixed, BorderStyle = BorderStyle.None,
        BackColor = Ui.Panel, ForeColor = Ui.Text, IntegralHeight = false,
    };
    readonly TextBox search = new() { BackColor = Ui.Field, ForeColor = Ui.Text, BorderStyle = BorderStyle.FixedSingle, PlaceholderText = "Search" };
    readonly Label status = new() { AutoSize = true, ForeColor = Ui.SubText, Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, 12, 6) };
    readonly Button runningTab, taskbarTab, addButton;
    readonly Dictionary<string, Bitmap> icons = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> ticked = new(StringComparer.OrdinalIgnoreCase);   // by Candidate.Path, kept across tabs
    readonly Dictionary<string, Candidate> known = new(StringComparer.OrdinalIgnoreCase);
    List<Candidate> all = new();
    List<Candidate> shown = new();
    Source source;
    readonly Panel unpinRow;

    readonly CheckBox unpinBox = new()
    {
        Text = "Unpin the ticked apps from the taskbar after adding them (they'll open from the group instead)",
        Checked = true, AutoSize = true, ForeColor = Ui.Text, FlatStyle = FlatStyle.Flat,
    };

    public List<AppItem> Result { get; } = new();

    /// <summary>What happened to the taskbar pins, for the settings window's status line (null = nothing unpinned).</summary>
    public string? UnpinReport { get; private set; }

    int S(int px) => (int)Math.Round(px * scale);

    public AddAppsForm(Group group)
    {
        this.group = group;
        Text = "Add Apps to " + group.Name;
        Icon = Icons.AppIcon();
        Font = new Font("Segoe UI", 9.5f);
        AutoScaleMode = AutoScaleMode.None;
        scale = DeviceDpi / 96f;
        ClientSize = new Size(S(640), S(560));
        MinimumSize = new Size(S(480), S(380));
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MinimizeBox = false;
        BackColor = Ui.Back;
        ForeColor = Ui.Text;
        KeyPreview = true;
        Ui.DarkFrame(this);

        runningTab = Ui.FlatButton("Running Apps", () => ShowSource(Source.Running), "Apps with a window open right now");
        taskbarTab = Ui.FlatButton("On the Taskbar", () => ShowSource(Source.Taskbar), "Apps pinned to your taskbar");
        search.Width = S(200);
        search.Margin = new Padding(S(12), S(3), 0, 0);
        search.TextChanged += (_, _) => Filter();
        Ui.Tip(search, "Type to filter by name or path");

        var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(S(10), S(10), S(10), S(4)), WrapContents = false };
        top.Controls.AddRange(new Control[] { runningTab, taskbarTab, search });

        list.ItemHeight = S(44);
        list.DrawItem += DrawItem;
        list.MouseDown += (_, e) =>
        {
            int i = list.IndexFromPoint(e.Location);
            if (e.Button == MouseButtons.Left && i >= 0) Toggle(i);
        };
        list.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Space && list.SelectedIndex >= 0) { Toggle(list.SelectedIndex); e.Handled = true; }
        };
        Ui.Tip(list, "Click an app to tick or untick it");
        var listHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(S(10), S(4), S(10), S(8)) };
        listHost.Controls.Add(list);

        addButton = Ui.AccentButton("Add Selected", AddSelected, "Add the ticked apps to the group, and unpin ticked taskbar apps if that box is ticked (Enter)");
        unpinBox.CheckedChanged += (_, _) => { UpdateStatus(); list.Invalidate(); };
        Ui.Tip(unpinBox, "Uses Windows' own Unpin from taskbar. The app is kept in the group (its shortcut is copied first), and you can pin it again any time.");
        unpinRow = new Panel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(S(12), 0, S(10), S(6)) };
        unpinRow.Controls.Add(unpinBox);
        var bar = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, AutoSize = true, BackColor = Ui.Panel,
            Padding = new Padding(S(10), S(8), S(10), S(2)), WrapContents = false,
        };
        bar.Controls.Add(addButton);
        bar.Controls.Add(Ui.FlatButton("Browse Files…", Browse, "Pick apps, shortcuts or other files from a folder instead"));
        bar.Controls.Add(Ui.FlatButton("Refresh", () => ShowSource(source), "Look again for running or pinned apps"));
        var cancel = Ui.FlatButton("Cancel", () => { DialogResult = DialogResult.Cancel; Close(); }, "Close without adding anything (Esc)");
        bar.Controls.Add(cancel);
        CancelButton = cancel;
        bar.Controls.Add(status);

        Controls.Add(listHost);
        Controls.Add(top);
        Controls.Add(unpinRow);
        Controls.Add(bar);
        AcceptButton = addButton;

        ShowSource(Source.Running);
    }

    void ShowSource(Source s)
    {
        source = s;
        foreach (var (b, active) in new[] { (runningTab, s == Source.Running), (taskbarTab, s == Source.Taskbar) })
        {
            b.BackColor = active ? Ui.Accent : Ui.Field;
            b.ForeColor = active ? Color.White : Ui.Text;
            b.FlatAppearance.MouseOverBackColor = active ? Ui.AccentHover : Ui.Hover;
        }
        Cursor = Cursors.WaitCursor;
        try { all = s == Source.Running ? AppSources.Running() : AppSources.Pinned(); }
        catch (Exception ex) { all = new(); status.Text = "Couldn't read the list: " + ex.Message; }
        finally { Cursor = Cursors.Default; }
        foreach (var c in all) known[c.Path] = c;
        unpinRow.Visible = s == Source.Taskbar || TickedPins().Any();
        Filter();
    }

    void Filter()
    {
        string q = search.Text.Trim();
        shown = q.Length == 0 ? all : all.Where(c =>
            c.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase) ||
            c.Detail.Contains(q, StringComparison.CurrentCultureIgnoreCase)).ToList();
        list.BeginUpdate();
        list.Items.Clear();
        foreach (var c in shown) list.Items.Add(c.Path);
        list.EndUpdate();
        UpdateStatus();
    }

    void UpdateStatus()
    {
        string where = source == Source.Running ? "running" : "pinned to the taskbar";
        status.Text = all.Count == 0 ? $"No apps {where} found."
            : ticked.Count == 0 ? $"{all.Count} apps {where}. Click to tick the ones to add."
            : ticked.Count == 1 ? "1 app ticked." : $"{ticked.Count} apps ticked.";
        addButton.Enabled = ticked.Count > 0;
        bool unpinning = unpinBox.Checked && TickedPins().Any();
        addButton.Text = unpinning ? "Add and Unpin" : "Add Selected";
        if (TickedPins().Any()) unpinRow.Visible = true;
    }

    void Toggle(int index)
    {
        var c = shown[index];
        if (AppSources.InGroup(c, group) && !c.CopyLink && c.PinnedAppId == null) return;  // an added pin can still be ticked, to unpin it
        if (!ticked.Remove(c.Path)) ticked.Add(c.Path);
        list.Invalidate(list.GetItemRectangle(index));
        UpdateStatus();
    }

    Bitmap IconFor(string path)
    {
        if (!icons.TryGetValue(path, out var bmp)) icons[path] = bmp = Icons.ForPath(path, 48);
        return bmp;
    }

    void DrawItem(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= shown.Count) return;
        var c = shown[e.Index];
        bool inGroup = AppSources.InGroup(c, group);
        bool isTicked = ticked.Contains(c.Path);
        bool selected = (e.State & DrawItemState.Selected) != 0;
        var g = e.Graphics;
        using (var back = new SolidBrush(selected ? Ui.Hover : Ui.Panel)) g.FillRectangle(back, e.Bounds);

        int h = e.Bounds.Height;
        int box = S(18);
        var boxRect = new Rectangle(e.Bounds.X + S(10), e.Bounds.Y + (h - box) / 2, box, box);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using (var path = Icons.Rounded(boxRect, S(4)))
        {
            if (isTicked || inGroup)
            {
                using var fill = new SolidBrush(isTicked ? Ui.Accent : Ui.Line);
                g.FillPath(fill, path);
                using var tick = new Pen(Color.White, Math.Max(1.5f, S(2)));
                g.DrawLines(tick, new[]
                {
                    new PointF(boxRect.X + box * 0.24f, boxRect.Y + box * 0.52f),
                    new PointF(boxRect.X + box * 0.43f, boxRect.Y + box * 0.72f),
                    new PointF(boxRect.X + box * 0.78f, boxRect.Y + box * 0.30f),
                });
            }
            else
            {
                using var pen = new Pen(Ui.SubText, Math.Max(1f, S(1)));
                g.DrawPath(pen, path);
            }
        }

        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        int iconSize = (int)(h * 0.72);
        var iconRect = new Rectangle(boxRect.Right + S(10), e.Bounds.Y + (h - iconSize) / 2, iconSize, iconSize);
        g.DrawImage(IconFor(c.Path), iconRect);

        int x = iconRect.Right + S(10);
        var titleRect = new Rectangle(x, e.Bounds.Y + S(3), e.Bounds.Right - x - S(6), h / 2);
        var subRect = new Rectangle(x, e.Bounds.Y + h / 2, e.Bounds.Right - x - S(6), h / 2 - S(3));
        const TextFormatFlags flags = TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter;
        string note = !inGroup ? "" : isTicked && unpinBox.Checked ? "  (already in this group: will only be unpinned)" : "  (already in this group)";
        TextRenderer.DrawText(g, c.Name + note, Font, titleRect, inGroup && !isTicked ? Ui.SubText : Ui.Text, flags);
        using var small = new Font(Font.FontFamily, Font.Size * 0.88f);
        TextRenderer.DrawText(g, c.Detail, small, subRect, Ui.SubText, flags);
    }

    void AddSelected()
    {
        try
        {
            foreach (var path in ticked)
                if (known.TryGetValue(path, out var c) && !AppSources.InGroup(c, group)) Result.Add(AppSources.ToItem(c));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Couldn't add an app:\n\n" + ex.Message, "Add Apps", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        // Unpin only after every pin has its copy in data\Links (ToItem above).
        var pins = TickedPins().ToList();
        if (unpinBox.Checked && pins.Count > 0)
        {
            Cursor = Cursors.WaitCursor;
            var failed = pins.Where(c => !AppSources.Unpin(c)).ToList();
            Cursor = Cursors.Default;
            int done = pins.Count - failed.Count;
            UnpinReport = failed.Count == 0
                ? (done == 1 ? "Unpinned 1 app from the taskbar." : $"Unpinned {done} apps from the taskbar.")
                : $"Unpinned {done} of {pins.Count} apps from the taskbar.";
            if (failed.Count > 0)
                MessageBox.Show(this,
                    "Windows didn't unpin these (they're still added to the group):\n\n" +
                    string.Join("\n", failed.Select(c => "  " + c.Name)) +
                    "\n\nRight-click them on the taskbar and choose Unpin from taskbar.",
                    "Unpin from Taskbar", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        DialogResult = DialogResult.OK;
        Close();
    }

    IEnumerable<Candidate> TickedPins() =>
        ticked.Select(p => known.TryGetValue(p, out var c) ? c : null).OfType<Candidate>().Where(c => c.CopyLink || c.PinnedAppId != null);

    void Browse()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Add Apps",
            Multiselect = true,
            DereferenceLinks = false,
            Filter = "Apps and Shortcuts|*.exe;*.lnk;*.url;*.bat;*.cmd;*.appref-ms|All Files|*.*",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        foreach (var f in dlg.FileNames)
            if (!group.Items.Any(i => string.Equals(i.Path, f, StringComparison.OrdinalIgnoreCase)))
                Result.Add(new AppItem { Name = SettingsForm.NameFor(f), Path = f });
        DialogResult = DialogResult.OK;
        Close();
    }

    /// <summary>For snapshots: shows a tab and ticks the first entries.</summary>
    public void Preview(bool taskbar, int tick)
    {
        ShowSource(taskbar ? Source.Taskbar : Source.Running);
        for (int i = 0; i < shown.Count && ticked.Count < tick; i++)
            if (!AppSources.InGroup(shown[i], group)) ticked.Add(shown[i].Path);
        UpdateStatus();
        list.Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) foreach (var b in icons.Values) b.Dispose();
        base.Dispose(disposing);
    }
}
