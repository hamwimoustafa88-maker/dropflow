#if !WINDOWS
using System.Drawing;
using ImageMagick;
using ImageMagick.Drawing;

namespace DropFlow.AI.Tests;

/// <summary>
/// macOS/Linux build of the test helper (GDI+ exists on Windows only): renders text to a PNG with Magick.NET and FreeType so OCR and
/// redaction can be tested on real glyphs, and estimates where each word landed from the font's metrics. Same API as the Windows
/// version. Fonts: the requested family if a file with that name is installed (Arial on macOS), else DejaVu Sans, else any TrueType font.
/// </summary>
public static class TextImage
{
    public sealed record Rendered(string Path, int Width, int Height, IReadOnlyList<(string Word, RectangleF Bounds)> Words);

    private const float Margin = 20;

    /// <summary>Black text on white by default. <paramref name="rtl"/> aligns the text to the right edge and lays the words out right to left.</summary>
    public static Rendered Render(
        string path, string text, float fontSize = 48, string fontName = "Arial", int width = 900, int height = 200,
        bool rtl = false, Color? foreground = null, Color? background = null, IReadOnlyList<(Rectangle Area, Color Color)>? boxes = null)
    {
        using var image = new MagickImage(ToMagick(background ?? Color.White), (uint)width, (uint)height);
        foreach (var (area, color) in boxes ?? [])
            new Drawables().FillColor(ToMagick(color)).StrokeWidth(0).Rectangle(area.Left, area.Top, area.Right - 1, area.Bottom - 1).Draw(image);

        var font = FontFile(fontName);
        var layout = Layout(image, text, font, fontSize, width, rtl);
        var drawables = new Drawables().Font(font).FontPointSize(fontSize).FillColor(ToMagick(foreground ?? Color.Black))
            .TextAlignment(rtl ? TextAlignment.Right : TextAlignment.Left);
        foreach (var (line, x, baseline) in layout.Lines) drawables.Text(x, baseline, line);
        drawables.Draw(image);

        image.Format = MagickFormat.Png;
        image.Write(path);
        return new Rendered(path, width, height, layout.Words);
    }

    public static Rendered MeasureOnly(string text, float fontSize, string fontName, int width, int height, bool rtl)
    {
        using var image = new MagickImage(MagickColors.White, (uint)width, (uint)height);
        return new Rendered("", width, height, Layout(image, text, FontFile(fontName), fontSize, width, rtl).Words);
    }

    private sealed record LaidOut(List<(string Line, double X, double Baseline)> Lines, List<(string Word, RectangleF Bounds)> Words);

    private static LaidOut Layout(MagickImage image, string text, string font, float fontSize, int width, bool rtl)
    {
        image.Settings.Font = font;
        image.Settings.FontPointsize = fontSize;
        var probe = image.FontTypeMetrics("Hg") ?? throw new InvalidOperationException($"Font {font} has no metrics.");
        var ascent = probe.Ascent;
        var lineHeight = probe.TextHeight;
        var space = Width(image, "a a") - Width(image, "aa");

        var lines = new List<(string, double, double)>();
        var words = new List<(string, RectangleF)>();
        var parts = text.Split('\n');
        for (var li = 0; li < parts.Length; li++)
        {
            var line = parts[li];
            var top = Margin + li * lineHeight;
            var baseline = top + ascent;
            lines.Add((line, rtl ? width - Margin : Margin, baseline));

            var offset = 0.0;
            foreach (var word in line.Split(' '))
            {
                if (word.Length > 0)
                {
                    var w = Width(image, word);
                    var left = rtl ? width - Margin - offset - w : Margin + offset;
                    words.Add((word, new RectangleF((float)left, (float)top, (float)w, (float)lineHeight)));
                    offset += w;
                }
                offset += space;
            }
        }
        return new LaidOut(lines, words);
    }

    private static double Width(MagickImage image, string text) => image.FontTypeMetrics(text)?.TextWidth ?? 0;

    private static MagickColor ToMagick(Color c) => new(c.R, c.G, c.B, c.A);

    private static readonly string[] FontFolders =
    [
        "/System/Library/Fonts/Supplemental", "/System/Library/Fonts", "/Library/Fonts",
        "/usr/share/fonts", "/usr/local/share/fonts", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".fonts"),
    ];

    private static readonly Dictionary<string, string> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A TrueType file for <paramref name="family"/>, or a stand-in.</summary>
    public static string FontFile(string family)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(family, out var hit)) return hit;
            var files = FontFolders.Where(Directory.Exists)
                .SelectMany(d => Directory.EnumerateFiles(d, "*.*", SearchOption.AllDirectories))
                .Where(f => f.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".ttc", StringComparison.OrdinalIgnoreCase))
                .ToList();
            var found = files.FirstOrDefault(f => Path.GetFileNameWithoutExtension(f).Equals(family, StringComparison.OrdinalIgnoreCase))
                ?? files.FirstOrDefault(f => Path.GetFileNameWithoutExtension(f).Equals("DejaVuSans", StringComparison.OrdinalIgnoreCase))
                ?? files.FirstOrDefault()
                ?? throw new InvalidOperationException("No TrueType font is installed (install fonts-dejavu-core).");
            return Cache[family] = found;
        }
    }

    /// <summary>Reads pixels with Magick.NET (on Windows the GDI+ decoder is used, a different one from the code under test).</summary>
    public sealed class Reader : IDisposable
    {
        private readonly byte[] _rgba;

        public Reader(string path)
        {
            using var image = new MagickImage(path);
            Width = (int)image.Width;
            Height = (int)image.Height;
            _rgba = image.GetPixels().ToByteArray(PixelMapping.RGBA) ?? throw new InvalidOperationException("No pixels.");
        }

        public int Width { get; }
        public int Height { get; }

        public Color Pixel(int x, int y)
        {
            var i = (y * Width + x) * 4;
            return Color.FromArgb(_rgba[i + 3], _rgba[i], _rgba[i + 1], _rgba[i + 2]);
        }

        public void Dispose() { }

        /// <summary>Number of pixels in the rectangle whose colour differs from the other image's pixel at the same place.</summary>
        public int CountDifferences(Reader other, Rectangle area)
        {
            var count = 0;
            for (var y = area.Top; y < area.Bottom; y++)
                for (var x = area.Left; x < area.Right; x++)
                    if (Pixel(x, y) != other.Pixel(x, y)) count++;
            return count;
        }
    }
}
#endif
