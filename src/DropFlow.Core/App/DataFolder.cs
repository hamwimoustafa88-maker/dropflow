namespace DropFlow.Core.App;

/// <summary>
/// Where DropFlow keeps settings, history, models and logs. It must NOT be the installer's folder: Velopack installs into
/// <c>%LOCALAPPDATA%\DropFlow</c> and deletes that folder on uninstall (and replaced its contents on the first install, which wiped
/// downloaded models in v0.1.0). User data therefore lives in <c>%LOCALAPPDATA%\DropFlow Data</c>.
/// </summary>
public static class DataFolder
{
    public static string Default => Path.Combine(LocalAppData, "DropFlow Data");

    /// <summary>The folder v0.1.0 used, which is also the install folder.</summary>
    public static string Legacy => Path.Combine(LocalAppData, "DropFlow");

    private static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    /// <summary>Files and folders that belong to the user (everything else in the legacy folder is the installer's).</summary>
    public static IReadOnlyList<string> UserItems { get; } =
        ["settings.json", "history.json", "watch.json", "recipes.json", "models", "engines", "webview"];

    /// <summary>
    /// Moves the user's items from <paramref name="legacy"/> to <paramref name="target"/> once. Nothing is overwritten: an item that
    /// already exists in the target stays as it is. Returns the names that were moved. Never throws for I/O problems.
    /// </summary>
    public static IReadOnlyList<string> MigrateOnce(string legacy, string target)
    {
        var moved = new List<string>();
        if (string.Equals(Path.GetFullPath(legacy), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase)) return moved;
        if (!Directory.Exists(legacy)) return moved;
        try { Directory.CreateDirectory(target); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return moved; }

        foreach (var name in UserItems)
        {
            var from = Path.Combine(legacy, name);
            var to = Path.Combine(target, name);
            try
            {
                if (File.Exists(from) && !File.Exists(to)) { File.Move(from, to); moved.Add(name); }
                else if (Directory.Exists(from) && !Directory.Exists(to)) { Directory.Move(from, to); moved.Add(name); }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // in use or locked: leave it; the app still starts with defaults for that item
            }
        }
        return moved;
    }
}
