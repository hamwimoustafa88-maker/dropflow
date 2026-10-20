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
    bool Undone = false);

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

    public HistoryEntry Add(string operation, IReadOnlyList<string> inputs, ConversionResult result)
    {
        lock (_gate)
        {
            var entry = new HistoryEntry(Guid.NewGuid(), _time.GetUtcNow(), operation, inputs, result.Outputs, result.Success, result.Error);
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
