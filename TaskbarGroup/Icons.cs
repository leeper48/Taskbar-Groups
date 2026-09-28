using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace WindowsTaskbarGroup;

static class Icons
{
    /// <summary>The icon Explorer shows for a file, folder, shortcut or exe, at up to
    /// <paramref name="size"/> pixels, with alpha. Falls back to the generic application icon.</summary>
    public static Bitmap ForPath(string path, int size)
    {
        try
        {
            var iid = typeof(Native.IShellItemImageFactory).GUID;
            if (Native.SHCreateItemFromParsingName(Environment.ExpandEnvironmentVariables(path),
                    IntPtr.Zero, ref iid, out object obj) == 0)
            {
                var factory = (Native.IShellItemImageFactory)obj;
                try
                {
                    if (factory.GetImage(new Native.SIZE { cx = size, cy = size },
                            Native.SIIGBF_ICONONLY | Native.SIIGBF_BIGGERSIZEOK, out IntPtr hbm) == 0)
                    {
                        try { return FromHBitmap(hbm); }
                        finally { Native.DeleteObject(hbm); }
                    }
                }
                finally { Marshal.ReleaseComObject(factory); }
            }
        }
        catch { /* fall through to the generic icon */ }
        using var fallback = new Icon(SystemIcons.Application, size, size);
        return fallback.ToBitmap();
    }

    /// <summary>Copies a 32-bit shell HBITMAP with its alpha channel (Image.FromHbitmap drops it).</summary>
    static Bitmap FromHBitmap(IntPtr hbm)
    {
        Native.GetObject(hbm, Marshal.SizeOf<Native.BITMAP>(), out var bm);
        int w = bm.bmWidth, h = bm.bmHeight;
        var bmi = new Native.BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<Native.BITMAPINFOHEADER>(),
            biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32,
        };
        var bits = new byte[w * h * 4];
        IntPtr dc = Native.GetDC(IntPtr.Zero);
        try { Native.GetDIBits(dc, hbm, 0, (uint)h, bits, ref bmi, 0); }
        finally { Native.ReleaseDC(IntPtr.Zero, dc); }

        bool anyAlpha = false;
        for (int i = 3; i < bits.Length; i += 4) if (bits[i] != 0) { anyAlpha = true; break; }
        if (!anyAlpha) for (int i = 3; i < bits.Length; i += 4) bits[i] = 255;

        var bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, bmp.PixelFormat);
        try
        {
            for (int y = 0; y < h; y++)
                Marshal.Copy(bits, y * w * 4, data.Scan0 + y * data.Stride, w * 4);
        }
        finally { bmp.UnlockBits(data); }
        return bmp;
    }

    /// <summary>The group's taskbar picture: a dark rounded tile with its first four apps in a 2x2 grid
    /// (an empty group shows its first letter).</summary>
    public static Bitmap GroupTile(IReadOnlyList<Bitmap> appIcons, string name, int size)
    {
        var bmp = FramedTile(size, out var g, out var tile);
        using var disposeGraphics = g;

        if (appIcons.Count == 0)
        {
            string letter = name.Trim().Length > 0 ? name.Trim()[..1].ToUpperInvariant() : "?";
            using var font = new Font("Segoe UI Semibold", size * 0.42f, GraphicsUnit.Pixel);
            using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(letter, font, Brushes.White, tile, sf);
            return bmp;
        }

        // One app fills the tile; two to four share a 2x2 grid.
        float inner = size * (size >= 32 ? InsetLarge : InsetSmall);
        if (appIcons.Count == 1)
        {
            g.DrawImage(appIcons[0], new RectangleF(inner, inner, size - 2 * inner, size - 2 * inner));
            return bmp;
        }
        float gap = size * GridGap;
        float cell = (size - 2 * inner - gap) / 2;
        for (int i = 0; i < Math.Min(4, appIcons.Count); i++)
        {
            float x = inner + (i % 2) * (cell + gap), y = inner + (i / 2) * (cell + gap);
            g.DrawImage(appIcons[i], new RectangleF(x, y, cell, cell));
        }
        return bmp;
    }

    // Sizes as a fraction of the icon. The taskbar draws every icon into the same box (28 px on Kurt's
    // 175% taskbar, measured), so the frame can't get bigger in pixels, only bolder: 0.5.0's thin dim
    // border on a dark tile blended into the taskbar and read small. 0.5.1 let the picture fill the
    // frame; 0.5.2 (Kurt chose option D of four mockups next to the Claude icon) makes the border
    // thicker and brighter and the corners squarer, closer to a solid square app icon.
    /// <summary>Bump when the drawing changes (part of the icon file name). 3 = 0.5.2 frame.</summary>
    public const string Style = "3";

    const float Border = 0.08f, Corner = 0.14f, InsetLarge = 0.10f, InsetSmall = 0.09f, GridGap = 0.045f;

    /// <summary>The frame's border: a brighter shade of the accent, so it stands out on a dark taskbar.</summary>
    public static readonly Color FrameColor = Color.FromArgb(150, 128, 255);

    /// <summary>The frame every group picture shares: a dark rounded tile with the accent border, drawn
    /// right to the icon's edge (the border sits fully inside).</summary>
    static Bitmap FramedTile(int size, out Graphics g, out RectangleF tile)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        float width = Math.Max(1f, size * Border);
        float pad = width / 2;
        tile = new RectangleF(pad, pad, size - 2 * pad, size - 2 * pad);
        using var path = Rounded(tile, size * Corner);
        using var fill = new SolidBrush(Color.FromArgb(235, 40, 40, 52));
        g.FillPath(fill, path);
        using var pen = new Pen(FrameColor, width);
        g.DrawPath(pen, path);
        return bmp;
    }

    /// <summary>One picture (an app's icon or a custom image) inside the frame, fitted, aspect kept.</summary>
    public static Bitmap ImageTile(Image image, int size)
    {
        var bmp = FramedTile(size, out var g, out _);
        using var disposeGraphics = g;
        float inner = size * (size >= 32 ? InsetLarge : InsetSmall);
        float box = size - 2 * inner;
        float scale = Math.Min(box / image.Width, box / image.Height);
        float w = image.Width * scale, h = image.Height * scale;
        g.DrawImage(image, new RectangleF(inner + (box - w) / 2, inner + (box - h) / 2, w, h));
        return bmp;
    }

    /// <summary>What a group's picture actually shows: a chosen app that left the group, or a custom
    /// file that's gone, falls back to the grid.</summary>
    public static string EffectiveKind(Group group)
    {
        var c = group.Icon;
        if (c.Kind == GroupIcon.App && c.AppPath != null &&
            group.Items.Any(i => string.Equals(i.Path, c.AppPath, StringComparison.OrdinalIgnoreCase)))
            return GroupIcon.App;
        if (c.Kind == GroupIcon.Custom && c.CustomPath is { } p && File.Exists(p)) return GroupIcon.Custom;
        return GroupIcon.Grid;
    }

    /// <summary>The group's picture at one size. <paramref name="appIcon"/> supplies app icons (the
    /// caller owns them).</summary>
    public static Bitmap GroupPicture(Group group, int size, Func<string, Bitmap> appIcon)
    {
        switch (EffectiveKind(group))
        {
            case GroupIcon.App:
                return ImageTile(appIcon(group.Icon.AppPath!), size);
            case GroupIcon.Custom:
                using (var img = LoadImage(group.Icon.CustomPath!)) return ImageTile(img, size);
            default:
                return GroupTile(group.Items.Take(4).Select(i => appIcon(i.Path)).ToList(), group.Name, size);
        }
    }

    public static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff" };

    /// <summary>A custom picture: image files as they are; anything else (.ico, .exe, .dll, .lnk) as the
    /// icon the shell shows for it, at 256 px.</summary>
    public static Bitmap LoadImage(string path)
    {
        if (ImageExtensions.Contains(Path.GetExtension(path).ToLowerInvariant()))
        {
            using var fs = File.OpenRead(path);   // a copy, so the file isn't kept locked
            using var img = Image.FromStream(fs);
            return new Bitmap(img);
        }
        return ForPath(path, 256);
    }

    public static GraphicsPath Rounded(RectangleF r, float radius)
    {
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    static readonly int[] IcoSizes = { 16, 20, 24, 32, 40, 48, 64, 256 };

    /// <summary>Renders the group's tile at every taskbar size and writes it as a PNG-framed .ico.</summary>
    public static void WriteGroupIcon(Group group, string file)
    {
        var apps = new Dictionary<string, Bitmap>(StringComparer.OrdinalIgnoreCase);
        Bitmap AppIcon(string path) => apps.TryGetValue(path, out var b) ? b : apps[path] = ForPath(path, 256);
        try
        {
            var frames = IcoSizes.Select(s => GroupPicture(group, s, AppIcon)).ToList();
            try { WriteIco(file, frames); }
            finally { frames.ForEach(f => f.Dispose()); }
        }
        finally { foreach (var b in apps.Values) b.Dispose(); }
    }

    public static void WriteIco(string file, IReadOnlyList<Bitmap> frames)
    {
        // The conventional layout: 32-bit DIB frames below 256 px, PNG only for 256.
        var pngs = frames.Select(f =>
        {
            if (f.Width < 256) return Dib(f);
            using var ms = new MemoryStream();
            f.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }).ToList();

        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        string tmp = file + ".tmp";
        using (var w = new BinaryWriter(File.Create(tmp)))
        {
            w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)frames.Count);
            int offset = 6 + 16 * frames.Count;
            for (int i = 0; i < frames.Count; i++)
            {
                w.Write((byte)(frames[i].Width >= 256 ? 0 : frames[i].Width));
                w.Write((byte)(frames[i].Height >= 256 ? 0 : frames[i].Height));
                w.Write((byte)0); w.Write((byte)0);
                w.Write((ushort)1); w.Write((ushort)32);
                w.Write(pngs[i].Length); w.Write(offset);
                offset += pngs[i].Length;
            }
            pngs.ForEach(w.Write);
        }
        File.Move(tmp, file, overwrite: true);
    }

    /// <summary>An icon DIB frame: BITMAPINFOHEADER (double height), BGRA rows bottom-up, then an
    /// all-clear 1-bit AND mask (alpha does the masking).</summary>
    static byte[] Dib(Bitmap f)
    {
        int w = f.Width, h = f.Height, maskStride = (w + 31) / 32 * 4;
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write(40); bw.Write(w); bw.Write(h * 2);
        bw.Write((ushort)1); bw.Write((ushort)32);
        bw.Write(0); bw.Write(w * h * 4 + maskStride * h); bw.Write(0); bw.Write(0); bw.Write(0); bw.Write(0);
        var data = f.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var row = new byte[w * 4];
            for (int y = h - 1; y >= 0; y--)
            {
                Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, row.Length);
                bw.Write(row);
            }
        }
        finally { f.UnlockBits(data); }
        bw.Write(new byte[maskStride * h]);
        return ms.ToArray();
    }

    public static Icon AppIcon()
    {
        using var s = typeof(Icons).Assembly.GetManifestResourceStream("app.ico")!;
        return new Icon(s);
    }
}
