using DropFlow.Engines.Pdf.Infrastructure;

namespace DropFlow.Engines.Pdf.Extraction;

/// <summary>What kind of text a <see cref="PdfTextBlock"/> is.</summary>
public enum PdfTextBlockKind { Heading, Paragraph, ListItem, TableRow }

/// <summary>
/// A piece of the text layer of a PDF, in reading order. A table row has one entry in <see cref="Cells"/> per cell; every other block has
/// its text in <see cref="Text"/>. <see cref="Marker"/> is the bullet or number of a list item exactly as printed.
/// </summary>
public sealed record PdfTextBlock(int Page, PdfTextBlockKind Kind, string Text, string Marker = "", int HeadingLevel = 0, IReadOnlyList<string>? Cells = null);

/// <param name="Error">A message when the file could not be read (damaged, password protected, text copying not allowed); the other fields are empty then.</param>
public sealed record PdfTextReadResult(IReadOnlyList<PdfTextBlock> Blocks, bool HasText, bool IsMostlyRtl, string? Error)
{
    public int PageCount { get; init; }
}

/// <summary>Public door to the PDF text extraction (reading order, headings, lists, tables) for tools in other projects, such as translation.</summary>
public static class PdfTextReader
{
    public static PdfTextReadResult Read(string path, CancellationToken cancellationToken = default, Action<double>? progress = null)
    {
        PdfTextDocument document;
        try
        {
            document = PdfTextExtractor.Read(path, cancellationToken, progress);
        }
        catch (ConversionFailure ex)
        {
            return new PdfTextReadResult([], false, false, ex.Message);
        }

        var blocks = new List<PdfTextBlock>();
        foreach (var page in document.Pages)
            foreach (var block in page.Blocks)
            {
                switch (block)
                {
                    case HeadingBlock h: blocks.Add(new PdfTextBlock(page.Number, PdfTextBlockKind.Heading, h.Text, HeadingLevel: h.Level)); break;
                    case ParagraphBlock p: blocks.Add(new PdfTextBlock(page.Number, PdfTextBlockKind.Paragraph, p.Text)); break;
                    case ListItemBlock l: blocks.Add(new PdfTextBlock(page.Number, PdfTextBlockKind.ListItem, l.Text, l.Marker)); break;
                    case TableBlock t:
                        foreach (var row in t.Rows) blocks.Add(new PdfTextBlock(page.Number, PdfTextBlockKind.TableRow, string.Join("\t", row), Cells: row));
                        break;
                }
            }
        return new PdfTextReadResult(blocks, document.HasText, document.IsMostlyRtl, null) { PageCount = document.Pages.Count };
    }
}
