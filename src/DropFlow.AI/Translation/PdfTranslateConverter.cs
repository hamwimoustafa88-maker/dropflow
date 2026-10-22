using System.Diagnostics;
using System.Text;
using DropFlow.Core.Operations;
using DropFlow.Engines.Pdf.Extraction;

namespace DropFlow.AI.Translation;

/// <summary>
/// <c>pdf.translate</c>: reads the text layer of a PDF (reading order, headings, lists, tables), translates it between English and Arabic on
/// this PC and writes "name (translated).txt" (and ".md" when asked). The PDF itself is not changed and no layout is reproduced.
/// A scanned PDF has no text layer: run "Make PDF searchable (OCR)" first.
/// <para>Options (keys): <c>direction</c> (auto | en-ar | ar-en), <c>quality</c> (fast | balanced | best), <c>outputs</c> (txt | md | "txt,md").</para>
/// </summary>
public sealed class PdfTranslateConverter : IConverter
{
    public const string Operation = "pdf.translate";
    public const string OutputsKey = "outputs";

    private readonly ITextTranslator _translator;
    private readonly TimeSpan _timeout;

    public PdfTranslateConverter(ITextTranslator translator, TimeSpan? timeout = null)
    {
        _translator = translator;
        _timeout = timeout ?? TranslationJobSupport.DefaultTimeout;
    }

    public IReadOnlyCollection<string> Operations => [Operation];

    public bool Supports(string operation, string inputExtension) =>
        operation == Operation && AiCatalog.Normalize(inputExtension) == "pdf";

    public EngineId EngineFor(string operation) => EngineId.None;

    public IReadOnlyList<ToolOption> OptionsFor(string operation) =>
    [
        .. TranslationJobSupport.CommonOptions(),
        new(OutputsKey, ToolOptionKind.Choice, "txt", ["txt", "md", "txt,md"]),
    ];

    public async Task<ConversionResult> RunAsync(ConversionJob job, IProgress<JobProgress> progress, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        if (job.Inputs.Count == 0) return ConversionResult.Fail("No input file.");
        var input = Path.GetFullPath(job.Inputs[0]);
        if (!File.Exists(input)) return ConversionResult.Fail($"File not found: {input}");
        if (!Supports(job.Operation, Path.GetExtension(input).TrimStart('.')))
            return ConversionResult.Fail($"'{Path.GetExtension(input)}' files are not supported by {job.Operation}.");

        var wantMd = false;
        var wantTxt = false;
        foreach (var part in (job.GetString(OutputsKey, "txt") ?? "txt").Split([',', '+', ' '], StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.Equals("md", StringComparison.OrdinalIgnoreCase)) wantMd = true; else wantTxt = true;
        }
        if (!wantMd) wantTxt = true;

        progress.Report(new JobProgress(0.02, "Reading the PDF text"));
        PdfTextReadResult read;
        try
        {
            read = await Task.Run(() => PdfTextReader.Read(input, cancellationToken, p => progress.Report(new JobProgress(0.02 + p * 0.1, "Reading the PDF text"))), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ConversionResult.Fail("Could not read the PDF: " + ex.Message, sw.Elapsed);
        }
        if (read.Error is not null) return ConversionResult.Fail(read.Error, sw.Elapsed);
        if (!read.HasText || read.Blocks.Count == 0)
            return ConversionResult.Fail("This PDF has no text layer (it looks scanned). Run \"Make PDF searchable (OCR)\" first, then translate the result.", sw.Elapsed);

        // One unit per paragraph, heading, list item and table cell.
        var units = new List<string>();
        foreach (var b in read.Blocks)
        {
            if (b.Cells is { } cells) units.AddRange(cells);
            else units.Add(b.Text);
        }

        var (chosen, error) = TranslationJobSupport.ChooseDirection(job.GetString(TranslationJobSupport.DirectionKey), units);
        if (chosen is null) return ConversionResult.Fail(error!, sw.Elapsed);
        var direction = chosen.Direction;
        if (!TranslationJobSupport.AnyToTranslate(units, direction))
            return ConversionResult.Fail(TranslationJobSupport.NothingToTranslate(direction), sw.Elapsed);

        var quality = TranslationInfo.ParseQuality(job.GetString(TranslationJobSupport.QualityKey));
        var (translations, failure) = await TranslationJobSupport.TranslateAsync(
            _translator, direction, units, quality, progress, 0.12, 0.92, "Translating", _timeout, cancellationToken).ConfigureAwait(false);
        if (translations is null) return ConversionResult.Fail(failure!, sw.Elapsed);

        // Pair the translations back with the blocks.
        var translated = new List<PdfTextBlock>();
        var next = 0;
        foreach (var b in read.Blocks)
        {
            if (b.Cells is { } cells)
            {
                var row = cells.Select(_ => translations[next++]).ToList();
                translated.Add(b with { Text = string.Join("\t", row), Cells = row });
            }
            else translated.Add(b with { Text = translations[next++] });
        }

        progress.Report(new JobProgress(0.94, "Writing the files"));
        var extensions = new List<string>();
        if (wantTxt) extensions.Add("txt");
        if (wantMd) extensions.Add("md");
        var paths = OutputNaming.ForSet(input, extensions, "translated", job.OutputDirectory);
        var written = new List<string>();
        try
        {
            for (var i = 0; i < paths.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                OutputNaming.WriteNew(paths[i], extensions[i] == "md" ? ToMarkdown(translated) : ToText(translated));
                written.Add(paths[i]);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or UnauthorizedAccessException)
        {
            foreach (var p in written) { try { File.Delete(p); } catch (IOException) { } }
            if (ex is OperationCanceledException) throw;
            return ConversionResult.Fail("Could not write the translated text: " + ex.Message, sw.Elapsed);
        }
        progress.Report(new JobProgress(1, "Done"));
        var note = TranslationJobSupport.NoteFor(chosen, TranslationJobSupport.CountSkipped(units, direction));
        return ConversionResult.Ok(written, sw.Elapsed, note.Length == 0 ? null : note);
    }

    internal static string ToText(IReadOnlyList<PdfTextBlock> blocks)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < blocks.Count; i++)
        {
            if (i > 0) sb.Append(ContinuesList(blocks, i) || (blocks[i].Kind == PdfTextBlockKind.TableRow && blocks[i - 1].Kind == PdfTextBlockKind.TableRow) ? "\n" : "\n\n");
            var b = blocks[i];
            sb.Append(b.Kind == PdfTextBlockKind.ListItem ? (b.Marker.Length > 0 ? b.Marker : "\u2022") + " " + b.Text : b.Text);
        }
        return sb.ToString().Trim() + "\n";
    }

    internal static string ToMarkdown(IReadOnlyList<PdfTextBlock> blocks)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < blocks.Count; i++)
        {
            var b = blocks[i];
            if (i > 0) sb.Append(ContinuesList(blocks, i) || (b.Kind == PdfTextBlockKind.TableRow && blocks[i - 1].Kind == PdfTextBlockKind.TableRow) ? "\n" : "\n\n");
            switch (b.Kind)
            {
                case PdfTextBlockKind.Heading: sb.Append(new string('#', Math.Clamp(b.HeadingLevel, 1, 6))).Append(' ').Append(Escape(b.Text)); break;
                case PdfTextBlockKind.ListItem: sb.Append("- ").Append(Escape(b.Text)); break;
                case PdfTextBlockKind.TableRow:
                    var cells = b.Cells ?? [b.Text];
                    sb.Append("| ").Append(string.Join(" | ", cells.Select(c => Escape(c).Replace("|", "\\|").Replace("\n", " ")))).Append(" |");
                    // A table's first row is its header.
                    if (i == 0 || blocks[i - 1].Kind != PdfTextBlockKind.TableRow)
                        sb.Append("\n| ").Append(string.Join(" | ", cells.Select(_ => "---"))).Append(" |");
                    break;
                default: sb.Append(Escape(b.Text)); break;
            }
        }
        return sb.ToString().Trim() + "\n";
    }

    private static bool ContinuesList(IReadOnlyList<PdfTextBlock> blocks, int i) =>
        blocks[i].Kind == PdfTextBlockKind.ListItem && blocks[i - 1].Kind == PdfTextBlockKind.ListItem;

    private static string Escape(string text)
    {
        var sb = new StringBuilder(text.Length + 4);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c is '\\' or '*' or '`' or '[' or ']' or '<' or '>' or '_') sb.Append('\\');
            else if (i == 0 && c is '#' or '+' or '-') sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }
}
