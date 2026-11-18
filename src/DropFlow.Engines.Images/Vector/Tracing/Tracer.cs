// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text;
using ImageMagick;

namespace DropFlow.Engines.Images.Vector.Tracing;

/// <summary>How to trace: <c>bw</c> (Otsu threshold, black shapes only) or <c>color</c> (k-means palette of 2-16 colours), and how much detail.</summary>
internal sealed record TraceOptions(string Mode = "color", int Colors = 8, string Detail = "medium")
{
    public const int MinColors = 2;
    public const int MaxColors = 16;
}

/// <summary>
/// Raster to SVG: quantise (Otsu or k-means), fold specks into their surroundings, then trace one filled path per colour.
/// Colour layers are stacked: layer i covers every pixel whose colour comes at or after position i in the drawing order (largest
/// area first), so each layer lies under the ones drawn after it and no background can show through between two neighbouring
/// colours. Paths use the non-zero fill rule (holes run the other way round).
/// </summary>
internal static class Tracer
{
    private sealed record DetailSettings(uint MaxSide, int MinArea, int CornerWindow, double CornerDegrees, double MaxError);

    private static DetailSettings SettingsFor(string? detail) => detail switch
    {
        "low" => new(400, 30, 5, 55, 1.6),
        "high" => new(1600, 3, 3, 50, 0.55),
        _ => new(800, 10, 4, 52, 0.9),
    };

    public static string Trace(MagickImage source, TraceOptions options, CancellationToken ct)
    {
        var settings = SettingsFor(options.Detail);
        uint originalWidth = source.Width, originalHeight = source.Height;

        using var work = (MagickImage)source.Clone();
        ImageIo.EnsureColor(work);
        if (Math.Max(work.Width, work.Height) > settings.MaxSide) work.Resize(new MagickGeometry(settings.MaxSide, settings.MaxSide));
        var width = (int)work.Width;
        var height = (int)work.Height;
        var rgba = work.GetPixels().ToByteArray(PixelMapping.RGBA) ?? throw new InvalidDataException("The picture has no pixels.");
        ct.ThrowIfCancellationRequested();

        var bw = string.Equals(options.Mode, "bw", StringComparison.OrdinalIgnoreCase);
        var (labels, palette) = bw ? Threshold(rgba) : Quantise(rgba, Math.Clamp(options.Colors, TraceOptions.MinColors, TraceOptions.MaxColors));
        ct.ThrowIfCancellationRequested();
        Regions.RemoveSpecks(labels, width, height, settings.MinArea, ct);

        // Drawing order: largest area first. In black-and-white mode only the dark class is drawn (index 0).
        var area = new long[palette.Length];
        foreach (var l in labels) if (l >= 0) area[l]++;
        var order = Enumerable.Range(0, palette.Length).Where(i => area[i] > 0).OrderByDescending(i => area[i]).ThenBy(i => i).ToList();
        var rank = new int[palette.Length];
        for (var i = 0; i < order.Count; i++) rank[order[i]] = i;

        var svg = new StringBuilder();
        svg.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
        svg.Append(CultureInfo.InvariantCulture, $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{originalWidth}\" height=\"{originalHeight}\" viewBox=\"0 0 {width} {height}\">\n");

        var mask = new bool[labels.Length];
        for (var layer = 0; layer < order.Count; layer++)
        {
            ct.ThrowIfCancellationRequested();
            var colour = order[layer];
            if (bw && colour != 0) continue;
            for (var p = 0; p < labels.Length; p++)
                mask[p] = labels[p] >= 0 && (bw ? labels[p] == 0 : rank[labels[p]] >= layer);

            var d = new StringBuilder();
            foreach (var loop in BoundaryTracer.Trace(mask, width, height, ct))
                AppendLoop(d, ContourFitter.Fit(loop, settings.CornerWindow, settings.CornerDegrees, settings.MaxError));
            if (d.Length == 0) continue;
            svg.Append(CultureInfo.InvariantCulture, $"<path fill=\"{palette[colour].Hex}\" d=\"{d}\"/>\n");
        }

        svg.Append("</svg>\n");
        return svg.ToString();
    }

    /// <summary>Black and white: Otsu on the luminance (transparent pixels count as white). Index 0 = dark, 1 = light.</summary>
    private static (int[] Labels, Rgb[] Palette) Threshold(byte[] rgba)
    {
        var count = rgba.Length / 4;
        var gray = new byte[count];
        for (var i = 0; i < count; i++)
        {
            double a = rgba[i * 4 + 3] / 255.0;
            var luma = 0.2126 * rgba[i * 4] + 0.7152 * rgba[i * 4 + 1] + 0.0722 * rgba[i * 4 + 2];
            gray[i] = (byte)Math.Clamp(Math.Round(luma * a + 255 * (1 - a)), 0, 255);
        }
        var t = Otsu.Threshold(gray);
        var labels = new int[count];
        for (var i = 0; i < count; i++) labels[i] = gray[i] <= t ? 0 : 1;
        return (labels, [new Rgb(0, 0, 0), new Rgb(255, 255, 255)]);
    }

    /// <summary>Colour: k-means palette, every opaque pixel mapped to its nearest colour; pixels under half opacity stay empty (-1).</summary>
    private static (int[] Labels, Rgb[] Palette) Quantise(byte[] rgba, int colours)
    {
        var palette = ColorQuantizer.Palette(rgba, colours);
        var count = rgba.Length / 4;
        var labels = new int[count];
        // Nearest colour per 6-bit-per-channel cell (1 MB table, at most 2 levels off), so a photo with millions of colours stays fast.
        var nearest = new int[1 << 18];
        Array.Fill(nearest, -1);
        for (var i = 0; i < count; i++)
        {
            if (rgba[i * 4 + 3] < 128 || palette.Length == 0) { labels[i] = -1; continue; }
            int r = rgba[i * 4] >> 2, g = rgba[i * 4 + 1] >> 2, b = rgba[i * 4 + 2] >> 2;
            var cell = (r << 12) | (g << 6) | b;
            if (nearest[cell] < 0) nearest[cell] = ColorQuantizer.Nearest(palette, (byte)((r << 2) | 2), (byte)((g << 2) | 2), (byte)((b << 2) | 2));
            labels[i] = nearest[cell];
        }
        return (labels, palette);
    }

    private static void AppendLoop(StringBuilder d, List<CubicBezier> curves)
    {
        if (curves.Count == 0) return;
        d.Append('M').Append(N(curves[0].P0.X)).Append(' ').Append(N(curves[0].P0.Y));
        foreach (var c in curves)
        {
            if (c.IsNearlyStraight(0.05))
            {
                d.Append('L').Append(N(c.P3.X)).Append(' ').Append(N(c.P3.Y));
                continue;
            }
            d.Append('C')
                .Append(N(c.P1.X)).Append(' ').Append(N(c.P1.Y)).Append(' ')
                .Append(N(c.P2.X)).Append(' ').Append(N(c.P2.Y)).Append(' ')
                .Append(N(c.P3.X)).Append(' ').Append(N(c.P3.Y));
        }
        d.Append('Z');
    }

    private static string N(double v)
    {
        var rounded = Math.Round(v, 2);
        return (rounded == 0 ? 0 : rounded).ToString("0.##", CultureInfo.InvariantCulture);
    }
}
