using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace DropFlow.AI.Tests;

/// <summary>Renders text to a PNG with GDI+ so OCR and redaction can be tested on real glyphs, and measures where each word landed.</summary>
public static class TextImage
{
    public sealed record Rendered(string Path, int Width, int Height, IReadOnlyList<(string Word, RectangleF Bounds)> Words);

    /// <summary>Black text on white by default. <paramref name="rtl"/> lays the text out right to left (Arabic, Hebrew).</summary>
    public static Rendered Render(
        string path, string text, float fontSize = 48, string fontName = "Arial", int width = 900, int height = 200,
        bool rtl = false, Color? foreground = null, Color? background = null, Action<Graphics>? decorate = null)
    {
        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);
        g.Clear(background ?? Color.White);
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        decorate?.Invoke(g);

        using var font = new Font(fontName, fontSize, FontStyle.Regular, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(foreground ?? Color.Black);
        using var format = Format(rtl);
        var layout = new RectangleF(20, 20, width - 40, height - 40);
        g.DrawString(text, font, brush, layout, format);

        var words = MeasureWords(g, text, font, layout, rtl);
        bitmap.Save(path, ImageFormat.Png);
        return new Rendered(path, width, height, words);
    }

    private static StringFormat Format(bool rtl)
    {
        var format = new StringFormat(StringFormat.GenericTypographic);
        format.FormatFlags |= StringFormatFlags.NoWrap;
        if (rtl) format.FormatFlags |= StringFormatFlags.DirectionRightToLeft;
        return format;
    }

    /// <summary>Where GDI+ put each space-separated word (logical order in, pixel rectangles out).</summary>
    public static IReadOnlyList<(string Word, RectangleF Bounds)> MeasureWords(Graphics g, string text, Font font, RectangleF layout, bool rtl)
    {
        var ranges = new List<CharacterRange>();
        var words = new List<string>();
        var index = 0;
        foreach (var word in text.Split(' '))
        {
            if (word.Length > 0)
            {
                ranges.Add(new CharacterRange(index, word.Length));
                words.Add(word);
            }
            index += word.Length + 1;
        }

        using var format = Format(rtl);
        format.SetMeasurableCharacterRanges(ranges.ToArray());
        var regions = g.MeasureCharacterRanges(text, font, layout, format);
        return words.Select((w, i) =>
        {
            using var region = regions[i];
            return (w, region.GetBounds(g));
        }).ToList();
    }

    public static Rendered MeasureOnly(string text, float fontSize, string fontName, int width, int height, bool rtl)
    {
        using var bitmap = new Bitmap(width, height);
        using var g = Graphics.FromImage(bitmap);
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        using var font = new Font(fontName, fontSize, FontStyle.Regular, GraphicsUnit.Pixel);
        return new Rendered("", width, height, MeasureWords(g, text, font, new RectangleF(20, 20, width - 40, height - 40), rtl));
    }

    /// <summary>Reads a pixel with GDI+ (a different decoder from the Magick.NET code that wrote the file).</summary>
    public sealed class Reader : IDisposable
    {
        private readonly Bitmap _bitmap;

        public Reader(string path)
        {
            using var stream = File.OpenRead(path);
            using var loaded = new Bitmap(stream);
            _bitmap = new Bitmap(loaded); // detach from the stream
        }

        public int Width => _bitmap.Width;
        public int Height => _bitmap.Height;
        public Color Pixel(int x, int y) => _bitmap.GetPixel(x, y);
        public void Dispose() => _bitmap.Dispose();

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
