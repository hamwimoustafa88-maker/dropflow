// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using DropFlow.Engines.Pdf.Infrastructure;
using DropFlow.Engines.Pdf.Office;
using ImageMagick;

namespace DropFlow.Engines.Pdf.Books;

/// <summary>
/// Writes Mobipocket 6 books (.mobi, the Kindle format before KF8, sometimes called "KF7"), from the published description of the format:
/// a Palm database whose record 0 holds the PalmDOC header, a 232-byte MOBI header (a type 2 book in UTF-8) and EXTH metadata (author,
/// title, language, cover and thumbnail); then the text as one HTML stream cut into 4096-byte records, each compressed with PalmDOC LZ77
/// and followed by its multibyte trailing entry; then the pictures, and the FLIS, FCIS and end-of-file records.
/// The HTML is the old Mobipocket markup: chapters separated by &lt;mbp:pagebreak/&gt;, pictures as &lt;img recindex&gt;, links as byte
/// offsets (filepos) filled in after the layout, a contents page that the guide points at. KF8 (AZW3) is not written.
/// </summary>
internal static class MobiWriter
{
    public const int RecordSize = 4096;

    /// <summary>The largest picture Mobipocket 6 readers show (Amazon's limit for the format); larger ones are re-encoded smaller.</summary>
    public const int MaxImageBytes = 127 * 1024;

    private const int MobiHeaderLength = 232;
    private const uint None = 0xFFFFFFFF;
    private const int ThumbnailWidth = 180;
    private const int ThumbnailHeight = 240;

    private static readonly byte[] Flis =
        [.. "FLIS"u8, 0, 0, 0, 8, 0, 0x41, 0, 0, 0, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0xFF, 0, 1, 0, 3, 0, 0, 0, 3, 0, 0, 0, 1, 0xFF, 0xFF, 0xFF, 0xFF];

    private static readonly byte[] EndOfFile = [0xE9, 0x8E, 0x0D, 0x0A];

    public static void Write(EpubBook book, string path, CancellationToken ct = default) => File.WriteAllBytes(path, Build(book, ct));

    internal static byte[] Build(EpubBook book, CancellationToken ct)
    {
        var language = BookLanguage.Primary(book.Language);
        var pictures = new Pictures();
        var body = BookHtml.ExtractImages(book.BodyHtml, (_, _, bytes) => pictures.Add(bytes, coverQuality: false) is { } n ? $"recindex:{n}" : null);
        ct.ThrowIfCancellationRequested();
        var cover = book.Cover is { } coverBytes ? pictures.Add(coverBytes, coverQuality: true) : null;
        var thumbnail = cover is { } c ? pictures.AddThumbnail(c) : null;
        ct.ThrowIfCancellationRequested();

        var text = MobiMarkup.Build(book.Title, language, BookHtml.Chapters(body, book.Title), ct);

        var records = new List<byte[]> { Array.Empty<byte>() };
        for (var start = 0; start < text.Length; start += RecordSize)
        {
            ct.ThrowIfCancellationRequested();
            records.Add(TextRecord(text, start));
        }
        var textRecords = records.Count - 1;
        var firstImage = records.Count;
        records.AddRange(pictures.Records);
        var lastContent = records.Count - 1;
        var flis = records.Count;
        records.Add(Flis);
        records.Add(Fcis(text.Length));
        records.Add(EndOfFile);
        if (records.Count > ushort.MaxValue) throw new ConversionFailure("The book is too large for a MOBI file.");

        var exth = Exth(book, language, cover - 1, thumbnail - 1);
        records[0] = Record0(book.Title, language, text.Length, textRecords, pictures.Records.Count > 0 ? firstImage : -1, lastContent, flis, exth);
        return PalmDatabase(book.Title, records);
    }

    /// <summary>
    /// One text record: exactly <see cref="RecordSize"/> bytes of the stream (fewer for the last), compressed, then the multibyte trailing
    /// entry: the continuation bytes of a character this record cuts (they also start the next record), and one byte with their count.
    /// Every record but the last holds the same number of bytes, so a reader can find a filepos by dividing.
    /// </summary>
    private static byte[] TextRecord(byte[] text, int start)
    {
        var end = Math.Min(start + RecordSize, text.Length);
        var overlap = 0;
        while (end + overlap < text.Length && overlap < 3 && (text[end + overlap] & 0xC0) == 0x80) overlap++;
        var compressed = PalmDoc.Compress(text.AsSpan(start, end - start));
        var record = new byte[compressed.Length + overlap + 1];
        compressed.CopyTo(record, 0);
        text.AsSpan(end, overlap).CopyTo(record.AsSpan(compressed.Length));
        record[^1] = (byte)overlap;
        return record;
    }

    private static byte[] Fcis(int textLength)
    {
        var fcis = new byte[44];
        "FCIS"u8.CopyTo(fcis);
        byte[] head = [0, 0, 0, 0x14, 0, 0, 0, 0x10, 0, 0, 0, 1, 0, 0, 0, 0];
        head.CopyTo(fcis, 4);
        BinaryPrimitives.WriteUInt32BigEndian(fcis.AsSpan(20), (uint)textLength);
        byte[] tail = [0, 0, 0, 0, 0, 0, 0, 0x20, 0, 0, 0, 8, 0, 1, 0, 1, 0, 0, 0, 0];
        tail.CopyTo(fcis, 24);
        return fcis;
    }

    // ---- record 0 -----------------------------------------------------------------------------------------------------------

    private static byte[] Record0(string title, string language, int textLength, int textRecords, int firstImage, int lastContent, int flis, byte[] exth)
    {
        var name = Encoding.UTF8.GetBytes(title);
        var nameOffset = 16 + MobiHeaderLength + exth.Length;
        var size = nameOffset + name.Length + 2;
        size += (4 - size % 4) % 4;
        var r = new byte[size];
        void U16(int at, int value) => BinaryPrimitives.WriteUInt16BigEndian(r.AsSpan(at), (ushort)value);
        void U32(int at, uint value) => BinaryPrimitives.WriteUInt32BigEndian(r.AsSpan(at), value);

        // PalmDOC header
        U16(0, 2);                                  // PalmDOC compression
        U32(4, (uint)textLength);
        U16(8, textRecords);
        U16(10, RecordSize);
        U16(12, 0);                                 // no encryption

        // MOBI header
        "MOBI"u8.CopyTo(r.AsSpan(16));
        U32(20, MobiHeaderLength);
        U32(24, 2);                                 // a book
        U32(28, 65001);                             // UTF-8
        U32(32, (uint)RandomNumberGenerator.GetInt32(int.MaxValue));
        U32(36, 6);                                 // file version
        for (var at = 40; at < 80; at += 4) U32(at, None); // orthographic, inflection, index names and keys, extra indexes 0-5
        U32(80, (uint)(textRecords + 1));           // first record that is not text
        U32(84, (uint)nameOffset);
        U32(88, (uint)name.Length);
        U32(92, Locale(language));
        U32(104, 6);                                // minimum reader version
        U32(108, firstImage < 0 ? None : (uint)firstImage);
        U32(128, 0x50);                             // EXTH present
        U32(164, None);
        U32(168, None);                             // no DRM
        U16(192, 1);                                // first content record
        U16(194, lastContent);
        U32(196, 1);
        U32(200, (uint)(flis + 1));                 // FCIS
        U32(204, 1);
        U32(208, (uint)flis);                       // FLIS
        U32(212, 1);
        U32(224, None);
        U32(232, None);
        U32(236, None);
        U32(240, 1);                                // trailing entries: multibyte overlap only
        U32(244, None);                             // no index

        exth.CopyTo(r, 16 + MobiHeaderLength);
        name.CopyTo(r, nameOffset);
        return r;
    }

    private static byte[] Exth(EpubBook book, string language, int? cover, int? thumbnail)
    {
        var items = new List<(int Type, byte[] Value)>();
        static byte[] Number(int value) { var b = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, (uint)value); return b; }
        if (!string.IsNullOrWhiteSpace(book.Author)) items.Add((100, Encoding.UTF8.GetBytes(book.Author.Trim())));
        items.Add((503, Encoding.UTF8.GetBytes(book.Title)));
        items.Add((524, Encoding.UTF8.GetBytes(language)));
        items.Add((501, "EBOK"u8.ToArray()));
        if (cover is { } c)
        {
            items.Add((201, Number(c)));
            items.Add((203, Number(0)));            // the cover is a real one, not made up by the reader
        }
        if (thumbnail is { } t) items.Add((202, Number(t)));

        var length = 12 + items.Sum(i => 8 + i.Value.Length);
        var exth = new byte[length + (4 - length % 4) % 4];
        "EXTH"u8.CopyTo(exth);
        BinaryPrimitives.WriteUInt32BigEndian(exth.AsSpan(4), (uint)length); // without the padding
        BinaryPrimitives.WriteUInt32BigEndian(exth.AsSpan(8), (uint)items.Count);
        var at = 12;
        foreach (var (type, value) in items)
        {
            BinaryPrimitives.WriteUInt32BigEndian(exth.AsSpan(at), (uint)type);
            BinaryPrimitives.WriteUInt32BigEndian(exth.AsSpan(at + 4), (uint)(8 + value.Length));
            value.CopyTo(exth, at + 8);
            at += 8 + value.Length;
        }
        return exth;
    }

    /// <summary>The Windows-style language number Mobipocket keeps in the header (the reader reads its low byte).</summary>
    internal static uint Locale(string language) => language switch
    {
        "ar" => 0x01,
        "zh" => 0x04,
        "de" => 0x07,
        "en" => 0x09,
        "es" => 0x0A,
        "fr" => 0x0C,
        "he" or "iw" => 0x0D,
        "it" => 0x10,
        "ja" => 0x11,
        "nl" => 0x13,
        "pt" => 0x16,
        "ru" => 0x19,
        "tr" => 0x1F,
        "ur" => 0x20,
        "fa" => 0x29,
        _ => 0,
    };

    // ---- Palm database ------------------------------------------------------------------------------------------------------

    private static byte[] PalmDatabase(string title, List<byte[]> records)
    {
        var headerSize = 78 + records.Count * 8 + 2;
        var output = new byte[headerSize + records.Sum(r => (long)r.Length)];
        var name = DatabaseName(title);
        Encoding.ASCII.GetBytes(name).CopyTo(output, 0);
        var now = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(36), now); // created
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(40), now); // modified
        "BOOK"u8.CopyTo(output.AsSpan(60));
        "MOBI"u8.CopyTo(output.AsSpan(64));
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(68), (uint)(2 * records.Count - 1)); // unique id seed
        BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(76), (ushort)records.Count);
        var offset = headerSize;
        for (var i = 0; i < records.Count; i++)
        {
            var entry = 78 + i * 8;
            BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(entry), (uint)offset);
            var id = 2 * i; // attributes 0, then a three-byte unique id
            output[entry + 5] = (byte)(id >> 16);
            output[entry + 6] = (byte)(id >> 8);
            output[entry + 7] = (byte)id;
            records[i].CopyTo(output, offset);
            offset += records[i].Length;
        }
        return output;
    }

    /// <summary>The 31-character ASCII name of the database (letters, digits and hyphens; anything else becomes one underscore).</summary>
    internal static string DatabaseName(string title)
    {
        var sb = new StringBuilder();
        foreach (var c in title)
        {
            if (c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-') sb.Append(c);
            else if (sb.Length > 0 && sb[^1] != '_') sb.Append('_');
        }
        var name = sb.ToString().Trim('_');
        if (name.Length > 31) name = name[..31].TrimEnd('_');
        return name.Length == 0 ? "Book" : name;
    }

    // ---- pictures -----------------------------------------------------------------------------------------------------------

    /// <summary>The picture records, in recindex order (1-based), each stored once.</summary>
    private sealed class Pictures
    {
        private readonly Dictionary<string, int> _bySource = new(StringComparer.Ordinal);

        public List<byte[]> Records { get; } = [];

        /// <summary>Adds a picture as JPEG, PNG or GIF of at most <see cref="MaxImageBytes"/>; returns its recindex, or null when it is not a
        /// picture. A cover must be a JPEG (what Kindle library views expect).</summary>
        public int? Add(byte[] bytes, bool coverQuality)
        {
            var key = Convert.ToHexString(SHA256.HashData(bytes));
            if (_bySource.TryGetValue(key, out var existing) && (!coverQuality || BookHtml.SniffImage(Records[existing - 1]) == "image/jpeg"))
                return existing;
            var prepared = Prepare(bytes, jpegOnly: coverQuality);
            if (prepared is null) return null;
            Records.Add(prepared);
            _bySource[key] = Records.Count;
            return Records.Count;
        }

        /// <summary>A small JPEG of the cover for library views (EXTH 202).</summary>
        public int? AddThumbnail(int cover)
        {
            try
            {
                using var image = new MagickImage(Records[cover - 1]);
                image.Thumbnail(new MagickGeometry(ThumbnailWidth, ThumbnailHeight) { Greater = true });
                image.Format = MagickFormat.Jpeg;
                image.Quality = 80;
                Records.Add(image.ToByteArray());
                return Records.Count;
            }
            catch (MagickException)
            {
                return null;
            }
        }

        private static byte[]? Prepare(byte[] bytes, bool jpegOnly)
        {
            var mime = BookHtml.SniffImage(bytes);
            var kept = jpegOnly ? mime == "image/jpeg" : mime is "image/jpeg" or "image/png" or "image/gif";
            if (kept && bytes.Length <= MaxImageBytes && Upright(bytes, mime)) return bytes;
            try
            {
                using var image = new MagickImage(bytes); // the first frame of an animation
                image.AutoOrient();
                if (image.HasAlpha)
                {
                    image.BackgroundColor = MagickColors.White;
                    image.Alpha(AlphaAction.Remove);
                }
                image.Strip();
                if (image.Width > 1600 || image.Height > 1600) image.Resize(new MagickGeometry(1600, 1600) { Greater = true });
                image.Format = MagickFormat.Jpeg;
                uint quality = 90;
                while (true)
                {
                    image.Quality = quality;
                    var data = image.ToByteArray();
                    if (data.Length <= MaxImageBytes) return data;
                    if (quality > 60) quality -= 10;
                    else if (image.Width > 64 && image.Height > 64) image.Resize(new Percentage(80));
                    else return null;
                }
            }
            catch (MagickException)
            {
                return null; // not a picture Magick can read (an SVG without a renderer, damaged bytes): the alt text stays
            }
        }

        /// <summary>A JPEG whose EXIF orientation asks for a turn is re-encoded upright: Mobipocket readers ignore the tag.</summary>
        private static bool Upright(byte[] bytes, string? mime)
        {
            if (mime != "image/jpeg") return true;
            try
            {
                return new MagickImageInfo(bytes).Orientation is Orientation.Undefined or Orientation.TopLeft;
            }
            catch (MagickException)
            {
                return false;
            }
        }
    }
}

/// <summary>
/// The text stream of a Mobipocket 6 book: old-style HTML in UTF-8. Element ids are not kept (Mobipocket readers do not use them); links
/// to them become <c>filepos=</c> byte offsets, written as ten-digit placeholders first and filled in once every target's offset is known.
/// </summary>
internal sealed class MobiMarkup
{
    private const string TocKey = "\u0001toc";

    private static readonly HashSet<string> Kept = new(StringComparer.Ordinal)
    {
        "p", "div", "span", "h1", "h2", "h3", "h4", "h5", "h6", "b", "strong", "i", "em", "u", "s", "strike", "del", "ins", "sub", "sup", "small",
        "big", "br", "hr", "ul", "ol", "li", "dl", "dt", "dd", "blockquote", "pre", "code", "tt", "kbd", "samp", "var", "q", "cite", "abbr", "dfn",
        "table", "caption", "thead", "tbody", "tfoot", "tr", "th", "td", "center",
    };

    private static readonly HashSet<string> Blocks = new(StringComparer.Ordinal)
    {
        "section", "article", "main", "header", "footer", "nav", "aside", "figure", "figcaption", "address", "details", "summary", "hgroup",
    };

    private static readonly HashSet<string> Void = new(StringComparer.Ordinal) { "br", "hr", "img" };

    private readonly MemoryStream _out = new();
    private readonly Dictionary<string, long> _anchors = new(StringComparer.Ordinal);
    private readonly List<(long At, string Target)> _links = [];
    private readonly HashSet<string> _ids;

    private MobiMarkup(HashSet<string> ids) => _ids = ids;

    public static byte[] Build(string title, string language, IReadOnlyList<BookHtml.Chapter> chapters, CancellationToken ct)
    {
        var parser = new HtmlParser();
        var documents = chapters.Select(c => parser.ParseDocument("<body>" + c.Html + "</body>")).ToList();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var chapter in chapters) ids.Add(chapter.Id);
        foreach (var document in documents)
            foreach (var element in document.QuerySelectorAll("[id]"))
                if (!string.IsNullOrEmpty(element.Id)) ids.Add(element.Id);

        var markup = new MobiMarkup(ids);
        var withContents = chapters.Count > 1;
        markup.Raw("<html><head><guide>");
        if (withContents)
        {
            markup.Raw("<reference type=\"toc\" title=\"Table of Contents\" ");
            markup.FilePos(TocKey);
            markup.Raw(" />");
        }
        markup.Raw("<reference type=\"text\" title=\"Start\" ");
        markup.FilePos(ChapterKey(0));
        markup.Raw(" /></guide></head><body");
        if (BookLanguage.IsRtl(language)) markup.Raw(" dir=\"rtl\"");
        markup.Raw(" lang=\"").Raw(Attribute(language)).Raw("\">");

        if (withContents)
        {
            markup._anchors[TocKey] = markup.Position;
            markup.Raw("<h2>").Raw(Text(title)).Raw("</h2>");
            for (var i = 0; i < chapters.Count; i++)
            {
                markup.Raw("<p><a ");
                markup.FilePos(ChapterKey(i));
                markup.Raw(">").Raw(Text(chapters[i].Title)).Raw("</a></p>");
            }
            markup.Raw("<mbp:pagebreak/>");
        }

        for (var i = 0; i < chapters.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (i > 0) markup.Raw("<mbp:pagebreak/>");
            markup._anchors[ChapterKey(i)] = markup.Position;
            markup._anchors.TryAdd(chapters[i].Id, markup.Position);
            foreach (var node in documents[i].Body!.ChildNodes) markup.Node(node);
        }
        markup.Raw("</body></html>");
        return markup.Finish();
    }

    private static string ChapterKey(int index) => "\u0001ch" + index.ToString(CultureInfo.InvariantCulture);

    private long Position => _out.Length;

    private MobiMarkup Raw(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        _out.Write(bytes);
        return this;
    }

    private void FilePos(string target)
    {
        Raw("filepos=");
        _links.Add((Position, target));
        Raw("0000000000");
    }

    private byte[] Finish()
    {
        var bytes = _out.ToArray();
        foreach (var (at, target) in _links)
        {
            var position = _anchors.TryGetValue(target, out var p) ? p : 0;
            Encoding.ASCII.GetBytes(position.ToString("D10", CultureInfo.InvariantCulture)).CopyTo(bytes, at);
        }
        return bytes;
    }

    private void Node(INode node)
    {
        switch (node)
        {
            case IText text:
                Raw(Text(text.Data));
                break;
            case IElement element:
                Element(element);
                break;
        }
    }

    private void Children(IElement element)
    {
        foreach (var child in element.ChildNodes) Node(child);
    }

    private void Element(IElement element)
    {
        var name = element.LocalName;
        if (!string.IsNullOrEmpty(element.Id) && _ids.Contains(element.Id)) _anchors.TryAdd(element.Id, Position);
        if (EpubReader.RemovedElements.Contains(name)) return;

        switch (name)
        {
            case "img":
                Image(element);
                return;
            case "a":
                Link(element);
                return;
        }

        var tag = Kept.Contains(name) ? name : Blocks.Contains(name) ? "div" : null;
        if (tag is null)
        {
            Children(element); // an element Mobipocket does not know: keep what it holds
            return;
        }
        Raw("<").Raw(tag);
        Attributes(element, tag);
        if (Void.Contains(tag))
        {
            Raw("/>");
            return;
        }
        Raw(">");
        Children(element);
        Raw("</").Raw(tag).Raw(">");
    }

    private void Attributes(IElement element, string tag)
    {
        var dir = element.GetAttribute("dir")?.Trim().ToLowerInvariant();
        if (dir is "rtl" or "ltr") Raw(" dir=\"").Raw(dir).Raw("\"");
        if (element.GetAttribute("lang") is { Length: > 0 } lang) Raw(" lang=\"").Raw(Attribute(lang)).Raw("\"");
        string[] numbers = tag switch
        {
            "td" or "th" => ["colspan", "rowspan"],
            "ol" => ["start"],
            _ => [],
        };
        foreach (var attribute in numbers)
            if (int.TryParse(element.GetAttribute(attribute), NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value > 0)
                Raw($" {attribute}=\"{value.ToString(CultureInfo.InvariantCulture)}\"");
        var type = element.GetAttribute("type");
        if (tag == "ol" && type is "1" or "a" or "A" or "i" or "I") Raw($" type=\"{type}\"");
    }

    private void Image(IElement element)
    {
        var src = element.GetAttribute("src") ?? "";
        var alt = element.GetAttribute("alt");
        if (src.StartsWith("recindex:", StringComparison.Ordinal) && int.TryParse(src[9..], NumberStyles.None, CultureInfo.InvariantCulture, out var index))
        {
            Raw("<img recindex=\"").Raw(index.ToString("D5", CultureInfo.InvariantCulture)).Raw("\"");
            if (!string.IsNullOrWhiteSpace(alt)) Raw(" alt=\"").Raw(Attribute(alt)).Raw("\"");
            Raw("/>");
        }
        else if (!string.IsNullOrWhiteSpace(alt))
        {
            Raw(Text(alt)); // a picture that could not be stored keeps its description
        }
    }

    private void Link(IElement element)
    {
        var href = element.GetAttribute("href")?.Trim() ?? "";
        if (href.Length > 1 && href[0] == '#' && _ids.Contains(href[1..]))
        {
            Raw("<a ");
            FilePos(href[1..]);
            Raw(">");
            Children(element);
            Raw("</a>");
        }
        else if (BookHtml.DefaultLink(href) is { } web && !web.StartsWith('#'))
        {
            Raw("<a href=\"").Raw(Attribute(web)).Raw("\">");
            Children(element);
            Raw("</a>");
        }
        else
        {
            Children(element);
        }
    }

    /// <summary>Escapes only &amp;, &lt;, &gt; and the double quote: everything else (Arabic included) stays UTF-8 text, in logical order.</summary>
    private static string Text(string text)
    {
        var sb = new StringBuilder(text.Length + 16);
        foreach (var c in text)
        {
            switch (c)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '"': sb.Append("&quot;"); break;
                case '\0': break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }

    private static string Attribute(string value) => Text(value);
}

/// <summary>The compressing half of PalmDOC (see <see cref="Decompress"/>).</summary>
internal static partial class PalmDoc
{
    private const int Window = 2047;
    private const int MaxMatch = 10;
    private const int MaxChain = 256;

    /// <summary>
    /// PalmDOC LZ77 for one record: back references of 3-10 bytes up to 2047 bytes back (never overlapping the bytes they produce),
    /// "space + letter" pairs in one byte, plain bytes 0x00 and 0x09-0x7F, and runs of up to eight other bytes behind a count.
    /// Earlier matches are found through hash chains over three-byte prefixes.
    /// </summary>
    public static byte[] Compress(ReadOnlySpan<byte> input) => new Lz(input.ToArray()).Run();

    private sealed class Lz(byte[] data)
    {
        private readonly int[] _head = Enumerable.Repeat(-1, 4096).ToArray();
        private readonly int[] _previous = new int[data.Length];
        private readonly MemoryStream _out = new(data.Length + data.Length / 8 + 8);
        private int _inserted;

        public byte[] Run()
        {
            var n = data.Length;
            var i = 0;
            while (i < n)
            {
                Insert(i);
                var (length, distance) = Match(i);
                if (length >= 3)
                {
                    var code = 0x8000 | (distance << 3) | (length - 3);
                    _out.WriteByte((byte)(code >> 8));
                    _out.WriteByte((byte)code);
                    i += length;
                    continue;
                }

                var c = data[i];
                if (c == (byte)' ' && i + 1 < n && data[i + 1] is >= 0x40 and <= 0x7F)
                {
                    _out.WriteByte((byte)(data[i + 1] ^ 0x80));
                    i += 2;
                    continue;
                }
                if (!NeedsCount(c))
                {
                    _out.WriteByte(c);
                    i++;
                    continue;
                }

                // A run of bytes that cannot stand alone (1-8 and 0x80 and up: UTF-8 letters), cut where a back reference would pay off.
                var start = i;
                var run = 0;
                while (run < 8 && i < n && NeedsCount(data[i]))
                {
                    if (run > 0)
                    {
                        Insert(i);
                        if (Match(i).Length >= 3) break;
                    }
                    run++;
                    i++;
                }
                _out.WriteByte((byte)run);
                _out.Write(data, start, run);
            }
            return _out.ToArray();
        }

        private static bool NeedsCount(byte b) => b is >= 1 and <= 8 or >= 0x80;

        private int Hash(int at) => ((data[at] << 7) ^ (data[at + 1] << 3) ^ data[at + 2]) & 0xFFF;

        /// <summary>Adds every position before <paramref name="end"/> to the hash chains.</summary>
        private void Insert(int end)
        {
            for (; _inserted < end; _inserted++)
            {
                if (_inserted + 2 >= data.Length) continue;
                var h = Hash(_inserted);
                _previous[_inserted] = _head[h];
                _head[h] = _inserted;
            }
        }

        private (int Length, int Distance) Match(int at)
        {
            if (at + 3 > data.Length) return (0, 0);
            int best = 0, distance = 0, steps = 0;
            for (var candidate = _head[Hash(at)]; candidate >= 0 && at - candidate <= Window && steps++ < MaxChain; candidate = _previous[candidate])
            {
                var limit = Math.Min(Math.Min(MaxMatch, data.Length - at), at - candidate);
                var length = 0;
                while (length < limit && data[candidate + length] == data[at + length]) length++;
                if (length > best)
                {
                    best = length;
                    distance = at - candidate;
                    if (best == MaxMatch) break;
                }
            }
            return (best, distance);
        }
    }
}
