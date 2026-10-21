using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace DropFlow.AI.Ocr;

/// <summary>A rectangle in image pixels (origin top-left, y grows downwards).</summary>
public readonly record struct OcrRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public double CenterX => X + Width / 2;
    public double CenterY => Y + Height / 2;

    public static OcrRect FromEdges(double left, double top, double right, double bottom) =>
        new(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));

    public static OcrRect Union(IEnumerable<OcrRect> rects)
    {
        double l = double.MaxValue, t = double.MaxValue, r = double.MinValue, b = double.MinValue;
        var any = false;
        foreach (var rect in rects)
        {
            any = true;
            l = Math.Min(l, rect.X); t = Math.Min(t, rect.Y);
            r = Math.Max(r, rect.Right); b = Math.Max(b, rect.Bottom);
        }
        return any ? FromEdges(l, t, r, b) : default;
    }

    /// <summary>Grows the rectangle by <paramref name="amount"/> on every side.</summary>
    public OcrRect Inflate(double amount) => new(X - amount, Y - amount, Width + 2 * amount, Height + 2 * amount);

    public OcrRect Scale(double factor) => new(X * factor, Y * factor, Width * factor, Height * factor);

    /// <summary>Fraction of the shorter vertical extent that the two rectangles share (1 = same rows, 0 = no overlap).</summary>
    public double VerticalOverlap(OcrRect other)
    {
        var overlap = Math.Min(Bottom, other.Bottom) - Math.Max(Y, other.Y);
        var shorter = Math.Min(Height, other.Height);
        return shorter <= 0 ? 0 : Math.Max(0, overlap) / shorter;
    }
}

/// <param name="Confidence">0..1 when the engine reports one (Tesseract does, Windows OCR does not).</param>
public sealed record OcrWord(string Text, OcrRect Bounds, double? Confidence = null);

/// <param name="Words">In reading (logical) order: for Arabic the first word is the rightmost one.</param>
public sealed record OcrLine(string Text, IReadOnlyList<OcrWord> Words, OcrRect Bounds, bool IsRightToLeft);

/// <summary>
/// What an OCR engine found. All rectangles are in pixels of the upright (EXIF-rotated) source image at its original size,
/// which is exactly the coordinate space auto-redaction draws in.
/// </summary>
public sealed record OcrResult(string Engine, string Language, int ImageWidth, int ImageHeight, IReadOnlyList<OcrLine> Lines)
{
    public string Text => string.Join("\n", Lines.Select(l => l.Text));

    public IEnumerable<OcrWord> Words => Lines.SelectMany(l => l.Words);

    public bool IsEmpty => Lines.Count == 0 || Lines.All(l => l.Words.Count == 0);

    /// <summary>The same result with every rectangle multiplied by <paramref name="factor"/> (used to undo a down-scale before recognition).</summary>
    public OcrResult Scaled(double factor, int imageWidth, int imageHeight) => this with
    {
        ImageWidth = imageWidth,
        ImageHeight = imageHeight,
        Lines = Lines.Select(l => new OcrLine(
            l.Text,
            l.Words.Select(w => w with { Bounds = w.Bounds.Scale(factor) }).ToList(),
            l.Bounds.Scale(factor),
            l.IsRightToLeft)).ToList(),
    };

    private static readonly JsonWriterOptions JsonOptions = new() { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>
    /// <c>{ engine, language, imageWidth, imageHeight, text, lines: [ { text, rtl, bounds: [x,y,w,h], words: [ { text, bounds, confidence } ] } ] }</c>
    /// </summary>
    public string ToJson()
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, JsonOptions))
        {
            w.WriteStartObject();
            w.WriteString("engine", Engine);
            w.WriteString("language", Language);
            w.WriteNumber("imageWidth", ImageWidth);
            w.WriteNumber("imageHeight", ImageHeight);
            w.WriteString("text", Text);
            w.WriteStartArray("lines");
            foreach (var line in Lines)
            {
                w.WriteStartObject();
                w.WriteString("text", line.Text);
                w.WriteBoolean("rtl", line.IsRightToLeft);
                WriteRect(w, "bounds", line.Bounds);
                w.WriteStartArray("words");
                foreach (var word in line.Words)
                {
                    w.WriteStartObject();
                    w.WriteString("text", word.Text);
                    WriteRect(w, "bounds", word.Bounds);
                    if (word.Confidence is { } c) w.WriteNumber("confidence", Math.Round(c, 3));
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return new UTF8Encoding(false).GetString(stream.ToArray()) + "\n";
    }

    private static void WriteRect(Utf8JsonWriter w, string name, OcrRect r)
    {
        w.WriteStartArray(name);
        foreach (var v in new[] { r.X, r.Y, r.Width, r.Height }) w.WriteNumberValue(Math.Round(v, 1));
        w.WriteEndArray();
    }

    /// <summary>Text with a trailing newline, or empty when nothing was recognised.</summary>
    public string ToPlainText() => Text.Length == 0 ? "" : Text.Replace("\n", Environment.NewLine) + Environment.NewLine;

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Engine}/{Language}: {Lines.Count} lines, {Words.Count()} words");
}
