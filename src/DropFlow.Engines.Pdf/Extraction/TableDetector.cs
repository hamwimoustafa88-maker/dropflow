namespace DropFlow.Engines.Pdf.Extraction;

/// <summary>A page item in reading order: either an ordinary line or a table recognised from several aligned lines.</summary>
internal sealed record PageItem(TextLine? Line, TableBlock? Table);

/// <summary>
/// Finds grids: runs of consecutive lines that each split into two or more cells and whose cells line up in shared columns.
/// Two-column prose is told apart from a table by cell length (table cells are short).
/// </summary>
internal static class TableDetector
{
    private const int MinRows = 2;
    private const double MaxAverageCellLength = 40;

    public static IReadOnlyList<PageItem> Group(IReadOnlyList<TextLine> lines)
    {
        var items = new List<PageItem>();
        var i = 0;
        while (i < lines.Count)
        {
            if (lines[i].Segments.Count < 2)
            {
                items.Add(new PageItem(lines[i], null));
                i++;
                continue;
            }

            var j = i + 1;
            while (j < lines.Count && lines[j].Segments.Count >= 2
                   && lines[j - 1].BaseY - lines[j].BaseY <= 2.6 * Math.Max(lines[j].FontSize, lines[j - 1].FontSize))
            {
                j++;
            }

            var group = lines.Skip(i).Take(j - i).ToList();
            var table = group.Count >= MinRows ? TryBuild(group) : null;
            if (table is null)
            {
                items.AddRange(group.Select(l => new PageItem(l, null)));
            }
            else
            {
                items.Add(new PageItem(null, table));
            }
            i = j;
        }
        return items;
    }

    private static TableBlock? TryBuild(List<TextLine> rows)
    {
        var segments = rows.SelectMany(r => r.Segments).ToList();
        if (segments.Average(s => s.Text.Length) > MaxAverageCellLength) return null;

        // Column bands: merge the horizontal spans of all cells that overlap.
        var pad = 0.3 * rows.Average(r => r.FontSize);
        var bands = new List<(double Left, double Right)>();
        foreach (var s in segments.OrderBy(s => s.Left))
        {
            var left = s.Left - pad;
            var right = s.Right + pad;
            if (bands.Count > 0 && left <= bands[^1].Right) bands[^1] = (bands[^1].Left, Math.Max(bands[^1].Right, right));
            else bands.Add((left, right));
        }
        if (bands.Count < 2) return null;

        var grid = new List<IReadOnlyList<string>>();
        var multiCellRows = 0;
        foreach (var row in rows)
        {
            var cells = new string?[bands.Count];
            var used = new HashSet<int>();
            foreach (var s in row.Segments)
            {
                var centre = (s.Left + s.Right) / 2;
                var band = bands.FindIndex(b => centre >= b.Left && centre <= b.Right);
                if (band < 0) band = 0;
                used.Add(band);
                cells[band] = cells[band] is null ? s.Text : cells[band] + " " + s.Text;
            }
            if (used.Count >= 2) multiCellRows++;
            grid.Add(cells.Select(c => c ?? "").ToList());
        }

        if (multiCellRows * 10 < rows.Count * 6) return null; // fewer than 60 % of the rows really use several columns

        var rtl = rows.Count(r => r.Rtl) * 2 > rows.Count;
        if (rtl) grid = grid.Select(r => (IReadOnlyList<string>)r.Reverse().ToList()).ToList();
        return new TableBlock(grid, rtl);
    }
}
