using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using DropFlow.Engines.Pdf.Extraction;

namespace DropFlow.Engines.Pdf.Export;

/// <summary>Writes the reconstructed PDF content as a Word document: heading styles, real lists, tables, right-to-left paragraphs.</summary>
internal static class DocxWriter
{
    private const int BulletNumberId = 1;
    private const string Note = "Converted from PDF by DropFlow. Layout, fonts and spacing may differ from the original PDF.";

    public static void Write(PdfTextDocument source, string path, string title, string language)
    {
        using var package = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = package.AddMainDocumentPart();
        var body = new Body();
        main.Document = new Document(body);

        var rtlDocument = source.IsMostlyRtl;
        var orderedLists = new List<int>(); // number ids of ordered lists, each restarting at 1
        var nextNumberId = 2;
        var pageBreak = false;

        foreach (var page in source.Pages)
        {
            var blocks = page.Blocks;
            var firstOnPage = true;
            for (var i = 0; i < blocks.Count; i++)
            {
                var breakBefore = pageBreak && firstOnPage;
                switch (blocks[i])
                {
                    case HeadingBlock h:
                        body.Append(TextParagraph(h.Text, h.Rtl, style: $"Heading{Math.Clamp(h.Level, 1, 6)}", pageBreakBefore: breakBefore));
                        break;
                    case ParagraphBlock p:
                        body.Append(TextParagraph(p.Text, p.Rtl, pageBreakBefore: breakBefore));
                        break;
                    case ListItemBlock item:
                        var startsList = i == 0 || blocks[i - 1] is not ListItemBlock previous || previous.Ordered != item.Ordered;
                        if (startsList && item.Ordered) orderedLists.Add(nextNumberId++);
                        var numberId = item.Ordered ? orderedLists[^1] : BulletNumberId;
                        body.Append(TextParagraph(item.Text, item.Rtl, style: "ListParagraph", numberId: numberId, pageBreakBefore: breakBefore));
                        break;
                    case TableBlock t:
                        if (breakBefore) body.Append(TextParagraph("", false, pageBreakBefore: true));
                        body.Append(BuildTable(t));
                        body.Append(TextParagraph("", false)); // Word needs a paragraph between consecutive tables and after the last one
                        break;
                }
                firstOnPage = false;
            }
            if (blocks.Count > 0) pageBreak = true;
        }

        if (!body.Elements<Paragraph>().Any() && !body.Elements<Table>().Any()) body.Append(TextParagraph("", false));
        body.Append(BuildSection(source, rtlDocument));

        main.AddNewPart<StyleDefinitionsPart>().Styles = BuildStyles(language);
        main.AddNewPart<NumberingDefinitionsPart>().Numbering = BuildNumbering(orderedLists.Select(id => (id, 1)).ToList());

        package.PackageProperties.Title = title;
        package.PackageProperties.Creator = source.Author ?? "DropFlow";
        package.PackageProperties.Description = Note;
        package.PackageProperties.Created = DateTime.UtcNow;
        package.PackageProperties.Modified = DateTime.UtcNow;
    }

    private static Paragraph TextParagraph(string text, bool rtl, string? style = null, int? numberId = null, bool pageBreakBefore = false)
    {
        var properties = new ParagraphProperties();
        if (style is not null) properties.Append(new ParagraphStyleId { Val = style });
        if (pageBreakBefore) properties.Append(new PageBreakBefore());
        if (numberId is { } id) properties.Append(new NumberingProperties(new NumberingLevelReference { Val = 0 }, new NumberingId { Val = id }));
        if (rtl) properties.Append(new BiDi());

        var run = new Run();
        if (rtl) run.Append(new RunProperties(new RightToLeftText()));
        if (text.Length > 0) run.Append(new Text(text) { Space = SpaceProcessingModeValues.Preserve });
        return new Paragraph(properties, run);
    }

    private static Table BuildTable(TableBlock block)
    {
        var columns = block.Rows.Max(r => r.Count);
        var properties = new TableProperties(new TableStyle { Val = "TableGrid" });
        if (block.Rtl) properties.Append(new BiDiVisual());
        properties.Append(new TableWidth { Width = "5000", Type = TableWidthUnitValues.Pct });

        var table = new Table(properties);
        var grid = new TableGrid();
        for (var c = 0; c < columns; c++) grid.Append(new GridColumn { Width = (9000 / columns).ToString() });
        table.Append(grid);

        foreach (var row in block.Rows)
        {
            var tableRow = new TableRow();
            for (var c = 0; c < columns; c++)
            {
                var text = c < row.Count ? row[c] : "";
                var rtl = block.Rtl || (text.Length > 0 && Infrastructure.ArabicShaper.ContainsRtl(text));
                tableRow.Append(new TableCell(
                    new TableCellProperties(new TableCellWidth { Width = (9000 / columns).ToString(), Type = TableWidthUnitValues.Dxa }),
                    TextParagraph(text, rtl)));
            }
            table.Append(tableRow);
        }
        return table;
    }

    private static SectionProperties BuildSection(PdfTextDocument source, bool rtl)
    {
        var first = source.Pages.FirstOrDefault();
        var widthPoints = first?.Width > 0 ? first.Width : 612;
        var heightPoints = first?.Height > 0 ? first.Height : 792;
        var landscape = widthPoints > heightPoints;

        var section = new SectionProperties(
            new PageSize
            {
                Width = (uint)Math.Round(widthPoints * 20),
                Height = (uint)Math.Round(heightPoints * 20),
                Orient = landscape ? PageOrientationValues.Landscape : PageOrientationValues.Portrait,
            },
            new PageMargin { Top = 1134, Right = 1134u, Bottom = 1134, Left = 1134u, Header = 708u, Footer = 708u, Gutter = 0u });
        if (rtl) section.Append(new BiDi());
        return section;
    }

    // ---- styles and numbering -------------------------------------------------------------------------------------------

    internal static Styles BuildStyles(string language)
    {
        var styles = new Styles(new DocDefaults(
            new RunPropertiesDefault(new RunPropertiesBaseStyle(
                new RunFonts { Ascii = "Arial", HighAnsi = "Arial", ComplexScript = "Arial", EastAsia = "Arial" },
                new FontSize { Val = "22" },
                new FontSizeComplexScript { Val = "22" },
                new Languages { Val = "en-US", Bidi = language == "he" ? "he-IL" : "ar-SA" })),
            new ParagraphPropertiesDefault(new ParagraphPropertiesBaseStyle(new SpacingBetweenLines { After = "120", Line = "276", LineRule = LineSpacingRuleValues.Auto }))));

        styles.Append(new Style(
            new StyleName { Val = "Normal" },
            new PrimaryStyle()) { Type = StyleValues.Paragraph, StyleId = "Normal", Default = true });

        var sizes = new[] { 40, 32, 28, 26, 24, 22 }; // half-points for Heading1..6
        for (var level = 1; level <= 6; level++)
        {
            var id = $"Heading{level}";
            styles.Append(new Style(
                new StyleName { Val = $"heading {level}" },
                new BasedOn { Val = "Normal" },
                new NextParagraphStyle { Val = "Normal" },
                new PrimaryStyle(),
                new StyleParagraphProperties(
                    new KeepNext(),
                    new KeepLines(),
                    new SpacingBetweenLines { Before = "240", After = "120" },
                    new OutlineLevel { Val = level - 1 }),
                new StyleRunProperties(
                    new Bold(),
                    new BoldComplexScript(),
                    new FontSize { Val = sizes[level - 1].ToString() },
                    new FontSizeComplexScript { Val = sizes[level - 1].ToString() }))
            { Type = StyleValues.Paragraph, StyleId = id });
        }

        styles.Append(new Style(
            new StyleName { Val = "List Paragraph" },
            new BasedOn { Val = "Normal" },
            new PrimaryStyle(),
            new StyleParagraphProperties(new SpacingBetweenLines { After = "60" }))
        { Type = StyleValues.Paragraph, StyleId = "ListParagraph" });

        styles.Append(new Style(
            new StyleName { Val = "Normal Table" },
            new UIPriority { Val = 99 },
            new SemiHidden(),
            new UnhideWhenUsed(),
            new StyleTableProperties(new TableIndentation { Width = 0, Type = TableWidthUnitValues.Dxa }))
        { Type = StyleValues.Table, StyleId = "TableNormal", Default = true });

        var border = (Func<BorderValues, TopBorder>)(v => new TopBorder { Val = v, Size = 4, Space = 0, Color = "808080" });
        styles.Append(new Style(
            new StyleName { Val = "Table Grid" },
            new BasedOn { Val = "TableNormal" },
            new PrimaryStyle(),
            new StyleTableProperties(new TableBorders(
                border(BorderValues.Single),
                new LeftBorder { Val = BorderValues.Single, Size = 4, Space = 0, Color = "808080" },
                new BottomBorder { Val = BorderValues.Single, Size = 4, Space = 0, Color = "808080" },
                new RightBorder { Val = BorderValues.Single, Size = 4, Space = 0, Color = "808080" },
                new InsideHorizontalBorder { Val = BorderValues.Single, Size = 4, Space = 0, Color = "808080" },
                new InsideVerticalBorder { Val = BorderValues.Single, Size = 4, Space = 0, Color = "808080" }),
                new TableCellMarginDefault(
                    new TopMargin { Width = "40", Type = TableWidthUnitValues.Dxa },
                    new TableCellLeftMargin { Width = 100, Type = TableWidthValues.Dxa },
                    new BottomMargin { Width = "40", Type = TableWidthUnitValues.Dxa },
                    new TableCellRightMargin { Width = 100, Type = TableWidthValues.Dxa })))
        { Type = StyleValues.Table, StyleId = "TableGrid" });

        styles.Append(new Style(
            new StyleName { Val = "Hyperlink" },
            new UIPriority { Val = 99 },
            new UnhideWhenUsed(),
            new StyleRunProperties(new Color { Val = "0563C1" }, new Underline { Val = UnderlineValues.Single }))
        { Type = StyleValues.Character, StyleId = "Hyperlink" });

        styles.Append(new Style(
            new StyleName { Val = "Code" },
            new BasedOn { Val = "Normal" },
            new PrimaryStyle(),
            new StyleParagraphProperties(
                new Shading { Val = ShadingPatternValues.Clear, Color = "auto", Fill = "F2F2F2" },
                new SpacingBetweenLines { After = "0", Line = "240", LineRule = LineSpacingRuleValues.Auto },
                new Indentation { Left = "360" }),
            new StyleRunProperties(new RunFonts { Ascii = "Consolas", HighAnsi = "Consolas", ComplexScript = "Consolas" }, new FontSize { Val = "20" }))
        { Type = StyleValues.Paragraph, StyleId = "Code" });

        styles.Append(new Style(
            new StyleName { Val = "Quote" },
            new BasedOn { Val = "Normal" },
            new PrimaryStyle(),
            new StyleParagraphProperties(
                new ParagraphBorders(new LeftBorder { Val = BorderValues.Single, Size = 18, Space = 8, Color = "BBBBBB" }),
                new Indentation { Left = "720" }),
            new StyleRunProperties(new Italic(), new ItalicComplexScript()))
        { Type = StyleValues.Paragraph, StyleId = "Quote" });

        return styles;
    }

    /// <summary>Bullet list definition (number id 1) plus one decimal definition per ordered list, each with three nesting levels.</summary>
    internal static Numbering BuildNumbering(IReadOnlyList<(int Id, int Start)> orderedLists)
    {
        var numbering = new Numbering();
        numbering.Append(AbstractList(0, bullet: true));
        numbering.Append(AbstractList(1, bullet: false));

        numbering.Append(new NumberingInstance(new AbstractNumId { Val = 0 }) { NumberID = BulletNumberId });
        foreach (var (id, start) in orderedLists)
        {
            numbering.Append(new NumberingInstance(
                new AbstractNumId { Val = 1 },
                new LevelOverride(new StartOverrideNumberingValue { Val = start }) { LevelIndex = 0 })
            { NumberID = id });
        }
        return numbering;
    }

    private static AbstractNum AbstractList(int id, bool bullet)
    {
        var abstractNum = new AbstractNum(new MultiLevelType { Val = MultiLevelValues.HybridMultilevel }) { AbstractNumberId = id };
        for (var level = 0; level < 3; level++)
        {
            abstractNum.Append(new Level(
                new StartNumberingValue { Val = 1 },
                new NumberingFormat { Val = bullet ? NumberFormatValues.Bullet : NumberFormatValues.Decimal },
                new LevelText { Val = bullet ? "•" : $"%{level + 1}." },
                new LevelJustification { Val = LevelJustificationValues.Left },
                new PreviousParagraphProperties(new Indentation { Left = (720 * (level + 1)).ToString(), Hanging = "360" }))
            { LevelIndex = level });
        }
        return abstractNum;
    }
}
