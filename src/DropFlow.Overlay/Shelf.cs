using System.IO;

namespace DropFlow.Overlay;

public enum ShelfEdge { Left, Right, Top, Bottom }

/// <summary>The files parked on the floating shelf. Pure logic (no WPF), so it is unit-tested.</summary>
public sealed class ShelfModel
{
    private readonly List<string> _items = [];

    public IReadOnlyList<string> Items => _items;

    public event EventHandler? Changed;

    /// <summary>Adds files, skipping duplicates and paths that no longer exist. Returns how many were added.</summary>
    public int Add(IEnumerable<string> paths)
    {
        var added = 0;
        foreach (var raw in paths)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var full = Path.GetFullPath(raw);
            if (!File.Exists(full) && !Directory.Exists(full)) continue;
            if (_items.Contains(full, StringComparer.OrdinalIgnoreCase)) continue;
            _items.Add(full);
            added++;
        }
        if (added > 0) Changed?.Invoke(this, EventArgs.Empty);
        return added;
    }

    public bool Remove(string path)
    {
        var i = _items.FindIndex(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        if (i < 0) return false;
        _items.RemoveAt(i);
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void Clear()
    {
        if (_items.Count == 0) return;
        _items.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Drops entries whose files were deleted or moved since they were added.</summary>
    public int PruneMissing()
    {
        var removed = _items.RemoveAll(p => !File.Exists(p) && !Directory.Exists(p));
        if (removed > 0) Changed?.Invoke(this, EventArgs.Empty);
        return removed;
    }
}

/// <summary>Where the shelf sits when pinned to a screen edge (device-independent pixels).</summary>
public static class ShelfDocking
{
    public readonly record struct Bounds(double Left, double Top, double Width, double Height);

    /// <summary>Position and size for a shelf of <paramref name="thickness"/> pinned to <paramref name="edge"/> of the work area, centred along it.</summary>
    public static Bounds Compute(Bounds workArea, ShelfEdge edge, double thickness, double length)
    {
        length = Math.Clamp(length, 120, edge is ShelfEdge.Left or ShelfEdge.Right ? workArea.Height : workArea.Width);
        return edge switch
        {
            ShelfEdge.Left => new(workArea.Left, workArea.Top + (workArea.Height - length) / 2, thickness, length),
            ShelfEdge.Right => new(workArea.Left + workArea.Width - thickness, workArea.Top + (workArea.Height - length) / 2, thickness, length),
            ShelfEdge.Top => new(workArea.Left + (workArea.Width - length) / 2, workArea.Top, length, thickness),
            _ => new(workArea.Left + (workArea.Width - length) / 2, workArea.Top + workArea.Height - thickness, length, thickness),
        };
    }

    /// <summary>Nearest edge to a window centre; used to snap the shelf when the user drops it near the border.</summary>
    public static ShelfEdge NearestEdge(Bounds workArea, double centerX, double centerY)
    {
        var distances = new (ShelfEdge Edge, double D)[]
        {
            (ShelfEdge.Left, centerX - workArea.Left),
            (ShelfEdge.Right, workArea.Left + workArea.Width - centerX),
            (ShelfEdge.Top, centerY - workArea.Top),
            (ShelfEdge.Bottom, workArea.Top + workArea.Height - centerY),
        };
        return distances.MinBy(x => x.D).Edge;
    }
}
