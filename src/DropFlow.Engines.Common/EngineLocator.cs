using DropFlow.Core.Operations;

namespace DropFlow.Engines.Common;

/// <summary>Where each engine's executable is expected, and the winget package that installs it.</summary>
public static class EngineInfo
{
    public static IReadOnlyList<string> ExecutableNames(EngineId id) => id switch
    {
        EngineId.Ffmpeg => ["ffmpeg.exe"],
        EngineId.Ffprobe => ["ffprobe.exe"],
        EngineId.Qpdf => ["qpdf.exe"],
        EngineId.LibreOffice => ["soffice.exe"],
        EngineId.SevenZip => ["7z.exe", "7za.exe"],
        EngineId.Pandoc => ["pandoc.exe"],
        EngineId.Tesseract => ["tesseract.exe"],
        _ => [],
    };

    /// <summary>Folder under <c>engines\</c> in the app directory; ffprobe lives with ffmpeg.</summary>
    public static string BundledFolder(EngineId id) => id == EngineId.Ffprobe ? nameof(EngineId.Ffmpeg) : id.ToString();

    /// <summary>winget package id (verified to exist in the winget source on 2026-10-21), or null when winget cannot install it.</summary>
    public static string? WingetId(EngineId id) => id switch
    {
        EngineId.Ffmpeg or EngineId.Ffprobe => "Gyan.FFmpeg",
        EngineId.Qpdf => "QPDF.QPDF",
        EngineId.LibreOffice => "TheDocumentFoundation.LibreOffice",
        EngineId.SevenZip => "7zip.7zip",
        EngineId.Pandoc => "JohnMacFarlane.Pandoc",
        EngineId.Tesseract => "UB-Mannheim.TesseractOCR",
        _ => null,
    };

    /// <summary>Approximate download size in MB, shown to the user before they agree to install.</summary>
    public static int ApproxSizeMb(EngineId id) => id switch
    {
        EngineId.Ffmpeg or EngineId.Ffprobe => 100,
        EngineId.Qpdf => 25,
        EngineId.LibreOffice => 350,
        EngineId.SevenZip => 2,
        EngineId.Pandoc => 40,
        EngineId.Tesseract => 50,
        _ => 0,
    };
}

/// <summary>
/// Finds installed engines. Search order: the engines shipped inside the app (<c>engines\&lt;Engine&gt;\</c> next to DropFlow.exe),
/// DropFlow's own data folder, PATH, then well-known install folders (including winget's package and link folders).
/// Results are cached until <see cref="Refresh"/> (call it after an install).
/// </summary>
public sealed class EngineLocator : IEngineProvider
{
    private readonly string? _dataDirectory;
    private readonly string? _bundledDirectory;
    private readonly IReadOnlyList<string> _pathDirs;
    private readonly IReadOnlyList<string> _knownDirs;
    private readonly Func<string, bool> _fileExists;
    private readonly Func<string, IEnumerable<string>> _subDirs;
    private readonly Dictionary<EngineId, string?> _cache = [];
    private readonly object _gate = new();

    public EngineLocator(
        string? dataDirectory = null,
        IEnumerable<string>? pathDirs = null,
        IEnumerable<string>? knownDirs = null,
        Func<string, bool>? fileExists = null,
        Func<string, IEnumerable<string>>? subDirs = null,
        string? bundledDirectory = null)
    {
        _dataDirectory = dataDirectory;
        _bundledDirectory = bundledDirectory ?? Path.Combine(AppContext.BaseDirectory, "engines");
        _pathDirs = (pathDirs ?? (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToList();
        _knownDirs = (knownDirs ?? DefaultKnownDirs()).ToList();
        _fileExists = fileExists ?? File.Exists;
        _subDirs = subDirs ?? (d => Directory.Exists(d) ? Directory.EnumerateDirectories(d) : []);
    }

    public string? Locate(EngineId engine)
    {
        if (engine == EngineId.None) return null;
        lock (_gate)
        {
            if (_cache.TryGetValue(engine, out var cached)) return cached;
            return _cache[engine] = Search(engine);
        }
    }

    public void Refresh()
    {
        lock (_gate) _cache.Clear();
    }

    private string? Search(EngineId engine)
    {
        var names = EngineInfo.ExecutableNames(engine);

        // 0) Shipped with the app: <app>\engines\<engine>\ (the installer puts FFmpeg, qpdf and Tesseract there, so nothing is downloaded).
        if (_bundledDirectory is not null)
        {
            var shipped = Path.Combine(_bundledDirectory, EngineInfo.BundledFolder(engine));
            foreach (var dir in new[] { shipped, Path.Combine(shipped, "bin") })
                foreach (var name in names)
                    if (_fileExists(Path.Combine(dir, name))) return Path.Combine(dir, name);
        }

        // 1) DropFlow's own folder: <data>\engines\<engine>\ and one level below (zip archives unpack into a subfolder).
        if (_dataDirectory is not null)
        {
            var own = Path.Combine(_dataDirectory, "engines", engine.ToString());
            foreach (var dir in new[] { own }.Concat(_subDirs(own)).SelectMany(d => new[] { d, Path.Combine(d, "bin") }))
                foreach (var name in names)
                    if (_fileExists(Path.Combine(dir, name))) return Path.Combine(dir, name);
        }

        // 2) PATH, then 3) known install folders.
        foreach (var dir in _pathDirs.Concat(_knownDirs))
            foreach (var name in names)
            {
                var candidate = Path.Combine(dir, name);
                if (_fileExists(candidate)) return candidate;
            }
        return null;
    }

    /// <summary>
    /// Folders in <paramref name="directories"/> named like an install that carries its version ("qpdf 12.4.2" is what the qpdf installer
    /// creates), newest first. The exact folder name "qpdf" is handled separately.
    /// </summary>
    internal static IEnumerable<string> VersionedInstallDirs(string parent, string product, IEnumerable<string> directories) =>
        directories
            .Where(d => Path.GetFileName(d).StartsWith(product + " ", StringComparison.OrdinalIgnoreCase)
                        || Path.GetFileName(d).StartsWith(product + "-", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase);

    public static IEnumerable<string> DefaultKnownDirs()
    {
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        yield return Path.Combine(pf, "LibreOffice", "program");
        yield return Path.Combine(pf86, "LibreOffice", "program");
        yield return Path.Combine(pf, "Tesseract-OCR");
        yield return Path.Combine(pf, "7-Zip");
        yield return Path.Combine(pf, "qpdf", "bin");
        foreach (var dir in VersionedInstallDirs(pf, "qpdf", Directory.Exists(pf) ? Directory.EnumerateDirectories(pf) : [])) yield return Path.Combine(dir, "bin");
        yield return Path.Combine(local, "Pandoc");
        yield return Path.Combine(local, "Microsoft", "WinGet", "Links");

        // winget "portable" packages unpack under Packages\<Id>_<source>\ (ffmpeg, qpdf ...), sometimes in a bin subfolder.
        var packages = Path.Combine(local, "Microsoft", "WinGet", "Packages");
        if (Directory.Exists(packages))
            foreach (var pkg in Directory.EnumerateDirectories(packages))
            {
                yield return pkg;
                yield return Path.Combine(pkg, "bin");
                foreach (var sub in Directory.EnumerateDirectories(pkg))
                {
                    yield return Path.Combine(sub, "bin");
                    yield return sub;
                }
            }
    }
}

/// <summary>Builds the winget command that installs an engine. Pure so it can be tested without running winget.</summary>
public static class WingetCommand
{
    public static IReadOnlyList<string>? Install(EngineId engine)
    {
        var id = EngineInfo.WingetId(engine);
        return id is null
            ? null
            : ["install", "--id", id, "--exact", "--source", "winget", "--silent",
               "--accept-package-agreements", "--accept-source-agreements"];
    }
}

/// <summary>Installs engines through winget (signed manifests, hash-checked by winget). Only ever called after the user agrees.</summary>
public sealed class EngineInstaller(EngineLocator locator, string wingetPath = "winget")
{
    public async Task<ProcessResult> InstallAsync(EngineId engine, IProgress<string>? log = null, CancellationToken cancellationToken = default)
    {
        var args = WingetCommand.Install(engine) ?? throw new NotSupportedException($"{engine} cannot be installed with winget.");
        var result = await ProcessRunner.RunAsync(
            wingetPath, args,
            new ProcessOptions { OnOutputLine = l => log?.Report(l), OnErrorLine = l => log?.Report(l) },
            cancellationToken).ConfigureAwait(false);

        locator.Refresh();
        return result;
    }
}
