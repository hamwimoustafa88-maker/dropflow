// SPDX-License-Identifier: GPL-3.0-or-later
namespace DropFlow.Engines.Images.Vector.Tracing;

/// <summary>
/// Traces the boundaries of a pixel mask along pixel edges (the cracks between pixels, so a shape and its neighbour share exactly the
/// same edge). Each boundary is a closed loop of pixel-corner vertices with the shape on the right of the direction of travel
/// (y pointing down): outer boundaries run clockwise on screen and holes counter-clockwise, so the non-zero fill rule draws holes.
/// Where two shape pixels touch only at a corner the tracer turns towards the current pixel, so they become separate loops.
/// </summary>
internal static class BoundaryTracer
{
    // Directions: 0 = +x, 1 = +y (down), 2 = -x, 3 = -y. Adding 1 turns right on screen.
    private static readonly int[] Dx = [1, 0, -1, 0];
    private static readonly int[] Dy = [0, 1, 0, -1];

    public static List<List<(int X, int Y)>> Trace(bool[] mask, int width, int height, CancellationToken ct = default)
    {
        var stride = width + 1;
        var outgoing = new byte[stride * (height + 1)];
        bool M(int x, int y) => x >= 0 && y >= 0 && x < width && y < height && mask[y * width + x];

        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                if (!mask[y * width + x]) continue;
                if (!M(x, y - 1)) outgoing[y * stride + x] |= 1;                 // top edge, left to right
                if (!M(x + 1, y)) outgoing[y * stride + x + 1] |= 2;             // right edge, downwards
                if (!M(x, y + 1)) outgoing[(y + 1) * stride + x + 1] |= 4;       // bottom edge, right to left
                if (!M(x - 1, y)) outgoing[(y + 1) * stride + x] |= 8;           // left edge, upwards
            }

        var loops = new List<List<(int X, int Y)>>();
        for (var start = 0; start < outgoing.Length; start++)
        {
            if ((start & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();
            while (outgoing[start] != 0)
            {
                var firstDir = System.Numerics.BitOperations.TrailingZeroCount(outgoing[start]);
                loops.Add(Follow(outgoing, stride, start, firstDir));
            }
        }
        return loops;
    }

    private static List<(int X, int Y)> Follow(byte[] outgoing, int stride, int start, int firstDir)
    {
        // The first edge stays marked until the loop closes on it, so the turn choices at the start vertex are the same as anywhere else.
        var loop = new List<(int X, int Y)>();
        int vertex = start, dir = firstDir;
        do
        {
            loop.Add((vertex % stride, vertex / stride));
            if (vertex != start || dir != firstDir) outgoing[vertex] &= (byte)~(1 << dir);
            vertex += Dx[dir] + Dy[dir] * stride;
            dir = NextDirection(outgoing[vertex], dir);
        }
        while (dir >= 0 && (vertex != start || dir != firstDir));

        outgoing[start] &= (byte)~(1 << firstDir);
        return loop;
    }

    /// <summary>
    /// Prefer turning right, then straight on, then left. Only a vertex where two shape pixels meet diagonally has a choice, and turning
    /// right keeps hugging the current pixel. -1 cannot happen for a valid mask (every boundary vertex has as many edges out as in).
    /// </summary>
    private static int NextDirection(byte bits, int dir)
    {
        var right = (dir + 1) & 3;
        if ((bits & (1 << right)) != 0) return right;
        if ((bits & (1 << dir)) != 0) return dir;
        var left = (dir + 3) & 3;
        return (bits & (1 << left)) != 0 ? left : -1;
    }
}
