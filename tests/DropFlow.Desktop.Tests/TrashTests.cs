using DropFlow.Desktop.Platform;
using DropFlow.TestSupport;

namespace DropFlow.Desktop.Tests;

public sealed class TrashTests : IDisposable
{
    private readonly TempDir _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Freedesktop_trash_moves_the_file_and_writes_its_info()
    {
        var trash = Path.Combine(_temp.Path, "Trash");
        var file = Path.Combine(_temp.Path, "photo (converted).png");
        File.WriteAllText(file, "x");
        var when = new DateTime(2026, 10, 22, 9, 5, 7);

        new FreedesktopTrash(trash, () => when).Dispose(file);

        Assert.False(File.Exists(file));
        Assert.True(File.Exists(Path.Combine(trash, "files", "photo (converted).png")));
        var info = File.ReadAllText(Path.Combine(trash, "info", "photo (converted).png.trashinfo"));
        Assert.StartsWith("[Trash Info]\n", info);
        Assert.Contains("DeletionDate=2026-10-22T09:05:07", info);
        Assert.Contains("Path=" + FreedesktopTrash.EncodePath(Path.GetFullPath(file)), info);
    }

    [Fact]
    public void A_second_file_with_the_same_name_gets_a_unique_trash_name()
    {
        var trash = Path.Combine(_temp.Path, "Trash");
        var t = new FreedesktopTrash(trash, () => DateTime.Now);
        foreach (var sub in new[] { "a", "b" })
        {
            Directory.CreateDirectory(Path.Combine(_temp.Path, sub));
            var f = Path.Combine(_temp.Path, sub, "out.pdf");
            File.WriteAllText(f, sub);
            t.Dispose(f);
        }
        Assert.Equal(2, Directory.GetFiles(Path.Combine(trash, "files")).Length);
        Assert.Equal(2, Directory.GetFiles(Path.Combine(trash, "info")).Length);
    }

    [Fact]
    public void Folders_go_to_the_trash_as_well()
    {
        var trash = Path.Combine(_temp.Path, "Trash");
        var folder = Path.Combine(_temp.Path, "pages (split)");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "1.pdf"), "1");
        new FreedesktopTrash(trash, () => DateTime.Now).Dispose(folder);
        Assert.False(Directory.Exists(folder));
        Assert.True(File.Exists(Path.Combine(trash, "files", "pages (split)", "1.pdf")));
    }

    [Fact]
    public void Trash_info_paths_are_percent_encoded_but_keep_slashes() =>
        Assert.Equal("/home/u/%D9%85%D9%84%D9%81%20(1).png", FreedesktopTrash.EncodePath("/home/u/ملف (1).png"));

    [Fact]
    public void Mac_trash_moves_into_the_users_trash_folder_with_a_unique_name()
    {
        var trash = Path.Combine(_temp.Path, ".Trash");
        Directory.CreateDirectory(trash);
        File.WriteAllText(Path.Combine(trash, "x.png"), "already there");
        var file = Path.Combine(_temp.Path, "x.png");
        File.WriteAllText(file, "new");

        new FolderTrash(trash).Dispose(file);

        Assert.False(File.Exists(file));
        Assert.Equal("already there", File.ReadAllText(Path.Combine(trash, "x.png")));
        Assert.Equal("new", File.ReadAllText(Path.Combine(trash, "x 2.png")));
    }
}
