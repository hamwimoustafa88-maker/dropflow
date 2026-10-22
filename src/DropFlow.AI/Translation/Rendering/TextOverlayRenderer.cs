using DropFlow.AI.Imaging;
using DropFlow.AI.Ocr;
using DropFlow.Engines.Common;
using ImageMagick;
using ImageMagick.Drawing;

namespace DropFlow.AI.Translation.Rendering;

/// <summary>One translated paragraph to paint over the place where the original text was.</summary>
/// <param name="AlignRight">True when the text hugs the right edge of its area (Arabic, or a translation of an Arabic original).</param>
public sealed record OverlayText(OcrRect Bounds, double LineHeight, string Text, bool AlignRight);

/// <summary>
/// Paints translated paragraphs over an image: the original text area is covered with the colour of its surroundings, then the translation is
/// wrapped and drawn inside that area in the largest size that fits.
/// <para>
/// Arabic needs shaping (letters change form to connect) and right-to-left ordering. The ImageMagick build inside Magick.NET lays text
/// out with Raqm (HarfBuzz and FriBidi), so logical text is drawn as it is and comes out right. When a build lacks Raqm
/// (<see cref="HasComplexTextLayout"/> is false), <see cref="ArabicShaper"/> shapes and orders the line first.
/// </para>
/// </summary>
public static class TextOverlayRenderer
{
    private const int MinFontPixels = 9;

    /// <summary>A TrueType font with Arabic and Latin glyphs, or null when none is installed.</summary>
    public static string? FontFile() => FontFiles.DefaultSans();

    /// <summary>True when the ImageMagick build lays text out itself (Raqm), so right-to-left and joining scripts need no help.</summary>
    public static bool HasComplexTextLayout { get; } = MagickNET.Delegates.Contains("raqm", StringComparison.OrdinalIgnoreCase);

    /// <summary>Test seam: forces the self-made shaping path (<c>false</c>) or the library path (<c>true</c>).</summary>
    internal static bool? ComplexLayoutOverride { get; set; }

    internal static string PrepareForDrawing(string logicalLine) =>
        ComplexLayoutOverride ?? HasComplexTextLayout ? logicalLine : ArabicShaper.Prepare(logicalLine);

    public static void Apply(MagickImage image, IReadOnlyList<OverlayText> items, string fontFile, CancellationToken ct = default)
    {
        using var measure = new MagickImage(MagickColors.Transparent, 8, 8);
        measure.Settings.Font = fontFile;
        var areas = items.Select(i => Clamp(Inflate(i.Bounds, i.LineHeight), image)).ToList();
        for (var n = 0; n < items.Count; n++)
        {
            ct.ThrowIfCancellationRequested();
            var item = items[n];
            var box = areas[n];
            if (box.Width < 4 || box.Height < 4 || item.Text.Trim().Length == 0) continue;

            var (background, foreground) = SampleColors(image, box);
            FillBox(image, box, background);
            DrawParagraph(image, measure, ExtendSideways(measure, box, item, areas, n, image), item, foreground);
        }
    }

    /// <summary>
    /// A translation is often longer than the original. When it cannot be drawn at the original size inside the original area, the text
    /// area grows sideways (towards the free side of the text, never over another paragraph) up to double its width.
    /// </summary>
    private static OcrRect ExtendSideways(MagickImage measure, OcrRect box, OverlayText item, IReadOnlyList<OcrRect> all, int self, MagickImage image)
    {
        var padding = Math.Max(1.0, item.LineHeight * 0.1);
        var preferred = Math.Max(MinFontPixels, (int)Math.Round(item.LineHeight * 0.9));
        bool Fits(OcrRect r) => Wrap(measure, item.Text, preferred, Math.Max(4, r.Width - 2 * padding)).Count * preferred * 1.3 <= Math.Max(4, r.Height - 2 * padding);
        if (Fits(box)) return box;

        var rtl = item.AlignRight || ArabicShaper.HasArabic(item.Text);
        bool Overlaps(OcrRect o) => o.Y < box.Bottom && o.Bottom > box.Y;
        if (rtl)
        {
            var limit = 4.0;
            for (var i = 0; i < all.Count; i++)
                if (i != self && Overlaps(all[i]) && all[i].Right <= box.X + 1) limit = Math.Max(limit, all[i].Right + 6);
            var left = Math.Max(limit, box.X - box.Width);
            return OcrRect.FromEdges(left, box.Y, box.Right, box.Bottom);
        }
        else
        {
            var limit = image.Width - 4.0;
            for (var i = 0; i < all.Count; i++)
                if (i != self && Overlaps(all[i]) && all[i].X >= box.Right - 1) limit = Math.Min(limit, all[i].X - 6);
            var right = Math.Min(limit, box.Right + box.Width);
            return OcrRect.FromEdges(box.X, box.Y, Math.Max(box.Right, right), box.Bottom);
        }
    }

    /// <summary>Grows an OCR box so the patch also covers marks above and below the letters (Arabic diacritics, descenders) that the engine leaves out of its box.</summary>
    private static OcrRect Inflate(OcrRect r, double lineHeight)
    {
        var x = Math.Max(3, lineHeight * 0.2);
        var y = Math.Max(3, lineHeight * 0.3);
        return new OcrRect(r.X - x, r.Y - y, r.Width + 2 * x, r.Height + 2 * y);
    }

    private static OcrRect Clamp(OcrRect r, MagickImage image)
    {
        var left = Math.Max(0, r.X);
        var top = Math.Max(0, r.Y);
        var right = Math.Min(image.Width, r.Right);
        var bottom = Math.Min(image.Height, r.Bottom);
        return OcrRect.FromEdges(left, top, right, bottom);
    }

    private static void FillBox(MagickImage image, OcrRect box, MagickColor color) =>
        new Drawables().StrokeColor(MagickColors.Transparent).FillColor(color)
            .Rectangle(box.X, box.Y, box.Right - 1, box.Bottom - 1).Draw(image);

    /// <summary>
    /// The background is the most common colour of the area (text covers only a minority of its pixels); the text colour is the average of
    /// the pixels farthest from it, replaced by black or white when it would not contrast enough.
    /// </summary>
    internal static (MagickColor Background, MagickColor Foreground) SampleColors(MagickImage image, OcrRect box)
    {
        using var crop = (MagickImage)image.Clone();
        crop.Crop(new MagickGeometry((int)box.X, (int)box.Y, (uint)Math.Max(1, box.Width), (uint)Math.Max(1, box.Height)));
        crop.ResetPage();
        crop.Alpha(AlphaAction.Remove);
        var pixels = ImageIo.GetBytes(crop, PixelMapping.RGB);
        var count = pixels.Length / 3;

        var bins = new Dictionary<int, (int Count, long R, long G, long B)>();
        for (var i = 0; i < count; i++)
        {
            int r = pixels[i * 3], g = pixels[i * 3 + 1], b = pixels[i * 3 + 2];
            var key = (r >> 4) << 8 | (g >> 4) << 4 | (b >> 4);
            bins.TryGetValue(key, out var e);
            bins[key] = (e.Count + 1, e.R + r, e.G + g, e.B + b);
        }
        var top = bins.Values.MaxBy(e => e.Count);
        double bgR = (double)top.R / top.Count, bgG = (double)top.G / top.Count, bgB = (double)top.B / top.Count;

        // average of the farthest 8 percent of pixels = the ink
        var distances = new (double D, int I)[count];
        for (var i = 0; i < count; i++)
        {
            var dr = pixels[i * 3] - bgR;
            var dg = pixels[i * 3 + 1] - bgG;
            var db = pixels[i * 3 + 2] - bgB;
            distances[i] = (dr * dr + dg * dg + db * db, i);
        }
        var take = Math.Max(1, count / 12);
        double fr = 0, fg = 0, fb = 0;
        foreach (var d in distances.OrderByDescending(d => d.D).Take(take))
        {
            fr += pixels[d.I * 3]; fg += pixels[d.I * 3 + 1]; fb += pixels[d.I * 3 + 2];
        }
        fr /= take; fg /= take; fb /= take;

        var background = Color(bgR, bgG, bgB);
        var foreground = Contrast(bgR, bgG, bgB, fr, fg, fb) >= 3.0
            ? Color(fr, fg, fb)
            : Luminance(bgR, bgG, bgB) > 0.45 ? MagickColors.Black : MagickColors.White;
        return (background, foreground);
    }

    private static MagickColor Color(double r, double g, double b) =>
        new((byte)Math.Round(r), (byte)Math.Round(g), (byte)Math.Round(b));

    private static double Luminance(double r, double g, double b)
    {
        static double Lin(double v) { v /= 255; return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4); }
        return 0.2126 * Lin(r) + 0.7152 * Lin(g) + 0.0722 * Lin(b);
    }

    private static double Contrast(double r1, double g1, double b1, double r2, double g2, double b2)
    {
        var a = Luminance(r1, g1, b1);
        var b = Luminance(r2, g2, b2);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    // ---------------------------------------------------------------------------------------------------------- text

    private static void DrawParagraph(MagickImage image, MagickImage measure, OcrRect box, OverlayText item, MagickColor foreground)
    {
        var padding = Math.Max(1.0, item.LineHeight * 0.1);
        var availableWidth = Math.Max(4, box.Width - 2 * padding);
        var availableHeight = Math.Max(4, box.Height - 2 * padding);
        var arabic = ArabicShaper.HasArabic(item.Text);
        var rtl = item.AlignRight || arabic;

        // Largest size whose wrapped text fits the box; the starting size follows the height of the original lines.
        var size = Math.Max(MinFontPixels, (int)Math.Round(item.LineHeight * 0.9));
        List<string> lines = [];
        for (; ; size--)
        {
            lines = Wrap(measure, item.Text, size, availableWidth);
            if (lines.Count * size * 1.3 <= availableHeight || size <= MinFontPixels) break;
        }

        var lineHeight = size * 1.3;
        var totalHeight = lines.Count * lineHeight;
        var topY = box.Y + padding + Math.Max(0, (availableHeight - totalHeight) / 2);      // centred vertically when there is room to spare

        var drawables = new Drawables().Font(measure.Settings.Font!).FontPointSize(size).StrokeColor(MagickColors.Transparent).FillColor(foreground)
            .TextAlignment(TextAlignment.Left);
        for (var i = 0; i < lines.Count; i++)
        {
            var visual = PrepareForDrawing(lines[i]);
            var width = TextWidth(measure, visual, size);
            var x = rtl ? box.Right - padding - width : box.X + padding;
            var baseline = topY + i * lineHeight + size * 1.0;
            drawables.Text(x, baseline, visual);
        }
        drawables.Draw(image);
    }

    /// <summary>Greedy word wrap in logical order; measurement uses the shaped form so widths match what is drawn.</summary>
    private static List<string> Wrap(MagickImage measure, string text, int size, double maxWidth)
    {
        var words = text.Split([' ', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries);
        var lines = new List<string>();
        var line = "";
        foreach (var word in words)
        {
            var candidate = line.Length == 0 ? word : line + " " + word;
            if (line.Length > 0 && TextWidth(measure, PrepareForDrawing(candidate), size) > maxWidth)
            {
                lines.Add(line);
                line = word;
            }
            else line = candidate;
        }
        if (line.Length > 0) lines.Add(line);
        return lines;
    }

    private static double TextWidth(MagickImage measure, string text, int size)
    {
        measure.Settings.FontPointsize = size;
        return measure.FontTypeMetrics(text)?.TextWidth ?? text.Length * size * 0.55;
    }
}
