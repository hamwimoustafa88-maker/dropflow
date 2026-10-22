using System.Text.RegularExpressions;

namespace DropFlow.AI.Translation;

/// <summary>
/// Cuts a string into the pieces the translator treats separately: <b>kept</b> pieces (white space, URLs, e-mail addresses, code spans, times,
/// dates, versions and amounts, placeholders such as <c>{0}</c> and <c>%s</c>) that are copied through untouched, and <b>sentences</b> that go to the model. Long
/// sentences are cut at commas or, failing that, between words, because the models are trained on sentences and lose track of very long inputs.
/// </summary>
public static partial class TextSegmenter
{
    public readonly record struct Piece(string Text, bool IsSentence);

    /// <summary>Sentences longer than this many words are cut.</summary>
    public const int MaxWords = 60;

    // A URL ends before trailing punctuation; the e-mail pattern is deliberately loose.
    [GeneratedRegex(@"https?://[^\s<>""]*[^\s<>"".,;:!?)\]]|www\.[^\s<>""]*[^\s<>"".,;:!?)\]]|[\w.+\-]+@[\w\-]+(?:\.[\w\-]+)+|`[^`\n]*`|\{[^{}\s]{0,24}\}|%(?:\d+\$)?[sdfi]|\$\{[^}]*\}|[A-Za-z]:\\[^\s""<>|*?]+|\b\d{1,2}:\d{2}(?::\d{2})?\b|[$€£¥]\s?\d[\d.,]*\d|\d+(?:[.,/\-]\d+){2,}|\b\d{3,}-\d{3,}\b", RegexOptions.CultureInvariant)]
    private static partial Regex ProtectedSpan();

    private static readonly HashSet<string> Abbreviations = new(StringComparer.OrdinalIgnoreCase)
    {
        "mr", "mrs", "ms", "dr", "prof", "sr", "jr", "vs", "etc", "e.g", "i.e", "fig", "no", "st", "inc", "ltd", "co", "approx", "dept", "est", "vol", "pp",
    };

    public static List<Piece> Split(string text)
    {
        var pieces = new List<Piece>();
        var position = 0;
        foreach (Match m in ProtectedSpan().Matches(text))
        {
            if (m.Index > position) AddProse(text[position..m.Index], pieces);
            pieces.Add(new Piece(m.Value, false));
            position = m.Index + m.Length;
        }
        if (position < text.Length) AddProse(text[position..], pieces);
        return pieces;
    }

    private static void AddProse(string prose, List<Piece> pieces)
    {
        foreach (var sentence in SplitSentences(prose))
        {
            // White space around a sentence stays out of the model's input and is put back unchanged.
            var start = 0;
            while (start < sentence.Length && char.IsWhiteSpace(sentence[start])) start++;
            var end = sentence.Length;
            while (end > start && char.IsWhiteSpace(sentence[end - 1])) end--;
            if (start > 0) pieces.Add(new Piece(sentence[..start], false));
            if (end > start)
            {
                var core = sentence[start..end];
                var parts = CutLong(core);
                for (var i = 0; i < parts.Count; i++)
                {
                    if (i > 0) pieces.Add(new Piece(" ", false));
                    pieces.Add(new Piece(parts[i], true));
                }
            }
            if (end < sentence.Length) pieces.Add(new Piece(sentence[end..], false));
        }
    }

    /// <summary>Splits after sentence-ending punctuation that is followed by white space (and at every line break); the white space stays with the sentence it follows.</summary>
    internal static List<string> SplitSentences(string text)
    {
        var result = new List<string>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            var boundary = false;
            var end = i + 1;
            if (c is '\n' or '\r')
            {
                while (end < text.Length && text[end] is '\n' or '\r') end++;
                boundary = true;
            }
            else if (c is '.' or '!' or '?' or '؟' or '…' or '۔' or '؛' or ';')
            {
                // closing quotes and brackets belong to the sentence that just ended
                while (end < text.Length && text[end] is '"' or '\'' or ')' or ']' or '”' or '»' or '.' or '!' or '?') end++;
                var spaceEnd = end;
                while (spaceEnd < text.Length && char.IsWhiteSpace(text[spaceEnd]) && text[spaceEnd] is not '\n' and not '\r') spaceEnd++;
                if (spaceEnd > end && spaceEnd < text.Length && !IsAbbreviationEnd(text, i) && !(c == '.' && char.IsLower(text[spaceEnd])))
                {
                    boundary = true;
                    end = spaceEnd;
                }
            }
            if (!boundary) continue;
            result.Add(text[start..end]);
            start = end;
            i = end - 1;
        }
        if (start < text.Length) result.Add(text[start..]);
        return result;
    }

    /// <summary>True for "Dr.", "e.g." and single initials ("J. Smith"), which do not end a sentence.</summary>
    private static bool IsAbbreviationEnd(string text, int dotIndex)
    {
        if (text[dotIndex] != '.') return false;
        var s = dotIndex;
        while (s > 0 && (char.IsLetter(text[s - 1]) || text[s - 1] == '.')) s--;
        var word = text[s..dotIndex];
        if (word.Length == 1 && char.IsUpper(word[0])) return true;
        return Abbreviations.Contains(word);
    }

    /// <summary>Cuts a sentence of more than <see cref="MaxWords"/> words into parts of at most that many, preferably after a comma.</summary>
    internal static List<string> CutLong(string sentence)
    {
        var words = WordCount(sentence);
        if (words <= MaxWords) return [sentence];

        var middle = sentence.Length / 2;
        var best = -1;
        var bestDistance = int.MaxValue;
        for (var i = 4; i < sentence.Length - 4; i++)
        {
            var c = sentence[i];
            if (c is not (',' or '،' or ';' or '؛' or ':') || i + 1 >= sentence.Length || sentence[i + 1] != ' ') continue;
            var d = Math.Abs(i - middle);
            if (d < bestDistance && WordCount(sentence[..(i + 1)]) >= 4 && WordCount(sentence[(i + 1)..]) >= 4) { best = i + 1; bestDistance = d; }
        }
        if (best < 0)
        {
            // No comma: cut at the space nearest to the middle.
            for (var offset = 0; offset < sentence.Length / 2 && best < 0; offset++)
            {
                if (middle + offset < sentence.Length && sentence[middle + offset] == ' ') best = middle + offset;
                else if (middle - offset > 0 && sentence[middle - offset] == ' ') best = middle - offset;
            }
            if (best < 0) return [sentence];   // one enormous "word": nothing sensible to cut
        }
        var head = sentence[..best].TrimEnd();
        var tail = sentence[best..].TrimStart();
        var result = CutLong(head);
        result.AddRange(CutLong(tail));
        return result;
    }

    private static int WordCount(string text)
    {
        var n = 0;
        var inWord = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c)) inWord = false;
            else if (!inWord) { inWord = true; n++; }
        }
        return n;
    }

    /// <summary>Removes the spaces a tokenizer leaves before punctuation and inside brackets, and collapses runs of spaces.</summary>
    public static string Tidy(string text)
    {
        if (text.Length == 0) return text;
        var s = SpaceBeforePunctuation().Replace(text, "$1");
        s = SpaceAfterOpening().Replace(s, "$1");
        s = TimeOrRatio().Replace(s, "$1:$2");
        s = SpaceRun().Replace(s, " ");
        return s.Trim();
    }

    /// <summary>
    /// Cleans a model answer for one sentence: tidies spaces, and when the source ended with a full stop, question mark or exclamation mark
    /// but the answer ends with no punctuation (the models often drop it), puts the matching mark back.
    /// </summary>
    public static string Finish(string source, string answer, TranslationDirection direction)
    {
        var text = Tidy(answer);
        // The English-to-Arabic model sometimes scatters vowel marks over its answer ("نَفْتحُ"); modern Arabic is written without them.
        // Fathatan (as in "شكراً") and shadda are kept because they are part of the spelling.
        if (direction == TranslationDirection.EnglishToArabic) text = Vowels().Replace(text, "");
        if (text.Length == 0) return text;
        var s = source.TrimEnd();
        if (s.Length == 0) return text;
        var last = s[^1];
        var mark = last switch
        {
            '.' or '\u06D4' => ".",
            '!' => "!",
            '?' or '\u061F' => direction == TranslationDirection.EnglishToArabic ? "\u061F" : "?",
            _ => null,
        };
        if (mark is null)
        {
            // A fragment that does not end a sentence (a piece before a time or a link) must not gain a full stop from the model.
            if ((char.IsLetterOrDigit(last) || last == ',') && text.EndsWith('.')) text = text.TrimEnd('.').TrimEnd();
            return text;
        }
        var end = text[^1];
        if (char.IsLetterOrDigit(end) || end == '\u0651') return text + mark;
        return text;
    }

    [GeneratedRegex(@"[\u064C-\u0650\u0652\u0670]", RegexOptions.CultureInvariant)]
    private static partial Regex Vowels();

    [GeneratedRegex(@"(\d):\s+(\d)", RegexOptions.CultureInvariant)]
    private static partial Regex TimeOrRatio();

    [GeneratedRegex(@"\s+([,.;:!?%)\]\u060C\u061B\u061F\u2026])", RegexOptions.CultureInvariant)]
    private static partial Regex SpaceBeforePunctuation();

    [GeneratedRegex(@"([(\[])\s+", RegexOptions.CultureInvariant)]
    private static partial Regex SpaceAfterOpening();

    [GeneratedRegex(@"[ \t]{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex SpaceRun();
}
