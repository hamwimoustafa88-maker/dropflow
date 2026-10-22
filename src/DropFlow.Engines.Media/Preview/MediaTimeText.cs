// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using DropFlow.Engines.Media.Ffmpeg;

namespace DropFlow.Engines.Media.Preview;

/// <summary>
/// Times as the media editors show and read them, and as the media tools' options expect them. Display is a clock with hundredths
/// ("1:02.50", "1:02:03.25"); reading accepts everything the tools accept (seconds, clock times, Arabic-Indic digits).
/// </summary>
public static class MediaTimeText
{
    /// <summary>"0:05.25", "12:00.00", "1:02:03.40". Negative times show as zero.</summary>
    public static string Clock(double seconds)
    {
        var cs = (long)Math.Round(Math.Max(0, seconds) * 100, MidpointRounding.AwayFromZero);
        var t = TimeSpan.FromMilliseconds(cs * 10);
        var hundredths = (t.Milliseconds / 10).ToString("00", CultureInfo.InvariantCulture);
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}.{hundredths}"
            : $"{(int)t.TotalMinutes}:{t.Seconds:00}.{hundredths}";
    }

    /// <summary>A short length for labels: "45.5 s", "2:05", "1:02:03".</summary>
    public static string Length(double seconds)
    {
        if (seconds < 60) return Math.Round(Math.Max(0, seconds), 1).ToString("0.#", CultureInfo.InvariantCulture) + " s";
        var t = TimeSpan.FromSeconds(Math.Round(seconds));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{(int)t.TotalMinutes}:{t.Seconds:00}";
    }

    /// <summary>Reads a typed time ("1:30", "90.5", "١:٣٠"); false for anything else.</summary>
    public static bool TryParse(string? text, out double seconds) => TimeSpec.TryParseSeconds(text, out seconds);

    /// <summary>The value a tool option expects: invariant seconds with up to three decimals ("12.5").</summary>
    public static string ForOption(double seconds) => TimeSpec.Format(Math.Max(0, seconds));
}
