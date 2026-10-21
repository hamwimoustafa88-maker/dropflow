using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using DropFlow.Core.Catalog;
using DropFlow.Core.Operations;
using DropFlow.Engines.Pdf.Extraction;
using DropFlow.Engines.Pdf.Infrastructure;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace DropFlow.Engines.Pdf.Tools;

/// <summary>
/// <c>pdf.compare</c>: takes exactly two PDFs and writes <c>"A vs B (compare).pdf"</c> and <c>"A vs B (compare).txt"</c>.
/// Every page of the PDF is a wide sheet with three panels: page n of A, page n of B (both drawn by PDFium) and the differences - the
/// pixels whose colour differs by more than the sensitivity threshold in red over a faded copy of A. A page that exists in only one file is
/// shown on its own and labelled "only in A" / "only in B". The report lists the page counts, for each page the share of changed pixels
/// and a word-level summary (words removed from and added to the page, from the same text reader the text exports use).
/// <para>
/// Sensitivity runs from 1 (only gross differences) to 100 (anything); the per-channel colour distance (0..255) a pixel must exceed is
/// <c>max(1, 255 * 2^(-sensitivity / 11.5))</c>, so the default 30 means "more than 42 levels" and 100 means "any difference at all".
/// Pages of different size are compared on the larger of the two canvases (the missing area counts as white) and reported as such.
/// </para>
/// </summary>
internal static class PdfCompareTools
{
    private const int MaxListedWords = 25;
    private const int HeaderWords = 8;
    private const double FadeToWhite = 0.35;
    private const double SheetWidth = 1190.55;   // A3 landscape width
    private const double MaxSheetHeight = 12000; // viewers refuse pages above 14400 pt
    private const double MinPanelHeight = 260;
    private const double Margin = 24;
    private const double Gap = 16;
    private const double HeaderHeight = 70;
    private const double LabelHeight = 16;

    public static readonly IReadOnlyList<ToolOption> Options =
    [
        new("sensitivity", ToolOptionKind.Number, 30, Min: 1, Max: 100),
        new("dpi", ToolOptionKind.Number, 100, Min: 50, Max: 300),
    ];

    public static IEnumerable<OperationSpec> Specs()
    {
        yield return OperationSpec.For("pdf.compare", "pdf", ctx => Task.FromResult(Compare(ctx)), Options);
    }

    private enum Presence { Both, OnlyA, OnlyB }

    private sealed record PageResult(
        int Number,
        Presence Presence,
        long Changed,
        double Percent,
        (double Width, double Height) SizeA,
        (double Width, double Height) SizeB,
        IReadOnlyList<string> Removed,
        IReadOnlyList<string> Added)
    {
        public bool SizeDiffers => Presence == Presence.Both && (Math.Abs(SizeA.Width - SizeB.Width) > 0.5 || Math.Abs(SizeA.Height - SizeB.Height) > 0.5);

        public bool Identical => Presence == Presence.Both && Changed == 0 && !SizeDiffers && Removed.Count == 0 && Added.Count == 0;
    }

    /// <summary>What goes on one sheet: three pictures (null when the page does not exist) and the texts of the header.</summary>
    private sealed record Sheet(PageResult Result, byte[]? A, byte[]? B, byte[]? Diff, (double W, double H) PageA, (double W, double H) PageB, (double W, double H) Canvas);

    private static ConversionResult Compare(JobContext ctx)
    {
        var inputs = ctx.Job.Inputs;
        if (inputs.Count != 2) return ctx.Fail($"Select exactly two PDF files to compare (got {inputs.Count}).");
        foreach (var input in inputs)
        {
            if (!string.Equals(Path.GetExtension(input), ".pdf", StringComparison.OrdinalIgnoreCase))
                return ctx.Fail($"Only PDF files can be compared: {Path.GetFileName(input)}");
        }

        var sensitivity = ctx.Job.GetDouble("sensitivity", 30);
        if (!double.IsFinite(sensitivity) || sensitivity < 1 || sensitivity > 100) return ctx.Fail("Sensitivity must be between 1 and 100.");
        var dpi = ctx.Job.GetDouble("dpi", 100);
        if (!double.IsFinite(dpi) || dpi < 50 || dpi > 300) return ctx.Fail("DPI must be between 50 and 300.");
        var threshold = Threshold(sensitivity);

        PdfSharpSupport.EnsureFonts();
        string nameA = Path.GetFileName(inputs[0]), nameB = Path.GetFileName(inputs[1]);
        using var a = PdfiumDocument.Open(inputs[0]);
        using var b = PdfiumDocument.Open(inputs[1]);
        if (a.PageCount < 1) return ctx.Fail($"{nameA} has no pages.");
        if (b.PageCount < 1) return ctx.Fail($"{nameB} has no pages.");

        // Words per page (also refuses files whose owner forbade copying their text).
        ctx.Report(0.0, "Reading text");
        var wordsA = PageWords(PdfTextExtractor.Read(inputs[0], ctx.Ct));
        ctx.Report(0.05, "Reading text");
        var wordsB = PageWords(PdfTextExtractor.Read(inputs[1], ctx.Ct));
        ctx.Report(0.1);

        var total = Math.Max(a.PageCount, b.PageCount);
        var sheets = new List<Sheet>(total);
        for (var i = 0; i < total; i++)
        {
            ctx.Ct.ThrowIfCancellationRequested();
            ctx.Report(0.1 + 0.8 * i / total, $"Page {i + 1}");
            sheets.Add(BuildSheet(a, b, i, dpi, threshold, wordsA, wordsB));
        }

        var results = sheets.Select(s => s.Result).ToList();
        var identical = a.PageCount == b.PageCount && results.All(r => r.Identical);
        var report = BuildReport(nameA, nameB, a, b, results, identical, (int)Math.Round(dpi), sensitivity, threshold);

        // Both files are reserved together so that the pair always carries the same "(compare N)" label.
        var stemA = Trim(Path.GetFileNameWithoutExtension(inputs[0]));
        var stemB = Trim(Path.GetFileNameWithoutExtension(inputs[1]));
        var naming = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(inputs[0])) ?? ".", $"{stemA} vs {stemB}.pdf");
        var paths = ctx.ReserveSet(naming, "compare", "pdf", "txt");

        var streams = new List<MemoryStream>();
        var images = new List<XImage>();
        try
        {
            using var document = new PdfDocument();
            PdfSharpSupport.ApplyDefaultOptions(document);
            document.Info.Title = "DropFlow PDF comparison";
            for (var i = 0; i < sheets.Count; i++)
            {
                ctx.Ct.ThrowIfCancellationRequested();
                ctx.Report(0.9 + 0.1 * i / sheets.Count, $"Writing page {i + 1}");
                DrawSheet(document, sheets[i], total, identical, nameA, nameB, streams, images);
            }

            document.Save(paths[0]);
            File.WriteAllText(paths[1], report, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            ctx.Report(1);
            return ctx.Ok(paths[0], paths[1]);
        }
        finally
        {
            foreach (var image in images) image.Dispose();
            foreach (var stream in streams) stream.Dispose();
        }
    }

    /// <summary>The largest per-channel difference (0..255) that still counts as "the same colour".</summary>
    internal static int Threshold(double sensitivity) => Math.Max(1, (int)Math.Round(255 * Math.Pow(2, -sensitivity / 11.5)));

    private static string Trim(string stem) => stem.Length <= 60 ? stem : stem[..60];

    // ---- words ----------------------------------------------------------------------------------------------------------

    private static List<List<string>> PageWords(PdfTextDocument document) =>
        [.. document.Pages.Select(p => p.Lines.SelectMany(l => Regex.Split(l.Text, @"\s+"))
            .Select(w => w.Trim('.', ',', ';', ':', '!', '?', '(', ')', '[', ']', '{', '}', '"', '\'', '،', '؛', '؟'))
            .Where(w => w.Length > 0)
            .ToList())];

    /// <summary>Words of A that B lacks and words of B that A lacks, as multisets in page order (a reflowed word is not a change).</summary>
    private static (List<string> Removed, List<string> Added) WordDiff(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        static List<string> Missing(IReadOnlyList<string> from, IReadOnlyList<string> other)
        {
            var available = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var word in other) available[word] = available.GetValueOrDefault(word) + 1;
            var missing = new List<string>();
            foreach (var word in from)
            {
                if (available.TryGetValue(word, out var left) && left > 0) available[word] = left - 1;
                else missing.Add(word);
            }
            return missing;
        }

        return (Missing(a, b), Missing(b, a));
    }

    // ---- pixels ---------------------------------------------------------------------------------------------------------

    private static Sheet BuildSheet(PdfiumDocument a, PdfiumDocument b, int index, double dpi, int threshold, List<List<string>> wordsA, List<List<string>> wordsB)
    {
        var number = index + 1;
        var inA = index < a.PageCount;
        var inB = index < b.PageCount;
        var sizeA = inA ? a.PageSize(index) : (0, 0);
        var sizeB = inB ? b.PageSize(index) : (0, 0);

        // Both sides are drawn at the same DPI, lowered if either page would be too big for memory.
        var useDpi = dpi;
        if (inA) useDpi = Math.Min(useDpi, PdfiumDocument.FitBitmap(sizeA.Item1, sizeA.Item2, dpi).Dpi);
        if (inB) useDpi = Math.Min(useDpi, PdfiumDocument.FitBitmap(sizeB.Item1, sizeB.Item2, dpi).Dpi);
        var pointsPerPixel = 72 / useDpi;

        var imageA = inA ? a.Render(index, useDpi) : null;
        var imageB = inB ? b.Render(index, useDpi) : null;

        if (imageA is not null && imageB is not null)
        {
            var (diff, changed, width, height) = Diff(imageA, imageB, threshold);
            var (removed, added) = WordDiff(index < wordsA.Count ? wordsA[index] : [], index < wordsB.Count ? wordsB[index] : []);
            var result = new PageResult(number, Presence.Both, changed, changed * 100.0 / ((double)width * height), sizeA, sizeB, removed, added);
            return new Sheet(result, Jpeg(imageA), Jpeg(imageB), PixelOps.ToPng(diff, width, height),
                (imageA.Width * pointsPerPixel, imageA.Height * pointsPerPixel), (imageB.Width * pointsPerPixel, imageB.Height * pointsPerPixel),
                (width * pointsPerPixel, height * pointsPerPixel));
        }

        var only = imageA ?? imageB!;
        var presence = imageA is not null ? Presence.OnlyA : Presence.OnlyB;
        var words = presence == Presence.OnlyA ? (index < wordsA.Count ? wordsA[index] : []) : (index < wordsB.Count ? wordsB[index] : []);
        var lone = new PageResult(number, presence, 0, 0, sizeA, sizeB, presence == Presence.OnlyA ? words : [], presence == Presence.OnlyB ? words : []);
        var pageSize = (only.Width * pointsPerPixel, only.Height * pointsPerPixel);
        return new Sheet(lone, imageA is null ? null : Jpeg(imageA), imageB is null ? null : Jpeg(imageB), null,
            imageA is null ? (0, 0) : pageSize, imageB is null ? (0, 0) : pageSize, pageSize);
    }

    private static byte[] Jpeg(RenderedPage page) => PixelOps.ToJpeg(page.Bgra, page.Width, page.Height, 80);

    /// <summary>
    /// The difference picture (BGRA): a faded copy of A with every pixel whose colour differs from B by more than <paramref name="threshold"/>
    /// in any channel painted red, plus the number of such pixels. The red is grown by one pixel only for drawing, so thin strokes stay visible
    /// when the sheet is scaled down; the count is of the pixels that really differ. The area outside the smaller page counts as white.
    /// </summary>
    private static (byte[] Image, long Changed, int Width, int Height) Diff(RenderedPage a, RenderedPage b, int threshold)
    {
        var width = Math.Max(a.Width, b.Width);
        var height = Math.Max(a.Height, b.Height);
        var changedMask = new bool[width * height];
        var image = new byte[width * height * 4];
        long changed = 0;

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var (ab, ag, ar) = Pixel(a, x, y);
                var (bb, bg, br) = Pixel(b, x, y);
                var o = (y * width + x) * 4;
                if (Math.Max(Math.Abs(ar - br), Math.Max(Math.Abs(ag - bg), Math.Abs(ab - bb))) > threshold)
                {
                    changedMask[y * width + x] = true;
                    changed++;
                }

                image[o] = Fade(ab);
                image[o + 1] = Fade(ag);
                image[o + 2] = Fade(ar);
                image[o + 3] = 255;
            }
        }

        // Paint the red (a pixel and its 8 neighbours).
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (!changedMask[y * width + x]) continue;
                for (var dy = -1; dy <= 1; dy++)
                {
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        int px = x + dx, py = y + dy;
                        if (px < 0 || py < 0 || px >= width || py >= height) continue;
                        var o = (py * width + px) * 4;
                        image[o] = 20;
                        image[o + 1] = 20;
                        image[o + 2] = 230;
                    }
                }
            }
        }
        return (image, changed, width, height);
    }

    private static (byte B, byte G, byte R) Pixel(RenderedPage page, int x, int y)
    {
        if (x >= page.Width || y >= page.Height) return (255, 255, 255);
        var i = (y * page.Width + x) * 4;
        return (page.Bgra[i], page.Bgra[i + 1], page.Bgra[i + 2]);
    }

    private static byte Fade(byte value) => (byte)(255 - (255 - value) * FadeToWhite);

    // ---- the sheet ------------------------------------------------------------------------------------------------------

    private static void DrawSheet(PdfDocument document, Sheet sheet, int total, bool identical, string nameA, string nameB, List<MemoryStream> streams, List<XImage> images)
    {
        // The sheet is always 1190.55 pt (A3 landscape width) wide; its height follows the page being shown, within what viewers accept.
        var canvas = sheet.Canvas;
        var panelWidth = (SheetWidth - 2 * Margin - 2 * Gap) / 3;
        var scale = Math.Min(1.0, panelWidth / Math.Max(1, canvas.W));
        scale = Math.Min(scale, MaxSheetHeight / Math.Max(1, canvas.H));
        var panelHeight = Math.Max(MinPanelHeight, canvas.H * scale);

        var page = document.AddPage();
        page.Width = XUnitPt.FromPoint(SheetWidth);
        page.Height = XUnitPt.FromPoint(Margin + HeaderHeight + LabelHeight + panelHeight + Margin);
        using var gfx = XGraphics.FromPdfPage(page);

        var title = new XFont("Arial", 11, XFontStyleEx.Bold);
        var body = new XFont("Arial", 10, XFontStyleEx.Regular);
        var small = new XFont("Arial", 9, XFontStyleEx.Regular);
        var ink = XBrushes.Black;
        var muted = new XSolidBrush(XColor.FromArgb(90, 90, 90));
        var contentWidth = SheetWidth - 2 * Margin;

        // header
        var half = (contentWidth - Gap) / 2;
        gfx.DrawString(Fit(gfx, $"A: {nameA}", title, half), title, ink, new XPoint(Margin, Margin + 12));
        gfx.DrawString(Fit(gfx, $"B: {nameB}", title, half), title, ink, new XPoint(Margin + half + Gap, Margin + 12));
        gfx.DrawString(Fit(gfx, $"Page {sheet.Result.Number} of {total} - {StatusText(sheet.Result)}", body, contentWidth), body, ink, new XPoint(Margin, Margin + 30));
        var third = identical ? "The two documents are identical." : WordsHeader(sheet.Result);
        if (third.Length > 0) gfx.DrawString(Fit(gfx, third, small, contentWidth), small, muted, new XPoint(Margin, Margin + 46));

        // panels
        var top = Margin + HeaderHeight;

        (string Label, byte[]? Data, (double W, double H) Size)[] panels =
        [
            ("A (first)", sheet.A, sheet.PageA),
            ("B (second)", sheet.B, sheet.PageB),
            ("Differences (red) over A", sheet.Diff, canvas),
        ];
        for (var i = 0; i < 3; i++)
        {
            var x = Margin + i * (panelWidth + Gap);
            var (label, data, size) = panels[i];
            gfx.DrawString(Fit(gfx, label, small, panelWidth), small, muted, new XPoint(x, top + 10));
            var frame = new XRect(x, top + LabelHeight, panelWidth, panelHeight);
            if (data is null)
            {
                var note = sheet.Result.Presence switch
                {
                    Presence.OnlyA when i == 1 => "No page B",
                    Presence.OnlyA when i == 2 => "Page only in A",
                    Presence.OnlyB when i == 0 => "No page A",
                    Presence.OnlyB when i == 2 => "Page only in B",
                    _ => "",
                };
                gfx.DrawRectangle(new XPen(XColor.FromArgb(200, 200, 200), 0.5), new XSolidBrush(XColor.FromArgb(245, 245, 245)), frame.X, frame.Y, Math.Min(panelWidth, canvas.W * scale), Math.Min(panelHeight, canvas.H * scale));
                if (note.Length > 0) gfx.DrawString(note, body, muted, new XPoint(x + 8, top + LabelHeight + 18));
                continue;
            }

            var stream = new MemoryStream(data);
            var image = XImage.FromStream(stream);
            streams.Add(stream);
            images.Add(image);
            gfx.DrawImage(image, x, top + LabelHeight, size.W * scale, size.H * scale);
            gfx.DrawRectangle(new XPen(XColor.FromArgb(200, 200, 200), 0.5), x, top + LabelHeight, size.W * scale, size.H * scale);
        }
    }

    /// <summary>Text for a sheet header; Arabic and other right-to-left text is put in visual order the way watermarks are.</summary>
    private static string Fit(XGraphics gfx, string text, XFont font, double maxWidth)
    {
        var visual = ArabicShaper.ToVisual(text);
        if (gfx.MeasureString(visual, font).Width <= maxWidth) return visual;
        var shortened = text;
        while (shortened.Length > 1)
        {
            shortened = shortened[..^1];
            var candidate = ArabicShaper.ToVisual(shortened + "...");
            if (gfx.MeasureString(candidate, font).Width <= maxWidth) return candidate;
        }
        return "...";
    }

    private static string WordsHeader(PageResult result)
    {
        if (result.Removed.Count == 0 && result.Added.Count == 0) return "";
        var parts = new List<string>();
        if (result.Removed.Count > 0) parts.Add($"Removed: {WordList(result.Removed, HeaderWords)}");
        if (result.Added.Count > 0) parts.Add($"Added: {WordList(result.Added, HeaderWords)}");
        return string.Join("      ", parts);
    }

    // ---- report ---------------------------------------------------------------------------------------------------------

    private static string StatusText(PageResult r) => r.Presence switch
    {
        Presence.OnlyA => "only in A",
        Presence.OnlyB => "only in B",
        _ when r.Identical => "identical",
        _ => $"{FormatPercent(r)} % of pixels changed" + (r.SizeDiffers ? $" - page size differs: A {Size(r.SizeA)}, B {Size(r.SizeB)}" : ""),
    };

    private static string FormatPercent(PageResult r) =>
        r.Changed == 0 ? "0.00" : r.Percent < 0.01 ? "<0.01" : r.Percent.ToString("0.00", CultureInfo.InvariantCulture);

    private static string Size((double Width, double Height) points) =>
        string.Create(CultureInfo.InvariantCulture, $"{PageGeometry.PointsToMm(points.Width):0.#} x {PageGeometry.PointsToMm(points.Height):0.#} mm");

    private static string WordList(IReadOnlyList<string> words, int limit) =>
        string.Join(", ", words.Take(limit)) + (words.Count > limit ? $" (+{words.Count - limit} more)" : "");

    private static string BuildReport(string nameA, string nameB, PdfiumDocument a, PdfiumDocument b, List<PageResult> results, bool identical, int dpi, double sensitivity, int threshold)
    {
        string Pages(int n) => n == 1 ? "1 page" : $"{n} pages";
        var sb = new StringBuilder();
        sb.AppendLine("DropFlow PDF comparison");
        sb.AppendLine("=======================");
        sb.AppendLine();
        sb.AppendLine($"A: {nameA} ({Pages(a.PageCount)}, first page {Size(a.PageSize(0))})");
        sb.AppendLine($"B: {nameB} ({Pages(b.PageCount)}, first page {Size(b.PageSize(0))})");
        sb.AppendLine();

        var compared = results.Count(r => r.Presence == Presence.Both);
        var differing = results.Count(r => r.Presence == Presence.Both && !r.Identical);
        sb.AppendLine(identical ? "Result: IDENTICAL" : "Result: DIFFERENT");
        sb.AppendLine(identical
            ? $"The two PDFs are identical: the same number of pages, the same page sizes, no visual difference at sensitivity {sensitivity.ToString("0.##", CultureInfo.InvariantCulture)} and the same words on every page."
            : $"A has {Pages(a.PageCount)} and B has {Pages(b.PageCount)}. Of the {compared} page{(compared == 1 ? "" : "s")} present in both, {differing} differ{(differing == 1 ? "s" : "")}." +
              (a.PageCount == b.PageCount ? "" : $" {Math.Abs(a.PageCount - b.PageCount)} page{(Math.Abs(a.PageCount - b.PageCount) == 1 ? " is" : "s are")} only in {(a.PageCount > b.PageCount ? "A" : "B")}."));
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"Settings: {dpi} DPI, sensitivity {sensitivity:0.##} (a pixel counts as changed when one of its colours differs by more than {threshold} of 255)."));
        sb.AppendLine();

        foreach (var r in results)
        {
            sb.AppendLine($"Page {r.Number}: {StatusText(r)}");
            if (r.Presence != Presence.Both) continue;
            if (r.Removed.Count > 0) sb.AppendLine($"        text removed ({r.Removed.Count}): {WordList(r.Removed, MaxListedWords)}");
            if (r.Added.Count > 0) sb.AppendLine($"        text added ({r.Added.Count}): {WordList(r.Added, MaxListedWords)}");
        }
        return sb.ToString();
    }
}
