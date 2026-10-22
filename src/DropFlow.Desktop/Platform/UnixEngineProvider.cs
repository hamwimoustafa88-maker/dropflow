// SPDX-License-Identifier: GPL-3.0-or-later
using DropFlow.Core.Operations;

namespace DropFlow.Desktop.Platform;

/// <summary>
/// Finds helper programs on macOS and Linux. Order: inside the app (<c>Contents/Resources/engines</c> in the .app bundle,
/// <c>engines/</c> next to the binary in the AppImage and packages), DropFlow's data folder, PATH, then the usual install folders —
/// a Mac app started from Finder only gets <c>/usr/bin:/bin:/usr/sbin:/sbin</c>, so Homebrew's folders must be searched explicitly.
/// </summary>
/// <remarks>
/// <c>DropFlow.Engines.Common.EngineLocator</c> still looks for Windows names (<c>ffmpeg.exe</c>); this provider stands in until it is
/// portable. Both implement <see cref="IEngineProvider"/>, so swapping is one line in <c>AppController</c>.
/// </remarks>
public sealed class UnixEngineProvider : IEngineProvider
{
    private readonly OsKind _os;
    private readonly string _appDir;
    private readonly string? _dataDir;
    private readonly IReadOnlyList<string> _path;
    private readonly Func<string, bool> _exists;
    private readonly Dictionary<EngineId, string?> _cache = [];
    private readonly object _gate = new();

    public UnixEngineProvider(OsKind os, string appDirectory, string? dataDirectory = null, IEnumerable<string>? pathDirs = null, Func<string, bool>? exists = null)
    {
        _os = os;
        _appDir = appDirectory.TrimEnd('/');
        _dataDir = dataDirectory?.TrimEnd('/');
        _path = (pathDirs ?? (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries)).ToList();
        _exists = exists ?? IsExecutableFile;
    }

    public static IReadOnlyList<string> Names(EngineId id) => id switch
    {
        EngineId.Ffmpeg => ["ffmpeg"],
        EngineId.Ffprobe => ["ffprobe"],
        EngineId.Qpdf => ["qpdf"],
        EngineId.LibreOffice => ["soffice", "libreoffice"],
        EngineId.SevenZip => ["7zz", "7z", "7za"],
        EngineId.Pandoc => ["pandoc"],
        EngineId.Tesseract => ["tesseract"],
        _ => [],
    };

    public string? Locate(EngineId engine)
    {
        if (engine == EngineId.None) return null;
        lock (_gate)
        {
            if (_cache.TryGetValue(engine, out var hit)) return hit;
            return _cache[engine] = Search(engine);
        }
    }

    public void Refresh()
    {
        lock (_gate) _cache.Clear();
    }

    private string? Search(EngineId engine)
    {
        foreach (var dir in Candidates(engine))
            foreach (var name in Names(engine))
            {
                var file = AppDirectories.Join(dir, name);
                if (_exists(file)) return file;
            }
        return null;
    }

    private IEnumerable<string> Candidates(EngineId engine)
    {
        // 1) shipped inside DropFlow
        if (_os == OsKind.MacOS && _appDir.LastIndexOf('/') is > 0 and var slash)
            yield return AppDirectories.Join(_appDir[..slash], "Resources", "engines");   // Contents/MacOS → Contents/Resources/engines
        yield return AppDirectories.Join(_appDir, "engines");
        yield return AppDirectories.Join(_appDir, "engines", engine.ToString());

        // 2) DropFlow's own data folder (same layout as Windows: engines/<Engine>/)
        if (_dataDir is not null)
        {
            yield return AppDirectories.Join(_dataDir, "engines", engine.ToString());
            yield return AppDirectories.Join(_dataDir, "engines", engine.ToString(), "bin");
        }

        // 3) PATH, 4) the usual places
        foreach (var dir in _path) yield return dir.TrimEnd('/');
        if (_os == OsKind.MacOS)
        {
            yield return "/opt/homebrew/bin";
            yield return "/usr/local/bin";
            yield return "/opt/local/bin";                         // MacPorts
            yield return "/Applications/LibreOffice.app/Contents/MacOS";
        }
        else
        {
            yield return "/usr/local/bin";
            yield return "/usr/bin";
            yield return "/bin";
            yield return "/snap/bin";
            yield return "/var/lib/flatpak/exports/bin";
            yield return "/opt/libreoffice/program";
        }
    }

    private static bool IsExecutableFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            if (OperatingSystem.IsWindows()) return true;
            return (File.GetUnixFileMode(path) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
