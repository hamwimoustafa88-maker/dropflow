using DropFlow.Engines.Pdf.Infrastructure;

namespace DropFlow.Engines.Pdf.Office;

/// <summary>A stretch of text with one set of character formats. <see cref="Break"/> is a line break (the text is empty).</summary>
internal sealed record Run(string Text, bool Bold = false, bool Italic = false, bool Code = false, string? Href = null, bool Break = false)
{
    public static Run LineBreak { get; } = new("", Break: true);
}

/// <summary>The common shape of an Office-style text document, so any reader can feed any writer.</summary>
internal abstract record RichBlock;

internal sealed record RHeading(int Level, IReadOnlyList<Run> Runs) : RichBlock;

internal sealed record RParagraph(IReadOnlyList<Run> Runs, bool Quote = false) : RichBlock;

/// <param name="Depth">0 for a top-level item, 1 for an item nested in it, and so on.</param>
/// <param name="Number">The printed number of an ordered item (1-based); ignored for bullets.</param>
internal sealed record RListItem(bool Ordered, int Depth, int Number, IReadOnlyList<Run> Runs) : RichBlock;

/// <summary>Rows of cells; each cell is a list of runs. The first row is the header row.</summary>
internal sealed record RTable(IReadOnlyList<IReadOnlyList<IReadOnlyList<Run>>> Rows) : RichBlock;

internal sealed record RCode(string Text, string? Language = null) : RichBlock;

internal sealed record RRule : RichBlock;

internal sealed record RichDocument(string? Title, IReadOnlyList<RichBlock> Blocks);

internal static class RichText
{
    public static string Plain(IEnumerable<Run> runs) =>
        string.Concat(runs.Select(r => r.Break ? "\n" : r.Text));

    /// <summary>True when the first strong letter of the text is right-to-left (Arabic, Hebrew): the paragraph direction a reader would pick.</summary>
    public static bool IsRtl(string text)
    {
        foreach (var c in text)
        {
            var kind = BidiOrder.KindOf(c);
            if (kind == CharKind.R) return true;
            if (kind == CharKind.L) return false;
        }
        return false;
    }

    public static bool IsRtl(IEnumerable<Run> runs) => IsRtl(Plain(runs));

    /// <summary>
    /// True when block <paramref name="index"/> belongs to the list that block index-1 belongs to. A nested item always continues; a
    /// top-level item continues only when the last top-level item before it is the same kind (bullets versus numbers).
    /// </summary>
    public static bool ContinuesList(IReadOnlyList<RichBlock> blocks, int index)
    {
        if (index == 0 || blocks[index] is not RListItem current || blocks[index - 1] is not RListItem) return false;
        if (current.Depth > 0) return true;
        for (var i = index - 1; i >= 0 && blocks[i] is RListItem earlier; i--)
            if (earlier.Depth == 0) return earlier.Ordered == current.Ordered;
        return true;
    }
}
