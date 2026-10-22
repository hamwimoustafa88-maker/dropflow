namespace DropFlow.Engines.Common;

/// <summary>
/// Finds an installed TrueType file for drawing text with libraries that need a font path (Magick.NET has no font lookup of its own
/// on macOS and Linux). Prefers Arial (Windows, macOS), then DejaVu Sans, Noto Sans and Liberation Sans (Linux); all but Liberation
/// carry Arabic glyphs.
/// </summary>
public static class FontFiles
{
    private static readonly string[] SansFiles = ["arial.ttf", "Arial.ttf", "DejaVuSans.ttf", "NotoSans-Regular.ttf", "LiberationSans-Regular.ttf", "FreeSans.ttf"];

    private static readonly Lazy<string?> Sans = new(() => FindSans(DefaultFolders()));

    /// <summary>A regular sans-serif TrueType file, or null when none is installed.</summary>
    public static string? DefaultSans() => Sans.Value;

    /// <summary>The first of the preferred sans files found in <paramref name="folders"/> (searched recursively), in preference order.</summary>
    public static string? FindSans(IEnumerable<string> folders)
    {
        var existing = folders.Where(Directory.Exists).ToList();
        foreach (var name in SansFiles)
            foreach (var folder in existing)
            {
                var direct = Path.Combine(folder, name);
                if (File.Exists(direct)) return direct;
                try
                {
                    var nested = Directory.EnumerateFiles(folder, name, new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MatchCasing = MatchCasing.CaseSensitive }).FirstOrDefault();
                    if (nested is not null) return nested;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        return null;
    }

    /// <summary>The operating system's font folders.</summary>
    public static IEnumerable<string> DefaultFolders()
    {
        if (OperatingSystem.IsWindows())
        {
            yield return Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
            yield break;
        }
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        yield return "/System/Library/Fonts/Supplemental";
        yield return "/Library/Fonts";
        yield return "/System/Library/Fonts";
        yield return "/usr/share/fonts";
        yield return "/usr/local/share/fonts";
        if (home.Length > 0)
        {
            yield return Path.Combine(home, "Library", "Fonts");
            yield return Path.Combine(home, ".fonts");
            yield return Path.Combine(home, ".local", "share", "fonts");
        }
    }
}
