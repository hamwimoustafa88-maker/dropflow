using System.Text;
using DropFlow.Engines.Pdf.Infrastructure;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Exceptions;

namespace DropFlow.Engines.Pdf.Extraction;

/// <summary>
/// Reads the text layer of a PDF with PdfPig and rebuilds reading order: glyphs become lines (by baseline), lines become tables,
/// headings, list items and paragraphs. Right-to-left lines are put in logical order from the glyph positions, so an Arabic line
/// comes out the way it is typed regardless of how the producer ordered the content stream.
/// </summary>
internal static class PdfTextExtractor
{
    private sealed record Glyph(string Text, double Left, double Right, double BaseY, double Size, bool Bold, bool Italic);

    public static PdfTextDocument Read(string path, CancellationToken ct = default, Action<double>? progress = null)
    {
        var name = Path.GetFileName(path);
        RequireTextExtractionAllowed(path, name);
        PdfDocument document;
        try
        {
            document = PdfDocument.Open(File.ReadAllBytes(path));
        }
        catch (PdfDocumentEncryptedException)
        {
            throw new ConversionFailure($"{name} is protected with a password. Unlock it first (Unlock PDF), then try again.");
        }
        catch (Exception ex) when (ex is PdfDocumentFormatException or InvalidOperationException or ArgumentException or NotSupportedException or IndexOutOfRangeException)
        {
            throw new ConversionFailure($"{name} is not a valid PDF or is damaged ({ex.Message}). Try Repair PDF first.");
        }

        using (document)
        {
            var skipped = 0;
            var rawPages = new List<(Page Page, List<TextLine> Lines)>();
            var count = document.NumberOfPages;
            for (var i = 1; i <= count; i++)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Invoke(0.5 * (i - 1) / count);
                var page = document.GetPage(i);
                var glyphs = ReadGlyphs(page, ref skipped);
                rawPages.Add((page, BuildLines(glyphs)));
            }

            var bodySize = BodyFontSize(rawPages.SelectMany(p => p.Lines).ToList());
            var headingSizes = HeadingSizes(rawPages.SelectMany(p => p.Lines).ToList(), bodySize);

            var pages = new List<PdfPageText>();
            for (var i = 0; i < rawPages.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Invoke(0.5 + 0.5 * i / Math.Max(1, rawPages.Count));
                var (page, lines) = rawPages[i];
                var (w, h) = VisualSize(page);
                pages.Add(new PdfPageText(page.Number, w, h, lines, BlockBuilder.Build(lines, bodySize, headingSizes)));
            }

            var info = document.Information;
            return new PdfTextDocument(pages, bodySize, skipped, Clean(info.Title), Clean(info.Author));
        }
    }

    /// <summary>Honours an owner password that forbids copying text out of the file (PdfPig itself ignores permissions).</summary>
    private static void RequireTextExtractionAllowed(string path, string name)
    {
        try
        {
            using var pdfium = PdfiumDocument.Open(path);
            if (!pdfium.AllowsExtraction)
                throw new ConversionFailure($"{name} has an owner password that does not allow its text to be copied. Unlock it first (Unlock PDF), then try again.");
        }
        catch (ConversionFailure ex) when (ex.Message.Contains("not a valid PDF", StringComparison.Ordinal))
        {
            // PDFium cannot read the file; PdfPig below reports the real problem (or reads it anyway).
        }
    }

    // ---- glyphs ---------------------------------------------------------------------------------------------------------

    // PdfPig already applies the page /Rotate entry: letter positions and Page.Width/Height are what the reader sees.
    private static (double Width, double Height) VisualSize(Page page) => (page.Width, page.Height);

    private static List<Glyph> ReadGlyphs(Page page, ref int skippedRotated)
    {
        var glyphs = new List<Glyph>();
        foreach (var letter in page.Letters)
        {
            var value = letter.Value;
            if (string.IsNullOrEmpty(value)) continue;

            var a = (X: letter.StartBaseLine.X, Y: letter.StartBaseLine.Y);
            var b = (X: letter.EndBaseLine.X, Y: letter.EndBaseLine.Y);
            if (Math.Abs(b.Y - a.Y) > Math.Abs(b.X - a.X) + 0.01)
            {
                skippedRotated++; // vertical text is not reconstructed
                continue;
            }

            var size = letter.PointSize > 0 ? letter.PointSize : Math.Max(1, letter.BoundingBox.Height);
            var font = letter.FontName ?? "";
            var bold = letter.FontDetails?.IsBold == true || font.Contains("Bold", StringComparison.OrdinalIgnoreCase);
            var italic = letter.FontDetails?.IsItalic == true || font.Contains("Italic", StringComparison.OrdinalIgnoreCase) || font.Contains("Oblique", StringComparison.OrdinalIgnoreCase);
            glyphs.Add(new Glyph(value, Math.Min(a.X, b.X), Math.Max(a.X, b.X), (a.Y + b.Y) / 2, size, bold, italic));
        }
        return glyphs;
    }

    // ---- lines ----------------------------------------------------------------------------------------------------------

    private static List<TextLine> BuildLines(List<Glyph> glyphs)
    {
        var lines = new List<TextLine>();
        var groups = new List<(double BaseY, double Size, List<Glyph> Glyphs)>();
        foreach (var glyph in glyphs.OrderByDescending(g => g.BaseY).ThenBy(g => g.Left))
        {
            var current = groups.Count > 0 ? groups[^1] : default;
            if (groups.Count > 0 && Math.Abs(current.BaseY - glyph.BaseY) <= 0.45 * Math.Max(glyph.Size, current.Size))
            {
                current.Glyphs.Add(glyph);
                var n = current.Glyphs.Count;
                groups[^1] = ((current.BaseY * (n - 1) + glyph.BaseY) / n, Math.Max(current.Size, glyph.Size), current.Glyphs);
            }
            else
            {
                groups.Add((glyph.BaseY, glyph.Size, [glyph]));
            }
        }

        foreach (var (baseY, _, members) in groups)
        {
            var line = MakeLine(members.OrderBy(g => g.Left).ToList(), baseY);
            if (line is not null) lines.Add(line);
        }
        return lines;
    }

    private static TextLine? MakeLine(List<Glyph> glyphs, double baseY)
    {
        var visible = glyphs.Where(g => !string.IsNullOrWhiteSpace(g.Text)).ToList();
        if (visible.Count == 0) return null;

        var size = Mode(visible.Select(g => Math.Round(g.Size * 2) / 2));
        var spaceGap = 0.2 * size;
        var cellGap = Math.Max(0.9 * size, 7);

        // Units in visual order (glyph text or a space) and the cell they fall in.
        var units = new List<string>();
        var cells = new List<List<string>> { new() };
        var cellBounds = new List<(double Left, double Right)>();
        Glyph? previous = null;
        foreach (var glyph in glyphs)
        {
            if (string.IsNullOrWhiteSpace(glyph.Text))
            {
                if (units.Count > 0 && units[^1] != " ") { units.Add(" "); cells[^1].Add(" "); }
                continue;
            }

            if (previous is not null)
            {
                var gap = glyph.Left - previous.Right;
                if (gap > cellGap)
                {
                    cells.Add(new List<string>());
                }
                if (gap > spaceGap && units.Count > 0 && units[^1] != " ")
                {
                    units.Add(" ");
                    cells[^1].Add(" ");
                }
            }

            units.Add(glyph.Text);
            cells[^1].Add(glyph.Text);
            if (cellBounds.Count < cells.Count) cellBounds.Add((glyph.Left, glyph.Right));
            else cellBounds[^1] = (cellBounds[^1].Left, Math.Max(cellBounds[^1].Right, glyph.Right));
            previous = glyph;
        }

        var kinds = units.Select(BidiOrder.KindOf).ToList();
        var rtl = BidiOrder.MostlyRtl(kinds);
        var text = Finish(BidiOrder.VisualToLogical(units, BidiOrder.KindOf, rtl));

        var segments = new List<TextSegment>();
        for (var i = 0; i < cells.Count; i++)
        {
            var cellUnits = cells[i];
            if (cellUnits.Count == 0 || i >= cellBounds.Count) continue;
            var cellRtl = BidiOrder.MostlyRtl(cellUnits.Select(BidiOrder.KindOf).ToList());
            var cellText = Finish(BidiOrder.VisualToLogical(cellUnits, BidiOrder.KindOf, cellRtl));
            if (cellText.Length > 0) segments.Add(new TextSegment(cellText, cellBounds[i].Left, cellBounds[i].Right));
        }

        var bold = visible.Count(g => g.Bold) * 2 > visible.Count;
        var italic = visible.Count(g => g.Italic) * 2 > visible.Count;
        return new TextLine(text, visible.Min(g => g.Left), visible.Max(g => g.Right), baseY, size, bold, italic, rtl, segments);
    }

    /// <summary>Joins units, maps Arabic presentation forms back to plain letters and drops invisible direction marks.</summary>
    internal static string Finish(IEnumerable<string> units)
    {
        var sb = new StringBuilder();
        foreach (var unit in units)
        {
            foreach (var c in unit)
            {
                if (c is >= '‪' and <= '‮' or '‎' or '‏' or '⁦' or '⁧' or '⁨' or '⁩') continue;
                if (c is >= 'ﭐ' and <= '﷿' or >= 'ﹰ' and <= '﻿') sb.Append(c.ToString().Normalize(NormalizationForm.FormKC));
                else sb.Append(c);
            }
        }
        return sb.ToString().Trim();
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static double Mode(IEnumerable<double> values) =>
        values.GroupBy(v => v).OrderByDescending(g => g.Count()).ThenByDescending(g => g.Key).First().Key;

    // ---- document-wide statistics ---------------------------------------------------------------------------------------

    private static double BodyFontSize(List<TextLine> lines)
    {
        if (lines.Count == 0) return 12;
        var weights = new Dictionary<double, int>();
        foreach (var line in lines)
        {
            var size = Math.Round(line.FontSize * 2) / 2;
            weights[size] = weights.GetValueOrDefault(size) + line.Text.Length;
        }
        return weights.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).First().Key;
    }

    private static List<double> HeadingSizes(List<TextLine> lines, double body)
    {
        return lines
            .Where(l => l.FontSize >= body * 1.15 && l.FontSize - body >= 1 && l.Text.Length <= 160)
            .Select(l => Math.Round(l.FontSize * 2) / 2)
            .Distinct()
            .OrderByDescending(s => s)
            .ToList();
    }

    internal static double Round(double size) => Math.Round(size * 2, MidpointRounding.AwayFromZero) / 2;

}
