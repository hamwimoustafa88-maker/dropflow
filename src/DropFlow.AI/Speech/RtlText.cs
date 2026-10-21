namespace DropFlow.AI.Speech;

/// <summary>How right-to-left lines are marked so players that do not run the bidi algorithm per cue still lay them out correctly.</summary>
public enum RtlMarkStyle
{
    None,

    /// <summary>U+200F (RIGHT-TO-LEFT MARK) before and after the line. Widest player support.</summary>
    Rlm,

    /// <summary>U+202B (RIGHT-TO-LEFT EMBEDDING) ... U+202C (POP DIRECTIONAL FORMATTING).</summary>
    Embedding,
}

/// <summary>Right-to-left text helpers shared by subtitles and OCR.</summary>
public static class RtlText
{
    /// <summary>U+200F RIGHT-TO-LEFT MARK.</summary>
    public const char RightToLeftMark = (char)0x200F;

    /// <summary>U+202B RIGHT-TO-LEFT EMBEDDING.</summary>
    public const char RightToLeftEmbedding = (char)0x202B;

    /// <summary>U+202C POP DIRECTIONAL FORMATTING.</summary>
    public const char PopDirectionalFormatting = (char)0x202C;

    /// <summary>Letters of scripts written right to left (Arabic, Hebrew, Syriac, Thaana, N'Ko, Arabic presentation forms).</summary>
    public static bool IsRtlLetter(int codePoint) =>
        codePoint is >= 0x0590 and <= 0x08FF
        or >= 0xFB1D and <= 0xFDFF
        or >= 0xFE70 and <= 0xFEFF
        or >= 0x10800 and <= 0x10FFF
        or >= 0x1E800 and <= 0x1EFFF;

    /// <summary>Counts strong right-to-left and left-to-right letters.</summary>
    public static (int Rtl, int Ltr) CountStrong(string text)
    {
        int rtl = 0, ltr = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (!System.Text.Rune.IsLetter(rune)) continue; // digits (including Arabic-Indic), punctuation and marks are not strong
            if (IsRtlLetter(rune.Value)) rtl++;
            else ltr++;
        }
        return (rtl, ltr);
    }

    /// <summary>
    /// True when the text should be laid out right to left: it has right-to-left letters and at least as many as left-to-right ones.
    /// (Majority beats "first strong character" because an Arabic sentence that starts with a Latin brand name is still Arabic.)
    /// </summary>
    public static bool IsRightToLeft(string text)
    {
        var (rtl, ltr) = CountStrong(text);
        return rtl > 0 && rtl >= ltr;
    }

    /// <summary>Wraps a single line with the requested marks when (and only when) the line is right to left.</summary>
    public static string MarkLine(string line, RtlMarkStyle style)
    {
        if (style == RtlMarkStyle.None || line.Length == 0 || !IsRightToLeft(line)) return line;
        return style switch
        {
            RtlMarkStyle.Rlm => RightToLeftMark + line + RightToLeftMark,
            RtlMarkStyle.Embedding => RightToLeftEmbedding + line + PopDirectionalFormatting,
            _ => line,
        };
    }

    /// <summary>Applies <see cref="MarkLine"/> to every line of a multi-line cue text.</summary>
    public static string MarkLines(string text, RtlMarkStyle style)
    {
        if (style == RtlMarkStyle.None) return text;
        return string.Join('\n', text.Split('\n').Select(l => MarkLine(l, style)));
    }

    /// <summary>Removes the bidi marks this class adds (for tests and for round-tripping text).</summary>
    public static string StripMarks(string text) =>
        text.Replace(RightToLeftMark.ToString(), "")
            .Replace(RightToLeftEmbedding.ToString(), "")
            .Replace(PopDirectionalFormatting.ToString(), "");
}
