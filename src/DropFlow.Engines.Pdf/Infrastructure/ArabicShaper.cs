using System.Text;

namespace DropFlow.Engines.Pdf.Infrastructure;

/// <summary>
/// PDFsharp draws one glyph per character, left to right, with no text shaping. To stamp Arabic text (watermark, page-number format)
/// this converts a logical string into "visual order": letters replaced by their joining forms (Arabic Presentation Forms-B, which
/// Arial and Tahoma contain), right-to-left runs reversed, digits and Latin words left in reading order, brackets mirrored.
/// It is a deliberately small subset of the Unicode bidi algorithm - enough for a watermark or a "Page 3 of 9" line, not for paragraphs.
/// Diacritics are dropped. Persian/Urdu-only letters are kept but do not join.
/// </summary>
public static class ArabicShaper
{
    // iso, final, initial, medial (0 = form does not exist)
    private static readonly Dictionary<char, (char Iso, char Fin, char Ini, char Med)> Forms = new()
    {
        ['ء'] = ('ﺀ', '\0', '\0', '\0'),
        ['آ'] = ('ﺁ', 'ﺂ', '\0', '\0'),
        ['أ'] = ('ﺃ', 'ﺄ', '\0', '\0'),
        ['ؤ'] = ('ﺅ', 'ﺆ', '\0', '\0'),
        ['إ'] = ('ﺇ', 'ﺈ', '\0', '\0'),
        ['ئ'] = ('ﺉ', 'ﺊ', 'ﺋ', 'ﺌ'),
        ['ا'] = ('ﺍ', 'ﺎ', '\0', '\0'),
        ['ب'] = ('ﺏ', 'ﺐ', 'ﺑ', 'ﺒ'),
        ['ة'] = ('ﺓ', 'ﺔ', '\0', '\0'),
        ['ت'] = ('ﺕ', 'ﺖ', 'ﺗ', 'ﺘ'),
        ['ث'] = ('ﺙ', 'ﺚ', 'ﺛ', 'ﺜ'),
        ['ج'] = ('ﺝ', 'ﺞ', 'ﺟ', 'ﺠ'),
        ['ح'] = ('ﺡ', 'ﺢ', 'ﺣ', 'ﺤ'),
        ['خ'] = ('ﺥ', 'ﺦ', 'ﺧ', 'ﺨ'),
        ['د'] = ('ﺩ', 'ﺪ', '\0', '\0'),
        ['ذ'] = ('ﺫ', 'ﺬ', '\0', '\0'),
        ['ر'] = ('ﺭ', 'ﺮ', '\0', '\0'),
        ['ز'] = ('ﺯ', 'ﺰ', '\0', '\0'),
        ['س'] = ('ﺱ', 'ﺲ', 'ﺳ', 'ﺴ'),
        ['ش'] = ('ﺵ', 'ﺶ', 'ﺷ', 'ﺸ'),
        ['ص'] = ('ﺹ', 'ﺺ', 'ﺻ', 'ﺼ'),
        ['ض'] = ('ﺽ', 'ﺾ', 'ﺿ', 'ﻀ'),
        ['ط'] = ('ﻁ', 'ﻂ', 'ﻃ', 'ﻄ'),
        ['ظ'] = ('ﻅ', 'ﻆ', 'ﻇ', 'ﻈ'),
        ['ع'] = ('ﻉ', 'ﻊ', 'ﻋ', 'ﻌ'),
        ['غ'] = ('ﻍ', 'ﻎ', 'ﻏ', 'ﻐ'),
        ['ـ'] = ('ـ', 'ـ', 'ـ', 'ـ'), // tatweel connects on both sides
        ['ف'] = ('ﻑ', 'ﻒ', 'ﻓ', 'ﻔ'),
        ['ق'] = ('ﻕ', 'ﻖ', 'ﻗ', 'ﻘ'),
        ['ك'] = ('ﻙ', 'ﻚ', 'ﻛ', 'ﻜ'),
        ['ل'] = ('ﻝ', 'ﻞ', 'ﻟ', 'ﻠ'),
        ['م'] = ('ﻡ', 'ﻢ', 'ﻣ', 'ﻤ'),
        ['ن'] = ('ﻥ', 'ﻦ', 'ﻧ', 'ﻨ'),
        ['ه'] = ('ﻩ', 'ﻪ', 'ﻫ', 'ﻬ'),
        ['و'] = ('ﻭ', 'ﻮ', '\0', '\0'),
        ['ى'] = ('ﻯ', 'ﻰ', '\0', '\0'),
        ['ي'] = ('ﻱ', 'ﻲ', 'ﻳ', 'ﻴ'),
    };

    // lam + alef ligatures: alef variant -> (isolated, final)
    private static readonly Dictionary<char, (char Iso, char Fin)> LamAlef = new()
    {
        ['آ'] = ('ﻵ', 'ﻶ'),
        ['أ'] = ('ﻷ', 'ﻸ'),
        ['إ'] = ('ﻹ', 'ﻺ'),
        ['ا'] = ('ﻻ', 'ﻼ'),
    };

    public static bool ContainsRtl(string text) => text.Any(c => BidiOrder.KindOf(c) == CharKind.R);

    public static string ToVisual(string text)
    {
        if (string.IsNullOrEmpty(text) || !ContainsRtl(text)) return text;

        var chars = StripDiacritics(text);
        var kinds = chars.Select(BidiOrder.KindOf).ToList();
        var paragraphRtl = BidiOrder.FirstStrong(kinds) == CharKind.R;
        var rtl = BidiOrder.ResolveDirections(kinds, paragraphRtl);

        var runs = BidiOrder.Runs(rtl);
        if (paragraphRtl) runs.Reverse();

        var sb = new StringBuilder(chars.Length);
        foreach (var (isRtl, start, length) in runs)
        {
            if (isRtl) AppendRtlRun(sb, chars, start, length);
            else sb.Append(chars, start, length);
        }
        return sb.ToString();
    }

    private static void AppendRtlRun(StringBuilder sb, char[] chars, int start, int length)
    {
        // Inside a right-to-left run digit groups keep their reading order; everything else is shaped and reversed.
        var pieces = new List<string>();
        for (var i = start; i < start + length;)
        {
            var isDigit = BidiOrder.KindOf(chars[i]) == CharKind.Digit;
            var j = i;
            while (j < start + length && (BidiOrder.KindOf(chars[j]) == CharKind.Digit) == isDigit) j++;
            var piece = new string(chars, i, j - i);
            pieces.Add(isDigit ? piece : ReverseVisual(Shape(piece)));
            i = j;
        }
        pieces.Reverse();
        foreach (var piece in pieces) sb.Append(piece);
    }

    private static string ReverseVisual(string shaped)
    {
        var arr = shaped.ToCharArray();
        Array.Reverse(arr);
        for (var i = 0; i < arr.Length; i++) arr[i] = Mirror(arr[i]);
        return new string(arr);
    }

    private static char Mirror(char c) => c switch
    {
        '(' => ')',
        ')' => '(',
        '[' => ']',
        ']' => '[',
        '{' => '}',
        '}' => '{',
        '<' => '>',
        '>' => '<',
        '«' => '»',
        '»' => '«',
        _ => c,
    };

    private static string Shape(string logical)
    {
        var sb = new StringBuilder(logical.Length);
        for (var i = 0; i < logical.Length; i++)
        {
            var c = logical[i];
            if (!Forms.TryGetValue(c, out var form))
            {
                sb.Append(c);
                continue;
            }

            var prevJoins = i > 0 && Forms.TryGetValue(logical[i - 1], out var prev) && prev.Ini != '\0' && form.Fin != '\0';

            if (c == 'ل' && i + 1 < logical.Length && LamAlef.TryGetValue(logical[i + 1], out var ligature))
            {
                sb.Append(prevJoins ? ligature.Fin : ligature.Iso);
                i++;
                continue;
            }

            var nextJoins = form.Ini != '\0' && i + 1 < logical.Length && Forms.TryGetValue(logical[i + 1], out var next) && next.Fin != '\0';
            sb.Append(prevJoins && nextJoins ? form.Med
                : prevJoins ? form.Fin
                : nextJoins ? form.Ini
                : form.Iso);
        }
        return sb.ToString();
    }

    // Arabic marks U+064B..U+065F and U+0670 (harakat, shadda, dagger alef) plus the left/right-to-left marks U+200E and U+200F.
    private static char[] StripDiacritics(string text) =>
        text.Where(c => !(c is >= (char)0x064B and <= (char)0x065F || c is (char)0x0670 or (char)0x200E or (char)0x200F)).ToArray();
}
