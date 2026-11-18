// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;
using DropFlow.Engines.Pdf.Infrastructure;
using DropFlow.Engines.Pdf.Office;

namespace DropFlow.Engines.Pdf.Books;

/// <summary>
/// Reads Mobipocket (.mobi) and Kindle KF8 (.azw3) books, written from the published descriptions of the formats: a Palm database whose
/// first record holds the PalmDOC header, the MOBI header and the EXTH metadata; the text in records compressed with PalmDOC LZ77 or
/// HUFF/CDIC; pictures in later records. Books protected by DRM are refused.
/// </summary>
internal static partial class Mobi
{
    private const int PalmDocCompression = 2;
    private const int HuffCdicCompression = 17480;
    private const long MaxTextBytes = 256L * 1024 * 1024;

    static Mobi() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static EpubBook Read(string path)
    {
        var name = Path.GetFileName(path);
        var data = File.ReadAllBytes(path);
        try
        {
            return Read(data, Path.GetFileNameWithoutExtension(path));
        }
        catch (ConversionFailure failure)
        {
            throw new ConversionFailure($"{name}: {failure.Message}");
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException or OverflowException)
        {
            throw new ConversionFailure($"{name} is damaged or is not a Mobipocket/Kindle book.");
        }
    }

    internal static EpubBook Read(byte[] data, string fallbackTitle)
    {
        var records = PalmDatabase(data, out var type);
        if (type is not ("BOOKMOBI" or "TEXtREAd")) throw new ConversionFailure("it is not a Mobipocket or Kindle book.");
        var header = new MobiHeader(records, 0, type);

        // A combined file carries an older Mobipocket part and a KF8 part after a boundary. The Mobipocket part is read; a file that is
        // KF8 only (version 8 in its first header) is read as KF8.
        var text = ExtractText(records, header);
        var title = header.Exth.GetValueOrDefault(503) ?? header.FullName ?? fallbackTitle;
        var author = header.Authors.Count > 0 ? string.Join(", ", header.Authors) : null;

        string body;
        if (header.IsKf8)
        {
            body = Kf8Body(text, records, header);
        }
        else if (type == "TEXtREAd")
        {
            body = PlainTextBody(header.Encoding.GetString(text));
        }
        else
        {
            body = Mobi6Body(text, records, header);
        }

        var language = header.Language ?? BookLanguage.Detect(body);
        return new EpubBook(string.IsNullOrWhiteSpace(title) ? fallbackTitle : title.Trim(), author, language, body) { Cover = Cover(records, header) };
    }

    /// <summary>The picture EXTH 201 names (an offset from the first picture record), when it is one.</summary>
    private static byte[]? Cover(List<byte[]> records, MobiHeader header)
    {
        if (header.FirstImage < 0 || header.CoverOffset < 0) return null;
        var index = header.FirstImage + header.CoverOffset;
        return index < records.Count && records[index].Length <= EpubReader.MaxImageBytes && BookHtml.SniffImage(records[index]) is not null ? records[index] : null;
    }

    // ---- Palm database ------------------------------------------------------------------------------------------------------

    /// <summary>The records of a Palm database (78-byte header, then eight bytes per record: offset, attributes, id).</summary>
    internal static List<byte[]> PalmDatabase(byte[] data, out string type)
    {
        if (data.Length < 86) throw new ConversionFailure("the file is too short to be an e-book.");
        type = Encoding.ASCII.GetString(data, 60, 8);
        int count = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(76));
        if (count == 0 || 78 + count * 8 > data.Length) throw new ConversionFailure("its record list is damaged.");
        var offsets = new long[count + 1];
        for (var i = 0; i < count; i++) offsets[i] = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(78 + i * 8));
        offsets[count] = data.Length;
        var records = new List<byte[]>(count);
        for (var i = 0; i < count; i++)
        {
            var start = offsets[i];
            var end = offsets[i + 1];
            if (start > data.Length || end > data.Length || end < start) throw new ConversionFailure("its record list is damaged.");
            records.Add(data.AsSpan((int)start, (int)(end - start)).ToArray());
        }
        return records;
    }

    /// <summary>Record 0: the PalmDOC header, the MOBI header and the EXTH block.</summary>
    internal sealed class MobiHeader
    {
        public MobiHeader(List<byte[]> records, int first, string type)
        {
            var r0 = records[first];
            if (r0.Length < 16) throw new ConversionFailure("its first record is damaged.");
            Compression = BinaryPrimitives.ReadUInt16BigEndian(r0);
            TextLength = BinaryPrimitives.ReadUInt32BigEndian(r0.AsSpan(4));
            TextRecordCount = BinaryPrimitives.ReadUInt16BigEndian(r0.AsSpan(8));
            Encoding = Encoding.GetEncoding(1252);
            if (type == "TEXtREAd" || r0.Length < 24 || Encoding.ASCII.GetString(r0, 16, 4) != "MOBI") return;

            var encryption = BinaryPrimitives.ReadUInt16BigEndian(r0.AsSpan(12));
            if (encryption != 0) throw new ConversionFailure("this book is protected by DRM and cannot be converted.");

            var headerLength = (int)BinaryPrimitives.ReadUInt32BigEndian(r0.AsSpan(20));
            var end = Math.Min(r0.Length, 16 + headerLength);
            uint U32(int offset) => offset + 4 <= end ? BinaryPrimitives.ReadUInt32BigEndian(r0.AsSpan(offset)) : 0xFFFFFFFF;

            var encoding = U32(28);
            Encoding = encoding == 65001 ? new UTF8Encoding(false) : Encoding.GetEncoding(1252);
            Version = (int)U32(36);
            FirstImage = U32(108) is var image and not 0xFFFFFFFF ? first + (int)image : -1;
            HuffRecord = U32(112) is var huff and not 0xFFFFFFFF ? first + (int)huff : -1;
            HuffCount = (int)(U32(116) is var hc and not 0xFFFFFFFF ? hc : 0);
            ExtraFlags = headerLength >= 0xE4 ? (ushort)(U32(240) & 0xFFFF) : (ushort)0;
            Fdst = Version >= 8 && U32(192) is var fdst and not 0xFFFFFFFF ? first + (int)fdst : -1;

            var fullNameOffset = (int)U32(84);
            var fullNameLength = (int)U32(88);
            if (fullNameOffset > 0 && fullNameLength > 0 && fullNameOffset + fullNameLength <= r0.Length)
                FullName = Encoding.GetString(r0, fullNameOffset, fullNameLength).TrimEnd('\0');

            var locale = U32(92);
            if ((U32(128) & 0x40) != 0 && 16 + headerLength + 12 <= r0.Length && Encoding.ASCII.GetString(r0, 16 + headerLength, 4) == "EXTH")
                ReadExth(r0, 16 + headerLength);

            Language = Exth.GetValueOrDefault(524) is { Length: > 0 } lang ? lang.Trim() : LanguageFromLocale(locale);
        }

        public int Compression { get; }
        public uint TextLength { get; }
        public int TextRecordCount { get; }
        public Encoding Encoding { get; }
        public int Version { get; }
        public bool IsKf8 => Version >= 8;
        public int FirstImage { get; } = -1;
        public int HuffRecord { get; } = -1;
        public int HuffCount { get; }
        public ushort ExtraFlags { get; }
        public int Fdst { get; } = -1;
        public string? FullName { get; }
        public string? Language { get; }
        public Dictionary<int, string> Exth { get; } = [];
        public List<string> Authors { get; } = [];

        /// <summary>EXTH 201: the cover picture, counted from the first picture record; -1 when there is none.</summary>
        public int CoverOffset { get; private set; } = -1;

        private void ReadExth(byte[] r0, int start)
        {
            var count = BinaryPrimitives.ReadUInt32BigEndian(r0.AsSpan(start + 8));
            var position = start + 12;
            for (var i = 0; i < count && position + 8 <= r0.Length; i++)
            {
                var type = (int)BinaryPrimitives.ReadUInt32BigEndian(r0.AsSpan(position));
                var length = (int)BinaryPrimitives.ReadUInt32BigEndian(r0.AsSpan(position + 4));
                if (length < 8 || position + length > r0.Length) break;
                var value = r0.AsSpan(position + 8, length - 8);
                if (type == 201 && value.Length == 4 && BinaryPrimitives.ReadUInt32BigEndian(value) is var cover and < 0xFFFF)
                    CoverOffset = (int)cover;
                else if (type is 100 or 503 or 524 or 105 or 101)
                {
                    var text = Encoding.GetString(value).TrimEnd('\0').Trim();
                    if (type == 100) { if (text.Length > 0) Authors.Add(text); }
                    else Exth.TryAdd(type, text);
                }
                position += length;
            }
        }

        /// <summary>The language of a Windows-style locale number (its low byte).</summary>
        private static string? LanguageFromLocale(uint locale) => (locale & 0xFF) switch
        {
            0x01 => "ar",
            0x04 => "zh",
            0x07 => "de",
            0x09 => "en",
            0x0A => "es",
            0x0C => "fr",
            0x0D => "he",
            0x10 => "it",
            0x11 => "ja",
            0x13 => "nl",
            0x16 => "pt",
            0x19 => "ru",
            0x1F => "tr",
            0x20 => "ur",
            0x29 => "fa",
            _ => null,
        };
    }

    // ---- text records -------------------------------------------------------------------------------------------------------

    internal static byte[] ExtractText(List<byte[]> records, MobiHeader header)
    {
        if (header.TextRecordCount < 1 || header.TextRecordCount >= records.Count) throw new ConversionFailure("it has no text records.");
        if (header.TextLength > MaxTextBytes) throw new ConversionFailure("its text is too large.");
        HuffCdic? huff = null;
        if (header.Compression == HuffCdicCompression)
        {
            if (header.HuffRecord < 0 || header.HuffCount < 2 || header.HuffRecord + header.HuffCount > records.Count)
                throw new ConversionFailure("its HUFF/CDIC dictionary is missing.");
            huff = new HuffCdic(records.GetRange(header.HuffRecord, header.HuffCount));
        }
        else if (header.Compression is not (1 or PalmDocCompression))
        {
            throw new ConversionFailure($"it uses an unknown compression ({header.Compression}).");
        }

        var output = new MemoryStream();
        for (var i = 1; i <= header.TextRecordCount; i++)
        {
            var record = records[i];
            var length = record.Length - TrailingSize(record, header.ExtraFlags);
            if (length < 0) throw new ConversionFailure("a text record is damaged.");
            var content = record.AsSpan(0, length);
            switch (header.Compression)
            {
                case 1: output.Write(content); break;
                case PalmDocCompression: PalmDoc.Decompress(content, output); break;
                default: huff!.Decompress(content.ToArray(), output); break;
            }
            if (output.Length > MaxTextBytes) throw new ConversionFailure("its text is too large.");
        }
        return output.ToArray();
    }

    /// <summary>
    /// Bytes at the end of a text record that are not text: one entry per flag bit 1..15 (each ends with its own size, a variable-length
    /// number read backwards), then, for flag bit 0, the multibyte overlap (its length in the low two bits of the last byte, plus one).
    /// </summary>
    internal static int TrailingSize(byte[] record, ushort flags)
    {
        var size = 0;
        for (var bit = 1; bit < 16; bit++)
        {
            if ((flags & (1 << bit)) == 0) continue;
            var end = record.Length - size;
            int value = 0, shift = 0;
            for (var k = end - 1; k >= 0 && k >= end - 4; k--)
            {
                value |= (record[k] & 0x7F) << shift;
                shift += 7;
                if ((record[k] & 0x80) != 0) break;
            }
            size += value;
        }
        if ((flags & 1) != 0 && record.Length - size - 1 >= 0) size += (record[record.Length - size - 1] & 0x3) + 1;
        return Math.Min(size, record.Length);
    }

    // ---- Mobipocket 6 markup -------------------------------------------------------------------------------------------------

    [GeneratedRegex(@"filepos\s*=\s*[""']?0*(?<pos>\d+)[""']?", RegexOptions.IgnoreCase)]
    private static partial Regex FilePos();

    [GeneratedRegex(@"<mbp:pagebreak[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex PageBreak();

    [GeneratedRegex(@"recindex\s*=\s*[""']?0*(?<n>\d+)[""']?", RegexOptions.IgnoreCase)]
    private static partial Regex RecIndex();

    private static string Mobi6Body(byte[] text, List<byte[]> records, MobiHeader header)
    {
        // Links point at byte offsets ("filepos"): put an anchor at each target before the bytes become characters.
        var ascii = Encoding.Latin1.GetString(text);
        var targets = FilePos().Matches(ascii).Select(m => long.Parse(m.Groups["pos"].Value, System.Globalization.CultureInfo.InvariantCulture))
            .Where(p => p >= 0 && p < text.Length).Distinct().ToList();
        var insertions = new List<(int At, long Target)>();
        foreach (var position in targets)
        {
            var at = (int)position;
            var open = at > 0 ? ascii.LastIndexOf('<', at - 1) : -1;
            var close = at > 0 ? ascii.LastIndexOf('>', at - 1) : -1;
            if (open > close) // inside a tag: move after it
            {
                var next = ascii.IndexOf('>', at);
                at = next < 0 ? at : next + 1;
            }
            insertions.Add((at, position));
        }
        // Insert from the end so the earlier offsets stay valid.
        var withAnchors = new List<byte>(text);
        foreach (var (at, target) in insertions.OrderByDescending(i => i.At))
            withAnchors.InsertRange(at, Encoding.ASCII.GetBytes($"<a id=\"filepos{target}\"></a>"));

        var html = header.Encoding.GetString(withAnchors.ToArray());
        html = FilePos().Replace(html, m => $"href=\"#filepos{m.Groups["pos"].Value.TrimStart('0').PadLeft(1, '0')}\"");
        html = RecIndex().Replace(html, m => $"src=\"recindex:{m.Groups["n"].Value}\"");
        var bodyStart = html.IndexOf("<body", StringComparison.OrdinalIgnoreCase);
        if (bodyStart >= 0) html = html[(html.IndexOf('>', bodyStart) + 1)..];
        html = Regex.Replace(html, @"</body>\s*</html>\s*$", "", RegexOptions.IgnoreCase);

        var chapters = PageBreak().Split(html).Where(c => !string.IsNullOrWhiteSpace(Regex.Replace(c, "<[^>]*>", "")) || c.Contains("<img", StringComparison.OrdinalIgnoreCase)).ToList();
        // A table of contents page (what the guide's "toc" reference points at, made of nothing but a heading and links) is navigation,
        // not text: the other formats build their own.
        if (GuideToc().Match(ascii) is { Success: true } toc && FilePos().Match(toc.Value) is { Success: true } tocPos)
        {
            var anchor = $"id=\"filepos{long.Parse(tocPos.Groups["pos"].Value, System.Globalization.CultureInfo.InvariantCulture)}\"";
            var index = chapters.FindIndex(c => c.Contains(anchor, StringComparison.Ordinal));
            if (index >= 0 && chapters.Count > 1 && IsLinkList(chapters[index])) chapters.RemoveAt(index);
        }
        if (chapters.Count == 0) chapters.Add(html);
        var body = new StringBuilder();
        for (var i = 0; i < chapters.Count; i++)
            body.Append("<section id=\"ch").Append(i + 1).Append("\">").Append(chapters[i]).Append("</section>\n");
        return BookHtml.Clean(body.ToString(), src => src.StartsWith("recindex:", StringComparison.Ordinal) && int.TryParse(src[9..], out var n) ? Image(records, header, n) : null);
    }

    [GeneratedRegex(@"<reference\b[^>]*\btype\s*=\s*[""']?toc\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex GuideToc();

    /// <summary>True when a chapter has links and no text outside them and its headings.</summary>
    private static bool IsLinkList(string chapterHtml)
    {
        var document = new AngleSharp.Html.Parser.HtmlParser().ParseDocument("<body>" + chapterHtml + "</body>");
        if (document.QuerySelector("a[href]") is null) return false;
        foreach (var element in document.QuerySelectorAll("a, h1, h2, h3, h4, h5, h6").ToList()) element.Remove();
        return string.IsNullOrWhiteSpace(document.Body!.TextContent) && document.Body.QuerySelector("img") is null;
    }

    private static string? Image(List<byte[]> records, MobiHeader header, int oneBased)
    {
        if (header.FirstImage < 0 || oneBased < 1) return null;
        var index = header.FirstImage + oneBased - 1;
        return index < records.Count ? BookHtml.DataUri(records[index]) : null;
    }

    private static string PlainTextBody(string text)
    {
        var body = new StringBuilder("<section id=\"ch1\">");
        foreach (var paragraph in Regex.Split(text.Replace("\r\n", "\n"), @"\n\s*\n"))
            if (!string.IsNullOrWhiteSpace(paragraph))
                body.Append("<p>").Append(System.Net.WebUtility.HtmlEncode(paragraph.Trim()).Replace("\n", "<br>")).Append("</p>");
        return body.Append("</section>").ToString();
    }

    // ---- KF8 ------------------------------------------------------------------------------------------------------------------

    [GeneratedRegex(@"kindle:embed:(?<n>[0-9A-Va-v]{4})", RegexOptions.IgnoreCase)]
    private static partial Regex KindleEmbed();

    [GeneratedRegex(@"<\?xml[^>]*\?>|<html[\s>]", RegexOptions.IgnoreCase)]
    private static partial Regex FileStart();

    /// <summary>
    /// KF8 text: the first flow holds the book's HTML files one after another (each file's skeleton followed by its fragments), the other
    /// flows hold stylesheets and SVG. Each file becomes a chapter; an HTML parser puts the fragments back into the body in reading order.
    /// </summary>
    private static string Kf8Body(byte[] text, List<byte[]> records, MobiHeader header)
    {
        var flow = text;
        if (header.Fdst >= 0 && header.Fdst < records.Count && records[header.Fdst] is { Length: >= 12 } fdst && Encoding.ASCII.GetString(fdst, 0, 4) == "FDST")
        {
            var sections = BinaryPrimitives.ReadUInt32BigEndian(fdst.AsSpan(8));
            if (sections >= 1 && fdst.Length >= 12 + 8)
            {
                var start = BinaryPrimitives.ReadUInt32BigEndian(fdst.AsSpan(12));
                var end = BinaryPrimitives.ReadUInt32BigEndian(fdst.AsSpan(16));
                if (start <= end && end <= text.Length) flow = text.AsSpan((int)start, (int)(end - start)).ToArray();
            }
        }

        var html = new UTF8Encoding(false).GetString(flow);
        var starts = FileStart().Matches(html).Select(m => m.Index).ToList();
        // An XML declaration directly before <html> belongs to the same file.
        var files = new List<string>();
        var cuts = new List<int>();
        foreach (var index in starts)
            if (cuts.Count == 0 || index - cuts[^1] > 120 || !html.AsSpan(cuts[^1], index - cuts[^1]).StartsWith("<?xml", StringComparison.OrdinalIgnoreCase))
                cuts.Add(index);
        if (cuts.Count == 0 || cuts[0] != 0) cuts.Insert(0, 0);
        for (var i = 0; i < cuts.Count; i++)
        {
            var end = i + 1 < cuts.Count ? cuts[i + 1] : html.Length;
            if (end > cuts[i]) files.Add(html[cuts[i]..end]);
        }

        var body = new StringBuilder();
        var chapter = 0;
        foreach (var file in files)
        {
            var document = new AngleSharp.Html.Parser.HtmlParser().ParseDocument(KindleEmbed().Replace(file, m => "kfimage:" + m.Groups["n"].Value));
            var inner = document.Body?.InnerHtml ?? "";
            if (string.IsNullOrWhiteSpace(Regex.Replace(inner, "<[^>]*>", "")) && !inner.Contains("<img", StringComparison.OrdinalIgnoreCase)) continue;
            body.Append("<section id=\"ch").Append(++chapter).Append("\">").Append(inner).Append("</section>\n");
        }
        if (chapter == 0) throw new ConversionFailure("it has no readable text.");

        return BookHtml.Clean(body.ToString(), src =>
        {
            if (!src.StartsWith("kfimage:", StringComparison.Ordinal)) return null;
            var number = Base32(src[8..].Split('?', '#')[0]);
            return number is { } n ? Image(records, header, n) : null;
        });
    }

    /// <summary>KF8 resource numbers: four characters of 0-9 then A-V (base 32).</summary>
    internal static int? Base32(string digits)
    {
        var value = 0;
        foreach (var c in digits.ToUpperInvariant())
        {
            var d = c is >= '0' and <= '9' ? c - '0' : c is >= 'A' and <= 'V' ? c - 'A' + 10 : -1;
            if (d < 0) return null;
            value = value * 32 + d;
        }
        return value;
    }
}

/// <summary>PalmDOC compression: a byte-oriented LZ77 (literals, back references of 3-10 bytes within 2047, and "space + letter" pairs).</summary>
internal static partial class PalmDoc
{
    public static void Decompress(ReadOnlySpan<byte> input, MemoryStream output)
    {
        var start = output.Length;
        var i = 0;
        while (i < input.Length)
        {
            int c = input[i++];
            if (c is >= 1 and <= 8)
            {
                if (i + c > input.Length) throw new ConversionFailure("a compressed text record is cut short.");
                output.Write(input.Slice(i, c));
                i += c;
            }
            else if (c <= 0x7F)
            {
                output.WriteByte((byte)c);
            }
            else if (c >= 0xC0)
            {
                output.WriteByte((byte)' ');
                output.WriteByte((byte)(c ^ 0x80));
            }
            else
            {
                if (i >= input.Length) throw new ConversionFailure("a compressed text record is cut short.");
                var pair = (c << 8) | input[i++];
                var distance = (pair & 0x3FFF) >> 3;
                var length = (pair & 7) + 3;
                var from = output.Length - distance;
                if (distance == 0 || from < start) throw new ConversionFailure("a compressed text record is damaged.");
                var buffer = output.GetBuffer();
                for (var k = 0; k < length; k++)
                {
                    output.WriteByte(buffer[from + k]);
                    buffer = output.GetBuffer(); // the stream may have grown into a new buffer
                }
            }
        }
    }
}

/// <summary>
/// HUFF/CDIC compression (Mobipocket "huffdic"): a canonical Huffman code (the HUFF record: a 256-entry lookup by the code's first byte
/// and a table of code ranges per length) whose symbols index a phrase dictionary (the CDIC records); a phrase may itself be compressed.
/// </summary>
internal sealed class HuffCdic
{
    private readonly (int CodeLength, bool Terminal, ulong MaxCode)[] _lookup = new (int, bool, ulong)[256];
    private readonly ulong[] _minCode = new ulong[33];
    private readonly ulong[] _maxCode = new ulong[33];
    private readonly List<(byte[] Data, bool Done)?> _phrases = [];

    public HuffCdic(List<byte[]> records)
    {
        var huff = records[0];
        if (huff.Length < 24 || Encoding.ASCII.GetString(huff, 0, 4) != "HUFF") throw new ConversionFailure("its HUFF record is damaged.");
        var lookupOffset = (int)BinaryPrimitives.ReadUInt32BigEndian(huff.AsSpan(8));
        var rangeOffset = (int)BinaryPrimitives.ReadUInt32BigEndian(huff.AsSpan(12));
        if (lookupOffset + 1024 > huff.Length || rangeOffset + 256 > huff.Length) throw new ConversionFailure("its HUFF record is damaged.");

        for (var i = 0; i < 256; i++)
        {
            var v = BinaryPrimitives.ReadUInt32BigEndian(huff.AsSpan(lookupOffset + i * 4));
            var length = (int)(v & 0x1F);
            if (length == 0) throw new ConversionFailure("its HUFF table is damaged.");
            var terminal = (v & 0x80) != 0;
            var max = (((ulong)(v >> 8) + 1) << (32 - length)) - 1;
            _lookup[i] = (length, terminal, max);
        }
        for (var length = 1; length <= 32; length++)
        {
            var min = BinaryPrimitives.ReadUInt32BigEndian(huff.AsSpan(rangeOffset + (length - 1) * 8));
            var max = BinaryPrimitives.ReadUInt32BigEndian(huff.AsSpan(rangeOffset + (length - 1) * 8 + 4));
            _minCode[length] = (ulong)min << (32 - length);
            _maxCode[length] = (((ulong)max + 1) << (32 - length)) - 1;
        }

        foreach (var cdic in records.Skip(1))
        {
            if (cdic.Length < 16 || Encoding.ASCII.GetString(cdic, 0, 4) != "CDIC") throw new ConversionFailure("its CDIC record is damaged.");
            var total = BinaryPrimitives.ReadUInt32BigEndian(cdic.AsSpan(8));
            var bits = (int)BinaryPrimitives.ReadUInt32BigEndian(cdic.AsSpan(12));
            var count = (int)Math.Min(1L << Math.Min(bits, 30), total - _phrases.Count);
            for (var i = 0; i < count; i++)
            {
                var offset = BinaryPrimitives.ReadUInt16BigEndian(cdic.AsSpan(16 + i * 2));
                var header = BinaryPrimitives.ReadUInt16BigEndian(cdic.AsSpan(16 + offset));
                var length = header & 0x7FFF;
                _phrases.Add((cdic.AsSpan(18 + offset, length).ToArray(), (header & 0x8000) != 0));
            }
        }
    }

    public void Decompress(byte[] data, MemoryStream output) => Unpack(data, output, 0);

    private void Unpack(byte[] data, MemoryStream output, int depth)
    {
        if (depth > 32) throw new ConversionFailure("its HUFF/CDIC dictionary refers to itself.");
        long bitsLeft = data.Length * 8L;
        var padded = new byte[data.Length + 8];
        data.CopyTo(padded, 0);
        var position = 0;
        var window = BinaryPrimitives.ReadUInt64BigEndian(padded);
        var shift = 32;
        while (true)
        {
            if (shift <= 0)
            {
                position += 4;
                window = position + 8 <= padded.Length ? BinaryPrimitives.ReadUInt64BigEndian(padded.AsSpan(position)) : 0;
                shift += 32;
            }
            var code = (window >> shift) & 0xFFFFFFFF;
            var (length, terminal, max) = _lookup[code >> 24];
            if (!terminal)
            {
                while (length < 32 && code < _minCode[length]) length++;
                max = _maxCode[length];
            }
            shift -= length;
            bitsLeft -= length;
            if (bitsLeft < 0) break;
            var index = (int)((max - code) >> (32 - length));
            if (index < 0 || index >= _phrases.Count || _phrases[index] is not { } phrase)
                throw new ConversionFailure("its HUFF/CDIC text is damaged.");
            if (!phrase.Done)
            {
                _phrases[index] = null; // guards against a phrase that contains itself
                using var expanded = new MemoryStream();
                Unpack(phrase.Data, expanded, depth + 1);
                phrase = (expanded.ToArray(), true);
                _phrases[index] = phrase;
            }
            output.Write(phrase.Data);
        }
    }
}
