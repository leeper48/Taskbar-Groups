using System.Text.Json;

namespace WindowsTaskbarGroup;

/// <summary>One app (or file, folder, shortcut) inside a group.</summary>
sealed class AppItem
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string? Arguments { get; set; }
    public string? WorkingDirectory { get; set; }
}

sealed class Group
{
    public string Id { get; set; } = NewId();
    public string Name { get; set; } = "New Group";
    public List<AppItem> Items { get; set; } = new();
    public GroupIcon Icon { get; set; } = new();

    public static string NewId() => Guid.NewGuid().ToString("N")[..8];

    public const string AppIdPrefix = "WindowsTaskbarGroup.Group.";

    /// <summary>The taskbar identity of this group's shortcut and pop-up. Distinct per group, so
    /// every pinned group is its own taskbar button even though they all start the same exe.
    /// The taskbar reports it as the button's UI Automation id ("Appid: ..."), which is how the
    /// hover helper recognizes group buttons.</summary>
    public string AppId => AppIdPrefix + Id;
}

/// <summary>What the group's tile shows. Every kind is drawn in the same framed tile.</summary>
sealed class GroupIcon
{
    public const string Grid = "grid", App = "app", Custom = "custom";

    /// <summary>"grid" (the first four apps; the default), "app" (one app's icon) or "custom" (an image).</summary>
    public string Kind { get; set; } = Grid;
    /// <summary>For "app": the item's Path.</summary>
    public string? AppPath { get; set; }
    /// <summary>For "custom": a file name in data\icons\custom (or a full path while choosing).</summary>
    public string? File { get; set; }

    public GroupIcon Clone() => new() { Kind = Kind, AppPath = AppPath, File = File };

    public static string CustomDir => System.IO.Path.Combine(Store.IconsDir, "custom");

    public string? CustomPath => File == null ? null
        : System.IO.Path.IsPathRooted(File) ? File : System.IO.Path.Combine(CustomDir, File);
}

sealed class HoverOptions
{
    /// <summary>Open a group when the mouse rests on its taskbar button (runs the helper, also at sign-in).</summary>
    public bool Enabled { get; set; }
    public int DelayMs { get; set; } = 400;
}

/// <summary>All groups, kept in <c>data\groups.json</c> next to the exe (portable; one copy of the
/// exe = one set of groups). <c>TASKBARGROUP_HOME</c> points it elsewhere, for tests.</summary>
sealed class Store
{
    public List<Group> Groups { get; set; } = new();
    public HoverOptions Hover { get; set; } = new();

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string Home =>
        Environment.GetEnvironmentVariable("TASKBARGROUP_HOME") is { Length: > 0 } h
            ? h
            : System.IO.Path.Combine(AppContext.BaseDirectory, "data");

    public static string FilePath => System.IO.Path.Combine(Home, "groups.json");
    public static string IconsDir => System.IO.Path.Combine(Home, "icons");
    public static string ShortcutsDir => System.IO.Path.Combine(Home, "Shortcuts");

    public static Store Load()
    {
        if (!File.Exists(FilePath)) return new Store();
        return JsonSerializer.Deserialize<Store>(File.ReadAllText(FilePath), Json) ?? new Store();
    }

    /// <summary>Writes a temp file, then swaps it in, so a crash never leaves half a file.</summary>
    public void Save()
    {
        Directory.CreateDirectory(Home);
        string tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, FilePath, overwrite: true);
    }

    public Group? Find(string id) => Groups.FirstOrDefault(g => g.Id == id);
}
