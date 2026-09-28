using System.Diagnostics;

namespace WindowsTaskbarGroup;

/// <summary>Creates, renames and deletes groups, fills them with apps, and makes the shortcuts to pin.
/// Every change is saved at once, and existing shortcuts are refreshed.</summary>
sealed class SettingsForm : Form
{
    readonly Store store;
    readonly ListBox groupList = DarkList();
    readonly ListBox appList = DarkList();
    readonly Label groupTitle = new() { AutoSize = false, AutoEllipsis = true, Dock = DockStyle.Fill, Font = new Font("Segoe UI Semibold", 15f), Margin = new Padding(0, 0, 0, 4) };
    float scale;

    /// <summary>Pixels at this window's display scaling (sizes here are written for 100%).</summary>
    int S(int px) => (int)Math.Round(px * scale);
    readonly Label status = new() { AutoSize = true, ForeColor = Ui.SubText, Anchor = AnchorStyles.Left, Margin = new Padding(8, 0, 0, 6) };
    readonly Dictionary<string, Bitmap> iconCache = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, Bitmap> tileCache = new();
    readonly List<Control> needGroup = new();

    public static string Title => "Windows Taskbar Group v" + Program.Version;

    public SettingsForm(Store store, string? selectId = null)
    {
        this.store = store;
        Text = Title;
        Icon = Icons.AppIcon();
        Font = new Font("Segoe UI", 9.5f);
        AutoScaleMode = AutoScaleMode.None;
        scale = DeviceDpi / 96f;
        ClientSize = new Size(S(960), S(600));
        MinimumSize = new Size(S(760), S(460));
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Ui.Back;
        ForeColor = Ui.Text;
        KeyPreview = true;
        Ui.DarkFrame(this);

        groupList.ItemHeight = S(44);
        groupList.DrawItem += DrawGroup;
        groupList.SelectedIndexChanged += (_, _) => ShowGroup();
        groupList.DoubleClick += (_, _) => RenameGroup();
        appList.ItemHeight = S(44);
        appList.DrawItem += DrawApp;
        appList.DoubleClick += (_, _) => RenameApp();
        appList.AllowDrop = true;
        appList.DragEnter += (_, e) => e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true && Current != null
            ? DragDropEffects.Copy : DragDropEffects.None;
        appList.DragDrop += (_, e) => { if (e.Data?.GetData(DataFormats.FileDrop) is string[] files) AddPaths(files); };
        appList.KeyDown += (_, e) => { if (e.KeyCode == Keys.Delete) RemoveApp(); };
        Ui.Tip(appList, "Drag apps, shortcuts, files or folders here to add them. Double-click to rename.");
        Ui.Tip(groupList, "Double-click a group to rename it.");

        // Left: groups
        var left = Column();
        left.Controls.Add(Ui.Heading("Groups"), 0, 0);
        left.Controls.Add(groupList, 0, 1);
        left.Controls.Add(Buttons(
            Ui.AccentButton("New Group", NewGroup, "Create an empty group (Ctrl+N)"),
            NeedsGroup(Ui.FlatButton("Rename", RenameGroup, "Rename the selected group (F2)")),
            NeedsGroup(Ui.FlatButton("Delete", DeleteGroup, "Delete the selected group and its shortcuts")),
            NeedsGroup(Ui.FlatButton("▲", () => MoveGroup(-1), "Move the group up")),
            NeedsGroup(Ui.FlatButton("▼", () => MoveGroup(1), "Move the group down"))), 0, 2);

        // Right: the selected group's apps
        var right = Column();
        right.RowStyles.Insert(0, new RowStyle(SizeType.AutoSize));
        right.RowCount = 5;
        right.Controls.Add(groupTitle, 0, 0);
        right.Controls.Add(Ui.Heading("Apps in This Group"), 0, 1);
        right.Controls.Add(appList, 0, 2);
        right.Controls.Add(Buttons(
            NeedsGroup(Ui.AccentButton("Add Apps…", AddApps, "Pick from running apps or apps pinned to the taskbar, or browse for files")),
            NeedsGroup(Ui.FlatButton("Add Folder…", AddFolder, "Add a folder; clicking it opens the folder")),
            NeedsGroup(Ui.FlatButton("Rename", RenameApp, "Rename the selected app (the name shown in the pop-up)")),
            NeedsGroup(Ui.FlatButton("Remove", RemoveApp, "Remove the selected app from the group (Delete)")),
            NeedsGroup(Ui.FlatButton("▲", () => MoveApp(-1), "Move the app up")),
            NeedsGroup(Ui.FlatButton("▼", () => MoveApp(1), "Move the app down"))), 0, 3);
        right.Controls.Add(new Label
        {
            Text = "Tip: drag apps, shortcuts, files or folders onto the list to add them.",
            AutoSize = false, AutoEllipsis = true, Dock = DockStyle.Fill, Height = S(24), ForeColor = Ui.SubText, Margin = new Padding(0, 0, 0, 4),
        }, 0, 4);

        var main = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Padding = new Padding(10, 8, 10, 0) };
        main.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 36));
        main.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 64));
        main.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        left.Margin = new Padding(0, 0, 12, 0);
        main.Controls.Add(left, 0, 0);
        main.Controls.Add(right, 1, 0);

        // Bottom bar
        var bar = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, AutoSize = true, BackColor = Ui.Panel,
            Padding = new Padding(10, 8, 10, 2), WrapContents = false,
        };
        bar.Controls.Add(NeedsGroup(Ui.AccentButton("Pin to Taskbar…", PinToTaskbar,
            "Make the group's shortcut and show how to pin it to the taskbar")));
        bar.Controls.Add(NeedsGroup(Ui.FlatButton("Preview", Preview, "Open the group's pop-up here, as a click on its taskbar button would")));
        bar.Controls.Add(Ui.FlatButton("Open Shortcut Folder", OpenShortcutFolder, "Open the folder with the groups' shortcuts"));
        bar.Controls.Add(status);

        Controls.Add(main);
        Controls.Add(bar);

        ReloadGroups(selectId);
    }

    Group? Current => groupList.SelectedIndex >= 0 && groupList.SelectedIndex < store.Groups.Count
        ? store.Groups[groupList.SelectedIndex] : null;

    static ListBox DarkList() => new()
    {
        Dock = DockStyle.Fill,
        DrawMode = DrawMode.OwnerDrawFixed,
        BorderStyle = BorderStyle.None,
        BackColor = Ui.Panel,
        ForeColor = Ui.Text,
        IntegralHeight = false,
        Margin = new Padding(0, 0, 0, 8),
    };

    static TableLayoutPanel Column()
    {
        var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = Padding.Empty };
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        return t;
    }

    static FlowLayoutPanel Buttons(params Control[] buttons)
    {
        var f = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = Padding.Empty };
        f.Controls.AddRange(buttons);
        return f;
    }

    /// <summary>Marks a control as needing a selected group (disabled otherwise).</summary>
    T NeedsGroup<T>(T c) where T : Control { needGroup.Add(c); return c; }

    // ---------- Drawing ----------

    Bitmap AppIcon(string path)
    {
        if (!iconCache.TryGetValue(path, out var bmp))
            iconCache[path] = bmp = Icons.ForPath(path, 48);
        return bmp;
    }

    Bitmap Tile(Group g)
    {
        string key = g.Id + "|" + g.Name + "|" + string.Join("|", g.Items.Take(4).Select(i => i.Path));
        if (!tileCache.TryGetValue(key, out var bmp))
        {
            var apps = g.Items.Take(4).Select(i => AppIcon(i.Path)).ToList();
            tileCache[key] = bmp = Icons.GroupTile(apps, g.Name, 64);
        }
        return bmp;
    }

    void DrawRow(DrawItemEventArgs e, Bitmap icon, string title, string sub)
    {
        if (e.Index < 0) return;
        bool selected = (e.State & DrawItemState.Selected) != 0;
        using (var back = new SolidBrush(selected ? Ui.Hover : Ui.Panel))
            e.Graphics.FillRectangle(back, e.Bounds);
        if (selected)
            using (var accent = new SolidBrush(Ui.Accent))
                e.Graphics.FillRectangle(accent, e.Bounds.X, e.Bounds.Y + 4, 3, e.Bounds.Height - 8);

        e.Graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        int h = e.Bounds.Height, iconSize = (int)(h * 0.72);
        var iconRect = new Rectangle(e.Bounds.X + 10, e.Bounds.Y + (h - iconSize) / 2, iconSize, iconSize);
        e.Graphics.DrawImage(icon, iconRect);

        int x = iconRect.Right + 10;
        var titleRect = new Rectangle(x, e.Bounds.Y + 3, e.Bounds.Right - x - 6, h / 2);
        var subRect = new Rectangle(x, e.Bounds.Y + h / 2, e.Bounds.Right - x - 6, h / 2 - 3);
        const TextFormatFlags flags = TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter;
        TextRenderer.DrawText(e.Graphics, title, Font, titleRect, Ui.Text, flags);
        using var small = new Font(Font.FontFamily, Font.Size * 0.88f);
        TextRenderer.DrawText(e.Graphics, sub, small, subRect, Ui.SubText, flags | TextFormatFlags.PathEllipsis);
    }

    void DrawGroup(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= store.Groups.Count) return;
        var g = store.Groups[e.Index];
        DrawRow(e, Tile(g), g.Name, g.Items.Count == 1 ? "1 app" : $"{g.Items.Count} apps");
    }

    void DrawApp(object? sender, DrawItemEventArgs e)
    {
        var g = Current;
        if (g == null || e.Index < 0 || e.Index >= g.Items.Count) return;
        var item = g.Items[e.Index];
        DrawRow(e, AppIcon(item.Path), item.Name,
            item.Path + (string.IsNullOrEmpty(item.Arguments) ? "" : " " + item.Arguments));
    }

    // ---------- State ----------

    void ReloadGroups(string? selectId = null, int? selectIndex = null)
    {
        groupList.BeginUpdate();
        groupList.Items.Clear();
        foreach (var g in store.Groups) groupList.Items.Add(g.Id);
        groupList.EndUpdate();
        int index = selectIndex ?? (selectId != null ? store.Groups.FindIndex(g => g.Id == selectId) : 0);
        if (store.Groups.Count > 0) groupList.SelectedIndex = Math.Clamp(index, 0, store.Groups.Count - 1);
        ShowGroup();
    }

    void ShowGroup(int? selectApp = null)
    {
        var g = Current;
        groupTitle.Text = g?.Name ?? "No Group Selected";
        groupTitle.Height = TextRenderer.MeasureText("Ag", groupTitle.Font).Height + S(4);
        foreach (var c in needGroup) c.Enabled = g != null;
        appList.BeginUpdate();
        appList.Items.Clear();
        if (g != null) foreach (var i in g.Items) appList.Items.Add(i.Path);
        appList.EndUpdate();
        if (g != null && g.Items.Count > 0 && selectApp != null)
            appList.SelectedIndex = Math.Clamp(selectApp.Value, 0, g.Items.Count - 1);
        groupList.Invalidate();
        if (store.Groups.Count == 0) SetStatus("Click New Group to start.");
    }

    void SetStatus(string text) => status.Text = text;

    /// <summary>Saves, refreshes the group's shortcuts if it has any, and redraws.</summary>
    void Changed(Group? g, int? selectApp = null)
    {
        try
        {
            store.Save();
            if (g != null) Shortcuts.Refresh(g);
            SetStatus("Saved.");
        }
        catch (Exception ex)
        {
            SetStatus("Couldn't save: " + ex.Message);
        }
        ShowGroup(selectApp);
    }

    // ---------- Group actions ----------

    void NewGroup()
    {
        var name = Ui.Prompt(this, "New Group", "Group name:", "New Group");
        if (string.IsNullOrWhiteSpace(name)) return;
        var g = new Group { Name = name };
        store.Groups.Add(g);
        store.Save();
        ReloadGroups(g.Id);
        SetStatus($"Created \"{g.Name}\". Add apps, then Pin to Taskbar.");
    }

    void RenameGroup()
    {
        var g = Current;
        if (g == null) return;
        var name = Ui.Prompt(this, "Rename Group", "Group name:", g.Name);
        if (string.IsNullOrWhiteSpace(name) || name == g.Name) return;
        g.Name = name;
        Changed(g, appList.SelectedIndex);
    }

    void DeleteGroup()
    {
        var g = Current;
        if (g == null) return;
        if (MessageBox.Show(this, $"Delete the group \"{g.Name}\" and its shortcuts?\n\nIf it's pinned to the taskbar, unpin it there too.",
                "Delete Group", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        int index = groupList.SelectedIndex;
        try { Shortcuts.Delete(g); } catch (Exception ex) { SetStatus("Couldn't delete a shortcut: " + ex.Message); }
        store.Groups.Remove(g);
        store.Save();
        ReloadGroups(selectIndex: index);
        SetStatus($"Deleted \"{g.Name}\".");
    }

    void MoveGroup(int delta)
    {
        int i = groupList.SelectedIndex, j = i + delta;
        if (i < 0 || j < 0 || j >= store.Groups.Count) return;
        (store.Groups[i], store.Groups[j]) = (store.Groups[j], store.Groups[i]);
        store.Save();
        ReloadGroups(selectIndex: j);
    }

    // ---------- App actions ----------

    void AddApps()
    {
        var g = Current;
        if (g == null) return;
        using var dlg = new AddAppsForm(g);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        AddItems(dlg.Result);
        if (dlg.UnpinReport != null)
            SetStatus((dlg.Result.Count == 0 ? "" : dlg.Result.Count == 1 ? "Added 1 app. " : $"Added {dlg.Result.Count} apps. ") + dlg.UnpinReport);
    }

    void AddFolder()
    {
        if (Current == null) return;
        using var dlg = new FolderBrowserDialog { Description = "Add a folder to the group", UseDescriptionForTitle = true };
        if (dlg.ShowDialog(this) == DialogResult.OK) AddPaths(new[] { dlg.SelectedPath });
    }

    void AddPaths(IEnumerable<string> paths)
    {
        var g = Current;
        if (g == null) return;
        AddItems(paths.Select(p => new AppItem { Name = NameFor(p), Path = p }));
    }

    void AddItems(IEnumerable<AppItem> items)
    {
        var g = Current;
        if (g == null) return;
        int added = 0;
        foreach (var item in items)
        {
            if (g.Items.Any(i => string.Equals(i.Path, item.Path, StringComparison.OrdinalIgnoreCase))) continue;
            g.Items.Add(item);
            added++;
        }
        if (added == 0) return;
        Changed(g, g.Items.Count - 1);
        SetStatus(added == 1 ? "Added 1 app." : $"Added {added} apps.");
    }

    public static string NameFor(string path)
    {
        if (Directory.Exists(path))
            return new DirectoryInfo(path).Name is { Length: > 0 } n ? n : path;
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".exe")
        {
            try
            {
                var desc = FileVersionInfo.GetVersionInfo(path).FileDescription?.Trim();
                if (!string.IsNullOrEmpty(desc)) return desc;
            }
            catch { }
        }
        return Path.GetFileNameWithoutExtension(path);
    }

    void RenameApp()
    {
        var g = Current;
        int i = appList.SelectedIndex;
        if (g == null || i < 0) return;
        var name = Ui.Prompt(this, "Rename App", "Name shown in the pop-up:", g.Items[i].Name);
        if (string.IsNullOrWhiteSpace(name)) return;
        g.Items[i].Name = name;
        Changed(g, i);
    }

    void RemoveApp()
    {
        var g = Current;
        int i = appList.SelectedIndex;
        if (g == null || i < 0) return;
        g.Items.RemoveAt(i);
        Changed(g, i);
    }

    void MoveApp(int delta)
    {
        var g = Current;
        int i = appList.SelectedIndex, j = i + delta;
        if (g == null || i < 0 || j < 0 || j >= g.Items.Count) return;
        (g.Items[i], g.Items[j]) = (g.Items[j], g.Items[i]);
        Changed(g, j);
    }

    // ---------- Shortcuts ----------

    void PinToTaskbar()
    {
        var g = Current;
        if (g == null) return;
        List<string> made;
        try { made = Shortcuts.Create(g); }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Couldn't create the shortcut:\n\n" + ex.Message, "Pin to Taskbar",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        SetStatus($"Shortcut for \"{g.Name}\" created.");
        Process.Start("explorer.exe", $"/select,\"{made[0]}\"");
        MessageBox.Show(this,
            $"The shortcut for \"{g.Name}\" is selected in the Explorer window that just opened.\n\n" +
            "To pin it: right-click it, choose Show more options, then Pin to taskbar.\n\n" +
            "It's also in the Start menu under Taskbar Groups (right-click, More, Pin to taskbar).\n\n" +
            "Changes you make to the group later update the shortcut. If the taskbar keeps showing an old " +
            "picture, unpin and pin it again.",
            "Pin to Taskbar", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    void Preview()
    {
        var g = Current;
        if (g == null) return;
        var popup = new PopupForm(g, Cursor.Position);
        popup.Show(this);
    }

    void OpenShortcutFolder()
    {
        Directory.CreateDirectory(Store.ShortcutsDir);
        Process.Start("explorer.exe", $"\"{Store.ShortcutsDir}\"");
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        switch (keyData)
        {
            case Keys.Control | Keys.N: NewGroup(); return true;
            case Keys.F2 when groupList.Focused: RenameGroup(); return true;
            case Keys.F2 when appList.Focused: RenameApp(); return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        scale = e.DeviceDpiNew / 96f;
        groupList.ItemHeight = S(44);
        appList.ItemHeight = S(44);
        ShowGroup(appList.SelectedIndex >= 0 ? appList.SelectedIndex : null);
    }

    /// <summary>For snapshots: selects a group and app without user input.</summary>
    public void Select(int group, int app)
    {
        groupList.SelectedIndex = group;
        appList.SelectedIndex = app;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (var b in iconCache.Values) b.Dispose();
            foreach (var b in tileCache.Values) b.Dispose();
        }
        base.Dispose(disposing);
    }
}
