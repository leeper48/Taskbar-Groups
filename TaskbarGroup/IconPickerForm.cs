using System.Security.Cryptography;

namespace WindowsTaskbarGroup;

/// <summary>Change Icon: the generated grid, one of the group's apps, or a custom image, each previewed
/// in the group frame. On OK, <see cref="Apply"/> stores the choice (copying a custom file into
/// data\icons\custom).</summary>
sealed class IconPickerForm : Form
{
    sealed record Option(string Caption, GroupIcon Icon);

    readonly Group group;
    readonly float scale;
    readonly FlowLayoutPanel options = new() { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Ui.Panel };
    readonly List<(Option Option, Control Tile)> tiles = new();
    readonly Dictionary<string, Bitmap> appIcons = new(StringComparer.OrdinalIgnoreCase);
    readonly List<Bitmap> previews = new();
    Option? selected;
    string? customSource;   // a picked file not yet copied into data\icons\custom

    int S(int px) => (int)Math.Round(px * scale);

    public IconPickerForm(Group group)
    {
        this.group = group;
        Text = "Change Icon for " + group.Name;
        Icon = Icons.AppIcon();
        Font = new Font("Segoe UI", 9.5f);
        AutoScaleMode = AutoScaleMode.None;
        scale = DeviceDpi / 96f;
        ClientSize = new Size(S(600), S(460));
        MinimumSize = new Size(S(420), S(320));
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MinimizeBox = false;
        BackColor = Ui.Back;
        ForeColor = Ui.Text;
        Ui.DarkFrame(this);

        options.Padding = new Padding(S(8));
        var host = new Panel { Dock = DockStyle.Fill, Padding = new Padding(S(10), S(10), S(10), S(8)) };
        host.Controls.Add(options);

        var ok = Ui.AccentButton("OK", () => { DialogResult = DialogResult.OK; Close(); }, "Use the selected icon (Enter)");
        var cancel = Ui.FlatButton("Cancel", () => { DialogResult = DialogResult.Cancel; Close(); }, "Keep the current icon (Esc)");
        var bar = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, AutoSize = true, BackColor = Ui.Panel,
            Padding = new Padding(S(10), S(8), S(10), S(2)), WrapContents = false,
        };
        bar.Controls.Add(ok);
        bar.Controls.Add(Ui.FlatButton("Choose Image…", ChooseImage,
            "Use a picture (.png, .jpg, .bmp), an .ico file, or the icon of an .exe"));
        bar.Controls.Add(cancel);
        bar.Controls.Add(new Label
        {
            Text = "Every choice keeps the group frame.", AutoSize = true, ForeColor = Ui.SubText,
            Anchor = AnchorStyles.Left, Margin = new Padding(S(6), S(6), 0, 0),
        });
        AcceptButton = ok;
        CancelButton = cancel;

        Controls.Add(host);
        Controls.Add(bar);

        AddOption(new Option("Grid of Apps", new GroupIcon { Kind = GroupIcon.Grid }));
        foreach (var item in group.Items)
            AddOption(new Option(item.Name, new GroupIcon { Kind = GroupIcon.App, AppPath = item.Path }));
        if (group.Icon.Kind == GroupIcon.Custom && group.Icon.CustomPath is { } p && File.Exists(p))
            AddOption(new Option("Custom Image", group.Icon.Clone()));

        // Select what the group shows now.
        string kind = Icons.EffectiveKind(group);
        Choose(tiles.Select(t => t.Option).FirstOrDefault(o => o.Icon.Kind == kind &&
            (kind != GroupIcon.App || string.Equals(o.Icon.AppPath, group.Icon.AppPath, StringComparison.OrdinalIgnoreCase)))
            ?? tiles[0].Option);
    }

    Bitmap AppIcon(string path) =>
        appIcons.TryGetValue(path, out var b) ? b : appIcons[path] = Icons.ForPath(path, 256);

    void AddOption(Option option)
    {
        var preview = Icons.GroupPicture(new Group { Name = group.Name, Items = group.Items, Icon = option.Icon },
            S(72), AppIcon);
        previews.Add(preview);
        var tile = new Panel { Size = new Size(S(112), S(122)), Margin = new Padding(S(4)), Cursor = Cursors.Hand };
        tile.Paint += (_, e) =>
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            bool isSelected = selected == option;
            var r = new Rectangle(S(2), S(2), tile.Width - S(4), tile.Height - S(4));
            using (var path = Icons.Rounded(r, S(8)))
            {
                using var back = new SolidBrush(isSelected ? Ui.Hover : Ui.Panel);
                g.FillPath(back, path);
                if (isSelected)
                {
                    using var pen = new Pen(Ui.Accent, Math.Max(2, S(2)));
                    g.DrawPath(pen, path);
                }
            }
            g.DrawImage(preview, (tile.Width - preview.Width) / 2, S(10));
            TextRenderer.DrawText(g, option.Caption, Font,
                new Rectangle(S(6), S(10) + preview.Height + S(4), tile.Width - S(12), tile.Height - preview.Height - S(16)),
                isSelected ? Ui.Text : Ui.SubText,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        };
        tile.Click += (_, _) => Choose(option);
        tile.DoubleClick += (_, _) => { Choose(option); DialogResult = DialogResult.OK; Close(); };
        Ui.Tip(tile, option.Icon.Kind switch
        {
            GroupIcon.Grid => "The group's first four apps in a grid (updates as you change the apps)",
            GroupIcon.App => "Use this app's icon",
            _ => "Your own picture: " + Path.GetFileName(option.Icon.CustomPath),
        });
        tiles.Add((option, tile));
        options.Controls.Add(tile);
    }

    void Choose(Option option)
    {
        selected = option;
        foreach (var (_, tile) in tiles) tile.Invalidate();
    }

    void ChooseImage()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Choose an Image for " + group.Name,
            Filter = "Images and Icons|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.ico;*.exe;*.dll|All Files|*.*",
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            using (Icons.LoadImage(dlg.FileName)) { }   // readable?
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Couldn't read that file as a picture:\n\n" + ex.Message, "Choose Image",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        customSource = dlg.FileName;
        // Replace an earlier custom tile with the new one.
        foreach (var (o, t) in tiles.Where(x => x.Option.Icon.Kind == GroupIcon.Custom).ToList())
        {
            options.Controls.Remove(t);
            t.Dispose();
            tiles.RemoveAll(x => x.Option == o);
        }
        var option = new Option("Custom Image", new GroupIcon { Kind = GroupIcon.Custom, File = dlg.FileName });
        AddOption(option);
        Choose(option);
    }

    /// <summary>Stores the selected choice in the group. A newly picked file is copied into
    /// data\icons\custom (named by its content), so the original can move or be deleted.</summary>
    public void Apply()
    {
        if (selected == null) return;
        var icon = selected.Icon.Clone();
        if (icon.Kind == GroupIcon.Custom && customSource != null && icon.File == customSource)
        {
            Directory.CreateDirectory(GroupIcon.CustomDir);
            var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(customSource)))[..8].ToLowerInvariant();
            string name = $"{group.Id}-{hash}{Path.GetExtension(customSource).ToLowerInvariant()}";
            File.Copy(customSource, Path.Combine(GroupIcon.CustomDir, name), overwrite: true);
            icon.File = name;
        }
        group.Icon = icon;
        // Custom pictures of this group that are no longer used.
        if (Directory.Exists(GroupIcon.CustomDir))
            foreach (var f in Directory.GetFiles(GroupIcon.CustomDir, group.Id + "-*"))
                if (icon.Kind != GroupIcon.Custom || !string.Equals(Path.GetFileName(f), icon.File, StringComparison.OrdinalIgnoreCase))
                    try { File.Delete(f); } catch { }
    }

    /// <summary>For snapshots and tests: selects the option at an index, or picks a file as custom.</summary>
    public void Preview(int index, string? customFile = null)
    {
        if (customFile != null)
        {
            customSource = customFile;
            var option = new Option("Custom Image", new GroupIcon { Kind = GroupIcon.Custom, File = customFile });
            AddOption(option);
            Choose(option);
        }
        else Choose(tiles[Math.Clamp(index, 0, tiles.Count - 1)].Option);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (var b in appIcons.Values) b.Dispose();
            previews.ForEach(p => p.Dispose());
        }
        base.Dispose(disposing);
    }
}
