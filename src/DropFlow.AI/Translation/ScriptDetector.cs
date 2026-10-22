namespace DropFlow.AI.Translation;

/// <summary>Counts letters by script. The translation tools use it to decide the direction and to skip text that is already in the target language.</summary>
public readonly record struct ScriptCounts(int Arabic, int Latin, int Other)
{
    public int Letters => Arabic + Latin + Other;

    /// <summary>Share of Arabic-script letters among all letters (0 when there are no letters).</summary>
    public double ArabicRatio => Letters == 0 ? 0 : (double)Arabic / Letters;

    public static ScriptCounts operator +(ScriptCounts a, ScriptCounts b) => new(a.Arabic + b.Arabic, a.Latin + b.Latin, a.Other + b.Other);
}

public static class ScriptDetector
{
    public static ScriptCounts Count(ReadOnlySpan<char> text)
    {
        int arabic = 0, latin = 0, other = 0;
        foreach (var c in text)
        {
            if (!char.IsLetter(c)) continue;
            if (IsArabicLetter(c)) arabic++;
            else if (c < 0x250 || (c >= 0x1E00 && c < 0x1F00)) latin++;   // Basic Latin, Latin-1, Latin Extended, Latin Extended Additional
            else other++;
        }
        return new ScriptCounts(arabic, latin, other);
    }

    public static bool IsArabicLetter(char c) =>
        char.IsLetter(c) && (c is >= '؀' and <= 'ۿ' or >= 'ݐ' and <= 'ݿ' or >= 'ࢠ' and <= 'ࣿ'
            or >= 'ﭐ' and <= '﷿' or >= 'ﹰ' and <= '﻿');

    /// <summary>
    /// The direction that fits a text, judged by its letters: mostly Arabic script means Arabic to English, otherwise English to Arabic.
    /// Null when there are no Arabic or Latin letters at all (digits only, symbols, another script).
    /// </summary>
    public static TranslationDirection? DetectDirection(ScriptCounts counts)
    {
        if (counts.Arabic + counts.Latin == 0) return null;
        return counts.Arabic >= counts.Latin ? TranslationDirection.ArabicToEnglish : TranslationDirection.EnglishToArabic;
    }

    public static TranslationDirection? DetectDirection(ReadOnlySpan<char> text) => DetectDirection(Count(text));

    /// <summary>
    /// True when a string is worth sending to the model for this direction. Strings without letters of the source script, and strings that
    /// are mostly in the target script already, are left alone (a document may mix languages).
    /// </summary>
    public static bool NeedsTranslation(ReadOnlySpan<char> text, TranslationDirection direction)
    {
        var c = Count(text);
        return direction switch
        {
            // English source: needs Latin letters and must not be mostly Arabic already.
            TranslationDirection.EnglishToArabic => c.Latin > 0 && c.ArabicRatio < 0.5,
            // Arabic source: a third of the letters in Arabic script is enough ("ملف report.pdf" counts).
            _ => c.Arabic > 0 && c.ArabicRatio >= 0.3,
        };
    }
}
