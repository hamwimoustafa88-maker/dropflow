using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using DropFlow.Core.Operations;
using DropFlow.Engines.Common;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;

namespace DropFlow.AI.Ocr;

/// <summary>Thrown by an <see cref="IPageRasterizer"/> for expected problems (password-protected or damaged PDF).</summary>
public sealed class PdfRenderException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Turns PDF pages into images. <c>pdf.ocr</c> needs this because Tesseract reads images, not PDFs.
/// <para>
/// The default, <see cref="WindowsPdfPageRasterizer"/>, uses the PDF renderer built into Windows 10/11 (<c>Windows.Data.Pdf</c>), so
/// no dependency on the Pdf engine project is needed. The app may bind a different implementation (for example one backed by PDFium)
/// by setting <see cref="PdfOcrConverter.Rasterizer"/> after <c>AiModule.Create</c>, or by passing one to its overload.
/// </para>
/// </summary>
public interface IPageRasterizer
{
    /// <summary>
    /// Renders every page to <c>page-0001.png</c>, <c>page-0002.png</c>... inside <paramref name="outputDirectory"/> (white background,
    /// <paramref name="dpi"/> pixels per inch) and returns the file paths in page order.
    /// </summary>
    /// <exception cref="PdfRenderException">The PDF is encrypted, damaged or has no pages.</exception>
    Task<IReadOnlyList<string>> RenderPagesAsync(string pdfPath, string outputDirectory, int dpi, IProgress<double>? progress, CancellationToken cancellationToken);
}

/// <summary>Page renderer built into Windows (<c>Windows.Data.Pdf</c>). Works with Unicode paths; cannot open password-protected files.</summary>
public sealed class WindowsPdfPageRasterizer : IPageRasterizer
{
    /// <summary>Longest side of a rendered page; keeps a poster-sized page from needing gigabytes.</summary>
    public const int MaxPixelsPerSide = 9000;

    public async Task<IReadOnlyList<string>> RenderPagesAsync(string pdfPath, string outputDirectory, int dpi, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        // The stream is opened (and closed) by us. LoadFromFileAsync would keep the file open until the garbage collector ran,
        // which would stop the user from renaming or deleting their PDF while DropFlow is still running.
        IRandomAccessStreamWithContentType stream;
        PdfDocument document;
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(pdfPath)).AsTask(cancellationToken).ConfigureAwait(false);
            stream = await file.OpenReadAsync().AsTask(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is COMException or FileNotFoundException or UnauthorizedAccessException or ArgumentException)
        {
            throw new PdfRenderException($"The PDF could not be opened: {ex.Message}", ex);
        }

        using (stream)
        {
            try
            {
                document = await PdfDocument.LoadFromStreamAsync(stream).AsTask(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is COMException or ArgumentException)
            {
                // Windows reports a wrong or missing password, and damage, as a generic COM failure.
                throw new PdfRenderException("Windows could not open the PDF (it may be password-protected or damaged).", ex);
            }
            return await RenderAllAsync(document, outputDirectory, dpi, progress, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<IReadOnlyList<string>> RenderAllAsync(PdfDocument document, string outputDirectory, int dpi, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        if (document.IsPasswordProtected) throw new PdfRenderException("The PDF is password-protected; unlock it first.");
        if (document.PageCount == 0) throw new PdfRenderException("The PDF has no pages.");

        Directory.CreateDirectory(outputDirectory);
        var paths = new List<string>();
        for (uint i = 0; i < document.PageCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var page = document.GetPage(i);

            // page.Size is in 1/96 inch units.
            var scale = dpi / 96.0;
            var width = page.Size.Width * scale;
            var height = page.Size.Height * scale;
            var limit = Math.Max(width, height);
            if (limit > MaxPixelsPerSide) { width *= MaxPixelsPerSide / limit; height *= MaxPixelsPerSide / limit; }

            var options = new PdfPageRenderOptions
            {
                DestinationWidth = (uint)Math.Max(1, Math.Round(width)),
                DestinationHeight = (uint)Math.Max(1, Math.Round(height)),
                BackgroundColor = new Windows.UI.Color { A = 255, R = 255, G = 255, B = 255 },
            };
            using var stream = new InMemoryRandomAccessStream();
            try
            {
                await page.RenderToStreamAsync(stream, options).AsTask(cancellationToken).ConfigureAwait(false);
            }
            catch (COMException ex)
            {
                throw new PdfRenderException($"Windows could not render page {i + 1}: {ex.Message}", ex);
            }

            var bytes = new byte[stream.Size];
            using (var reader = new DataReader(stream.GetInputStreamAt(0)))
            {
                await reader.LoadAsync((uint)stream.Size).AsTask(cancellationToken).ConfigureAwait(false);
                reader.ReadBytes(bytes);
            }
            var path = Path.Combine(outputDirectory, string.Create(CultureInfo.InvariantCulture, $"page-{i + 1:0000}.png"));
            await File.WriteAllBytesAsync(path, bytes, cancellationToken).ConfigureAwait(false);
            paths.Add(path);
            progress?.Report((i + 1.0) / document.PageCount);
        }
        return paths;
    }
}

/// <summary>
/// <c>pdf.ocr</c>: scanned PDF in, searchable PDF out ("name (ocr).pdf"). Pages are rendered to images, Tesseract reads them in
/// one run (<c>tesseract list.txt out -l ara+eng pdf</c>) and writes a PDF whose pages carry an invisible text layer.
/// <para>Options (keys): <c>language</c> ("auto", "en", "ar", "ar+en"...), <c>dpi</c> (150..400, default 300).</para>
/// Requires Tesseract (<see cref="EngineId.Tesseract"/>) with the traineddata for the chosen languages.
/// </summary>
public sealed class PdfOcrConverter : IConverter
{
    public const string LanguageKey = "language";
    public const string DpiKey = "dpi";

    private static readonly Regex PageLine = new(@"^Page\s+(\d+)", RegexOptions.Compiled);

    private readonly IEngineProvider _engines;
    private readonly ITesseractOcr _tesseract;

    public PdfOcrConverter(IEngineProvider engines, IPageRasterizer? rasterizer = null, ITesseractOcr? tesseract = null)
    {
        _engines = engines;
        Rasterizer = rasterizer ?? new WindowsPdfPageRasterizer();
        _tesseract = tesseract ?? new TesseractOcr(engines);
    }

    /// <summary>Page renderer. Replace it to use another PDF engine; it is read once per job.</summary>
    public IPageRasterizer Rasterizer { get; set; }

    public IReadOnlyCollection<string> Operations => [AiOperations.PdfOcr];

    public bool Supports(string operation, string inputExtension) =>
        operation == AiOperations.PdfOcr && AiCatalog.Normalize(inputExtension) == "pdf";

    public EngineId EngineFor(string operation) => EngineId.Tesseract;

    public IReadOnlyList<ToolOption> OptionsFor(string operation) =>
    [
        new(LanguageKey, ToolOptionKind.Choice, "auto", OcrConverter.LanguageChoices),
        new(DpiKey, ToolOptionKind.Number, 300, Min: 150, Max: 400),
    ];

    /// <summary>Picks the Tesseract languages for a request; null with an error message when some are not installed.</summary>
    internal static IReadOnlyList<string>? ResolveLanguages(string? requested, IReadOnlyList<string> installed, out string? error)
    {
        error = null;
        var langs = OcrLanguages.ParseList(requested, out var unknown);
        if (unknown.Count > 0) { error = $"Unknown OCR language: {string.Join(", ", unknown)}."; return null; }
        if (langs.Count == 0)
        {
            var auto = new[] { "ara", "eng" }.Where(installed.Contains).ToList();
            if (auto.Count == 0) auto = installed.Where(l => l != "osd").Take(1).ToList();
            if (auto.Count == 0) { error = "Tesseract has no language data installed."; return null; }
            return auto;
        }
        var missing = langs.Where(l => !installed.Contains(l.Tesseract)).ToList();
        if (missing.Count > 0)
        {
            error = "Tesseract lacks language data: " + string.Join(", ", missing.Select(l => $"{l.Tesseract} ({l.DisplayName})"))
                + ". Add the .traineddata files to Tesseract's tessdata folder.";
            return null;
        }
        return langs.Select(l => l.Tesseract).ToList();
    }

    public async Task<ConversionResult> RunAsync(ConversionJob job, IProgress<JobProgress> progress, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        if (job.Inputs.Count == 0) return ConversionResult.Fail("No input file.");
        var input = Path.GetFullPath(job.Inputs[0]);
        if (!File.Exists(input)) return ConversionResult.Fail($"File not found: {input}");

        var tesseract = _engines.Locate(EngineId.Tesseract);
        if (tesseract is null)
            return ConversionResult.Fail(AiFailures.MissingEngine("Tesseract", "It is needed to build the searchable PDF; install it from the engine installer."), sw.Elapsed);

        var dpi = Math.Clamp(job.GetInt(DpiKey, 300), 150, 400);
        IReadOnlyList<string>? languages;
        string? languageError;
        try
        {
            languages = ResolveLanguages(job.GetString(LanguageKey, "auto"), await _tesseract.LanguagesAsync(cancellationToken).ConfigureAwait(false), out languageError);
        }
        catch (OcrUnavailableException ex)
        {
            return ConversionResult.Fail(ex.Message, sw.Elapsed);
        }
        if (languages is null) return ConversionResult.Fail(languageError!, sw.Elapsed);

        using var work = WorkDirectory.Create(asciiOnly: true);
        try
        {
            progress.Report(new JobProgress(0.02, "Rendering pages"));
            IReadOnlyList<string> pages;
            try
            {
                var render = new Progress<double>(p => progress.Report(new JobProgress(0.02 + p * 0.38, "Rendering pages")));
                pages = await Rasterizer.RenderPagesAsync(input, work.Path, dpi, render, cancellationToken).ConfigureAwait(false);
            }
            catch (PdfRenderException ex)
            {
                return ConversionResult.Fail(ex.Message, sw.Elapsed);
            }

            var list = work.File("pages.txt");
            await File.WriteAllLinesAsync(list, pages, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            var outputBase = work.File("searchable");

            progress.Report(new JobProgress(0.4, "Recognising text"));
            var run = await ProcessRunner.RunAsync(
                tesseract, TesseractOcr.BuildPdfArguments(list, outputBase, languages, dpi),
                new ProcessOptions
                {
                    WorkingDirectory = work.Path,
                    Timeout = TimeSpan.FromHours(3),
                    OnErrorLine = line => ReportPage(line, pages.Count, progress),
                    OnOutputLine = line => ReportPage(line, pages.Count, progress),
                },
                cancellationToken).ConfigureAwait(false);
            if (run.Cancelled) throw new OperationCanceledException(cancellationToken);
            if (run.TimedOut) return ConversionResult.Fail("Tesseract took too long.", sw.Elapsed);

            var produced = outputBase + ".pdf";
            if (!run.Success || !File.Exists(produced) || !LooksLikePdf(produced))
                return ConversionResult.Fail("Tesseract failed to build the PDF: " + run.LastMessage, sw.Elapsed);

            var destination = OutputNaming.ForOne(input, "pdf", "ocr", job.OutputDirectory);
            File.Move(produced, destination, overwrite: false);
            progress.Report(new JobProgress(1, "Done"));
            return ConversionResult.Ok([destination], sw.Elapsed);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ConversionResult.Fail("Could not write the searchable PDF: " + ex.Message, sw.Elapsed);
        }
    }

    private static void ReportPage(string line, int total, IProgress<JobProgress> progress)
    {
        var m = PageLine.Match(line);
        if (m.Success && int.TryParse(m.Groups[1].Value, out var page) && total > 0)
            progress.Report(new JobProgress(0.4 + 0.58 * Math.Clamp((page - 1.0) / total, 0, 1), $"Recognising page {page} of {total}"));
    }

    private static bool LooksLikePdf(string path)
    {
        using var stream = File.OpenRead(path);
        Span<byte> head = stackalloc byte[5];
        return stream.Read(head) == 5 && head.SequenceEqual("%PDF-"u8);
    }
}
