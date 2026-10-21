using DropFlow.Core.Operations;
using DropFlow.Engines.Pdf.Extraction;
using DropFlow.Engines.Pdf.Infrastructure;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Exceptions;
using Pig = UglyToad.PdfPig.PdfDocument;

namespace DropFlow.Engines.Pdf.Tools;

/// <summary>
/// <c>pdf.redact</c>: true redaction. Covering text with a black rectangle is not redaction - the text is still in the file. Here every
/// page that gets any redaction is drawn with PDFium at the chosen DPI, the boxes are burned into the pixels, and the page is rebuilt from
/// that picture, so no text, vector or font data of the original page survives. The output document is built from scratch: pages
/// without a redaction are copied over unchanged, and the document properties (title, author, subject, keywords, producer, creator,
/// dates), XMP metadata, bookmarks and attachments of the original are not carried over.
/// What gets covered is given as <c>regions</c> (percent of the page, so they do not depend on its size) and/or <c>terms</c> (words or
/// phrases found with PdfPig, case-insensitive, Arabic supported, see <see cref="PageTextIndex"/>). A term that matches nothing is
/// reported but is not an error; if nothing at all is redacted the job fails instead of writing an identical copy.
/// </summary>
internal static class PdfRedactTools
{
    private static readonly string[] Styles = ["solid", "blur"];

    /// <summary>Space added around a found word so the box covers the whole glyphs and a little of the edge (points).</summary>
    private const double TermPadding = 1.5;

    public const int MinDpi = 100;
    public const int MaxDpi = 400;

    public static readonly IReadOnlyList<ToolOption> Options =
    [
        new("regions", ToolOptionKind.Text, ""),
        new("terms", ToolOptionKind.Text, ""),
        new("dpi", ToolOptionKind.Number, 200, Min: MinDpi, Max: MaxDpi),
        new("style", ToolOptionKind.Choice, "solid", Styles),
    ];

    public static IEnumerable<OperationSpec> Specs()
    {
        yield return OperationSpec.For("pdf.redact", "pdf", ctx => Task.FromResult(Redact(ctx)), Options);
    }

    private sealed record Region(IReadOnlyList<int> Pages, double X, double Y, double Width, double Height);

    private static ConversionResult Redact(JobContext ctx)
    {
        var regionText = ctx.Job.GetString("regions");
        var termText = ctx.Job.GetString("terms");
        if (string.IsNullOrWhiteSpace(regionText) && string.IsNullOrWhiteSpace(termText))
            return ctx.Fail("Enter the areas to cover (pages:x,y,width,height in percent, for example 1:10,20,30,5) or the words to find.");

        var dpi = ctx.Job.GetDouble("dpi", 200);
        if (dpi < MinDpi || dpi > MaxDpi) return ctx.Fail($"DPI must be between {MinDpi} and {MaxDpi}.");
        var style = (ctx.Job.GetString("style", "solid") ?? "solid").Trim().ToLowerInvariant();
        if (!Styles.Contains(style)) return ctx.Fail("Style must be solid or blur.");

        var terms = ParseTerms(termText);
        var name = Path.GetFileName(ctx.Input);

        using var pdfium = PdfiumDocument.Open(ctx.Input);
        var pageCount = pdfium.PageCount;
        if (pageCount < 1) return ctx.Fail("The PDF has no pages.");
        if (!TryParseRegions(regionText, pageCount, out var regions, out var error)) return ctx.Fail(error);
        if (terms.Count > 0 && !pdfium.AllowsExtraction)
            return ctx.Fail($"{name} has an owner password that does not allow its text to be copied, so words cannot be searched for. Unlock it first (Unlock PDF), then try again.");

        // Refuses files whose owner forbade copying pages: the output is a new document made of copied pages.
        using var source = PdfSharpSupport.OpenForImport(ctx.Input);

        // ---- decide what to cover on which page --------------------------------------------------------------------------
        var boxes = new SortedDictionary<int, List<VisualBox>>();
        foreach (var region in regions)
        {
            foreach (var page in region.Pages)
            {
                var (vw, vh) = pdfium.PageSize(page - 1);
                var x0 = region.X / 100 * vw;
                var x1 = (region.X + region.Width) / 100 * vw;
                var top = region.Y / 100 * vh;
                var bottom = (region.Y + region.Height) / 100 * vh;
                Add(boxes, page, new VisualBox(x0, vh - bottom, x1, vh - top));
            }
        }

        var found = terms.ToDictionary(t => t.Display, _ => 0);
        var anyText = false;
        if (terms.Count > 0)
        {
            using var pig = OpenText(ctx.Input, name);
            for (var i = 1; i <= pageCount; i++)
            {
                ctx.Ct.ThrowIfCancellationRequested();
                ctx.Report(0.2 * (i - 1) / pageCount, $"Searching page {i}");
                var index = BuildIndex(pig, i, name);
                anyText |= index.Text.Length > 0;
                if (index.Text.Length == 0) continue;

                var (vw, vh) = pdfium.PageSize(i - 1);
                if (Math.Abs(index.PageWidth - vw) > 2 || Math.Abs(index.PageHeight - vh) > 2)
                    return ctx.Fail($"{name}, page {i}: the page size could not be read reliably, so the words cannot be placed exactly. Use regions instead.");

                foreach (var term in terms)
                {
                    foreach (var occurrence in index.Find(term.Normalised))
                    {
                        found[term.Display]++;
                        foreach (var box in occurrence)
                            Add(boxes, i, new VisualBox(box.X0 * vw / index.PageWidth, box.Y0 * vh / index.PageHeight, box.X1 * vw / index.PageWidth, box.Y1 * vh / index.PageHeight).Expand(TermPadding));
                    }
                }
            }
        }

        var missing = found.Where(kv => kv.Value == 0).Select(kv => $"'{kv.Key}'").ToList();
        if (boxes.Count == 0)
        {
            if (terms.Count == 0)
                return ctx.Fail("Enter the areas to cover (pages:x,y,width,height in percent) or the words to find.");
            if (!anyText)
                return ctx.Fail($"{name} has no searchable text (it may be a scan), so words cannot be found. Use regions instead.");
            return ctx.Fail($"Nothing was redacted: {string.Join(", ", missing)} {(missing.Count == 1 ? "was" : "were")} not found on any page.");
        }

        // ---- build the output ---------------------------------------------------------------------------------------------
        using var output = new PdfDocument();
        PdfSharpSupport.ApplyDefaultOptions(output);
        var pictures = new List<(XImage Image, MemoryStream Data)>();
        var areaCount = boxes.Values.Sum(l => l.Count);
        var downscaled = false;
        try
        {
            var done = 0;
            for (var i = 1; i <= pageCount; i++)
            {
                ctx.Ct.ThrowIfCancellationRequested();
                ctx.Report(0.2 + 0.8 * done / boxes.Count, $"Page {i}");
                if (!boxes.TryGetValue(i, out var pageBoxes))
                {
                    output.AddPage(source.Pages[i - 1]);
                    continue;
                }

                var rendered = pdfium.Render(i - 1, dpi);
                downscaled |= rendered.WasDownscaled;
                var (vw, vh) = pdfium.PageSize(i - 1);
                Burn(rendered, pageBoxes, vw, vh, style);

                var data = new MemoryStream(Encode(rendered));
                var image = XImage.FromStream(data);
                pictures.Add((image, data));

                var page = output.AddPage();
                page.Width = XUnitPt.FromPoint(vw);
                page.Height = XUnitPt.FromPoint(vh);
                using var gfx = XGraphics.FromPdfPage(page);
                gfx.DrawImage(image, 0, 0, vw, vh);
                done++;
            }

            StripMetadata(output);
            var path = ctx.ReserveFile(ctx.Input, "pdf", "redacted");
            SaveScrubbed(output, path, pageCount);
            ctx.Report(1);
            return ctx.OkWithNote(BuildNote(areaCount, boxes.Keys.ToList(), (int)dpi, missing, downscaled), path);
        }
        finally
        {
            foreach (var (image, data) in pictures)
            {
                image.Dispose();
                data.Dispose();
            }
        }
    }

    // ---- parsing --------------------------------------------------------------------------------------------------------

    private sealed record Term(string Display, string Normalised);

    private static List<Term> ParseTerms(string? text)
    {
        var terms = new List<Term>();
        if (string.IsNullOrWhiteSpace(text)) return terms;
        foreach (var raw in text.Split([',', '\u060C', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
        {
            var display = raw.Trim();
            var normalised = PageTextIndex.NormaliseTerm(display);
            if (normalised.Length == 0 || terms.Any(t => t.Normalised == normalised)) continue;
            terms.Add(new Term(display, normalised));
        }
        return terms;
    }

    private static bool TryParseRegions(string? text, int pageCount, out List<Region> regions, out string error)
    {
        regions = [];
        error = "";
        if (string.IsNullOrWhiteSpace(text)) return true;

        foreach (var entry in text.Split([';', '\u061B', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries).Select(e => e.Trim()).Where(e => e.Length > 0))
        {
            var colon = entry.IndexOf(':');
            var pagesPart = colon < 0 ? "all" : entry[..colon];
            var rectPart = colon < 0 ? entry : entry[(colon + 1)..];

            if (!PageSpec.TryParse(pagesPart, pageCount, out var pages, out var pageError))
            {
                error = $"Region '{entry}': {pageError}";
                return false;
            }

            if (!NumberList.TryParse(rectPart, 4, out var v))
            {
                error = $"Region '{entry}': write it as pages:x,y,width,height in percent of the page, for example 1:10,20,30,5.";
                return false;
            }

            double x = v[0], y = v[1], w = v[2], h = v[3];
            if (x < 0 || y < 0 || x >= 100 || y >= 100 || w <= 0 || h <= 0)
            {
                error = $"Region '{entry}': x and y must be from 0 to 99 and the width and height above 0 (all in percent of the page).";
                return false;
            }

            regions.Add(new Region(pages.Select(p => p.Page).Distinct().ToList(), x, y, Math.Min(w, 100 - x), Math.Min(h, 100 - y)));
        }
        return true;
    }

    // ---- text -----------------------------------------------------------------------------------------------------------

    private static Pig OpenText(string path, string name)
    {
        try
        {
            return Pig.Open(File.ReadAllBytes(path));
        }
        catch (PdfDocumentEncryptedException)
        {
            throw new ConversionFailure($"{name} is protected with a password. Unlock it first (Unlock PDF), then try again.");
        }
        catch (Exception ex) when (ex is PdfDocumentFormatException or InvalidOperationException or ArgumentException or NotSupportedException or IndexOutOfRangeException)
        {
            throw new ConversionFailure($"{name} is not a valid PDF or is damaged ({ex.Message}). Try Repair PDF first.");
        }
    }

    private static PageTextIndex BuildIndex(Pig document, int pageNumber, string name)
    {
        try
        {
            return PageTextIndex.Build(document.GetPage(pageNumber));
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            // If the text of a page cannot be read, a word on it could be missed: that must never pass silently in a redaction tool.
            throw new ConversionFailure($"{name}, page {pageNumber}: the text could not be read ({ex.Message}), so the words cannot be guaranteed to be covered. Use regions instead.");
        }
    }

    // ---- pixels ---------------------------------------------------------------------------------------------------------

    private static void Add(SortedDictionary<int, List<VisualBox>> boxes, int page, VisualBox box)
    {
        if (!boxes.TryGetValue(page, out var list)) boxes[page] = list = [];
        list.Add(box);
    }

    private static void Burn(RenderedPage page, List<VisualBox> boxes, double pageWidth, double pageHeight, string style)
    {
        foreach (var box in boxes)
        {
            var rect = PixelOps.ToPixels(box, pageWidth, pageHeight, page.Width, page.Height);
            if (rect.IsEmpty) continue;
            if (style == "blur") PixelOps.Mosaic(page.Bgra, page.Width, rect, (int)Math.Clamp(0.6 * rect.Height, 10, 80));
            else PixelOps.FillBlack(page.Bgra, page.Width, rect);
        }
    }

    /// <summary>Lossless PNG for ordinary pages; JPEG only when the PNG is large (photographs) and the JPEG is clearly smaller.</summary>
    private static byte[] Encode(RenderedPage page)
    {
        var png = PixelOps.ToPng(page.Bgra, page.Width, page.Height);
        if (png.Length <= 1_000_000) return png;
        var jpeg = PixelOps.ToJpeg(page.Bgra, page.Width, page.Height, 85);
        return jpeg.Length < png.Length * 0.6 ? jpeg : png;
    }

    /// <summary>Removes every entry of the Info dictionary (title, author, subject, keywords, producer, creator, dates) and the XMP packet.</summary>
    private static void StripMetadata(PdfDocument document)
    {
        foreach (var key in document.Info.Elements.Keys.ToList()) document.Info.Elements.Remove(key);
        document.Internals.Catalog.Elements.Remove("/Metadata");
    }

    /// <summary>
    /// Saves the document and then blanks what PDFsharp stamps into every file (producer, creator, dates, generated XMP) - see
    /// <see cref="PdfScrubber"/>. The scrubbed file is opened again to be sure it is still a readable PDF with all its pages; if not,
    /// the plain PDFsharp output is written instead (metadata of the original is gone either way).
    /// </summary>
    private static void SaveScrubbed(PdfDocument document, string path, int pageCount)
    {
        using var buffer = new MemoryStream();
        document.Save(buffer, false);
        var plain = buffer.ToArray();
        var scrubbed = PdfScrubber.RemoveDocumentMetadata(plain);

        File.WriteAllBytes(path, scrubbed);
        try
        {
            using var check = PdfiumDocument.Open(path);
            if (check.PageCount == pageCount) return;
        }
        catch (ConversionFailure)
        {
            // fall through: write the unscrubbed bytes
        }
        File.WriteAllBytes(path, plain);
    }

    private static string BuildNote(int areas, IReadOnlyList<int> pages, int dpi, IReadOnlyList<string> missing, bool downscaled)
    {
        string Plural(int n, string word) => n == 1 ? $"{n} {word}" : $"{n} {word}s";
        var note =
            $"Redacted {Plural(areas, "area")} on {Plural(pages.Count, "page")} (page {string.Join(", ", pages)}). " +
            $"{(pages.Count == 1 ? "That page was" : "Those pages were")} rasterized at {dpi} DPI and rebuilt from the picture, so their text is no longer selectable or searchable. " +
            "Document properties, XMP metadata, bookmarks and attachments were not carried over.";
        if (missing.Count > 0) note += $" Not found: {string.Join(", ", missing)}.";
        if (downscaled) note += " Very large pages were drawn at a lower DPI to stay within memory limits.";
        return note;
    }
}
