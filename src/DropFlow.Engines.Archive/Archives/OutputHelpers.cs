using System.Text.RegularExpressions;
using DropFlow.Core.Operations;

namespace DropFlow.Engines.Archive.Archives;

/// <summary>Helpers shared by every converter in this project for choosing and cleaning up output paths.</summary>
internal static class OutputNaming
{
    /// <summary>
    /// A stand-in input path whose file name has no archive extension, so <c>OutputPathResolver</c> names the result after
    /// "photos" for "photos.tar.gz" (it would otherwise use "photos.tar"). Only the name and folder of the result are used.
    /// </summary>
    public static string BaseOf(string input)
    {
        var full = Path.GetFullPath(input);
        var dir = Path.GetDirectoryName(full) ?? ".";
        var name = Path.GetFileName(full);

        if (name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)) name = name[..^7];
        else
        {
            var stem = Path.GetFileNameWithoutExtension(name);
            name = stem.EndsWith(".tar", StringComparison.OrdinalIgnoreCase) && Path.GetExtension(name).Equals(".gz", StringComparison.OrdinalIgnoreCase)
                ? stem[..^4]
                : stem;
        }

        return Path.Combine(dir, (name.Length == 0 ? "archive" : name) + ".tmp");
    }

    /// <summary>Like <see cref="BaseOf"/> for a file or a folder: keeps dots in a folder's name ("My.Folder").</summary>
    public static string BaseOfAny(string path)
    {
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return Directory.Exists(full) ? Path.Combine(Path.GetDirectoryName(full) ?? ".", Path.GetFileName(full) + ".tmp") : BaseOf(full);
    }

    public static int Level(ConversionJob job, int fallback)
    {
        try { return Math.Clamp(job.GetInt("level", fallback), 0, 9); }
        catch (Exception ex) when (ex is FormatException or OverflowException or InvalidCastException) { return fallback; }
    }
}

/// <summary>Tracks what a run created so a failure or cancellation leaves nothing behind. Call <see cref="Commit"/> on success.</summary>
internal sealed class CreatedOutputs : IDisposable
{
    private readonly List<string> _files = [];
    private readonly List<string> _folders = [];
    private bool _committed;

    public void AddFile(string path) => _files.Add(path);
    public void AddFolder(string path) => _folders.Add(path);
    public void Commit() => _committed = true;

    public void Dispose()
    {
        if (_committed) return;
        foreach (var file in _files) TryDelete(() => { if (File.Exists(file)) File.Delete(file); });
        foreach (var folder in _folders) TryDelete(() => { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); });
    }

    internal static void TryDelete(Action delete)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try { delete(); return; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(50 * (attempt + 1)); // antivirus scanners briefly hold freshly written files
            }
        }
    }
}

/// <summary>Maps a sub-task's 0..1 progress into a slice of the whole job.</summary>
internal sealed class ScaledProgress(IProgress<JobProgress> inner, double from, double to) : IProgress<JobProgress>
{
    public void Report(JobProgress value) => inner.Report(value with { Fraction = from + (to - from) * Math.Clamp(value.Fraction, 0, 1) });
}

/// <summary>Builds and parses the 7-Zip command line. Pure, so it is unit-tested without running 7-Zip.</summary>
public static class SevenZipCommand
{
    /// <summary>Arguments that add everything inside the working directory to a new 7z archive.</summary>
    public static IReadOnlyList<string> CreateArguments(string archivePath, int level) =>
        ["a", "-t7z", $"-mx={Math.Clamp(level, 0, 9)}", "-y", "-bsp1", "--", archivePath, "*"];

    /// <summary>Arguments that list an archive in technical format (one "Path = ..." block per entry).</summary>
    public static IReadOnlyList<string> ListArguments(string archivePath) => ["l", "-slt", "-sccUTF-8", "--", archivePath];

    /// <summary>Arguments that test an archive's integrity.</summary>
    public static IReadOnlyList<string> TestArguments(string archivePath) => ["t", "-y", "--", archivePath];

    /// <summary>Extracts "42" from a progress line such as " 42% 3 + file.txt", or null.</summary>
    public static int? ParsePercent(string line)
    {
        var matches = PercentPattern.Matches(line);
        if (matches.Count == 0) return null;
        return int.TryParse(matches[^1].Groups[1].Value, out var value) && value is >= 0 and <= 100 ? value : null;
    }

    private static readonly Regex PercentPattern = new(@"(?<![\d.])(\d{1,3})%", RegexOptions.Compiled);
}
