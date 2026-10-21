using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using DropFlow.Engines.Pdf.Infrastructure;

namespace DropFlow.Engines.Pdf.Office;

/// <summary>
/// Reads the text content of a .docx: headings (by style or outline level), real lists (by numbering definition), tables, and the
/// bold / italic / monospace / hyperlink formatting of runs. Pictures, headers, footers, footnotes and comments are not read.
/// </summary>
internal static partial class DocxReader
{
    [GeneratedRegex(@"^heading\s*([1-9])$", RegexOptions.IgnoreCase)]
    private static partial Regex HeadingName();

    private sealed record StyleInfo(string Id, string Name, string? BasedOn, int? NumId, int? NumLevel, int? OutlineLevel);

    public static RichDocument Read(string path)
    {
        var name = Path.GetFileName(path);
        try
        {
            using var stream = new MemoryStream(File.ReadAllBytes(path));
            using var package = WordprocessingDocument.Open(stream, false);
            var main = package.MainDocumentPart ?? throw new ConversionFailure($"{name} has no document body.");
            var context = new ReadContext(main);

            var blocks = new List<RichBlock>();
            var body = main.Document?.Body ?? throw new ConversionFailure($"{name} has no document body.");
            ReadBody(body, blocks, context);

            var title = package.PackageProperties.Title;
            return new RichDocument(string.IsNullOrWhiteSpace(title) ? null : title.Trim(), blocks);
        }
        catch (Exception ex) when (ex is OpenXmlPackageException or InvalidDataException or FileFormatException or IOException or System.Xml.XmlException)
        {
            throw new ConversionFailure($"{name} is not a valid Word document ({ex.Message}).");
        }
    }

    private sealed class ReadContext
    {
        private readonly Dictionary<string, StyleInfo> _styles = new(StringComparer.Ordinal);
        private readonly Dictionary<(int NumId, int Level), bool> _ordered = [];
        private readonly Dictionary<(int NumId, int Level), int> _counters = [];
        private readonly Dictionary<(int NumId, int Level), int> _starts = [];

        public ReadContext(MainDocumentPart main)
        {
            Main = main;
            foreach (var style in main.StyleDefinitionsPart?.Styles?.Elements<Style>() ?? [])
            {
                var id = style.StyleId?.Value;
                if (id is null) continue;
                var numPr = style.StyleParagraphProperties?.NumberingProperties;
                _styles[id] = new StyleInfo(
                    id,
                    style.StyleName?.Val?.Value ?? id,
                    style.BasedOn?.Val?.Value,
                    numPr?.NumberingId?.Val?.Value,
                    numPr?.NumberingLevelReference?.Val?.Value,
                    style.StyleParagraphProperties?.OutlineLevel?.Val?.Value);
            }

            var numbering = main.NumberingDefinitionsPart?.Numbering;
            if (numbering is null) return;
            var abstracts = numbering.Elements<AbstractNum>().ToDictionary(a => a.AbstractNumberId?.Value ?? -1);
            foreach (var instance in numbering.Elements<NumberingInstance>())
            {
                var numId = instance.NumberID?.Value;
                var abstractId = instance.AbstractNumId?.Val?.Value;
                if (numId is null || abstractId is null || !abstracts.TryGetValue(abstractId.Value, out var abs)) continue;
                foreach (var level in abs.Elements<Level>())
                {
                    var format = level.NumberingFormat?.Val?.Value;
                    var key = (numId.Value, level.LevelIndex?.Value ?? 0);
                    _ordered[key] = format is not null && format != NumberFormatValues.Bullet && format != NumberFormatValues.None;
                    _starts[key] = level.StartNumberingValue?.Val?.Value ?? 1;
                }
                foreach (var over in instance.Elements<LevelOverride>())
                {
                    if (over.StartOverrideNumberingValue?.Val?.Value is { } start)
                        _starts[(numId.Value, over.LevelIndex?.Value ?? 0)] = start;
                }
            }
        }

        public MainDocumentPart Main { get; }

        public StyleInfo? Style(string? id) => id is not null && _styles.TryGetValue(id, out var s) ? s : null;

        public bool IsOrdered(int numId, int level) => _ordered.TryGetValue((numId, level), out var o) ? o : _ordered.TryGetValue((numId, 0), out var first) && first;

        public int NextNumber(int numId, int level)
        {
            foreach (var key in _counters.Keys.Where(k => k.NumId == numId && k.Level > level).ToList()) _counters.Remove(key);
            _counters[(numId, level)] = _counters.TryGetValue((numId, level), out var current) ? current + 1 : _starts.GetValueOrDefault((numId, level), 1);
            return _counters[(numId, level)];
        }
    }

    // ---- blocks ---------------------------------------------------------------------------------------------------------

    private static void ReadBody(OpenXmlElement container, List<RichBlock> blocks, ReadContext context)
    {
        foreach (var child in container.ChildElements)
        {
            switch (child)
            {
                case Paragraph paragraph:
                    ReadParagraph(paragraph, blocks, context);
                    break;
                case Table table:
                    blocks.Add(ReadTable(table, context));
                    break;
                case SdtBlock sdt:
                    if (sdt.SdtContentBlock is { } content) ReadBody(content, blocks, context);
                    break;
                case CustomXmlBlock custom:
                    ReadBody(custom, blocks, context);
                    break;
            }
        }
    }

    private static void ReadParagraph(Paragraph paragraph, List<RichBlock> blocks, ReadContext context)
    {
        var runs = ReadRuns(paragraph, context);
        var hasText = runs.Any(r => r.Break || r.Text.Trim().Length > 0);

        var styleId = paragraph.ParagraphProperties?.ParagraphStyleId?.Val?.Value;
        var style = context.Style(styleId);

        var numPr = paragraph.ParagraphProperties?.NumberingProperties;
        var numId = numPr?.NumberingId?.Val?.Value ?? InheritedNumId(style, context);
        var level = numPr?.NumberingLevelReference?.Val?.Value ?? InheritedLevel(style, context) ?? 0;

        var heading = HeadingLevel(paragraph, style, context);
        if (heading > 0 && hasText)
        {
            blocks.Add(new RHeading(heading, Trim(runs)));
            return;
        }

        if (numId is > 0 && hasText)
        {
            var ordered = context.IsOrdered(numId.Value, level);
            var number = ordered ? context.NextNumber(numId.Value, level) : 1;
            blocks.Add(new RListItem(ordered, Math.Clamp(level, 0, 8), number, Trim(runs)));
            return;
        }

        if (hasText)
        {
            if (IsCodeStyle(style)) blocks.Add(new RCode(RichText.Plain(runs)));
            else blocks.Add(new RParagraph(Trim(runs)));
        }
    }

    private static int HeadingLevel(Paragraph paragraph, StyleInfo? style, ReadContext context)
    {
        for (var s = style; s is not null; s = s.BasedOn is null ? null : context.Style(s.BasedOn))
        {
            var match = HeadingName().Match(s.Name);
            if (match.Success) return int.Parse(match.Groups[1].Value);
            if (s.Name.Equals("Title", StringComparison.OrdinalIgnoreCase)) return 1;
            if (s.OutlineLevel is { } outline and < 9) return outline + 1;
        }

        var direct = paragraph.ParagraphProperties?.OutlineLevel?.Val?.Value;
        return direct is { } d and < 9 ? d + 1 : 0;
    }

    private static int? InheritedNumId(StyleInfo? style, ReadContext context)
    {
        for (var s = style; s is not null; s = s.BasedOn is null ? null : context.Style(s.BasedOn))
            if (s.NumId is { } id) return id;
        return null;
    }

    private static int? InheritedLevel(StyleInfo? style, ReadContext context)
    {
        for (var s = style; s is not null; s = s.BasedOn is null ? null : context.Style(s.BasedOn))
            if (s.NumLevel is { } level) return level;
        return null;
    }

    private static bool IsCodeStyle(StyleInfo? style) =>
        style is not null && (style.Name.Equals("Source Code", StringComparison.OrdinalIgnoreCase) || style.Name.Equals("Code", StringComparison.OrdinalIgnoreCase) || style.Name.Equals("Preformatted Text", StringComparison.OrdinalIgnoreCase));

    private static RTable ReadTable(Table table, ReadContext context)
    {
        var rows = new List<IReadOnlyList<IReadOnlyList<Run>>>();
        foreach (var row in table.Elements<TableRow>())
        {
            var cells = new List<IReadOnlyList<Run>>();
            foreach (var cell in row.Elements<TableCell>())
            {
                var runs = new List<Run>();
                foreach (var paragraph in cell.Elements<Paragraph>())
                {
                    var paragraphRuns = Trim(ReadRuns(paragraph, context));
                    if (paragraphRuns.Count == 0) continue;
                    if (runs.Count > 0) runs.Add(Run.LineBreak);
                    runs.AddRange(paragraphRuns);
                }
                cells.Add(runs);
            }
            if (cells.Count > 0) rows.Add(cells);
        }
        return new RTable(rows);
    }

    // ---- runs -----------------------------------------------------------------------------------------------------------

    private static List<Run> ReadRuns(OpenXmlElement paragraph, ReadContext context)
    {
        var runs = new List<Run>();
        Walk(paragraph, href: null, runs, context);
        return MarkdownReader.Merge(runs);
    }

    private static void Walk(OpenXmlElement parent, string? href, List<Run> runs, ReadContext context)
    {
        foreach (var child in parent.ChildElements)
        {
            switch (child)
            {
                case DocumentFormat.OpenXml.Wordprocessing.Run run:
                    ReadRun(run, href, runs);
                    break;

                case Hyperlink link:
                    string? target = null;
                    if (link.Id?.Value is { } relationshipId)
                        target = context.Main.HyperlinkRelationships.FirstOrDefault(r => r.Id == relationshipId)?.Uri?.ToString();
                    Walk(link, target ?? href, runs, context);
                    break;

                case ParagraphProperties or DeletedRun or MoveFromRun or BookmarkStart or BookmarkEnd:
                    break;

                case OpenXmlCompositeElement composite:
                    Walk(composite, href, runs, context);
                    break;
            }
        }
    }

    private static void ReadRun(DocumentFormat.OpenXml.Wordprocessing.Run run, string? href, List<Run> runs)
    {
        var properties = run.RunProperties;
        var bold = On(properties?.Bold) || properties?.RunStyle?.Val?.Value is "Strong";
        var italic = On(properties?.Italic) || properties?.RunStyle?.Val?.Value is "Emphasis";
        var fonts = properties?.RunFonts;
        var font = fonts?.Ascii?.Value ?? fonts?.HighAnsi?.Value ?? "";
        var code = font.Contains("Consolas", StringComparison.OrdinalIgnoreCase) || font.Contains("Courier", StringComparison.OrdinalIgnoreCase)
                   || font.Contains("Mono", StringComparison.OrdinalIgnoreCase);

        foreach (var element in run.ChildElements)
        {
            switch (element)
            {
                case Text text:
                    runs.Add(new Run(text.Text, bold, italic, code, href));
                    break;
                case Break b when b.Type?.Value != BreakValues.Page && b.Type?.Value != BreakValues.Column:
                    runs.Add(Run.LineBreak);
                    break;
                case CarriageReturn:
                    runs.Add(Run.LineBreak);
                    break;
                case TabChar:
                    runs.Add(new Run("\t", bold, italic, code, href));
                    break;
                case NoBreakHyphen:
                    runs.Add(new Run("-", bold, italic, code, href));
                    break;
            }
        }
    }

    private static bool On(OnOffType? element) => element is not null && (element.Val is null || element.Val.Value);

    private static List<Run> Trim(List<Run> runs)
    {
        var result = runs.ToList();
        while (result.Count > 0 && result[^1].Break) result.RemoveAt(result.Count - 1);
        while (result.Count > 0 && result[0].Break) result.RemoveAt(0);
        if (result.Count > 0 && !result[0].Break) result[0] = result[0] with { Text = result[0].Text.TrimStart() };
        if (result.Count > 0 && !result[^1].Break) result[^1] = result[^1] with { Text = result[^1].Text.TrimEnd() };
        return result.Where(r => r.Break || r.Text.Length > 0).ToList();
    }
}
