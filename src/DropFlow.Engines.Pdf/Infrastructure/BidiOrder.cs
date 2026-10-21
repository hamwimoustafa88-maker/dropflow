namespace DropFlow.Engines.Pdf.Infrastructure;

internal enum CharKind { R, L, Digit, Neutral }

/// <summary>
/// The small slice of the Unicode bidi algorithm DropFlow needs: classify characters, give neutrals a direction, and reorder a
/// sequence of "units" (characters when stamping text, glyphs when reading a PDF line) between visual and logical order.
/// The reordering is its own inverse for the simple one-level cases handled here, so the same routine converts both ways.
/// </summary>
internal static class BidiOrder
{
    public static CharKind KindOf(char c)
    {
        if (c is >= '0' and <= '9' or >= '٠' and <= '٩' or >= '۰' and <= '۹') return CharKind.Digit;
        if (c is '،' or '؛' or '؟' or >= '٪' and <= '٭') return CharKind.Neutral;
        if (c is >= '֐' and <= 'ࣿ' or >= 'יִ' and <= '﷿' or >= 'ﹰ' and <= '﻿')
            return char.IsLetter(c) || c == 'ـ' ? CharKind.R : CharKind.Neutral;
        return char.IsLetter(c) ? CharKind.L : CharKind.Neutral;
    }

    /// <summary>Kind of a multi-character unit (a glyph): the first strong or digit character decides.</summary>
    public static CharKind KindOf(string unit)
    {
        var digit = false;
        foreach (var c in unit)
        {
            var kind = KindOf(c);
            if (kind is CharKind.R or CharKind.L) return kind;
            if (kind == CharKind.Digit) digit = true;
        }
        return digit ? CharKind.Digit : CharKind.Neutral;
    }

    public static CharKind FirstStrong(IReadOnlyList<CharKind> kinds)
    {
        foreach (var kind in kinds)
            if (kind is CharKind.R or CharKind.L) return kind;
        return CharKind.L;
    }

    /// <summary>Right-to-left when strong right-to-left letters are at least as common as strong left-to-right ones.</summary>
    public static bool MostlyRtl(IReadOnlyList<CharKind> kinds)
    {
        var rtl = kinds.Count(k => k == CharKind.R);
        var ltr = kinds.Count(k => k == CharKind.L);
        return rtl > 0 && rtl >= ltr;
    }

    /// <summary>True (right-to-left) or false for every unit: strong letters decide, digits follow the letters before them, neutrals follow their neighbours.</summary>
    public static bool[] ResolveDirections(IReadOnlyList<CharKind> kinds, bool paragraphRtl)
    {
        var result = new bool[kinds.Count];

        var lastStrongRtl = paragraphRtl;
        for (var i = 0; i < kinds.Count; i++)
        {
            switch (kinds[i])
            {
                case CharKind.R: lastStrongRtl = true; result[i] = true; break;
                case CharKind.L: lastStrongRtl = false; result[i] = false; break;
                case CharKind.Digit: result[i] = lastStrongRtl; break;
            }
        }

        for (var i = 0; i < kinds.Count; i++)
        {
            if (kinds[i] != CharKind.Neutral) continue;
            var left = i;
            while (left >= 0 && kinds[left] == CharKind.Neutral) left--;
            var right = i;
            while (right < kinds.Count && kinds[right] == CharKind.Neutral) right++;
            var leftRtl = left >= 0 ? result[left] : paragraphRtl;
            var rightRtl = right < kinds.Count ? result[right] : paragraphRtl;
            result[i] = leftRtl == rightRtl ? leftRtl : paragraphRtl;
        }
        return result;
    }

    /// <summary>Maximal stretches of equal direction.</summary>
    public static List<(bool Rtl, int Start, int Length)> Runs(bool[] directions)
    {
        var runs = new List<(bool, int, int)>();
        for (var i = 0; i < directions.Length;)
        {
            var j = i;
            while (j < directions.Length && directions[j] == directions[i]) j++;
            runs.Add((directions[i], i, j - i));
            i = j;
        }
        return runs;
    }

    /// <summary>
    /// Reorders units (glyphs read left to right from a page) into logical reading order. Left-to-right runs and digit groups keep
    /// their internal order; right-to-left runs are reversed; with a right-to-left paragraph the runs themselves are reversed too.
    /// </summary>
    public static List<T> VisualToLogical<T>(IReadOnlyList<T> units, Func<T, CharKind> kindOf, bool paragraphRtl)
    {
        var kinds = units.Select(kindOf).ToList();
        var directions = ResolveDirections(kinds, paragraphRtl);
        var runs = Runs(directions);
        if (paragraphRtl) runs.Reverse();

        var result = new List<T>(units.Count);
        foreach (var (rtl, start, length) in runs)
        {
            if (!rtl)
            {
                for (var i = start; i < start + length; i++) result.Add(units[i]);
                continue;
            }

            // Inside a right-to-left run: reverse the order of digit groups and reverse everything else glyph by glyph.
            var pieces = new List<List<T>>();
            for (var i = start; i < start + length;)
            {
                var isDigit = kinds[i] == CharKind.Digit;
                var j = i;
                while (j < start + length && (kinds[j] == CharKind.Digit) == isDigit) j++;
                var piece = units.Skip(i).Take(j - i).ToList();
                if (!isDigit) piece.Reverse();
                pieces.Add(piece);
                i = j;
            }
            pieces.Reverse();
            foreach (var piece in pieces) result.AddRange(piece);
        }
        return result;
    }
}
