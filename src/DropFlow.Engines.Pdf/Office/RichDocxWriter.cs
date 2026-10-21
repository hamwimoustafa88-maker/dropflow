using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using DropFlow.Engines.Pdf.Export;

namespace DropFlow.Engines.Pdf.Office;

/// <summary>Writes the rich model (from Markdown or plain text) as a .docx: heading styles, nested real lists, tables, links, code, quotes, RTL paragraphs.</summary>
internal static class RichDocxWriter
{
    private const int BulletNumberId = 1;

    public static void Write(RichDocument document, string path, string title, string language)
    {
        using var package = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = package.AddMainDocumentPart();
        var body = new Body();
        main.Document = new Document(body);

        var ordered = new List<(int Id, int Start)>();
        var nextNumberId = 2;
        var instanceByDepth = new Dictionary<int, int>();
        var blocks = document.Blocks;

        for (var i = 0; i < blocks.Count; i++)
        {
            switch (blocks[i])
            {
                case RHeading h:
                    body.Append(MakeParagraph(h.Runs, main, style: $"Heading{Math.Clamp(h.Level, 1, 6)}"));
                    break;

                case RParagraph p:
                    body.Append(MakeParagraph(p.Runs, main, style: p.Quote ? "Quote" : null));
                    break;

                case RListItem item:
                    if (i == 0 || blocks[i - 1] is not RListItem) instanceByDepth.Clear();
                    int numberId;
                    if (!item.Ordered)
                    {
                        numberId = BulletNumberId;
                    }
                    else
                    {
                        var depth = Math.Clamp(item.Depth, 0, 2);
                        foreach (var deeper in instanceByDepth.Keys.Where(k => k > depth).ToList()) instanceByDepth.Remove(deeper);
                        if (item.Number == 1 || !instanceByDepth.TryGetValue(depth, out numberId))
                        {
                            numberId = nextNumberId++;
                            ordered.Add((numberId, item.Number));
                            instanceByDepth[depth] = numberId;
                        }
                    }
                    body.Append(MakeParagraph(item.Runs, main, style: "ListParagraph", numbering: (numberId, Math.Clamp(item.Depth, 0, 2))));
                    break;

                case RTable t:
                    if (t.Rows.Count > 0)
                    {
                        body.Append(BuildTable(t, main));
                        body.Append(new Paragraph()); // keeps Word happy between a table and what follows
                    }
                    break;

                case RCode c:
                    foreach (var line in c.Text.Replace("\r\n", "\n").Split('\n'))
                        body.Append(MakeParagraph([new Run(line, Code: true)], main, style: "Code", forceLtr: true));
                    break;

                case RRule:
                    body.Append(new Paragraph(new ParagraphProperties(new ParagraphBorders(
                        new BottomBorder { Val = BorderValues.Single, Size = 6, Space = 1, Color = "999999" }))));
                    break;
            }
        }

        if (!body.HasChildren) body.Append(new Paragraph());
        body.Append(new SectionProperties(
            new PageSize { Width = 11906u, Height = 16838u }, // A4
            new PageMargin { Top = 1440, Right = 1440u, Bottom = 1440, Left = 1440u, Header = 708u, Footer = 708u, Gutter = 0u }));

        main.AddNewPart<StyleDefinitionsPart>().Styles = DocxWriter.BuildStyles(language);
        main.AddNewPart<NumberingDefinitionsPart>().Numbering = DocxWriter.BuildNumbering(ordered);

        package.PackageProperties.Title = title;
        package.PackageProperties.Creator = "DropFlow";
        package.PackageProperties.Created = DateTime.UtcNow;
        package.PackageProperties.Modified = DateTime.UtcNow;
    }

    private static Paragraph MakeParagraph(
        IReadOnlyList<Run> runs, MainDocumentPart main, string? style = null, (int Id, int Level)? numbering = null, bool forceLtr = false)
    {
        var rtl = !forceLtr && RichText.IsRtl(runs);
        var properties = new ParagraphProperties();
        if (style is not null) properties.Append(new ParagraphStyleId { Val = style });
        if (numbering is { } n) properties.Append(new NumberingProperties(new NumberingLevelReference { Val = n.Level }, new NumberingId { Val = n.Id }));
        if (rtl) properties.Append(new BiDi());

        var paragraph = new Paragraph(properties);
        foreach (var run in runs) AppendRun(paragraph, run, rtl, main);
        return paragraph;
    }

    private static void AppendRun(Paragraph paragraph, Run run, bool paragraphRtl, MainDocumentPart main)
    {
        if (run.Break)
        {
            paragraph.Append(new DocumentFormat.OpenXml.Wordprocessing.Run(new Break()));
            return;
        }

        var runsToAdd = new List<OpenXmlElement>();
        foreach (var piece in SplitControl(run.Text))
        {
            var properties = new RunProperties();
            if (run.Href is not null) properties.Append(new RunStyle { Val = "Hyperlink" });
            if (run.Code) properties.Append(new RunFonts { Ascii = "Consolas", HighAnsi = "Consolas", ComplexScript = "Consolas" });
            if (run.Bold) { properties.Append(new Bold()); properties.Append(new BoldComplexScript()); }
            if (run.Italic) { properties.Append(new Italic()); properties.Append(new ItalicComplexScript()); }
            if (paragraphRtl || Infrastructure.ArabicShaper.ContainsRtl(piece)) properties.Append(new RightToLeftText());

            var element = new DocumentFormat.OpenXml.Wordprocessing.Run(properties);
            switch (piece)
            {
                case "\t": element.Append(new TabChar()); break;
                case "\n": element.Append(new Break()); break;
                default: element.Append(new Text(piece) { Space = SpaceProcessingModeValues.Preserve }); break;
            }
            runsToAdd.Add(element);
        }

        if (run.Href is { Length: > 0 } href && Uri.TryCreate(href, UriKind.RelativeOrAbsolute, out var uri) && IsSafe(href))
        {
            var relationship = main.AddHyperlinkRelationship(uri, isExternal: true);
            var link = new Hyperlink { Id = relationship.Id };
            foreach (var element in runsToAdd) link.Append(element);
            paragraph.Append(link);
        }
        else
        {
            foreach (var element in runsToAdd) paragraph.Append(element);
        }
    }

    private static bool IsSafe(string href)
    {
        var colon = href.IndexOf(':');
        return colon < 0 || href[..colon].ToLowerInvariant() is "http" or "https" or "mailto" or "tel" or "ftp";
    }

    /// <summary>Splits text at tabs and line feeds so each becomes the proper Word element.</summary>
    private static IEnumerable<string> SplitControl(string text)
    {
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] is not ('\t' or '\n')) continue;
            if (i > start) yield return text[start..i];
            yield return text[i].ToString();
            start = i + 1;
        }
        if (start < text.Length) yield return text[start..];
    }

    private static Table BuildTable(RTable table, MainDocumentPart main)
    {
        var columns = Math.Max(1, table.Rows.Max(r => r.Count));
        var rtl = table.Rows.SelectMany(r => r).Any(c => RichText.IsRtl(c)) && RichText.IsRtl(table.Rows[0].FirstOrDefault() ?? []);
        var properties = new TableProperties(new TableStyle { Val = "TableGrid" });
        if (rtl) properties.Append(new BiDiVisual());
        properties.Append(new TableWidth { Width = "5000", Type = TableWidthUnitValues.Pct });

        var result = new Table(properties);
        var grid = new TableGrid();
        for (var c = 0; c < columns; c++) grid.Append(new GridColumn { Width = (9000 / columns).ToString() });
        result.Append(grid);

        for (var r = 0; r < table.Rows.Count; r++)
        {
            var row = new TableRow();
            for (var c = 0; c < columns; c++)
            {
                var runs = c < table.Rows[r].Count ? table.Rows[r][c] : [];
                if (r == 0) runs = runs.Select(x => x with { Bold = true }).ToList();
                row.Append(new TableCell(
                    new TableCellProperties(new TableCellWidth { Width = (9000 / columns).ToString(), Type = TableWidthUnitValues.Dxa }),
                    MakeParagraph(runs, main)));
            }
            result.Append(row);
        }
        return result;
    }
}
