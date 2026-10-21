using System.Globalization;
using System.Text;

namespace DropFlow.Engines.Pdf.Infrastructure;

/// <summary>Reads lists of plain numbers typed by a person ("10, 20, 30.5, 5"), including Arabic-Indic digits and the Arabic comma and decimal mark.</summary>
internal static class NumberList
{
    /// <summary>Arabic-Indic and Persian digits become ASCII; the Arabic decimal mark and comma become '.' and ','; '%' and "mm" are dropped.</summary>
    public static string Normalise(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            sb.Append(ch switch
            {
                >= '٠' and <= '٩' => (char)('0' + (ch - '٠')),
                >= '۰' and <= '۹' => (char)('0' + (ch - '۰')),
                '٫' => '.',
                '،' or '؛' => ',',
                '−' or '–' or '—' => '-',
                _ => ch,
            });
        }
        return sb.ToString().Replace("%", "", StringComparison.Ordinal).Replace("mm", "", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Parses exactly <paramref name="count"/> numbers separated by commas, semicolons or spaces. Decimals use a dot.</summary>
    public static bool TryParse(string? text, int count, out double[] values)
    {
        values = [];
        if (string.IsNullOrWhiteSpace(text)) return false;
        var parts = Normalise(text).Split([',', ';', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != count) return false;

        var result = new double[count];
        for (var i = 0; i < count; i++)
        {
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out result[i]) || !double.IsFinite(result[i])) return false;
        }

        values = result;
        return true;
    }
}
