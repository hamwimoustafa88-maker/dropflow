using System.Globalization;

namespace DropFlow.Core.App;

/// <summary>Reads and writes the clock-style times users type into trim boxes: "90", "1:30", "1:02:03", "12.5".</summary>
public static class TimeInput
{
    /// <summary>
    /// Parses a time. Blank text is valid and gives <c>null</c> ("not set"). Returns false for anything that is not a plain, non-negative
    /// time: negative numbers, minutes or seconds of 60 or more after a colon, more than three parts, exponents.
    /// </summary>
    public static bool TryParse(string? text, out TimeSpan? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(text)) return true;

        var parts = text.Trim().Split(':');
        if (parts.Length > 3) return false;

        double total = 0;
        for (var i = 0; i < parts.Length; i++)
        {
            var isLast = i == parts.Length - 1;
            var style = isLast ? NumberStyles.AllowDecimalPoint : NumberStyles.None;
            if (!double.TryParse(parts[i], style, CultureInfo.InvariantCulture, out var n)) return false;
            if (!double.IsFinite(n) || n < 0) return false;
            // with a colon, every part after the first is a clock field and must stay below 60
            if (parts.Length > 1 && i > 0 && n >= 60) return false;
            total = total * 60 + n;
        }

        value = TimeSpan.FromSeconds(total);
        return true;
    }

    /// <summary>"1:30", "0:05" or "1:02:03" (seconds are rounded). Negative durations give a dash.</summary>
    public static string Format(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero) return "—";
        var seconds = (long)Math.Round(duration.TotalSeconds, MidpointRounding.AwayFromZero);
        var h = seconds / 3600;
        var m = seconds % 3600 / 60;
        var s = seconds % 60;
        return h > 0 ? $"{h}:{m:00}:{s:00}" : $"{m}:{s:00}";
    }
}
