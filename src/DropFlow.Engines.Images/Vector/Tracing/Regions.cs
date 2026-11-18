// SPDX-License-Identifier: GPL-3.0-or-later
namespace DropFlow.Engines.Images.Vector.Tracing;

/// <summary>
/// Region labelling on a label picture (one colour index per pixel, -1 = transparent): 4-connected components, and the clean-up
/// that folds components smaller than a minimum area into the neighbouring colour they share the longest border with.
/// </summary>
internal static class Regions
{
    public static void RemoveSpecks(int[] labels, int width, int height, int minArea, CancellationToken ct = default)
    {
        if (minArea <= 1) return;
        // Two passes: a speck merged into another speck in the first pass can still be too small.
        for (var pass = 0; pass < 2; pass++)
        {
            ct.ThrowIfCancellationRequested();
            if (!MergePass(labels, width, height, minArea, ct)) return;
        }
    }

    private static bool MergePass(int[] labels, int width, int height, int minArea, CancellationToken ct)
    {
        var component = new int[labels.Length];
        Array.Fill(component, -1);
        var stack = new Stack<int>();
        var members = new List<int>();
        var neighbourCounts = new Dictionary<int, int>();
        var merged = false;
        var next = 0;

        for (var start = 0; start < labels.Length; start++)
        {
            if (component[start] >= 0) continue;
            if ((start & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();

            var label = labels[start];
            var id = next++;
            members.Clear();
            neighbourCounts.Clear();
            component[start] = id;
            stack.Push(start);
            while (stack.Count > 0)
            {
                var p = stack.Pop();
                members.Add(p);
                int x = p % width, y = p / width;
                Visit(x - 1, y); Visit(x + 1, y); Visit(x, y - 1); Visit(x, y + 1);
            }

            if (members.Count >= minArea || neighbourCounts.Count == 0) continue;
            var target = neighbourCounts.MaxBy(kv => kv.Value).Key;
            foreach (var m in members) labels[m] = target;
            merged = true;

            void Visit(int x, int y)
            {
                if (x < 0 || y < 0 || x >= width || y >= height) return;
                var q = y * width + x;
                if (labels[q] == label)
                {
                    if (component[q] >= 0) return;
                    component[q] = id;
                    stack.Push(q);
                }
                else
                {
                    neighbourCounts[labels[q]] = neighbourCounts.GetValueOrDefault(labels[q]) + 1;
                }
            }
        }
        return merged;
    }
}
