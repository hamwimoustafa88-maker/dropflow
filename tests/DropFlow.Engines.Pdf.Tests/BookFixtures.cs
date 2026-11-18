using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using DropFlow.TestSupport;

namespace DropFlow.Engines.Pdf.Tests;

/// <summary>
/// E-book files written by hand in the tests, field by field from the format descriptions (not with the production writers):
/// FB2, Mobipocket (PalmDOC and HUFF/CDIC compression, EXTH, trailing entries, DRM flag), KF8 (AZW3), HTMLZ, TXTZ and comic archives.
/// </summary>
internal static class BookFixtures
{
    public const string ArabicTitle = "كتاب تجريبي";
    public const string ArabicChapter = "الفصل الأول";
    public const string ArabicText = "هذا نص عربي للتجربة";

    public static byte[] Png(int width = 8, int height = 6)
    {
        using var dir = new TempDir();
        return File.ReadAllBytes(Samples.Png(dir.Combine("p.png"), width, height));
    }

    // ---- FB2 --------------------------------------------------------------------------------------------------------------------

    public static string Fb2Arabic(string path)
    {
        var png = Convert.ToBase64String(Png());
        var xml =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" +
            "<FictionBook xmlns=\"http://www.gribuser.ru/xml/fictionbook/2.0\" xmlns:l=\"http://www.w3.org/1999/xlink\">\n" +
            "<description><title-info><genre>prose_classic</genre><author><first-name>أحمد</first-name><last-name>شوقي</last-name></author>" +
            $"<book-title>{ArabicTitle}</book-title><lang>ar</lang></title-info>" +
            "<document-info><author><nickname>tester</nickname></author><date>2026</date><id>1</id><version>1.0</version></document-info></description>\n" +
            "<body><title><p>" + ArabicTitle + "</p></title>\n" +
            "<section id=\"s1\"><title><p>" + ArabicChapter + "</p></title>" +
            "<epigraph><p>حكمة</p><text-author>قائل</text-author></epigraph>" +
            "<p>" + ArabicText + " <strong>مهم</strong> و<emphasis>مائل</emphasis><a l:href=\"#n1\" type=\"note\">1</a></p>" +
            "<image l:href=\"#pic.png\"/>" +
            "<poem><stanza><v>بيت أول</v><v>بيت ثان</v></stanza></poem>" +
            "<section><title><p>قسم فرعي</p></title><p>نص القسم الفرعي</p></section>" +
            "</section>\n" +
            "<section><title><p>الفصل الثاني</p></title><p>نهاية الكتاب</p><empty-line/><table><tr><th>أ</th><th>ب</th></tr><tr><td>1</td><td>2</td></tr></table></section>" +
            "</body>\n" +
            "<body name=\"notes\"><section id=\"n1\"><title><p>1</p></title><p>حاشية</p></section></body>\n" +
            $"<binary id=\"pic.png\" content-type=\"image/png\">{png}</binary>\n" +
            "</FictionBook>\n";
        File.WriteAllText(path, xml, new UTF8Encoding(false));
        return path;
    }

    /// <summary>A Russian FB2 saved as windows-1251, as many old FB2 files are.</summary>
    public static string Fb2Cyrillic(string path)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var xml =
            "<?xml version=\"1.0\" encoding=\"windows-1251\"?>\n" +
            "<FictionBook xmlns=\"http://www.gribuser.ru/xml/fictionbook/2.0\"><description><title-info><genre>prose</genre>" +
            "<author><nickname>Автор</nickname></author><book-title>Книга</book-title><lang>ru</lang></title-info></description>" +
            "<body><section><title><p>Глава первая</p></title><p>Привет, мир!</p></section></body></FictionBook>";
        File.WriteAllBytes(path, Encoding.GetEncoding(1251).GetBytes(xml));
        return path;
    }

    // ---- MOBI -------------------------------------------------------------------------------------------------------------------

    public enum MobiCompression { None = 1, PalmDoc = 2, Huff = 17480 }

    /// <summary>
    /// A Mobipocket 6 book: two chapters split by mbp:pagebreak, a filepos link to the second chapter, a picture (recindex), Arabic text,
    /// EXTH author/title/language, trailing entries on each text record (multibyte byte + one TBS-like entry).
    /// </summary>
    public static string Mobi(string path, MobiCompression compression = MobiCompression.PalmDoc, ushort encryption = 0, int recordSize = 4096)
    {
        var chapter2 = "<h1>Chapter Two</h1><p>Second chapter text. Second chapter text. Second chapter text.</p>";
        string Build(int target) =>
            "<html><head><guide><reference type=\"toc\" filepos=0000000000 /></guide></head><body>" +
            $"<h1>{ArabicChapter}</h1><p>{ArabicText}</p><p><img recindex=\"00001\" /></p>" +
            $"<p><a filepos={target:D10}>go to chapter two</a></p>" +
            "<mbp:pagebreak />" + chapter2 + "</body></html>";
        // The link target is the byte offset of "<h1>Chapter Two"; find it with a first pass.
        var probe = Encoding.UTF8.GetBytes(Build(0));
        var offset = IndexOf(probe, Encoding.UTF8.GetBytes("<h1>Chapter Two"));
        var text = Encoding.UTF8.GetBytes(Build(offset));

        var records = new List<byte[]>();
        var textRecords = new List<byte[]>();
        for (var i = 0; i < text.Length; i += recordSize)
        {
            var chunk = text.AsSpan(i, Math.Min(recordSize, text.Length - i)).ToArray();
            var body = compression switch
            {
                MobiCompression.PalmDoc => PalmDocCompress(chunk),
                MobiCompression.Huff => HuffEncode(chunk),
                _ => chunk,
            };
            // trailing entries: multibyte indicator (0 = no overlap), then a 3-byte entry whose size (0x83) ends it
            textRecords.Add([.. body, 0x00, 0xAA, 0xBB, 0x83]);
        }

        var huffRecords = compression == MobiCompression.Huff ? HuffRecords() : [];
        var firstImage = 1 + textRecords.Count;
        var huffIndex = firstImage + 1;
        var exth = Exth((100, "أحمد شوقي"), (503, ArabicTitle), (524, "ar"));
        records.Add(Record0(compression, encryption, text.Length, textRecords.Count, recordSize, version: 6, firstImage, huffRecords.Count > 0 ? huffIndex : -1, huffRecords.Count, extraFlags: 0b11, exth, "Full Name", fdst: -1));
        records.AddRange(textRecords);
        records.Add(Png(10, 4));
        records.AddRange(huffRecords);
        records.Add([0xE9, 0x8E, 0x0D, 0x0A]);
        File.WriteAllBytes(path, PalmDb("Test_Book", "BOOK", "MOBI", records));
        return path;
    }

    /// <summary>A KF8-only book (.azw3): FDST with an HTML flow (two files, skeleton + fragment each) and a CSS flow, a kindle:embed picture.</summary>
    public static string Azw3(string path)
    {
        var flow0 =
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?><html xmlns=\"http://www.w3.org/1999/xhtml\"><head><title>a</title><link href=\"kindle:flow:0001?mime=text/css\" rel=\"stylesheet\"/></head><body aid=\"0\"></body></html>" +
            $"<div aid=\"1\"><h1>{ArabicChapter}</h1><p>{ArabicText}</p><p><img src=\"kindle:embed:0001?mime=image/png\"/></p></div>" +
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?><html xmlns=\"http://www.w3.org/1999/xhtml\"><head><title>b</title></head><body aid=\"2\"></body></html>" +
            "<div aid=\"3\"><h1>Chapter Two</h1><p>KF8 second file</p></div>";
        var flow1 = "p { color: red } /* CSS-FLOW-MARKER */";
        var text = Encoding.UTF8.GetBytes(flow0 + flow1);
        var len0 = Encoding.UTF8.GetByteCount(flow0);

        var fdst = new Be();
        fdst.Bytes("FDST"u8); fdst.U32(12); fdst.U32(2);
        fdst.U32(0); fdst.U32((uint)len0); fdst.U32((uint)len0); fdst.U32((uint)text.Length);

        var textRecord = PalmDocCompress(text);
        var exth = Exth((100, "Kindle Author"), (524, "ar"));
        var records = new List<byte[]>
        {
            Record0(MobiCompression.PalmDoc, 0, text.Length, 1, 4096, version: 8, firstImage: 2, huff: -1, huffCount: 0, extraFlags: 0, exth, "KF8 Book", fdst: 4),
            textRecord,
            Png(12, 5),
            new byte[] { 0x00 }, // placeholder resource that is not a picture
            fdst.ToArray(),
            new byte[] { 0xE9, 0x8E, 0x0D, 0x0A },
        };
        File.WriteAllBytes(path, PalmDb("KF8_Book", "BOOK", "MOBI", records));
        return path;
    }

    private static byte[] Record0(MobiCompression compression, ushort encryption, int textLength, int textRecords, int recordSize, int version,
        int firstImage, int huff, int huffCount, ushort extraFlags, byte[] exth, string fullName, int fdst)
    {
        const int headerLength = 232;
        var r = new byte[16 + headerLength];
        BinaryPrimitives.WriteUInt16BigEndian(r, (ushort)compression);
        BinaryPrimitives.WriteUInt32BigEndian(r.AsSpan(4), (uint)textLength);
        BinaryPrimitives.WriteUInt16BigEndian(r.AsSpan(8), (ushort)textRecords);
        BinaryPrimitives.WriteUInt16BigEndian(r.AsSpan(10), (ushort)recordSize);
        BinaryPrimitives.WriteUInt16BigEndian(r.AsSpan(12), encryption);
        r.AsSpan(16, headerLength).Fill(0xFF);
        "MOBI"u8.CopyTo(r.AsSpan(16));
        void U32(int offset, uint value) => BinaryPrimitives.WriteUInt32BigEndian(r.AsSpan(offset), value);
        U32(20, headerLength);
        U32(24, 2);           // book
        U32(28, 65001);       // UTF-8
        U32(32, 12345);
        U32(36, (uint)version);
        U32(92, 0x0401);      // Arabic (Saudi Arabia)
        U32(104, (uint)version);
        U32(108, (uint)firstImage);
        U32(112, huff < 0 ? 0xFFFFFFFF : (uint)huff);
        U32(116, (uint)huffCount);
        U32(128, 0x40);       // EXTH present
        U32(192, fdst < 0 ? 0x00010001u : (uint)fdst);
        U32(240, extraFlags);
        var nameBytes = Encoding.UTF8.GetBytes(fullName);
        var nameOffset = r.Length + exth.Length;
        U32(84, (uint)nameOffset);
        U32(88, (uint)nameBytes.Length);
        return [.. r, .. exth, .. nameBytes, 0, 0, 0, 0];
    }

    private static byte[] Exth(params (int Type, string Value)[] items)
    {
        var body = new Be();
        foreach (var (type, value) in items)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            body.U32((uint)type); body.U32((uint)(8 + bytes.Length)); body.Bytes(bytes);
        }
        var content = body.ToArray();
        var padding = (4 - content.Length % 4) % 4;
        var exth = new Be();
        exth.Bytes("EXTH"u8); exth.U32((uint)(12 + content.Length + padding)); exth.U32((uint)items.Length);
        exth.Bytes(content);
        for (var i = 0; i < padding; i++) exth.U8(0);
        return exth.ToArray();
    }

    private static byte[] PalmDb(string name, string type, string creator, List<byte[]> records)
    {
        var b = new Be();
        var nameBytes = new byte[32];
        Encoding.ASCII.GetBytes(name).CopyTo(nameBytes, 0);
        b.Bytes(nameBytes);
        b.U16(0); b.U16(0); b.U32(0); b.U32(0); b.U32(0); b.U32(0); b.U32(0); b.U32(0);
        b.Bytes(Encoding.ASCII.GetBytes(type)); b.Bytes(Encoding.ASCII.GetBytes(creator));
        b.U32(0); b.U32(0); b.U16((ushort)records.Count);
        var offset = 78 + records.Count * 8 + 2;
        for (var i = 0; i < records.Count; i++)
        {
            b.U32((uint)offset); b.U8(0); b.U8(0); b.U8((byte)(i >> 8)); b.U8((byte)i);
            offset += records[i].Length;
        }
        b.U16(0);
        foreach (var record in records) b.Bytes(record);
        return b.ToArray();
    }

    /// <summary>PalmDOC LZ77 with all three code kinds: back references, "space + letter", and literal runs for bytes 0x80 and up.</summary>
    public static byte[] PalmDocCompress(byte[] input)
    {
        var output = new List<byte>();
        var i = 0;
        while (i < input.Length)
        {
            // longest earlier match of 3..10 bytes within 2047 bytes
            int bestLength = 0, bestDistance = 0;
            for (var distance = 1; distance <= Math.Min(2047, i); distance++)
            {
                var length = 0;
                while (length < 10 && i + length < input.Length && input[i + length] == input[i - distance + length]) length++;
                if (length > bestLength) { bestLength = length; bestDistance = distance; }
            }
            if (bestLength >= 3)
            {
                var code = 0x8000 | (bestDistance << 3) | (bestLength - 3);
                output.Add((byte)(code >> 8));
                output.Add((byte)code);
                i += bestLength;
                continue;
            }
            var c = input[i];
            if (c == ' ' && i + 1 < input.Length && input[i + 1] is >= 0x40 and <= 0x7F)
            {
                output.Add((byte)(input[i + 1] ^ 0x80));
                i += 2;
                continue;
            }
            if (c is 0 or >= 0x09 and <= 0x7F)
            {
                output.Add(c);
                i++;
                continue;
            }
            var run = 0;
            while (run < 8 && i + run < input.Length && input[i + run] is >= 0x80 or (>= 1 and <= 8)) run++;
            output.Add((byte)run);
            for (var k = 0; k < run; k++) output.Add(input[i + k]);
            i += run;
        }
        return output.ToArray();
    }

    // HUFF/CDIC: every code is 8 bits long; code byte b means phrase 255 - b. Phrase 0..199 are single bytes (0x00..0xC7 is enough for the
    // ASCII and UTF-8 bytes used here... not all: bytes 0xC8..0xFF map to phrases 200..255 below), and phrase 255 is a compressed phrase.
    private static byte[] HuffEncode(byte[] text)
    {
        var output = new List<byte>();
        var i = 0;
        while (i < text.Length)
        {
            if (i + 8 <= text.Length && text.AsSpan(i, 8).SequenceEqual("chapter "u8))
            {
                output.Add(255 - 255); // phrase 255: a compressed phrase that expands to "chapter "
                i += 8;
                continue;
            }
            output.Add((byte)(255 - PhraseOf(text[i])));
            i++;
        }
        return output.ToArray();
    }

    // Byte value v (0..254) is phrase v; phrase 255 is the compressed "chapter ". Byte 0xFF never occurs in UTF-8 text.
    private static int PhraseOf(byte value) => value;

    private static List<byte[]> HuffRecords()
    {
        var huff = new Be();
        huff.Bytes("HUFF"u8); huff.U32(24); huff.U32(24); huff.U32(24 + 1024); huff.U32(0); huff.U32(0); // the header is 24 bytes
        for (var b = 0; b < 256; b++) huff.U32(8u | 0x80u | (255u << 8));
        for (var k = 0; k < 64; k++) huff.U32(0);

        var phrases = new List<byte[]>();
        for (var v = 0; v < 255; v++) phrases.Add([(byte)v]);
        phrases.Add("chapter "u8.ToArray().Select(c => (byte)(255 - c)).ToArray()); // compressed: codes of its letters
        var offsets = new Be();
        var data = new Be();
        var tableSize = phrases.Count * 2;
        for (var p = 0; p < phrases.Count; p++)
        {
            offsets.U16((ushort)(tableSize + data.Length));
            var literal = p != 255;
            data.U16((ushort)(phrases[p].Length | (literal ? 0x8000 : 0)));
            data.Bytes(phrases[p]);
        }
        var cdic = new Be();
        cdic.Bytes("CDIC"u8); cdic.U32(16); cdic.U32((uint)phrases.Count); cdic.U32(8);
        cdic.Bytes(offsets.ToArray()); cdic.Bytes(data.ToArray());
        return [huff.ToArray(), cdic.ToArray()];
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (var i = 0; i + needle.Length <= haystack.Length; i++)
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle)) return i;
        return -1;
    }

    // ---- HTMLZ / TXTZ -----------------------------------------------------------------------------------------------------------

    public static string Htmlz(string path)
    {
        using var zip = new ZipArchive(File.Create(path), ZipArchiveMode.Create);
        Add(zip, "index.html", "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>From HTML</title><script>alert(1)</script></head><body>" +
                               "<h1>First</h1><p>Alpha <b>bold</b> <img src=\"images/a.png\" alt=\"pic\"></p><h1>Second</h1><p>Beta</p></body></html>");
        AddBytes(zip, "images/a.png", Png());
        Add(zip, "metadata.opf", "<?xml version=\"1.0\"?><package xmlns=\"http://www.idpf.org/2007/opf\" version=\"2.0\"><metadata xmlns:dc=\"http://purl.org/dc/elements/1.1/\">" +
                                 "<dc:title>Zipped HTML Book</dc:title><dc:creator>Someone</dc:creator><dc:language>en</dc:language></metadata></package>");
        return path;
    }

    public static string Txtz(string path)
    {
        using var zip = new ZipArchive(File.Create(path), ZipArchiveMode.Create);
        Add(zip, "index.txt", $"# {ArabicChapter}\n\n{ArabicText}\n\n![صورة](images/p.png)\n\n# الفصل الثاني\n\nنهاية\n");
        AddBytes(zip, "images/p.png", Png());
        Add(zip, "metadata.opf", "<?xml version=\"1.0\"?><package xmlns=\"http://www.idpf.org/2007/opf\" version=\"2.0\"><metadata xmlns:dc=\"http://purl.org/dc/elements/1.1/\">" +
                                 $"<dc:title>{ArabicTitle}</dc:title><dc:language>ar</dc:language></metadata></package>");
        return path;
    }

    // ---- comics -----------------------------------------------------------------------------------------------------------------

    /// <summary>Page pictures whose width tells their number (page n is 10 * n pixels wide), named so that ordinal order would be wrong.</summary>
    public static IReadOnlyList<(string Name, byte[] Bytes)> ComicPages() =>
    [
        ("pages/page10.png", Png(100, 20)),
        ("pages/page2.png", Png(20, 20)),
        ("pages/page1.png", Png(10, 20)),
        ("pages/Page3.png", Png(30, 20)),
    ];

    public static string Cbz(string path, bool manga = false)
    {
        using var zip = new ZipArchive(File.Create(path), ZipArchiveMode.Create);
        foreach (var (name, bytes) in ComicPages()) AddBytes(zip, name, bytes);
        AddBytes(zip, "__MACOSX/pages/._page1.png", [0, 1, 2]);
        Add(zip, "notes.txt", "not a page");
        Add(zip, "ComicInfo.xml", "<?xml version=\"1.0\"?><ComicInfo><Series>Test Series</Series><Number>7</Number><Writer>Writer Name</Writer>" +
                                  (manga ? "<Manga>YesAndRightToLeft</Manga><LanguageISO>ja</LanguageISO>" : "") + "</ComicInfo>");
        return path;
    }

    /// <summary>The comic pages in a folder, for archivers that work on files (Rar.exe, 7-Zip).</summary>
    public static string ComicFolder(string folder)
    {
        foreach (var (name, bytes) in ComicPages())
        {
            var file = Path.Combine(folder, name.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllBytes(file, bytes);
        }
        return folder;
    }

    private static void Add(ZipArchive zip, string name, string content)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    private static void AddBytes(ZipArchive zip, string name, byte[] bytes)
    {
        using var stream = zip.CreateEntry(name).Open();
        stream.Write(bytes);
    }

    private sealed class Be
    {
        private readonly MemoryStream _s = new();
        public int Length => (int)_s.Length;
        public void U8(byte v) => _s.WriteByte(v);
        public void U16(ushort v) { _s.WriteByte((byte)(v >> 8)); _s.WriteByte((byte)v); }
        public void U32(uint v) { U16((ushort)(v >> 16)); U16((ushort)v); }
        public void Bytes(ReadOnlySpan<byte> b) => _s.Write(b);
        public byte[] ToArray() => _s.ToArray();
    }
}
