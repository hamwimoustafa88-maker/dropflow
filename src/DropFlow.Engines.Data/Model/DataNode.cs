// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text.RegularExpressions;

namespace DropFlow.Engines.Data.Model;

/// <summary>
/// The one tree every data format is read into and written from: objects (ordered keys), arrays and scalar values. Keeping a single
/// model means every reader works with every writer (JSON, JSON Lines, XML, YAML, CSV, TSV, XLSX, HTML).
/// </summary>
internal abstract class DataNode;

internal enum ValueKind { Null, Bool, Number, String }

/// <summary>
/// A scalar. A number keeps its text exactly as written (always valid JSON number syntax), so <c>1.10</c> and 20-digit ids survive.
/// </summary>
internal sealed partial class DataValue : DataNode
{
    public static readonly DataValue Null = new(ValueKind.Null, null);
    public static readonly DataValue True = new(ValueKind.Bool, "true");
    public static readonly DataValue False = new(ValueKind.Bool, "false");

    private DataValue(ValueKind kind, string? text)
    {
        Kind = kind;
        Text = text;
    }

    public ValueKind Kind { get; }

    /// <summary>The value as text: "true"/"false", the number as written, the string itself; null for <see cref="ValueKind.Null"/>.</summary>
    public string? Text { get; }

    public static DataValue String(string text) => new(ValueKind.String, text);

    public static DataValue Bool(bool value) => value ? True : False;

    /// <summary>A number from text that is already valid JSON number syntax; anything else stays a string.</summary>
    public static DataValue Number(string text) => JsonNumber().IsMatch(text) ? new(ValueKind.Number, text) : String(text);

    public static DataValue Number(long value) => new(ValueKind.Number, value.ToString(CultureInfo.InvariantCulture));

    /// <summary>
    /// Text from a format without types (CSV, TSV) read as a number when it is plainly one: no leading zeros ("0123" is a code, not a
    /// number), no plus sign, at most 15 significant digits so a spreadsheet keeps it exactly.
    /// </summary>
    public static DataValue Infer(string text)
    {
        if (PlainNumber().IsMatch(text) && text.Count(char.IsAsciiDigit) <= 15) return new(ValueKind.Number, text);
        return String(text);
    }

    public override string ToString() => Text ?? "";

    [GeneratedRegex(@"^-?(0|[1-9][0-9]*)(\.[0-9]+)?([eE][-+]?[0-9]+)?$")]
    private static partial Regex JsonNumber();

    [GeneratedRegex(@"^-?(0|[1-9][0-9]*)(\.[0-9]+)?$")]
    private static partial Regex PlainNumber();
}

/// <summary>An object: keys in the order they were read. Setting an existing key replaces its value in place.</summary>
internal sealed class DataObject : DataNode
{
    private readonly List<KeyValuePair<string, DataNode>> _entries = [];
    private readonly Dictionary<string, int> _index = new(StringComparer.Ordinal);

    public IReadOnlyList<KeyValuePair<string, DataNode>> Entries => _entries;

    public int Count => _entries.Count;

    public DataNode? this[string key] => _index.TryGetValue(key, out var i) ? _entries[i].Value : null;

    public bool Contains(string key) => _index.ContainsKey(key);

    public DataObject Set(string key, DataNode value)
    {
        if (_index.TryGetValue(key, out var i)) _entries[i] = new(key, value);
        else
        {
            _index[key] = _entries.Count;
            _entries.Add(new(key, value));
        }
        return this;
    }
}

internal sealed class DataArray : DataNode
{
    public DataArray() { }

    public DataArray(IEnumerable<DataNode> items) => Items.AddRange(items);

    public List<DataNode> Items { get; } = [];
}
