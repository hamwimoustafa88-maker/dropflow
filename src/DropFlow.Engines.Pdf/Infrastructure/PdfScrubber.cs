using System.Text;
using System.Text.RegularExpressions;

namespace DropFlow.Engines.Pdf.Infrastructure;

/// <summary>
/// PDFsharp stamps every file it saves: /Creator and /Producer (it even appends "(Original: ...)" with the producer it found), dates in
/// the Info dictionary, a generated XMP packet and a "% Created with PDFsharp" comment, and it cannot be told not to. For redaction that
/// is not good enough ("strip the metadata"), so the saved bytes are scrubbed. Every edit overwrites bytes in place with spaces and never
/// changes the length of the file, so the cross-reference offsets stay valid; the Info entries and the catalog's /Metadata link simply
/// disappear into whitespace, which every PDF reader skips. If the layout is not the one PDFsharp 6.2.4 writes (for example a
/// cross-reference stream) nothing is touched. Callers must still check that the result opens.
/// </summary>
internal static class PdfScrubber
{
    private static readonly string[] InfoKeys = ["/Title", "/Author", "/Subject", "/Keywords", "/Creator", "/Producer", "/CreationDate", "/ModDate"];

    public static byte[] RemoveDocumentMetadata(byte[] pdf)
    {
        // Latin-1 maps every byte to exactly one char, so positions in the string are positions in the file and the round trip is lossless.
        var chars = Encoding.Latin1.GetString(pdf).ToCharArray();
        var text = new string(chars);

        var trailer = text.LastIndexOf("trailer", StringComparison.Ordinal);
        if (trailer >= 0)
        {
            var tail = text[trailer..];
            if (Reference(tail, "/Info") is { } info && ObjectSpan(text, info.Number, info.Generation) is { } infoSpan)
            {
                foreach (var key in InfoKeys) BlankEntry(chars, infoSpan.Start, infoSpan.End, key);
            }

            if (Reference(tail, "/Root") is { } root && ObjectSpan(text, root.Number, root.Generation) is { } rootSpan)
                UnlinkXmp(chars, text, rootSpan.Start, rootSpan.End);
        }

        BlankComment(chars, text, "% Created with PDFsharp");
        return Encoding.Latin1.GetBytes(chars);
    }

    private readonly record struct Ref(int Number, int Generation);

    private readonly record struct Span(int Start, int End);

    private static Ref? Reference(string text, string key)
    {
        var m = Regex.Match(text, Regex.Escape(key) + @"\s+(\d+)\s+(\d+)\s+R");
        return m.Success ? new Ref(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value)) : null;
    }

    /// <summary>The text of indirect object "n g obj ... endobj" (start of the body to the endobj keyword).</summary>
    private static Span? ObjectSpan(string text, int number, int generation)
    {
        var header = $"{number} {generation} obj";
        for (var from = 0; ;)
        {
            var at = text.IndexOf(header, from, StringComparison.Ordinal);
            if (at < 0) return null;
            if (at == 0 || text[at - 1] is '\n' or '\r')
            {
                var end = text.IndexOf("endobj", at, StringComparison.Ordinal);
                return end < 0 ? null : new Span(at + header.Length, end);
            }
            from = at + 1;
        }
    }

    /// <summary>Overwrites "/Key value" (a literal string, hex string, name or number) inside the span with spaces.</summary>
    private static void BlankEntry(char[] chars, int start, int end, string key)
    {
        var body = new string(chars, start, end - start);
        var match = Regex.Match(body, Regex.Escape(key) + @"(?![A-Za-z0-9])\s*");
        if (!match.Success) return;

        var valueStart = match.Index + match.Length;
        var valueEnd = ValueEnd(body, valueStart);
        if (valueEnd <= valueStart) return;
        for (var i = match.Index; i < valueEnd; i++) chars[start + i] = ' ';
    }

    private static int ValueEnd(string body, int index)
    {
        if (index >= body.Length) return index;
        switch (body[index])
        {
            case '(':
            {
                var depth = 0;
                for (var i = index; i < body.Length; i++)
                {
                    if (body[i] == '\\') { i++; continue; }
                    if (body[i] == '(') depth++;
                    else if (body[i] == ')' && --depth == 0) return i + 1;
                }
                return index;
            }
            case '<' when index + 1 < body.Length && body[index + 1] != '<':
            {
                var close = body.IndexOf('>', index);
                return close < 0 ? index : close + 1;
            }
            default:
            {
                var i = index;
                while (i < body.Length && !char.IsWhiteSpace(body[i]) && body[i] is not ('/' or '>' or '(' or '<' or '[' or ']')) i++;
                return i;
            }
        }
    }

    /// <summary>Removes the catalog's /Metadata link and blanks the XMP stream it pointed to (the unreferenced object stays in the file, empty).</summary>
    private static void UnlinkXmp(char[] chars, string text, int catalogStart, int catalogEnd)
    {
        var catalog = text.Substring(catalogStart, catalogEnd - catalogStart);
        var match = Regex.Match(catalog, @"/Metadata\s+(\d+)\s+(\d+)\s+R");
        if (!match.Success) return;

        for (var i = 0; i < match.Length; i++) chars[catalogStart + match.Index + i] = ' ';
        if (ObjectSpan(text, int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value)) is not { } xmp) return;

        var stream = text.IndexOf("stream", xmp.Start, xmp.End - xmp.Start, StringComparison.Ordinal);
        if (stream < 0) return;
        var data = stream + "stream".Length;
        if (data < text.Length && text[data] == '\r') data++;
        if (data < text.Length && text[data] == '\n') data++;
        var endStream = text.IndexOf("endstream", data, xmp.End - data, StringComparison.Ordinal);
        if (endStream < 0) return;
        for (var i = data; i < endStream; i++) chars[i] = ' ';
    }

    private static void BlankComment(char[] chars, string text, string marker)
    {
        var at = text.IndexOf(marker, StringComparison.Ordinal);
        if (at < 0) return;
        for (var i = at; i < text.Length && text[i] is not ('\n' or '\r'); i++) chars[i] = ' ';
    }
}
