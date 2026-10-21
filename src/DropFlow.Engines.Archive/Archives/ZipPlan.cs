namespace DropFlow.Engines.Archive.Archives;

internal sealed record ZipItem(string? FullPath, string EntryName, bool IsDirectory, long Size, DateTime Modified);

/// <summary>
/// The list of entries for <c>arc.zip</c>: every dropped file or folder (recursively) under its own name at the top of the zip.
/// Symbolic links and junctions inside folders are skipped, never followed, so a link cannot pull files from elsewhere in.
/// </summary>
internal sealed class ZipPlan
{
    private const int MaxDepth = 200;

    public List<ZipItem> Items { get; } = [];
    public long TotalBytes { get; private set; }
    public int SkippedLinks { get; private set; }

    public static ZipPlan Build(IReadOnlyList<string> inputs, CancellationToken ct)
    {
        var plan = new ZipPlan();
        var topLevel = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var input in inputs)
        {
            ct.ThrowIfCancellationRequested();
            var full = Path.GetFullPath(input).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var name = UniqueName(Path.GetFileName(full) is { Length: > 0 } n ? n : "files", topLevel);

            if (File.Exists(full)) plan.AddFile(new FileInfo(full), name);
            else plan.AddDirectory(new DirectoryInfo(full), name, 0, ct);
        }

        return plan;
    }

    private void AddFile(FileInfo file, string entryName)
    {
        Items.Add(new ZipItem(file.FullName, entryName, false, file.Length, file.LastWriteTime));
        TotalBytes += file.Length;
    }

    private void AddDirectory(DirectoryInfo directory, string entryName, int depth, CancellationToken ct)
    {
        if (depth > MaxDepth)
            throw new ArchiveException(ArchiveProblem.Unsupported, $"Folder nesting is deeper than {MaxDepth} levels: {directory.FullName}");

        Items.Add(new ZipItem(null, entryName, true, 0, directory.LastWriteTime));

        foreach (var child in directory.EnumerateFileSystemInfos().OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            if (child.LinkTarget is not null)
            {
                SkippedLinks++;
                continue;
            }

            var childName = entryName + "/" + child.Name;
            if (child is DirectoryInfo sub) AddDirectory(sub, childName, depth + 1, ct);
            else if (child is FileInfo file) AddFile(file, childName);
        }
    }

    private static string UniqueName(string name, HashSet<string> taken)
    {
        var candidate = name;
        var ext = Path.GetExtension(name);
        var stem = name[..^ext.Length];
        for (var n = 2; !taken.Add(candidate); n++) candidate = $"{stem} ({n}){ext}";
        return candidate;
    }
}
