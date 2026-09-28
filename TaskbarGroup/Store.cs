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

    public static string NewId() => Guid.NewGuid().ToString("N")[..8];

    /// <summary>The taskbar identity of this group's shortcut and pop-up. Distinct per group, so
    /// every pinned group is its own taskbar button even though they all start the same exe.</summary>
    public string AppId => "WindowsTaskbarGroup.Group." + Id;
}

/// <summary>All groups, kept in <c>data\groups.json</c> next to the exe (portable; one copy of the
/// exe = one set of groups). <c>TASKBARGROUP_HOME</c> points it elsewhere, for tests.</summary>
sealed class Store
{
    public List<Group> Groups { get; set; } = new();

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
