namespace DropFlow.Core.Output;

/// <summary>
/// Decides where results go. DropFlow never overwrites or replaces an original: a result is written beside the source as
/// "name (converted).ext", or "name (converted 2).ext" when that exists. Multi-file results go into a new folder.
/// </summary>
public sealed class OutputPathResolver(Func<string, bool>? exists = null)
{
    private readonly Func<string, bool> _exists = exists ?? (p => File.Exists(p) || Directory.Exists(p));

    /// <param name="inputPath">The source file.</param>
    /// <param name="extension">New extension without the dot; null keeps the source extension.</param>
    /// <param name="suffix">Label inside the parentheses, e.g. "converted", "compressed", "cropped".</param>
    /// <param name="outputDirectory">Overrides the source folder.</param>
    public string ForFile(string inputPath, string? extension, string suffix = "converted", string? outputDirectory = null)
    {
        var dir = outputDirectory ?? Path.GetDirectoryName(Path.GetFullPath(inputPath)) ?? ".";
        var name = Path.GetFileNameWithoutExtension(inputPath);
        var ext = "." + (extension ?? Path.GetExtension(inputPath).TrimStart('.')).TrimStart('.');
        return Unique(dir, $"{name} ({suffix})", ext);
    }

    /// <summary>Output for a result made of several inputs (merge, collage, zip): named after the first input.</summary>
    public string ForGroup(IReadOnlyList<string> inputs, string extension, string suffix = "merged", string? outputDirectory = null) =>
        ForFile(inputs[0], extension, suffix, outputDirectory);

    /// <summary>A new folder for an operation that produces many files (split pages, extracted frames, unpacked archive).</summary>
    public string ForFolder(string inputPath, string suffix, string? outputDirectory = null)
    {
        var dir = outputDirectory ?? Path.GetDirectoryName(Path.GetFullPath(inputPath)) ?? ".";
        var name = Path.GetFileNameWithoutExtension(inputPath);
        return Unique(dir, $"{name} ({suffix})", "");
    }

    private string Unique(string dir, string stem, string ext)
    {
        var candidate = Path.Combine(dir, stem + ext);
        if (!_exists(candidate)) return candidate;

        // "name (converted)" -> "name (converted 2)"
        var open = stem.LastIndexOf('(');
        var head = stem[..(open + 1)];
        var label = stem[(open + 1)..^1];
        for (var n = 2; ; n++)
        {
            candidate = Path.Combine(dir, $"{head}{label} {n}){ext}");
            if (!_exists(candidate)) return candidate;
        }
    }
}
