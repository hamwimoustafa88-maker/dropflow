using DropFlow.AI.Ocr;

namespace DropFlow.AI.Translation.Rendering;

/// <summary>A paragraph of text found in an image: consecutive OCR lines that belong together, translated as one unit.</summary>
/// <param name="Text">The lines joined with spaces.</param>
public sealed record TextBlock(string Text, OcrRect Bounds, double LineHeight, int LineCount, bool IsRightToLeft);

public static class OcrBlocks
{
    /// <summary>
    /// Groups OCR lines into paragraphs: a line joins the previous one when it is close below it, about as tall, and lined up with it
    /// (overlapping, same left edge, same right edge or same centre). Lines are read in the order the OCR engine returned them.
    /// </summary>
    public static IReadOnlyList<TextBlock> Group(IReadOnlyList<OcrLine> lines)
    {
        var blocks = new List<List<OcrLine>>();
        foreach (var line in lines.Where(l => l.Text.Trim().Length > 0 && l.Bounds.Height > 0))
        {
            if (blocks.Count > 0 && Continues(blocks[^1], line)) blocks[^1].Add(line);
            else blocks.Add([line]);
        }

        return [.. blocks.Select(group =>
        {
            var text = string.Join(' ', group.Select(l => l.Text.Trim()));
            var bounds = OcrRect.Union(group.Select(l => l.Bounds));
            var height = group.Average(l => l.Bounds.Height);
            var rtl = group.Count(l => l.IsRightToLeft) * 2 > group.Count || ScriptDetector.Count(text).ArabicRatio >= 0.5;
            return new TextBlock(text, bounds, height, group.Count, rtl);
        })];
    }

    private static bool Continues(List<OcrLine> block, OcrLine next)
    {
        var last = block[^1];
        var a = last.Bounds;
        var b = next.Bounds;
        var similarHeight = b.Height / a.Height is > 0.6 and < 1.7;
        var gap = b.Y - a.Bottom;                                    // negative when the lines overlap vertically
        var below = b.CenterY > a.CenterY && gap < 0.7 * Math.Min(a.Height, b.Height);
        if (!similarHeight || !below) return false;

        var overlap = Math.Min(a.Right, b.Right) - Math.Max(a.X, b.X);
        var narrower = Math.Min(a.Width, b.Width);
        var tolerance = 2 * Math.Max(a.Height, b.Height);
        var alignedLeft = Math.Abs(a.X - b.X) <= tolerance;
        var alignedRight = Math.Abs(a.Right - b.Right) <= tolerance;
        var centred = Math.Abs(a.CenterX - b.CenterX) <= tolerance;
        return overlap >= 0.3 * narrower && (alignedLeft || alignedRight || centred);
    }
}
