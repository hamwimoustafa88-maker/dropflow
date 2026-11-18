using System.Text.Json;
using DropFlow.Core.Operations;

namespace DropFlow.Core.History;

public sealed record HistoryEntry(
    Guid Id,
    DateTimeOffset Time,
    string Operation,
    IReadOnlyList<string> Inputs,
    IReadOnlyList<string> Outputs,
    bool Success,
    string? Error = null,
    bool Undone = false,
    IReadOnlyDictionary<string, object?>? Options = null)
{
    /// <summary>
    /// The options worth keeping for "Retry": everything except passwords and folders (never written to history.json). Values are reduced to
    /// text, numbers and booleans so the file stays plain JSON.
    /// </summary>
    public static IReadOnlyDictionary<string, object?>? SafeOptions(IReadOnlyList<ToolOption> specs, IReadOnlyDictionary<string, object?>? values)
    {
        if (values is null || values.Count == 0) return null;
        var secret = specs.Where(o => o.Kind is ToolOptionKind.Password or ToolOptionKind.Folder).Select(o => o.Key).ToHashSet();
        var kept = values.Where(v => !secret.Contains(v.Key) && v.Value is null or string or bool or int or long or double or float or decimal)
            .ToDictionary(v => v.Key, v => v.Value is int or long or float or decimal ? Convert.ToDouble(v.Value, System.Globalization.CultureInfo.InvariantCulture) : v.Value);
        return kept.Count == 0 ? null : kept;
    }

    /// <summary>True when the run needed a password the history does not keep (Retry then asks again).</summary>
    public static bool NeededSecret(IReadOnlyList<ToolOption> specs) => specs.Any(o => o.Kind == ToolOptionKind.Password);
}

/// <summary>Remembers what DropFlow produced so the user can open the folder or undo (move the outputs to the Recycle Bin).</summary>
public sealed class HistoryStore(string path, int maxEntries = 500, TimeProvider? time = null)
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private readonly object _gate = new();
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private List<HistoryEntry>? _entries;

    public IReadOnlyList<HistoryEntry> Entries
    {
        get { lock (_gate) return Load().ToList(); }
    }

    public HistoryEntry Add(string operation, IReadOnlyList<string> inputs, ConversionResult result, IReadOnlyDictionary<string, object?>? safeOptions = null)
    {
        lock (_gate)
        {
            var entry = new HistoryEntry(Guid.NewGuid(), _time.GetUtcNow(), operation, inputs, result.Outputs, result.Success, result.Error, Options: safeOptions);
            var list = Load();
            list.Insert(0, entry);
            if (list.Count > maxEntries) list.RemoveRange(maxEntries, list.Count - maxEntries);
            Save(list);
            return entry;
        }
    }

    /// <summary>Disposes every output of the entry that still exists. Inputs are never touched. Returns how many files were removed.</summary>
    public int Undo(Guid id, IFileDisposer disposer)
    {
        lock (_gate)
        {
            var list = Load();
            var i = list.FindIndex(e => e.Id == id);
            if (i < 0 || list[i].Undone) return 0;

            var removed = 0;
            foreach (var output in list[i].Outputs)
            {
                if (!File.Exists(output) && !Directory.Exists(output)) continue;
                disposer.Dispose(output);
                removed++;
            }
            list[i] = list[i] with { Undone = true };
            Save(list);
            return removed;
        }
    }

    public void Clear()
    {
        lock (_gate) Save([]);
    }

    private List<HistoryEntry> Load()
    {
        if (_entries is not null) return _entries;
        try
        {
            _entries = File.Exists(path) ? JsonSerializer.Deserialize<List<HistoryEntry>>(File.ReadAllText(path), Options) ?? [] : [];
            // options come back as JsonElement: make them plain values again for Retry
            _entries = _entries.Select(e => e.Options is null ? e : e with { Options = Automation.RecipeStore.Normalize(e.Options) }).ToList();
        }
        catch (JsonException)
        {
            _entries = [];
        }
        return _entries;
    }

    private void Save(List<HistoryEntry> list)
    {
        _entries = list;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(list, Options));
        File.Move(temp, path, overwrite: true);
    }
}
