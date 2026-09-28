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
    readonly Button defaultButton;
    readonly CheckBox hoverBox = new()
    {
        Text = "Open a group when the mouse rests on its taskbar button",
        AutoSize = true, ForeColor = Ui.Text, FlatStyle = FlatStyle.Flat, Margin = new Padding(0, 4, 0, 0),
    };
    readonly NumericUpDown delayBox = new()
    {
        Minimum = 100, Maximum = 2000, Increment = 50, Width = 70,
        BackColor = Ui.Field, ForeColor = Ui.Text, BorderStyle = BorderStyle.FixedSingle,
    };

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
        appList.SelectedIndexChanged += (_, _) => RefreshDefaultButton();
        defaultButton = Ui.FlatButton("Set as Default", ToggleDefault,
            "A click on the group's taskbar button starts this app right away; hover (or Shift+click) still shows all apps");
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
            NeedsGroup(defaultButton),
            NeedsGroup(Ui.FlatButton("Rename", RenameApp, "Rename the selected app (the name shown in the pop-up)")),
            NeedsGroup(Ui.FlatButton("Remove", RemoveApp, "Remove the selected app from the group (Delete)")),
            NeedsGroup(Ui.FlatButton("▲", () => MoveApp(-1), "Move the app up")),
            NeedsGroup(Ui.FlatButton("▼", () => MoveApp(1), "Move the app down"))), 0, 3);
        right.Controls.Add(new Label
        {
            Text = "Tip: the top of this list sits nearest the taskbar in the pop-up. Drag files or folders here to add them.",
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
            "Make the group's shortcut and select it in Explorer, ready to pin to the taskbar")));
        bar.Controls.Add(NeedsGroup(Ui.FlatButton("Change Icon…", ChangeIcon,
            "Show the grid of apps, one app's icon, or your own picture on the group's taskbar button")));
        bar.Controls.Add(NeedsGroup(Ui.FlatButton("Preview", Preview, "Open the group's pop-up here, as a click on its taskbar button would")));
        bar.Controls.Add(Ui.FlatButton("Open Shortcut Folder", OpenShortcutFolder, "Open the folder with the groups' shortcuts"));
        bar.Controls.Add(status);

        // Hover row (above the bottom bar)
        var hoverRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(10, 6, 10, 2), WrapContents = false,
        };
        hoverBox.Checked = store.Hover.Enabled;
        delayBox.Value = Math.Clamp(store.Hover.DelayMs, (int)delayBox.Minimum, (int)delayBox.Maximum);
        delayBox.Enabled = hoverBox.Checked;
        hoverBox.CheckedChanged += (_, _) => HoverChanged();
        delayBox.ValueChanged += (_, _) => HoverChanged();
        Ui.Tip(hoverBox, "Runs a small helper (tray icon) that opens a group's pop-up when the mouse rests on its taskbar button. It also starts when you sign in.");
        Ui.Tip(delayBox, "How long the mouse must rest on a group's button before it opens (milliseconds)");
        hoverRow.Controls.Add(hoverBox);
        hoverRow.Controls.Add(new Label { Text = "Delay:", AutoSize = true, ForeColor = Ui.SubText, Margin = new Padding(16, 5, 4, 0) });
        hoverRow.Controls.Add(delayBox);
        hoverRow.Controls.Add(new Label { Text = "ms", AutoSize = true, ForeColor = Ui.SubText, Margin = new Padding(4, 5, 0, 0) });

        Controls.Add(main);
        Controls.Add(hoverRow);
        Controls.Add(bar);

        if (store.Hover.Enabled)
            try { Watcher.EnsureRunning(); } catch { /* shown when toggled */ }

        // A group whose picture is out of date (the drawing changed, see Icons.Style) gets it redrawn,
        // and its shortcuts, including the taskbar's pinned copy, point at the new one.
        foreach (var g in store.Groups)
            if (!File.Exists(Shortcuts.IconFile(g)))
                try { Shortcuts.Refresh(g); } catch { /* shown when the group is changed */ }

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
        string key = Path.GetFileName(Shortcuts.IconFile(g));   // changes whenever the picture does
        if (!tileCache.TryGetValue(key, out var bmp))
            tileCache[key] = bmp = Icons.GroupPicture(g, 64, AppIcon);
        return bmp;
    }

    static readonly Color Gold = Color.FromArgb(255, 196, 64);

    void DrawRow(DrawItemEventArgs e, Bitmap icon, string title, string sub, string? badge = null)
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
        if (badge != null)
        {
            using var bold = new Font(Font, FontStyle.Bold);
            var size = TextRenderer.MeasureText(e.Graphics, badge, bold);
            TextRenderer.DrawText(e.Graphics, badge, bold,
                new Rectangle(titleRect.Right - size.Width, titleRect.Y, size.Width, titleRect.Height), Gold, flags);
            titleRect.Width -= size.Width + 8;
        }
        TextRenderer.DrawText(e.Graphics, title, Font, titleRect, Ui.Text, flags);
        using var small = new Font(Font.FontFamily, Font.Size * 0.88f);
        TextRenderer.DrawText(e.Graphics, sub, small, subRect, Ui.SubText, flags | TextFormatFlags.PathEllipsis);
    }

    void DrawGroup(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= store.Groups.Count) return;
        var g = store.Groups[e.Index];
        string apps = g.Items.Count == 1 ? "1 app" : $"{g.Items.Count} apps";
        DrawRow(e, Tile(g), g.Name, g.DefaultItem() is { } d ? $"{apps}  ·  click starts {d.Name}" : apps);
    }

    void DrawApp(object? sender, DrawItemEventArgs e)
    {
        var g = Current;
        if (g == null || e.Index < 0 || e.Index >= g.Items.Count) return;
        var item = g.Items[e.Index];
        DrawRow(e, AppIcon(item.Path), item.Name,
            item.Path + (string.IsNullOrEmpty(item.Arguments) ? "" : " " + item.Arguments),
            ReferenceEquals(g.DefaultItem(), item) ? "★ Default" : null);
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

    void RefreshDefaultButton()
    {
        var g = Current;
        int i = appList.SelectedIndex;
        bool isDefault = g != null && i >= 0 && i < g.Items.Count && ReferenceEquals(g.DefaultItem(), g.Items[i]);
        defaultButton.Text = isDefault ? "Clear Default" : "Set as Default";
        Ui.Tip(defaultButton, isDefault
            ? "A click on the group's taskbar button opens the pop-up again"
            : "A click on the group's taskbar button starts this app right away; hover (or Shift+click) still shows all apps");
    }

    void ToggleDefault()
    {
        var g = Current;
        int i = appList.SelectedIndex;
        if (g == null) return;
        if (i < 0) { SetStatus("Select an app first."); return; }
        var item = g.Items[i];
        bool clear = ReferenceEquals(g.DefaultItem(), item);
        g.DefaultApp = clear ? null : item.Path;
        Changed(g, i);
        SetStatus(clear
            ? $"No default: a click on \"{g.Name}\" opens the pop-up."
            : $"A click on \"{g.Name}\" now starts {item.Name}. Hover or Shift+click shows all apps.");
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

    // ---------- Hover ----------

    void HoverChanged()
    {
        store.Hover.Enabled = hoverBox.Checked;
        store.Hover.DelayMs = (int)delayBox.Value;
        delayBox.Enabled = hoverBox.Checked;
        try
        {
            store.Save();
            if (hoverBox.Checked)
            {
                StartupLink.Create();
                if (!Watcher.Send("reload")) Watcher.EnsureRunning();
                SetStatus("Hover is on: rest the mouse on a pinned group's taskbar button.");
            }
            else
            {
                Watcher.Send("quit");
                StartupLink.Remove();
                SetStatus("Hover is off.");
            }
        }
        catch (Exception ex) { SetStatus("Couldn't change hover: " + ex.Message); }
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
        // Kurt (0.6.2): no instructions box, just the shortcut selected in Explorer and a hint here.
        Process.Start("explorer.exe", $"/select,\"{made[0]}\"");
        SetStatus($"Shortcut for \"{g.Name}\" is selected in Explorer: right-click it, Show more options, Pin to taskbar.");
    }

    void ChangeIcon()
    {
        var g = Current;
        if (g == null) return;
        using var dlg = new IconPickerForm(g);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try { dlg.Apply(); }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Couldn't use that picture:\n\n" + ex.Message, "Change Icon",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        Changed(g, appList.SelectedIndex >= 0 ? appList.SelectedIndex : null);
        bool pinned = Directory.Exists(AppSources.PinnedDir) &&
            Shortcuts.Find(AppSources.PinnedDir, g).Any();
        SetStatus(pinned
            ? "Icon changed. The pinned button was updated; if it still shows the old picture, unpin and pin it again."
            : "Icon changed.");
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
