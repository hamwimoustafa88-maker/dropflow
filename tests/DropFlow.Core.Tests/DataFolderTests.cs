using DropFlow.Core.App;

namespace DropFlow.Core.Tests;

public class DataFolderTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("dropflow-data-").FullName;
    public void Dispose() => Directory.Delete(_root, recursive: true);
    private string Combine(string name) => Path.Combine(_root, name);

    [Fact]
    public void User_data_is_kept_outside_the_installers_folder()
    {
        Assert.NotEqual(DataFolder.Legacy, DataFolder.Default, StringComparer.OrdinalIgnoreCase);
        Assert.False(DataFolder.Default.StartsWith(DataFolder.Legacy + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Migration_moves_only_the_users_items_and_leaves_the_installers_files()
    {
        var legacy = Combine("DropFlow");
        var target = Combine("DropFlow Data");
        Directory.CreateDirectory(Path.Combine(legacy, "models"));
        File.WriteAllText(Path.Combine(legacy, "models", "ggml-tiny.bin"), "model");
        File.WriteAllText(Path.Combine(legacy, "settings.json"), "{}");
        File.WriteAllText(Path.Combine(legacy, "Update.exe"), "installer");
        Directory.CreateDirectory(Path.Combine(legacy, "current"));

        var moved = DataFolder.MigrateOnce(legacy, target);

        Assert.Equal(["settings.json", "models"], moved);
        Assert.True(File.Exists(Path.Combine(target, "models", "ggml-tiny.bin")));
        Assert.True(File.Exists(Path.Combine(target, "settings.json")));
        Assert.True(File.Exists(Path.Combine(legacy, "Update.exe")));
        Assert.True(Directory.Exists(Path.Combine(legacy, "current")));
        Assert.False(Directory.Exists(Path.Combine(target, "current")));
    }

    [Fact]
    public void Migration_never_overwrites_what_the_new_folder_already_has_and_runs_once()
    {
        var legacy = Combine("old");
        var target = Combine("new");
        Directory.CreateDirectory(legacy);
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(legacy, "settings.json"), "old");
        File.WriteAllText(Path.Combine(target, "settings.json"), "new");

        Assert.Empty(DataFolder.MigrateOnce(legacy, target));
        Assert.Equal("new", File.ReadAllText(Path.Combine(target, "settings.json")));
        Assert.Equal("old", File.ReadAllText(Path.Combine(legacy, "settings.json")));
        Assert.Empty(DataFolder.MigrateOnce(Combine("missing"), target));
    }
}
