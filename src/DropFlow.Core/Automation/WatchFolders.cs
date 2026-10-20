using System.Text.Json;
using DropFlow.Core.Catalog;

namespace DropFlow.Core.Automation;

/// <summary>"Whenever a file like this lands in this folder, run this."</summary>
public sealed record WatchRule(
    string Id,
    string Folder,
    IReadOnlyList<string> Extensions,
    string? Recipe,
    bool Enabled = true,
    bool IncludeSubfolders = false,
    string OutputSubfolder = "DropFlow");

/// <summary>Which files a rule should act on. Pure, so it is unit-tested without a file system.</summary>
public static class WatchFilter
{
    private static readonly string[] PartialSuffixes = [".crdownload", ".part", ".tmp", ".download", ".partial", ".opdownload", ".!ut", ".temp"];

    public static bool IsPartial(string path)
    {
        var name = Path.GetFileName(path);
        return name.StartsWith("~$", StringComparison.Ordinal) || name.StartsWith('.') ||
               PartialSuffixes.Any(s => name.EndsWith(s, StringComparison.OrdinalIgnoreCase));
    }

    public static bool Matches(WatchRule rule, string path, ISet<string>? produced = null)
    {
        if (!rule.Enabled || IsPartial(path)) return false;
        if (produced is not null && produced.Contains(path)) return false;

        var folder = Path.GetFullPath(rule.Folder).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(path);
        if (!full.StartsWith(folder, StringComparison.OrdinalIgnoreCase)) return false;

        // never react to our own output folder (that would loop forever)
        var relative = full[folder.Length..];
        if (!string.IsNullOrEmpty(rule.OutputSubfolder) &&
            relative.StartsWith(rule.OutputSubfolder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return false;
        if (!rule.IncludeSubfolders && relative.Contains(Path.DirectorySeparatorChar)) return false;

        if (rule.Extensions.Count == 0) return true;
        var ext = ToolCatalog.ExtensionOf(full);
        return rule.Extensions.Any(e => ToolCatalog.Normalize(e) == ext);
    }
}

public sealed class WatchStore(string path)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public IReadOnlyList<WatchRule> Load()
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<List<WatchRule>>(File.ReadAllText(path), Json) ?? [] : []; }
        catch (JsonException) { return []; }
    }

    public void Save(IEnumerable<WatchRule> rules)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(rules.ToList(), Json));
        File.Move(temp, path, overwrite: true);
    }
}

/// <summary>
/// Watches folders and reports files that have finished arriving. A file counts as "finished" when its size stops changing and it
/// can be opened for reading — so a browser download or a slow copy is not processed half-written.
/// </summary>
public sealed class WatchService : IDisposable
{
    private readonly Func<WatchRule, IReadOnlyList<string>, Task> _onFiles;
    private readonly TimeSpan _poll;
    private readonly TimeSpan _timeout;
    private readonly Func<string, (long Size, bool Readable)> _probe;
    private readonly List<(WatchRule Rule, FileSystemWatcher Watcher)> _watchers = [];
    private readonly HashSet<string> _produced = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _inFlight = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();

    public WatchService(Func<WatchRule, IReadOnlyList<string>, Task> onFiles, TimeSpan? poll = null, TimeSpan? timeout = null, Func<string, (long, bool)>? probe = null)
    {
        _onFiles = onFiles;
        _poll = poll ?? TimeSpan.FromMilliseconds(700);
        _timeout = timeout ?? TimeSpan.FromMinutes(30);
        _probe = probe ?? DefaultProbe;
    }

    /// <summary>Tell the service a file was created by DropFlow so it is never processed again.</summary>
    public void MarkProduced(IEnumerable<string> paths)
    {
        lock (_gate) foreach (var p in paths) _produced.Add(Path.GetFullPath(p));
    }

    /// <summary>Replaces the watched set. Disabled rules and missing folders are skipped.</summary>
    public void Apply(IEnumerable<WatchRule> rules)
    {
        StopWatchers();
        foreach (var rule in rules.Where(r => r.Enabled && Directory.Exists(r.Folder)))
        {
            var w = new FileSystemWatcher(rule.Folder) { IncludeSubdirectories = rule.IncludeSubfolders, EnableRaisingEvents = false, InternalBufferSize = 64 * 1024 };
            w.Created += (_, e) => Candidate(rule, e.FullPath);
            w.Renamed += (_, e) => Candidate(rule, e.FullPath);     // browsers rename ".crdownload" to the real name when done
            w.EnableRaisingEvents = true;
            _watchers.Add((rule, w));
        }
    }

    private void Candidate(WatchRule rule, string path)
    {
        lock (_gate)
        {
            if (!WatchFilter.Matches(rule, path, _produced)) return;
            if (!_inFlight.Add(Path.GetFullPath(path))) return;       // already waiting for this one
        }
        _ = Task.Run(() => WaitAndReportAsync(rule, Path.GetFullPath(path)));
    }

    private async Task WaitAndReportAsync(WatchRule rule, string path)
    {
        try
        {
            var deadline = DateTime.UtcNow + _timeout;
            long lastSize = -1;
            var stableRounds = 0;
            while (DateTime.UtcNow < deadline && !_stop.IsCancellationRequested)
            {
                await Task.Delay(_poll, _stop.Token).ConfigureAwait(false);
                if (!File.Exists(path)) return;
                var (size, readable) = _probe(path);
                stableRounds = readable && size == lastSize && size > 0 ? stableRounds + 1 : 0;
                lastSize = size;
                if (stableRounds >= 2) break;
            }
            if (stableRounds < 2) return;

            lock (_gate) if (_produced.Contains(path)) return;
            await _onFiles(rule, [path]).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* stopping */ }
        finally
        {
            lock (_gate) _inFlight.Remove(path);
        }
    }

    private static (long, bool) DefaultProbe(string path)
    {
        try
        {
            using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return (s.Length, true);
        }
        catch (IOException)
        {
            return (-1, false);            // still being written or locked
        }
        catch (UnauthorizedAccessException)
        {
            return (-1, false);
        }
    }

    private void StopWatchers()
    {
        foreach (var (_, w) in _watchers) { w.EnableRaisingEvents = false; w.Dispose(); }
        _watchers.Clear();
    }

    public void Dispose()
    {
        _stop.Cancel();
        StopWatchers();
        _stop.Dispose();
    }
}
