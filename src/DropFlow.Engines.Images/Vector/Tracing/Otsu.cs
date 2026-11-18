// SPDX-License-Identifier: GPL-3.0-or-later
namespace DropFlow.Engines.Images.Vector.Tracing;

/// <summary>
/// Otsu's method: the grey level that splits a histogram into two classes with the largest between-class variance
/// (equivalently the smallest within-class variance). Values at or below the threshold are the dark class.
/// </summary>
internal static class Otsu
{
    public static int Threshold(ReadOnlySpan<byte> values)
    {
        if (values.IsEmpty) return 127;

        Span<long> histogram = stackalloc long[256];
        foreach (var v in values) histogram[v]++;

        double total = values.Length;
        double sumAll = 0;
        for (var i = 0; i < 256; i++) sumAll += i * (double)histogram[i];

        double weightDark = 0, sumDark = 0, best = -1;
        int firstBest = 127, lastBest = 127;
        for (var t = 0; t < 255; t++)
        {
            weightDark += histogram[t];
            sumDark += t * (double)histogram[t];
            if (weightDark == 0) continue;
            var weightLight = total - weightDark;
            if (weightLight == 0) break;

            var meanDark = sumDark / weightDark;
            var meanLight = (sumAll - sumDark) / weightLight;
            var between = weightDark * weightLight * (meanDark - meanLight) * (meanDark - meanLight);

            // Empty histogram bins make a plateau of equal scores; the middle of the plateau is the most robust split.
            if (between > best * (1 + 1e-12))
            {
                best = between;
                firstBest = lastBest = t;
            }
            else if (Math.Abs(between - best) <= best * 1e-12)
            {
                lastBest = t;
            }
        }
        return (firstBest + lastBest) / 2;
    }
}
