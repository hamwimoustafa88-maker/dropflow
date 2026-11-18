// SPDX-License-Identifier: GPL-3.0-or-later
namespace DropFlow.Engines.Data.Model;

/// <summary>A flat table: column names and one cell per column in every row (a missing value is <see cref="DataValue.Null"/>).</summary>
internal sealed record Table(IReadOnlyList<string> Columns, IReadOnlyList<DataValue[]> Rows);

/// <summary>
/// How a tree becomes a table (CSV, TSV, XLSX, HTML) and a list of records (JSON Lines).
/// <list type="bullet">
/// <item>The records are the items of the first array found by unwrapping: an object with one key is opened ({"people": [...]} or the
/// XML <c>&lt;people&gt;&lt;person/&gt;...</c> shape), and an object whose only array-valued key holds the rows is opened too
/// (so XML attributes beside the repeated element do not hide it). Anything else is one record.</item>
/// <item>Every record is flattened: nested objects give dotted keys (<c>address.city</c>), arrays give their index
/// (<c>tags.0</c>, <c>tags.1</c>). Columns are the union of keys in first-seen order.</item>
/// <item>An array of arrays is read like a CSV file: the first inner array is the header.</item>
/// <item>A lone scalar, an empty document, or a mix of records and plain values is not a table.</item>
/// </list>
/// </summary>
internal static class Tables
{
    /// <summary>The most columns and rows a sheet holds (Excel's limits), so every table target refuses the same inputs.</summary>
    public const int MaxColumns = 16_384;
    public const int MaxRows = 1_048_575;

    /// <summary>The records of a tree, not flattened (used by JSON Lines).</summary>
    public static IReadOnlyList<DataNode> Records(DataNode root)
    {
        // Only an array is opened: a single wrapped record keeps its wrapper, so nothing is lost.
        return Unwrap(root) is DataArray array ? array.Items : [root];
    }

    /// <summary>The tree as a table, or null when it is not one (see the rules above).</summary>
    public static Table? ToTable(DataNode root)
    {
        var node = Unwrap(root);
        if (node is DataValue) return null;

        if (node is DataArray array)
        {
            if (array.Items.Count == 0) return null;
            if (array.Items.All(i => i is DataArray)) return FromRows(array.Items.Cast<DataArray>().ToList());
            if (array.Items.All(i => i is DataObject)) return FromRecords(array.Items.Cast<DataObject>());
            if (array.Items.All(i => i is DataValue))
                return Checked(new Table(["value"], array.Items.Select(i => new[] { (DataValue)i }).ToList()));
            return null;
        }

        // One record: flattened from the top, so the wrapper keys stay in the column names (person.name).
        return root is DataObject { Count: > 0 } single ? FromRecords([single]) : null;
    }

    private static DataNode Unwrap(DataNode node)
    {
        for (var depth = 0; depth < 64 && node is DataObject obj; depth++)
        {
            if (obj.Count == 1 && obj.Entries[0].Value is DataObject or DataArray)
            {
                node = obj.Entries[0].Value;
                continue;
            }
            var arrays = obj.Entries.Where(e => e.Value is DataArray).ToList();
            var rest = obj.Entries.Where(e => e.Value is not DataArray).ToList();
            if (arrays.Count == 1 && rest.All(e => e.Value is DataValue) && ((DataArray)arrays[0].Value).Items.All(i => i is DataObject))
            {
                node = arrays[0].Value;
                continue;
            }
            break;
        }
        return node;
    }

    private static Table? FromRecords(IEnumerable<DataObject> records)
    {
        var columns = new List<string>();
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        var flat = new List<List<KeyValuePair<string, DataValue>>>();
        foreach (var record in records)
        {
            var cells = new List<KeyValuePair<string, DataValue>>();
            Flatten(record, null, cells);
            foreach (var (key, _) in cells)
            {
                if (index.ContainsKey(key)) continue;
                index[key] = columns.Count;
                columns.Add(key);
                if (columns.Count > MaxColumns) return null;
            }
            flat.Add(cells);
        }

        var rows = new List<DataValue[]>(flat.Count);
        foreach (var cells in flat)
        {
            var row = Enumerable.Repeat(DataValue.Null, columns.Count).ToArray();
            foreach (var (key, value) in cells) row[index[key]] = value;
            rows.Add(row);
        }
        return Checked(new Table(columns, rows));
    }

    private static void Flatten(DataNode node, string? prefix, List<KeyValuePair<string, DataValue>> cells)
    {
        switch (node)
        {
            case DataObject obj when obj.Count > 0:
                foreach (var (key, value) in obj.Entries) Flatten(value, prefix is null ? key : $"{prefix}.{key}", cells);
                break;
            case DataArray array when array.Items.Count > 0:
                for (var i = 0; i < array.Items.Count; i++) Flatten(array.Items[i], prefix is null ? i.ToString() : $"{prefix}.{i}", cells);
                break;
            case DataValue value:
                cells.Add(new(prefix ?? "value", value));
                break;
            default: // an empty object or array: an empty cell, so the column is not lost
                cells.Add(new(prefix ?? "value", DataValue.Null));
                break;
        }
    }

    private static Table? FromRows(List<DataArray> rows)
    {
        if (rows.Any(r => r.Items.Any(c => c is not DataValue))) return null;
        var width = rows.Max(r => r.Items.Count);
        if (width == 0) return null;
        var header = Enumerable.Range(0, width)
            .Select(i => i < rows[0].Items.Count && rows[0].Items[i] is DataValue { Text: { Length: > 0 } t } ? t : $"column{i + 1}")
            .ToList();
        var body = rows.Skip(1)
            .Select(r => Enumerable.Range(0, width).Select(i => i < r.Items.Count ? (DataValue)r.Items[i] : DataValue.Null).ToArray())
            .ToList();
        return Checked(new Table(UniqueNames(header), body));
    }

    private static Table? Checked(Table table) =>
        table.Columns.Count is 0 or > MaxColumns || table.Rows.Count > MaxRows ? null : table;

    /// <summary>Header names made unique and non-empty ("name", "name_2", "column3"), as CSV headers can repeat or be blank.</summary>
    public static List<string> UniqueNames(IReadOnlyList<string> names)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>(names.Count);
        for (var i = 0; i < names.Count; i++)
        {
            var name = string.IsNullOrWhiteSpace(names[i]) ? $"column{i + 1}" : names[i].Trim();
            var candidate = name;
            for (var n = 2; !used.Add(candidate); n++) candidate = $"{name}_{n}";
            result.Add(candidate);
        }
        return result;
    }

    /// <summary>A table as a tree: an array of flat objects, one per row (empty cells are left out only when the cell is null).</summary>
    public static DataArray ToTree(Table table)
    {
        var array = new DataArray();
        foreach (var row in table.Rows)
        {
            var obj = new DataObject();
            for (var c = 0; c < table.Columns.Count; c++) obj.Set(table.Columns[c], row[c]);
            array.Items.Add(obj);
        }
        return array;
    }
}
