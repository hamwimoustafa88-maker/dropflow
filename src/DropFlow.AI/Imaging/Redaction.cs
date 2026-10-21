using DropFlow.AI.Ocr;
using ImageMagick;

namespace DropFlow.AI.Imaging;

public enum RedactStyle
{
    /// <summary>Opaque black box.</summary>
    Solid,

    /// <summary>Mosaic followed by a blur: the original detail is destroyed, not merely softened.</summary>
    Blur,
}

/// <summary>A whole-pixel rectangle inside the image.</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;

    public bool Touches(PixelRect o) => X <= o.Right && o.X <= Right && Y <= o.Bottom && o.Y <= Bottom;

    public static PixelRect Union(PixelRect a, PixelRect b)
    {
        var x = Math.Min(a.X, b.X);
        var y = Math.Min(a.Y, b.Y);
        return new PixelRect(x, y, Math.Max(a.Right, b.Right) - x, Math.Max(a.Bottom, b.Bottom) - y);
    }
}

/// <summary>Pure geometry for redaction: padding, clamping to the image and merging overlaps.</summary>
public static class RedactionRegions
{
    /// <summary>
    /// Grows the rectangle by <paramref name="paddingPercent"/> of its shorter side on every edge, rounds outwards and clips it to the
    /// image. Returns null when nothing is left inside the image.
    /// </summary>
    public static PixelRect? PadAndClamp(OcrRect rect, double paddingPercent, int imageWidth, int imageHeight)
    {
        var pad = Math.Max(0, paddingPercent) / 100.0 * Math.Min(rect.Width, rect.Height);
        var left = (int)Math.Floor(Math.Max(0, rect.X - pad));
        var top = (int)Math.Floor(Math.Max(0, rect.Y - pad));
        var right = (int)Math.Ceiling(Math.Min(imageWidth, rect.Right + pad));
        var bottom = (int)Math.Ceiling(Math.Min(imageHeight, rect.Bottom + pad));
        return right > left && bottom > top ? new PixelRect(left, top, right - left, bottom - top) : null;
    }

    /// <summary>Repeatedly unions rectangles that overlap or touch until none do, so the same area is not processed twice.</summary>
    public static IReadOnlyList<PixelRect> Merge(IEnumerable<PixelRect> rects)
    {
        var list = rects.ToList();
        for (var changed = true; changed;)
        {
            changed = false;
            for (var i = 0; i < list.Count && !changed; i++)
                for (var j = i + 1; j < list.Count && !changed; j++)
                {
                    if (!list[i].Touches(list[j])) continue;
                    list[i] = PixelRect.Union(list[i], list[j]);
                    list.RemoveAt(j);
                    changed = true;
                }
        }
        return list;
    }

    /// <summary>One box per recognised line (not per word) so the gaps between words are covered too and word lengths do not leak.</summary>
    public static IEnumerable<OcrRect> TextBoxes(OcrResult ocr) =>
        ocr.Lines.Where(l => l.Words.Count > 0).Select(l => l.Bounds);
}

/// <summary>Draws redactions with Magick.NET. Pixel-exact: only pixels inside a region change.</summary>
public static class RedactionRenderer
{
    public static void Apply(IMagickImage<byte> image, IEnumerable<PixelRect> regions, RedactStyle style)
    {
        foreach (var r in regions)
        {
            if (r.Width <= 0 || r.Height <= 0) continue;
            if (style == RedactStyle.Solid) Solid(image, r);
            else Blur(image, r);
        }
    }

    private static void Solid(IMagickImage<byte> image, PixelRect r)
    {
        using var block = new MagickImage(MagickColors.Black, (uint)r.Width, (uint)r.Height);
        image.Composite(block, r.X, r.Y, CompositeOperator.Over);
    }

    private static void Blur(IMagickImage<byte> image, PixelRect r)
    {
        using var area = image.CloneArea(r.X, r.Y, (uint)r.Width, (uint)r.Height);
        area.ResetPage();

        // Average down to a coarse grid of cells, blow it back up, then smooth: nothing finer than a cell survives.
        var cell = Math.Max(6, Math.Min(r.Width, r.Height) / 8);
        var smallWidth = (uint)Math.Max(1, r.Width / cell);
        var smallHeight = (uint)Math.Max(1, r.Height / cell);
        area.Scale(smallWidth, smallHeight);
        area.Sample((uint)r.Width, (uint)r.Height);
        area.Blur(0, cell * 0.5);

        image.Composite(area, r.X, r.Y, CompositeOperator.Copy);
    }
}
