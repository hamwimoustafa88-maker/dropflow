using DocumentFormat.OpenXml.Packaging;
using DropFlow.Engines.Pdf.Extraction;
using DropFlow.TestSupport;
using ImageMagick;

namespace DropFlow.Engines.Pdf.Tests;

/// <summary>The scan reader is process-wide, so these tests never run next to the others.</summary>
[CollectionDefinition(nameof(ScannedPdfCollection), DisableParallelization = true)]
public sealed class ScannedPdfCollection;

/// <summary>
/// Owner request: converting a scanned PDF said "run OCR first". The text and export tools now read a scan through the OCR reader
/// DropFlow.AI registers (real Tesseract is exercised by the conversion matrix); here a fake reader stands in for it.
/// </summary>
[Collection(nameof(ScannedPdfCollection))]
public class ScannedPdfTests
{
    private sealed class FakeReader(string searchablePdf) : IScannedPdfReader
    {
        public int Calls { get; private set; }

        public Task<string> MakeSearchableAsync(string pdf, string folder, IProgress<double> progress, CancellationToken cancellationToken)
        {
            Calls++;
            var copy = Path.Combine(folder, "searchable.pdf");
            File.Copy(searchablePdf, copy);
            progress.Report(1);
            return Task.FromResult(copy);
        }
    }

    private sealed class BrokenReader : IScannedPdfReader
    {
        public Task<string> MakeSearchableAsync(string pdf, string folder, IProgress<double> progress, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Tesseract is not installed.");
    }

    private static string Scan(TempDir dir)
    {
        var photo = dir.Combine("scan.png");
        using (var image = new MagickImage(MagickColors.White, 600, 800)) image.Write(photo, MagickFormat.Png24);
        var b = new PdfBuilder();
        b.AddPage().Image(photo, 0, 0, 612, 792);
        return b.Save(dir.Combine("scan.pdf"));
    }

    private static string TextPdf(TempDir dir)
    {
        var b = new PdfBuilder();
        b.AddPage().Arabic("مرحبا بالعالم", 540, 100, 16).Text("Invoice 4821", 72, 140);
        return b.Save(dir.Combine("ocr-result.pdf"));
    }

    private static async Task<T> With<T>(IScannedPdfReader? reader, Func<Task<T>> body)
    {
        var before = ScannedPdf.Reader;
        ScannedPdf.Reader = reader;
        try { return await body(); }
        finally { ScannedPdf.Reader = before; }
    }

    [Theory]
    [InlineData("convert:txt")]
    [InlineData("convert:md")]
    [InlineData("convert:html")]
    [InlineData("pdf.extract-text")]
    public async Task A_scan_becomes_text_through_ocr_and_says_so(string operation)
    {
        using var dir = new TempDir();
        var scan = Scan(dir);
        var reader = new FakeReader(TextPdf(dir));

        var result = await With(reader, () => Kit.Ok(Kit.RunAsync(operation, [scan])));

        Assert.Equal(1, reader.Calls);
        Assert.Equal(ScannedPdf.ReadWithOcrNote, result.Note);
        var text = string.Concat(result.Outputs.Select(File.ReadAllText));
        Assert.Contains("مرحبا بالعالم", text);
        Assert.Contains("Invoice 4821", text);
    }

    [Fact]
    public async Task A_scan_becomes_a_word_file_with_the_ocr_text()
    {
        using var dir = new TempDir();
        var scan = Scan(dir);

        var result = await With(new FakeReader(TextPdf(dir)), () => Kit.Ok(Kit.RunAsync("convert:docx", [scan])));

        using var doc = WordprocessingDocument.Open(result.Outputs[0], false);
        Assert.Contains("مرحبا بالعالم", doc.MainDocumentPart!.Document!.InnerText);
        Assert.Equal(ScannedPdf.ReadWithOcrNote, result.Note);
    }

    [Fact]
    public async Task A_pdf_with_text_never_calls_ocr()
    {
        using var dir = new TempDir();
        var text = TextPdf(dir);
        var reader = new FakeReader(text);

        var result = await With(reader, () => Kit.Ok(Kit.RunAsync("convert:txt", [text])));

        Assert.Equal(0, reader.Calls);
        Assert.Null(result.Note);
    }

    [Fact]
    public async Task Without_ocr_the_scan_is_refused_with_the_reason()
    {
        using var dir = new TempDir();
        var scan = Scan(dir);

        var none = await With(null, () => Kit.RunAsync("convert:txt", [scan]));
        Assert.False(none.Success);
        Assert.Contains("no text layer", none.Error);

        var broken = await With(new BrokenReader(), () => Kit.RunAsync("convert:docx", [scan]));
        Assert.False(broken.Success);
        Assert.Contains("Tesseract is not installed", broken.Error);
    }
}
