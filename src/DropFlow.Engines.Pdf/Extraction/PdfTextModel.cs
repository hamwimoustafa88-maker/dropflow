namespace DropFlow.Engines.Pdf.Extraction;

/// <summary>One horizontal stretch of a line separated from its neighbours by a wide gap (a table cell candidate). Coordinates are in reader-visible points, x to the right.</summary>
internal sealed record TextSegment(string Text, double Left, double Right);

/// <summary>A line of text as the reader sees it, with the geometry later steps need.</summary>
internal sealed record TextLine(
    string Text,
    double Left,
    double Right,
    double BaseY,
    double FontSize,
    bool Bold,
    bool Italic,
    bool Rtl,
    IReadOnlyList<TextSegment> Segments);

/// <summary>Reconstructed content blocks of one page, in reading order.</summary>
internal abstract record Block;

internal sealed record HeadingBlock(int Level, string Text, bool Rtl) : Block;

internal sealed record ParagraphBlock(string Text, bool Rtl) : Block;

/// <param name="Marker">The bullet or number exactly as printed ("•", "3.", "b)").</param>
internal sealed record ListItemBlock(bool Ordered, string Marker, string Text, bool Rtl) : Block;

internal sealed record TableBlock(IReadOnlyList<IReadOnlyList<string>> Rows, bool Rtl) : Block;

internal sealed record PdfPageText(int Number, double Width, double Height, IReadOnlyList<TextLine> Lines, IReadOnlyList<Block> Blocks);

internal sealed record PdfTextDocument(
    IReadOnlyList<PdfPageText> Pages,
    double BodyFontSize,
    int SkippedRotatedCharacters,
    string? Title = null,
    string? Author = null)
{
    public bool HasText => Pages.Any(p => p.Lines.Count > 0);

    public bool IsMostlyRtl
    {
        get
        {
            var rtl = Pages.SelectMany(p => p.Lines).Count(l => l.Rtl);
            var total = Pages.SelectMany(p => p.Lines).Count();
            return total > 0 && rtl * 2 > total;
        }
    }
}
