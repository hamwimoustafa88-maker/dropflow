using System.Globalization;
using System.Text;

namespace DropFlow.Engines.Pdf.Infrastructure;

/// <summary>One page selected by a <see cref="PageSpec"/>: its 1-based number and an extra clockwise rotation (0/90/180/270).</summary>
public readonly record struct PageRef(int Page, int Rotation = 0);

/// <summary>
/// Parses page selections such as <c>3,1,2,2-4</c>, <c>8-</c>, <c>odd</c>, <c>last,first</c> or <c>3r90,1</c> (page 3 rotated 90 degrees).
/// Order and duplicates are kept, so the same grammar drives rotate, extract and organize. Arabic-Indic digits and the Arabic comma
/// are accepted so a user can type the selection in their own keyboard layout.
/// </summary>
public static class PageSpec
{
    public static bool TryParse(string? spec, int pageCount, out IReadOnlyList<PageRef> pages, out string error, bool allowBlank = false)
    {
        pages = [];
        error = "";
        if (pageCount < 1)
        {
            error = "The document has no pages.";
            return false;
        }

        spec = Normalize(spec);
        if (spec.Length == 0)
        {
            if (!allowBlank)
            {
                error = "Enter the pages to use, for example 1-3,5.";
                return false;
            }
            pages = Enumerable.Range(1, pageCount).Select(p => new PageRef(p)).ToList();
            return true;
        }

        var result = new List<PageRef>();
        foreach (var token in spec.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries))
        {
            if (!TryParseToken(token, pageCount, result, out error)) return false;
        }

        if (result.Count == 0)
        {
            error = "Enter the pages to use, for example 1-3,5.";
            return false;
        }

        pages = result;
        return true;
    }

    private static bool TryParseToken(string token, int pageCount, List<PageRef> result, out string error)
    {
        error = "";
        var rotation = 0;
        var rIndex = token.LastIndexOf('r');
        if (rIndex > 0 && rIndex < token.Length - 1 && token[(rIndex + 1)..].All(char.IsAsciiDigit))
        {
            if (!int.TryParse(token[(rIndex + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out rotation) || rotation is not (90 or 180 or 270))
            {
                error = $"'{token}': rotation must be r90, r180 or r270.";
                return false;
            }
            token = token[..rIndex];
        }

        switch (token)
        {
            case "all":
                result.AddRange(Enumerable.Range(1, pageCount).Select(p => new PageRef(p, rotation)));
                return true;
            case "odd":
                result.AddRange(Enumerable.Range(1, pageCount).Where(p => p % 2 == 1).Select(p => new PageRef(p, rotation)));
                return true;
            case "even":
                result.AddRange(Enumerable.Range(1, pageCount).Where(p => p % 2 == 0).Select(p => new PageRef(p, rotation)));
                return true;
        }

        var dash = token.IndexOf('-');
        if (dash < 0)
        {
            if (!TryPage(token, pageCount, out var page, out error)) return false;
            result.Add(new PageRef(page, rotation));
            return true;
        }

        var left = token[..dash];
        var right = token[(dash + 1)..];
        if (left.Length == 0)
        {
            error = $"'{token}' is not a valid page range.";
            return false;
        }

        if (!TryPage(left, pageCount, out var from, out error)) return false;
        var to = pageCount;
        if (right.Length > 0 && !TryPage(right, pageCount, out to, out error)) return false;

        var step = from <= to ? 1 : -1;
        for (var p = from; p != to + step; p += step) result.Add(new PageRef(p, rotation));
        return true;
    }

    private static bool TryPage(string text, int pageCount, out int page, out string error)
    {
        error = "";
        page = 0;
        if (text == "last") { page = pageCount; return true; }
        if (text == "first") { page = 1; return true; }

        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out page))
        {
            error = $"'{text}' is not a page number.";
            return false;
        }
        if (page < 1 || page > pageCount)
        {
            error = $"Page {page} does not exist (the document has {pageCount} pages).";
            return false;
        }
        return true;
    }

    private static string Normalize(string? spec)
    {
        if (string.IsNullOrWhiteSpace(spec)) return "";
        var sb = new StringBuilder(spec.Length);
        foreach (var ch in spec.Trim())
        {
            sb.Append(ch switch
            {
                >= '٠' and <= '٩' => (char)('0' + (ch - '٠')), // Arabic-Indic digits
                >= '۰' and <= '۹' => (char)('0' + (ch - '۰')), // Extended Arabic-Indic digits
                '،' or '؛' => ',',                                   // Arabic comma and semicolon
                '–' or '—' or '−' => '-',                       // en dash, em dash, minus sign
                _ => char.ToLowerInvariant(ch),
            });
        }
        return sb.ToString();
    }
}
