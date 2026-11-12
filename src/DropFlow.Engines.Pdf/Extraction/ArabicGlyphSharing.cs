// SPDX-License-Identifier: GPL-3.0-or-later
namespace DropFlow.Engines.Pdf.Extraction;

/// <summary>
/// Fonts such as Tahoma draw the joined (initial and medial) forms of Arabic heh and Persian heh doachashmee with ONE glyph, and the
/// same for Arabic yeh and Farsi yeh. A PDF's ToUnicode table can map that shared glyph to only one character, and Chromium/Edge
/// pick the Persian/Urdu ones, so "هذه" comes out as "ھذه" and "الكمية" as "الكمیة": the words look right but search, copy and
/// translation fail. In Arabic text (no Persian or Urdu letters on the line) those joined forms are put back to the Arabic letters.
/// Final and isolated forms are never touched: their glyphs differ, so the PDF already says which letter it is.
/// </summary>
internal static class ArabicGlyphSharing
{
    private const char HehDoachashmee = 'ھ';
    private const char FarsiYeh = 'ی';
    private const char Heh = 'ه';
    private const char Yeh = 'ي';

    /// <summary>Letters only Persian or Urdu use (keheh, gaf, peh, tcheh, jeh, tteh, ddal, rreh, noon ghunna, heh goal, yeh barree).</summary>
    private static bool IsPersianOrUrduOnly(char c) =>
        c is 'ک' or 'گ' or 'پ' or 'چ' or 'ژ' or 'ٹ' or 'ڈ' or 'ڑ' or 'ں' or 'ہ' or 'ے';

    /// <summary>Arabic letters that join to the following letter (so the one before them is in a joined form).</summary>
    private static bool IsArabicLetter(char c) => c is >= 'ؠ' and <= 'ي' or >= 'ٮ' and <= 'ۓ' or 'ە';

    public static string Fix(string text)
    {
        if (text.IndexOf(HehDoachashmee) < 0 && text.IndexOf(FarsiYeh) < 0) return text;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (IsPersianOrUrduOnly(c)) return text;
            // A Farsi yeh at the end of a word is a real Persian letter (its final form has no dots).
            if (c == FarsiYeh && !NextIsLetter(text, i)) return text;
        }

        var chars = text.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (!NextIsLetter(text, i)) continue;
            if (chars[i] == HehDoachashmee) chars[i] = Heh;
            else if (chars[i] == FarsiYeh) chars[i] = Yeh;
        }
        return new string(chars);
    }

    private static bool NextIsLetter(string text, int i)
    {
        var j = i + 1;
        while (j < text.Length && text[j] is >= 'ً' and <= 'ٟ' or 'ٰ' or 'ـ') j++; // harakat, tatweel
        return j < text.Length && IsArabicLetter(text[j]);
    }
}
