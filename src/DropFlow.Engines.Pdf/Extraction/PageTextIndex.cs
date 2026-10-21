using System.Text;
using DropFlow.Engines.Pdf.Infrastructure;
using UglyToad.PdfPig.Content;

namespace DropFlow.Engines.Pdf.Extraction;

/// <summary>
/// The text of one page as a searchable string plus, for every character, the glyph rectangle it came from. It is what lets
/// <c>pdf.redact</c> find "Alice" or an Arabic name and know exactly which part of the page to cover.
/// Letters are grouped into lines (per writing direction, by baseline), put in reading order with the same small bidi routine the text
/// extractor uses (so Arabic drawn as shaped glyphs in visual order is searchable by its ordinary letters) and normalised: Unicode
/// compatibility forms (Arabic presentation forms, ligatures) become plain letters, case is folded, Arabic diacritics and tatweel are
/// dropped, the alef and yeh variants are merged and Arabic-Indic digits become ASCII digits. Matching is a plain substring search over
/// that string, so a word is also found inside a longer word - for redaction, covering too much is the safe direction.
/// </summary>
internal sealed class PageTextIndex
{
    private readonly record struct CharInfo(int Line, int Visual, VisualBox? Box);

    private sealed record Glyph(string Text, VisualBox Box, double Along, double EndAlong, double Across, double Size, int Key);

    private sealed record Unit(string Text, VisualBox? Box, int Visual);

    private readonly string _text;
    private readonly List<CharInfo> _chars;

    private PageTextIndex(string text, List<CharInfo> chars, double width, double height)
    {
        _text = text;
        _chars = chars;
        PageWidth = width;
        PageHeight = height;
    }

    /// <summary>Page size in points as PdfPig reports it (after /Rotate and CropBox) - the space the boxes are in.</summary>
    public double PageWidth { get; }

    public double PageHeight { get; }

    /// <summary>The normalised page text (lines separated by a space).</summary>
    public string Text => _text;

    public static PageTextIndex Build(Page page)
    {
        var glyphs = new List<Glyph>();
        foreach (var letter in page.Letters)
        {
            var value = letter.Value;
            if (string.IsNullOrEmpty(value)) continue;

            var (dx, dy) = Direction(letter);
            var size = letter.PointSize > 0 ? letter.PointSize : 10;
            var (sx, sy) = (letter.StartBaseLine.X, letter.StartBaseLine.Y);
            var (ex, ey) = (letter.EndBaseLine.X, letter.EndBaseLine.Y);
            var along = sx * dx + sy * dy;
            var endAlong = ex * dx + ey * dy;
            var across = -sx * dy + sy * dx;
            var key = (int)Math.Round(Math.Atan2(dy, dx) * 180 / Math.PI / 5);
            glyphs.Add(new Glyph(value, GlyphBox(letter, sx, sy, ex, ey, dx, dy, size), along, endAlong, across, size, key));
        }

        var text = new StringBuilder();
        var chars = new List<CharInfo>();
        var lineId = 0;
        foreach (var group in glyphs.GroupBy(g => g.Key).OrderBy(g => g.Key))
        {
            var horizontal = group.Key == 0;
            foreach (var line in BuildLines(group.ToList()))
            {
                var units = VisualUnits(line);
                var logical = horizontal
                    ? BidiOrder.VisualToLogical(units, u => BidiOrder.KindOf(u.Text), BidiOrder.MostlyRtl(units.Select(u => BidiOrder.KindOf(u.Text)).ToList()))
                    : units;
                foreach (var unit in logical)
                {
                    foreach (var ch in Normalise(unit.Text))
                    {
                        if (ch == ' ' && (text.Length == 0 || text[^1] == ' ')) continue;
                        text.Append(ch);
                        chars.Add(new CharInfo(lineId, unit.Visual, unit.Box));
                    }
                }

                if (text.Length > 0 && text[^1] != ' ')
                {
                    text.Append(' ');
                    chars.Add(new CharInfo(-1, -1, null));
                }
                lineId++;
            }
        }

        return new PageTextIndex(text.ToString(), chars, page.Width, page.Height);
    }

    /// <summary>
    /// Every occurrence of <paramref name="normalisedTerm"/> (see <see cref="NormaliseTerm"/>) as the rectangles to cover: one per
    /// stretch of glyphs that sit next to each other on a line (a phrase that wraps gives one rectangle per line).
    /// </summary>
    public IReadOnlyList<IReadOnlyList<VisualBox>> Find(string normalisedTerm)
    {
        var found = new List<IReadOnlyList<VisualBox>>();
        if (normalisedTerm.Length == 0) return found;

        for (var start = _text.IndexOf(normalisedTerm, StringComparison.Ordinal); start >= 0; start = _text.IndexOf(normalisedTerm, start + 1, StringComparison.Ordinal))
        {
            var boxes = new List<VisualBox>();
            var matched = Enumerable.Range(start, normalisedTerm.Length).Select(i => _chars[i]).Where(c => c.Line >= 0);
            foreach (var line in matched.GroupBy(c => c.Line))
            {
                VisualBox? run = null;
                var last = int.MinValue;
                foreach (var c in line.OrderBy(c => c.Visual))
                {
                    if (run is not null && c.Visual - last > 1)
                    {
                        boxes.Add(run.Value);
                        run = null;
                    }
                    last = c.Visual;
                    if (c.Box is { } box) run = run is null ? box : run.Value.Union(box);
                }
                if (run is not null) boxes.Add(run.Value);
            }
            found.Add(boxes);
        }
        return found;
    }

    // ---- normalisation --------------------------------------------------------------------------------------------------

    /// <summary>A search term in the same normal form as the page text: folded, whitespace collapsed, trimmed.</summary>
    public static string NormaliseTerm(string term)
    {
        var sb = new StringBuilder();
        foreach (var ch in Normalise(term))
        {
            if (ch == ' ' && (sb.Length == 0 || sb[^1] == ' ')) continue;
            sb.Append(ch);
        }
        return sb.ToString().TrimEnd();
    }

    internal static string Normalise(string text)
    {
        var sb = new StringBuilder(text.Length);
        string folded;
        try
        {
            folded = text.Normalize(NormalizationForm.FormKC);
        }
        catch (ArgumentException)
        {
            folded = text; // a lone surrogate or other invalid sequence: use it as it is
        }

        foreach (var raw in folded)
        {
            var ch = raw;
            if (ch is >= 'ً' and <= 'ٟ' or 'ٰ' or 'ـ') continue;                 // harakat, dagger alef, tatweel
            if (ch is '​' or '‌' or '‍' or '‎' or '‏' or '­' or '﻿' or (>= '‪' and <= '‮') or (>= '⁦' and <= '⁩')) continue;
            if (char.IsWhiteSpace(ch)) ch = ' ';
            else if (ch is 'أ' or 'إ' or 'آ' or 'ٱ') ch = 'ا';                // alef with hamza / madda / wasla -> alef
            else if (ch is 'ى' or 'ی') ch = 'ي';                                        // alef maqsura, Persian yeh -> yeh
            else if (ch == 'ک') ch = 'ك';                                                    // Persian kaf -> kaf
            else if (ch is >= '٠' and <= '٩') ch = (char)('0' + (ch - '٠'));            // Arabic-Indic digits
            else if (ch is >= '۰' and <= '۹') ch = (char)('0' + (ch - '۰'));            // Persian digits
            sb.Append(char.ToLowerInvariant(ch));
        }
        return sb.ToString();
    }

    // ---- lines and glyph geometry ---------------------------------------------------------------------------------------

    private static (double Dx, double Dy) Direction(Letter letter)
    {
        switch (letter.TextOrientation)
        {
            case TextOrientation.Horizontal: return (1, 0);
            case TextOrientation.Rotate90: return (0, -1);
            case TextOrientation.Rotate180: return (-1, 0);
            case TextOrientation.Rotate270: return (0, 1);
        }

        var (vx, vy) = (letter.EndBaseLine.X - letter.StartBaseLine.X, letter.EndBaseLine.Y - letter.StartBaseLine.Y);
        var length = Math.Sqrt(vx * vx + vy * vy);
        return length > 1e-6 ? (vx / length, vy / length) : (1, 0);
    }

    /// <summary>The glyph's own rectangle merged with the full line height around its baseline, so every glyph of a line covers the same band.</summary>
    private static VisualBox GlyphBox(Letter letter, double sx, double sy, double ex, double ey, double dx, double dy, double size)
    {
        var rect = letter.BoundingBox;
        double[] xs = [rect.BottomLeft.X, rect.BottomRight.X, rect.TopLeft.X, rect.TopRight.X];
        double[] ys = [rect.BottomLeft.Y, rect.BottomRight.Y, rect.TopLeft.Y, rect.TopRight.Y];
        var box = new VisualBox(xs.Min(), ys.Min(), xs.Max(), ys.Max());

        // The normal to the writing direction points to the left of the text: up for horizontal text.
        double nx = -dy, ny = dx;
        double below = -0.25 * size, above = 0.95 * size;
        double[] px = [sx + nx * below, sx + nx * above, ex + nx * below, ex + nx * above];
        double[] py = [sy + ny * below, sy + ny * above, ey + ny * below, ey + ny * above];
        return box.Union(new VisualBox(px.Min(), py.Min(), px.Max(), py.Max()));
    }

    /// <summary>Groups glyphs of one writing direction into lines: same baseline within 45 % of the font size, top to bottom.</summary>
    private static List<List<Glyph>> BuildLines(List<Glyph> glyphs)
    {
        var lines = new List<(double Across, double Size, List<Glyph> Glyphs)>();
        foreach (var glyph in glyphs.OrderByDescending(g => g.Across).ThenBy(g => g.Along))
        {
            if (lines.Count > 0 && Math.Abs(lines[^1].Across - glyph.Across) <= 0.45 * Math.Max(glyph.Size, lines[^1].Size))
            {
                var current = lines[^1];
                current.Glyphs.Add(glyph);
                var n = current.Glyphs.Count;
                lines[^1] = ((current.Across * (n - 1) + glyph.Across) / n, Math.Max(current.Size, glyph.Size), current.Glyphs);
            }
            else
            {
                lines.Add((glyph.Across, glyph.Size, [glyph]));
            }
        }
        return [.. lines.Select(l => l.Glyphs)];
    }

    /// <summary>The glyphs of a line in visual order, with a space unit wherever the gap between two glyphs is wide enough to be a word break.</summary>
    private static List<Unit> VisualUnits(List<Glyph> line)
    {
        var units = new List<Unit>();
        Glyph? previous = null;
        foreach (var glyph in line.OrderBy(g => g.Along))
        {
            if (previous is not null && !string.IsNullOrWhiteSpace(previous.Text) && !string.IsNullOrWhiteSpace(glyph.Text))
            {
                var gap = glyph.Along - Math.Max(previous.Along, previous.EndAlong);
                if (gap > 0.2 * Math.Max(previous.Size, glyph.Size)) units.Add(new Unit(" ", null, units.Count));
            }
            units.Add(new Unit(glyph.Text, glyph.Box, units.Count));
            previous = glyph;
        }
        return units;
    }
}
