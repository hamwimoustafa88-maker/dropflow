using DropFlow.Core.Operations;

namespace DropFlow.Engines.Common;

/// <summary>Where each engine's executable is expected, and the winget package that installs it.</summary>
public static class EngineInfo
{
    /// <summary>Executable file names for this operating system (<c>ffmpeg.exe</c> on Windows, <c>ffmpeg</c> on macOS and Linux).</summary>
    public static IReadOnlyList<string> ExecutableNames(EngineId id) => ExecutableNames(id, OperatingSystem.IsWindows());

    /// <param name="windows">True for Windows names (with <c>.exe</c>), false for macOS/Linux names.</param>
    public static IReadOnlyList<string> ExecutableNames(EngineId id, bool windows) => windows
        ? id switch
        {
            EngineId.Ffmpeg => ["ffmpeg.exe"],
            EngineId.Ffprobe => ["ffprobe.exe"],
            EngineId.Qpdf => ["qpdf.exe"],
            EngineId.LibreOffice => ["soffice.exe"],
            EngineId.SevenZip => ["7z.exe", "7za.exe"],
            EngineId.Pandoc => ["pandoc.exe"],
            EngineId.Tesseract => ["tesseract.exe"],
            _ => [],
        }
        : id switch
        {
            EngineId.Ffmpeg => ["ffmpeg"],
            EngineId.Ffprobe => ["ffprobe"],
            EngineId.Qpdf => ["qpdf"],
            EngineId.LibreOffice => ["soffice", "libreoffice"],
            EngineId.SevenZip => ["7zz", "7z", "7za"],   // 7zz: the official 7-Zip for Linux/macOS; 7z/7za: p7zip
            EngineId.Pandoc => ["pandoc"],
            EngineId.Tesseract => ["tesseract"],
            _ => [],
        };

    /// <summary>Folder under <c>engines</c> in the app directory; ffprobe lives with ffmpeg.</summary>
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
/// Finds installed engines. Search order: the engines shipped inside the app (<c>engines/&lt;Engine&gt;/</c> next to the executable, or
/// <c>Contents/Resources/engines/&lt;Engine&gt;/</c> inside a macOS <c>.app</c>), DropFlow's own data folder, PATH, then well-known install
/// folders (winget's package and link folders on Windows; Homebrew, <c>/usr/local/bin</c> and <c>/usr/bin</c> on macOS and Linux).
/// Executable names follow the operating system (<c>ffmpeg.exe</c> or <c>ffmpeg</c>). Results are cached until <see cref="Refresh"/>
/// (call it after an install).
/// </summary>
public sealed class EngineLocator : IEngineProvider
{
    private readonly string? _dataDirectory;
    private readonly IReadOnlyList<string> _bundledDirectories;
    private readonly IReadOnlyList<string> _pathDirs;
    private readonly IReadOnlyList<string> _knownDirs;
    private readonly Func<string, bool> _fileExists;
    private readonly Func<string, IEnumerable<string>> _subDirs;
    private readonly bool _windows;
    private readonly Dictionary<EngineId, string?> _cache = [];
    private readonly object _gate = new();

    /// <param name="bundledDirectory">The app's <c>engines</c> folder; null derives it (and the macOS bundle's) from <paramref name="appDirectory"/>.</param>
    /// <param name="windows">Use Windows executable names and install folders; null means "the operating system this runs on".</param>
    /// <param name="appDirectory">Folder of the running executable; null is <see cref="AppContext.BaseDirectory"/>.</param>
    public EngineLocator(
        string? dataDirectory = null,
        IEnumerable<string>? pathDirs = null,
        IEnumerable<string>? knownDirs = null,
        Func<string, bool>? fileExists = null,
        Func<string, IEnumerable<string>>? subDirs = null,
        string? bundledDirectory = null,
        bool? windows = null,
        string? appDirectory = null)
    {
        _windows = windows ?? OperatingSystem.IsWindows();
        _dataDirectory = dataDirectory;
        _bundledDirectories = bundledDirectory is not null ? [bundledDirectory] : BundledDirectories(appDirectory ?? AppContext.BaseDirectory);
        _pathDirs = (pathDirs ?? (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToList();
        _knownDirs = (knownDirs ?? DefaultKnownDirs(_windows)).ToList();
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

    /// <summary>
    /// Where an installed app keeps its shipped engines: <c>engines</c> next to the executable (Windows, Linux, the CLI) and
    /// <c>../Resources/engines</c> for an executable in a macOS bundle's <c>Contents/MacOS</c> folder.
    /// </summary>
    public static IReadOnlyList<string> BundledDirectories(string appDirectory)
    {
        var app = appDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return [Path.Combine(app, "engines"), Path.GetFullPath(Path.Combine(app, "..", "Resources", "engines"))];
    }

    private string? Search(EngineId engine)
    {
        var names = EngineInfo.ExecutableNames(engine, _windows);

        // 0) Shipped with the app: <app>/engines/<engine>/ (the installer puts FFmpeg, qpdf and Tesseract there, so nothing is downloaded).
        foreach (var bundled in _bundledDirectories)
        {
            var shipped = Path.Combine(bundled, EngineInfo.BundledFolder(engine));
            foreach (var dir in new[] { shipped, Path.Combine(shipped, "bin") })
                foreach (var name in names)
                    if (_fileExists(Path.Combine(dir, name))) return Path.Combine(dir, name);
        }

        // 1) DropFlow's own folder: <data>/engines/<engine>/ and one level below (zip archives unpack into a subfolder).
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

    /// <summary>Well-known install folders for the operating system this runs on.</summary>
    public static IEnumerable<string> DefaultKnownDirs() => DefaultKnownDirs(OperatingSystem.IsWindows());

    /// <param name="windows">True for the Windows folders (Program Files, winget), false for the macOS/Linux ones.</param>
    public static IEnumerable<string> DefaultKnownDirs(bool windows) => windows ? WindowsKnownDirs() : UnixKnownDirs();

    /// <summary>
    /// macOS and Linux: Homebrew on Apple Silicon (<c>/opt/homebrew/bin</c>) and on Intel (<c>/usr/local/bin</c>), MacPorts, the
    /// distribution's <c>/usr/bin</c>, snaps, <c>~/.local/bin</c>, and LibreOffice's app bundle or program folder (soffice is often not on PATH).
    /// </summary>
    private static IEnumerable<string> UnixKnownDirs()
    {
        yield return "/opt/homebrew/bin";
        yield return "/usr/local/bin";
        yield return "/opt/local/bin";
        yield return "/usr/bin";
        yield return "/bin";
        yield return "/snap/bin";
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (home.Length > 0) yield return Path.Combine(home, ".local", "bin");
        yield return "/Applications/LibreOffice.app/Contents/MacOS";
        yield return "/usr/lib/libreoffice/program";
        yield return "/opt/libreoffice/program";
        foreach (var dir in VersionedInstallDirs("/opt", "libreoffice", SafeDirs("/opt"))) yield return Path.Combine(dir, "program");
    }

    private static IEnumerable<string> WindowsKnownDirs()
    {
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        yield return Path.Combine(pf, "LibreOffice", "program");
        yield return Path.Combine(pf86, "LibreOffice", "program");
        yield return Path.Combine(pf, "Tesseract-OCR");
        yield return Path.Combine(pf, "7-Zip");
        yield return Path.Combine(pf, "qpdf", "bin");
        foreach (var dir in VersionedInstallDirs(pf, "qpdf", SafeDirs(pf))) yield return Path.Combine(dir, "bin");
        yield return Path.Combine(local, "Pandoc");
        yield return Path.Combine(local, "Microsoft", "WinGet", "Links");

        // winget "portable" packages unpack under Packages\<Id>_<source>\ (ffmpeg, qpdf ...), sometimes in a bin subfolder.
        var packages = Path.Combine(local, "Microsoft", "WinGet", "Packages");
        foreach (var pkg in SafeDirs(packages))
        {
            yield return pkg;
            yield return Path.Combine(pkg, "bin");
            foreach (var sub in SafeDirs(pkg))
            {
                yield return Path.Combine(sub, "bin");
                yield return sub;
            }
        }
    }

    private static IReadOnlyList<string> SafeDirs(string parent)
    {
        try
        {
            return parent.Length > 0 && Directory.Exists(parent) ? Directory.EnumerateDirectories(parent).ToList() : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
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

/// <summary>Installs engines through winget (signed manifests, hash-checked by winget). Only ever called after the user agrees. Windows only.</summary>
public sealed class EngineInstaller(EngineLocator locator, string wingetPath = "winget")
{
    public async Task<ProcessResult> InstallAsync(EngineId engine, IProgress<string>? log = null, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException($"{engine} is installed with winget, which exists on Windows only. Install it with your package manager (Homebrew, apt...).");
        var args = WingetCommand.Install(engine) ?? throw new NotSupportedException($"{engine} cannot be installed with winget.");
        var result = await ProcessRunner.RunAsync(
            wingetPath, args,
            new ProcessOptions { OnOutputLine = l => log?.Report(l), OnErrorLine = l => log?.Report(l) },
            cancellationToken).ConfigureAwait(false);

        locator.Refresh();
        return result;
    }
}
