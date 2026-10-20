
namespace DropFlow.Core.App;

/// <summary>Picks the UI language: the user's setting, or the Windows UI language when set to "system".</summary>
public static class LanguageResolver
{
    public static string Resolve(string setting, string systemLanguageTag)
    {
        var s = (setting ?? "system").Trim().ToLowerInvariant();
        if (s is "ar" or "en") return s;
        return systemLanguageTag.StartsWith("ar", StringComparison.OrdinalIgnoreCase) ? "ar" : "en";
    }
}

public enum AppCommandKind { Start, Open, Settings, Shelf, History, Quit }

public sealed record AppCommand(AppCommandKind Kind, IReadOnlyList<string> Paths, bool Minimized = false, string? Wheel = null);

/// <summary>
/// DropFlow's command line. Anything that is not a switch is a path, so "Send to" (which passes bare paths) and the context menu
/// (<c>--open "%1"</c>) work the same way.
/// </summary>
public static class AppCommandLine
{
    public static AppCommand Parse(IEnumerable<string> args)
    {
        var kind = AppCommandKind.Start;
        var minimized = false;
        string? wheel = null;
        var paths = new List<string>();

        foreach (var arg in args)
        {
            switch (arg.ToLowerInvariant())
            {
                case "--minimized" or "/minimized": minimized = true; break;
                case "--settings": kind = AppCommandKind.Settings; break;
                case "--shelf": kind = AppCommandKind.Shelf; break;
                case "--history": kind = AppCommandKind.History; break;
                case "--quit": kind = AppCommandKind.Quit; break;
                case "--open": kind = AppCommandKind.Open; break;
                case "--convert": kind = AppCommandKind.Open; wheel = "convert"; break;
                case "--tools": kind = AppCommandKind.Open; wheel = "tools"; break;
                case "--actions": kind = AppCommandKind.Open; wheel = "actions"; break;
                default:
                    if (arg.StartsWith("--", StringComparison.Ordinal)) break;      // unknown switch: ignore, never crash
                    paths.Add(arg);
                    if (kind == AppCommandKind.Start) kind = AppCommandKind.Open;
                    break;
            }
        }
        return new AppCommand(kind, paths, minimized, wheel);
    }

    /// <summary>The argument line that opens the given paths (used when forwarding to the running instance).</summary>
    public static string[] Build(AppCommand command) =>
        command.Kind switch
        {
            AppCommandKind.Open => ["--open", .. command.Paths],
            AppCommandKind.Settings => ["--settings"],
            AppCommandKind.Shelf => ["--shelf"],
            AppCommandKind.History => ["--history"],
            AppCommandKind.Quit => ["--quit"],
            _ => [],
        };
}

/// <summary>Pure planners for the Copy, Move, Copy Path and Terminal actions.</summary>
public static class FileActionPlans
{
    /// <summary>Text for "Copy path": one full path per line (quoted when asked).</summary>
    public static string PathLines(IEnumerable<string> paths, bool quote = false) =>
        string.Join(Environment.NewLine, paths.Select(p => quote ? $"\"{Path.GetFullPath(p)}\"" : Path.GetFullPath(p)));

    public sealed record MoveStep(string From, string To);

    /// <summary>Where each file lands when moved into a folder. Existing names are never overwritten ("a (2).txt").</summary>
    public static IReadOnlyList<MoveStep> PlanMove(IEnumerable<string> files, string destinationFolder, Func<string, bool>? exists = null)
    {
        exists ??= p => File.Exists(p) || Directory.Exists(p);
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var steps = new List<MoveStep>();
        foreach (var file in files)
        {
            var full = Path.GetFullPath(file);
            var name = Path.GetFileNameWithoutExtension(full);
            var ext = Path.GetExtension(full);
            var candidate = Path.Combine(destinationFolder, Path.GetFileName(full));
            for (var n = 2; exists(candidate) || taken.Contains(candidate); n++)
                candidate = Path.Combine(destinationFolder, $"{name} ({n}){ext}");
            taken.Add(candidate);
            steps.Add(new MoveStep(full, candidate));
        }
        return steps;
    }

    /// <summary>The folder a terminal should open in: the folder itself, or a file's containing folder.</summary>
    public static string TerminalFolder(string path, Func<string, bool>? isDirectory = null)
    {
        isDirectory ??= Directory.Exists;
        var full = Path.GetFullPath(path);
        return isDirectory(full) ? full : Path.GetDirectoryName(full) ?? full;
    }

    public sealed record ProcessPlan(string FileName, IReadOnlyList<string> Arguments);

    /// <summary>Command that opens the folder in Windows Terminal; PowerShell when Terminal is not installed.</summary>
    public static ProcessPlan Terminal(string folder, bool windowsTerminalAvailable) =>
        windowsTerminalAvailable
            ? new("wt.exe", ["-d", folder])
            : new("powershell.exe", ["-NoExit", "-NoProfile", "-Command", $"Set-Location -LiteralPath '{folder.Replace("'", "''")}'"]);
}

/// <summary>A registry value to write (kept abstract so the plans are testable and the real writer is one small adapter).</summary>
public sealed record RegistryEntry(string KeyPath, string? ValueName, string Value);

/// <summary>What DropFlow writes to the registry for Explorer integration and run-at-startup. All under HKEY_CURRENT_USER (no admin needed).</summary>
public static class ShellIntegrationPlan
{
    public const string StartupKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string StartupValue = "DropFlow";

    /// <summary>"Open with DropFlow" for every file and every folder in the classic context menu.</summary>
    public static IReadOnlyList<RegistryEntry> ContextMenu(string exePath, string menuText)
    {
        var entries = new List<RegistryEntry>();
        foreach (var root in new[] { @"Software\Classes\*\shell\DropFlow", @"Software\Classes\Directory\shell\DropFlow" })
        {
            entries.Add(new RegistryEntry(root, null, menuText));
            entries.Add(new RegistryEntry(root, "Icon", $"\"{exePath}\",0"));
            entries.Add(new RegistryEntry(root + @"\command", null, $"\"{exePath}\" --open \"%1\""));
        }
        return entries;
    }

    public static IReadOnlyList<string> ContextMenuKeys() =>
        [@"Software\Classes\*\shell\DropFlow", @"Software\Classes\Directory\shell\DropFlow"];

    public static RegistryEntry Startup(string exePath) => new(StartupKey, StartupValue, $"\"{exePath}\" --minimized");
}

/// <summary>
/// Explorer starts one process per selected file for a classic context-menu verb. This merges those rapid requests into one batch so
/// ten selected files open ONE wheel with ten files.
/// </summary>
public sealed class RequestBatcher(Session.IScheduler scheduler, Action<IReadOnlyList<string>> flush, TimeSpan? quiet = null)
{
    public static readonly TimeSpan DefaultQuiet = TimeSpan.FromMilliseconds(350);
    private readonly TimeSpan _quiet = quiet ?? DefaultQuiet;
    private readonly List<string> _pending = [];
    private readonly object _gate = new();
    private IDisposable? _timer;

    public void Add(IEnumerable<string> paths)
    {
        lock (_gate)
        {
            foreach (var p in paths)
                if (!_pending.Contains(p, StringComparer.OrdinalIgnoreCase)) _pending.Add(p);
            _timer?.Dispose();
            _timer = scheduler.After(_quiet, Flush);
        }
    }

    private void Flush()
    {
        List<string> batch;
        lock (_gate)
        {
            batch = [.. _pending];
            _pending.Clear();
            _timer = null;
        }
        if (batch.Count > 0) flush(batch);
    }
}
