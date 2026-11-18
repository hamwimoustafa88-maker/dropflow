using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;
using DropFlow.Engines.Pdf.Books;
using DropFlow.Engines.Pdf.Office;
using DropFlow.TestSupport;
using ImageMagick;

namespace DropFlow.Engines.Pdf.Tests;

/// <summary>
/// Writing Mobipocket 6 books (.mobi). Every file is checked twice: read back with DropFlow's own MOBI reader (chapters, title, author,
/// language, pictures, cover), and taken apart with <see cref="MobiInspector"/>, which shares no code with the product.
/// </summary>
public sealed class MobiWriterTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    // ---- round trips through our own reader ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Epub_to_mobi_reads_back_with_the_same_chapters_metadata_pictures_links_and_cover()
    {
        var epub = EpubFixture.Write(_dir.Combine("book.epub"), cover: true);
        var mobi = (await Kit.Ok(Kit.RunAsync("convert:mobi", [epub]))).Outputs[0];
        Assert.Equal(_dir.Combine("book (converted).mobi"), mobi);

        var source = BookReader.Read(epub, EpubImages.Inline);
        Assert.NotNull(source.Cover);
        var back = Mobi.Read(mobi);
        Assert.Equal("Sample Book", back.Title);
        Assert.Equal("An Author", back.Author);
        Assert.Equal("en", back.Language);
        Assert.Equal(ChapterTexts(source), ChapterTexts(back)); // the generated table of contents is not read back as a chapter
        Assert.Equal(source.Cover, back.Cover);                // a small JPEG cover is stored as it is

        var doc = new HtmlParser().ParseDocument("<body>" + back.BodyHtml + "</body>");
        var picture = BookHtml.FromDataUri(doc.QuerySelector("img")!.GetAttribute("src")!)!.Value;
        Assert.Equal(PictureOf(source), picture.Bytes);         // the chapter picture (a PNG) is stored as it is
        Assert.Equal("A cover picture", doc.QuerySelector("img")!.GetAttribute("alt"));

        // "link to chapter two" pointed at <h2 id="sec">: it still lands right before that heading.
        var link = doc.QuerySelectorAll("a[href]").Single(a => a.TextContent == "link to chapter two");
        var target = doc.GetElementById(link.GetAttribute("href")![1..])!;
        var all = doc.QuerySelectorAll("*").ToList();
        Assert.Equal("Section", all[all.IndexOf(target) + 1].TextContent);
    }

    [Fact]
    public async Task Arabic_fb2_to_mobi_keeps_the_logical_order_the_language_the_direction_and_the_cover()
    {
        var fb2 = BookFixtures.Fb2Arabic(_dir.Combine("كتاب.fb2"), cover: true);
        var mobi = (await Kit.Ok(Kit.RunAsync("convert:mobi", [fb2]))).Outputs[0];
        Assert.Equal(_dir.Combine("كتاب (converted).mobi"), mobi);

        var source = BookReader.Read(fb2, EpubImages.Inline);
        var back = Mobi.Read(mobi);
        Assert.Equal(BookFixtures.ArabicTitle, back.Title);
        Assert.Equal("أحمد شوقي", back.Author);
        Assert.Equal("ar", back.Language);
        Assert.Equal(ChapterTexts(source), ChapterTexts(back));
        Assert.Equal(EpubFixture.CoverJpeg(), back.Cover);

        var text = new HtmlParser().ParseDocument("<body>" + back.BodyHtml + "</body>").Body!.TextContent;
        Assert.True(text.IndexOf(BookFixtures.ArabicChapter, StringComparison.Ordinal) < text.IndexOf(BookFixtures.ArabicText, StringComparison.Ordinal));
        Assert.True(text.IndexOf(BookFixtures.ArabicText, StringComparison.Ordinal) < text.IndexOf("نهاية الكتاب", StringComparison.Ordinal));

        // The markup itself says right to left and Arabic, so a reader that ignores EXTH 524 still lays it out correctly.
        var html = new HtmlParser().ParseDocument(Encoding.UTF8.GetString(MobiInspector.Open(mobi).Text));
        Assert.Equal("rtl", html.Body!.GetAttribute("dir"));
        Assert.Equal("ar", html.Body.GetAttribute("lang"));
    }

    [Theory]
    [InlineData("htmlz")]
    [InlineData("txtz")]
    [InlineData("azw3")]
    public async Task Other_ebooks_become_mobi_with_their_text(string input)
    {
        var path = input switch
        {
            "htmlz" => BookFixtures.Htmlz(_dir.Combine("book.htmlz")),
            "txtz" => BookFixtures.Txtz(_dir.Combine("book.txtz")),
            _ => BookFixtures.Azw3(_dir.Combine("book.azw3")),
        };
        var mobi = (await Kit.Ok(Kit.RunAsync("convert:mobi", [path]))).Outputs[0];
        var source = BookReader.Read(path, EpubImages.Inline);
        var back = Mobi.Read(mobi);
        Assert.Equal(source.Title, back.Title);
        Assert.Equal(BookLanguage.Primary(source.Language), back.Language);
        Assert.Equal(ChapterTexts(source), ChapterTexts(back));
        Assert.Equal(Regex.Matches(source.BodyHtml, "<img").Count, Regex.Matches(back.BodyHtml, "<img").Count);
    }

    // ---- structure, checked without the product ---------------------------------------------------------------------------------

    [Fact]
    public async Task A_written_mobi_is_a_valid_palm_database_with_a_mobi_6_header()
    {
        var epub = EpubFixture.Write(_dir.Combine("book.epub"), cover: true);
        var file = (await Kit.Ok(Kit.RunAsync("convert:mobi", [epub]))).Outputs[0];
        var m = MobiInspector.Open(file);

        Assert.Equal("BOOK", m.Type);
        Assert.Equal("MOBI", m.Creator);
        Assert.Equal("Sample_Book", m.Name);

        Assert.Equal(2, m.Compression);      // PalmDOC
        Assert.Equal(0, m.Encryption);
        Assert.Equal(4096, m.RecordSize);
        Assert.Equal("MOBI", m.Magic);
        Assert.Equal(232u, m.HeaderLength);
        Assert.Equal(2u, m.MobiType);        // a book
        Assert.Equal(65001u, m.TextEncoding);  // UTF-8
        Assert.Equal(6u, m.FileVersion);
        Assert.Equal(6u, m.MinVersion);
        Assert.Equal(0x09u, m.Locale & 0xFF); // English
        Assert.Equal("Sample Book", m.FullName);
        Assert.Equal(1u, m.ExtraFlags);      // multibyte overlap entries only

        // Text records: the count in the header is the real count, the lengths add up, every record holds 4096 bytes except the last.
        Assert.Equal(m.TextRecordCount, m.Chunks.Count);
        Assert.Equal((uint)(m.TextRecordCount + 1), m.FirstNonBook);
        Assert.Equal(m.TextLength, (uint)m.Text.Length);
        Assert.All(m.Chunks.SkipLast(1), c => Assert.Equal(4096, c.Length));
        Assert.InRange(m.Chunks[^1].Length, 1, 4096);
        Assert.Equal(1, m.FirstContent);

        // EXTH
        Assert.True((m.ExthFlags & 0x40) != 0);
        Assert.Equal("An Author", m.ExthText(100));
        Assert.Equal("Sample Book", m.ExthText(503));
        Assert.Equal("en", m.ExthText(524));

        // Pictures follow the text: the chapter PNG, then the cover (JPEG) and its thumbnail (a smaller JPEG).
        Assert.Equal(m.FirstNonBook, m.FirstImage);
        var first = (int)m.FirstImage;
        var cover = m.Records[first + (int)m.ExthNumber(201)];
        var thumbnail = m.Records[first + (int)m.ExthNumber(202)];
        Assert.Equal(EpubFixture.CoverJpeg(), cover);
        var thumb = new MagickImageInfo(thumbnail);
        Assert.Equal(MagickFormat.Jpeg, thumb.Format);
        Assert.True(thumb.Width <= 180 && thumb.Height <= 240, $"thumbnail is {thumb.Width} x {thumb.Height}");
        var images = m.Records.Skip(first).Take(m.LastContent - first + 1).ToList();
        Assert.Equal(3, images.Count);
        Assert.All(images, i => Assert.Contains(new MagickImageInfo(i).Format, new[] { MagickFormat.Jpeg, MagickFormat.Png, MagickFormat.Gif }));
        Assert.All(images, i => Assert.True(i.Length <= 127 * 1024));

        // FLIS, FCIS and the end-of-file record close the database.
        Assert.Equal(m.Records.Count - 3, (int)m.FlisIndex);
        Assert.Equal(m.Records.Count - 2, (int)m.FcisIndex);
        Assert.Equal("FLIS"u8.ToArray(), m.Records[^3][..4]);
        Assert.Equal("FCIS"u8.ToArray(), m.Records[^2][..4]);
        Assert.Equal(m.TextLength, System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(m.Records[^2].AsSpan(20)));
        Assert.Equal(new byte[] { 0xE9, 0x8E, 0x0D, 0x0A }, m.Records[^1]);

        // The text is UTF-8 HTML: a guide in the head, recindex pictures that exist, a page break between the contents and each chapter.
        var html = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(m.Text);
        Assert.StartsWith("<html>", html);
        Assert.EndsWith("</html>", html);
        var head = Regex.Match(html, "^<html><head>(?<head>.*?)</head><body[^>]*>", RegexOptions.Singleline);
        Assert.True(head.Success);
        // (an HTML5 parser would move <guide> out of the head, so the head is checked as text)
        Assert.Matches("^<guide><reference type=\"toc\"[^>]*filepos=\\d{10} */><reference type=\"text\"[^>]*filepos=\\d{10} */></guide>$", head.Groups["head"].Value);
        var doc = new HtmlParser().ParseDocument(html);
        Assert.Equal(2, doc.QuerySelectorAll("h1").Length);
        var recindexes = Regex.Matches(html, "recindex=\"(\\d{5})\"").Select(x => int.Parse(x.Groups[1].Value)).ToList();
        Assert.Equal([1], recindexes.Distinct());
        Assert.Equal(2, Regex.Matches(html, "<mbp:pagebreak/>").Count); // contents | chapter one | chapter two
        Assert.DoesNotContain("data:image", html);
        Assert.DoesNotContain(" src=", html);
        Assert.DoesNotContain(" id=", html);
    }

    [Fact]
    public void Filepos_links_point_at_the_byte_offset_of_their_target()
    {
        var book = new EpubBook("Links", null, "en",
            "<section id=\"ch1\"><h1>One</h1><p>See <a href=\"#later\">later</a> and <a href=\"#ch2\">two</a>; <a href=\"#nowhere\">dead</a>; <a href=\"https://example.org/\">web</a>.</p></section>" +
            "<section id=\"ch2\"><h1>Two</h1><p id=\"later\">Later paragraph</p></section>");
        var path = _dir.Combine("links.mobi");
        MobiWriter.Write(book, path);
        var text = MobiInspector.Open(path).Text;
        var html = Encoding.UTF8.GetString(text);

        string At(long position) => Encoding.UTF8.GetString(text, (int)position, Math.Min(40, text.Length - (int)position));
        var links = Regex.Matches(html, "<a filepos=(\\d{10})>([^<]*)</a>").ToDictionary(x => x.Groups[2].Value, x => long.Parse(x.Groups[1].Value));
        Assert.StartsWith("<p>Later paragraph", At(links["later"]));
        Assert.StartsWith("<h1>Two", At(links["two"]));
        Assert.DoesNotContain("dead", links.Keys);                // a link to nothing loses its link, keeps its text
        Assert.Contains("; dead;", html);
        Assert.Contains("<a href=\"https://example.org/\">web</a>", html);

        // The table of contents and the guide point at the chapters and at the contents page itself.
        var toc = Regex.Match(html, "<reference type=\"toc\" title=\"[^\"]*\" filepos=(\\d{10}) */>");
        Assert.True(toc.Success);
        Assert.StartsWith("<h2>Links</h2>", At(long.Parse(toc.Groups[1].Value)));
        Assert.StartsWith("<h1>One", At(links["One"]));
    }

    [Fact]
    public void A_long_arabic_book_splits_characters_across_records_with_multibyte_trailing_entries()
    {
        var body = new StringBuilder();
        for (var chapter = 1; chapter <= 3; chapter++)
        {
            body.Append($"<section id=\"ch{chapter}\"><h1>الفصل {chapter}</h1>");
            for (var i = 0; i < 400; i++) body.Append($"<p>الفقرة {chapter}.{i}: {BookFixtures.ArabicText} - نص {i * 7919 % 1000}</p>");
            body.Append("</section>");
        }
        var book = new EpubBook("كتاب طويل", "مؤلف", "ar", body.ToString());
        var path = _dir.Combine("long.mobi");
        MobiWriter.Write(book, path);

        var m = MobiInspector.Open(path);
        Assert.True(m.Chunks.Count > 10);
        Assert.All(m.Chunks.SkipLast(1), c => Assert.Equal(4096, c.Length));
        Assert.Contains(m.Overlaps, o => o.Length > 0); // the layout really cuts characters, so the overlap path is exercised
        for (var i = 0; i < m.Chunks.Count; i++)
        {
            var overlap = m.Overlaps[i];
            if (overlap.Length == 0)
            {
                if (i + 1 < m.Chunks.Count) Assert.False((m.Chunks[i + 1][0] & 0xC0) == 0x80, $"record {i + 2} starts inside a character without an overlap entry");
                continue;
            }
            // The overlap repeats the continuation bytes that start the next record.
            Assert.Equal(overlap, m.Chunks[i + 1][..overlap.Length]);
            Assert.All(overlap, b => Assert.Equal(0x80, b & 0xC0));
        }
        new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(m.Text); // valid UTF-8 as a whole

        var back = Mobi.Read(path);
        Assert.Equal("ar", back.Language);
        Assert.Equal(ChapterTexts(book), ChapterTexts(back));
    }

    [Fact]
    public void Pictures_mobi_readers_cannot_show_become_jpeg_and_big_ones_are_made_small_enough()
    {
        byte[] Encode(MagickImage image, MagickFormat format) { image.Format = format; return image.ToByteArray(); }
        using var small = new MagickImage(MagickColors.OrangeRed, 40, 30);
        var bmp = Encode(small, MagickFormat.Bmp);
        var gif = Encode(small, MagickFormat.Gif);
        using var noise = new MagickImage(MagickColors.Gray, 900, 900);
        noise.AddNoise(NoiseType.Random);
        var bigPng = Encode(noise, MagickFormat.Png);
        Assert.True(bigPng.Length > 127 * 1024);

        string Img(string mime, byte[] bytes) => $"<p><img src=\"data:{mime};base64,{Convert.ToBase64String(bytes)}\" alt=\"{mime}\"/></p>";
        var book = new EpubBook("Pictures", null, "en", "<section id=\"ch1\"><h1>Pictures</h1>" + Img("image/bmp", bmp) + Img("image/png", bigPng) + Img("image/gif", gif) + Img("image/bmp", bmp) + "</section>");
        var path = _dir.Combine("pictures.mobi");
        MobiWriter.Write(book, path);

        var m = MobiInspector.Open(path);
        var images = m.Records.Skip((int)m.FirstImage).Take(m.LastContent - (int)m.FirstImage + 1).ToList();
        Assert.Equal([MagickFormat.Jpeg, MagickFormat.Jpeg, MagickFormat.Gif], images.Select(i => new MagickImageInfo(i).Format));
        Assert.True(images[1].Length <= 127 * 1024, $"{images[1].Length} bytes");
        Assert.Equal(gif, images[2]);
        var html = Encoding.UTF8.GetString(m.Text);
        Assert.Equal([1, 2, 3, 1], Regex.Matches(html, "recindex=\"(\\d{5})\"").Select(x => int.Parse(x.Groups[1].Value))); // the same picture twice is stored once
        Assert.False(m.Exth.ContainsKey(201)); // no cover was given
    }

    [Fact]
    public async Task Cancellation_leaves_nothing_behind()
    {
        var epub = EpubFixture.Write(_dir.Combine("book.epub"));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Kit.RunAsync("convert:mobi", [epub], ct: cts.Token));
        Assert.Equal(["book.epub"], Kit.FilesIn(_dir.Path));
    }

    [Fact]
    public async Task An_existing_output_is_never_overwritten()
    {
        var epub = EpubFixture.Write(_dir.Combine("book.epub"));
        var taken = _dir.WriteText("book (converted).mobi", "keep me");
        var mobi = (await Kit.Ok(Kit.RunAsync("convert:mobi", [epub]))).Outputs[0];
        Assert.NotEqual(taken, mobi);
        Assert.Equal("keep me", File.ReadAllText(taken));
    }

    // ---- PalmDOC compression ------------------------------------------------------------------------------------------------------

    public static TheoryData<string> CompressionInputs() => new()
    {
        "empty", "ascii", "arabic", "every-byte", "random", "spaces",
    };

    [Theory]
    [MemberData(nameof(CompressionInputs))]
    public void PalmDoc_compression_round_trips_through_an_independent_decoder(string kind)
    {
        var input = kind switch
        {
            "empty" => [],
            "ascii" => Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("The quick brown fox jumps over the lazy dog. ", 100)))[..4096],
            "arabic" => Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat(BookFixtures.ArabicText + " ", 200)))[..4000],
            "every-byte" => Enumerable.Range(0, 4096).Select(i => (byte)(i % 256)).ToArray(),
            "random" => RandomBytes(4096),
            _ => Encoding.ASCII.GetBytes(new string(' ', 3000) + "x" + new string(' ', 1000)),
        };
        var packed = PalmDoc.Compress(input);
        Assert.Equal(input, MobiInspector.Inflate(packed));
        using var product = new MemoryStream();
        PalmDoc.Decompress(packed, product);
        Assert.Equal(input, product.ToArray());
    }

    [Fact]
    public void PalmDoc_compression_makes_ordinary_text_smaller()
    {
        var input = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("<p>The quick brown fox jumps over the lazy dog.</p>", 100)))[..4096];
        Assert.True(PalmDoc.Compress(input).Length < input.Length / 2);
    }

    // ---- Calibre, when it happens to be installed -------------------------------------------------------------------------------

    [CalibreFact]
    public async Task Calibre_reads_the_title_author_language_and_text()
    {
        var fb2 = BookFixtures.Fb2Arabic(_dir.Combine("book.fb2"), cover: true);
        var mobi = (await Kit.Ok(Kit.RunAsync("convert:mobi", [fb2]))).Outputs[0];

        var meta = Calibre.Run("ebook-meta", mobi);
        Assert.Contains(BookFixtures.ArabicTitle, meta);
        Assert.Contains("أحمد شوقي", meta);
        Assert.Contains("ara", meta.ToLowerInvariant());

        var txt = _dir.Combine("calibre.txt");
        Calibre.Run("ebook-convert", mobi, txt);
        var text = File.ReadAllText(txt);
        Assert.Contains(BookFixtures.ArabicText, text);
        Assert.Contains("نهاية الكتاب", text);
    }

    // ---- helpers ------------------------------------------------------------------------------------------------------------------

    /// <summary>The text of each chapter with all whitespace removed (markup may differ; words and their order may not).</summary>
    private static List<string> ChapterTexts(EpubBook book) =>
        BookHtml.Chapters(book.BodyHtml, book.Title)
            .Select(c => Regex.Replace(new HtmlParser().ParseDocument("<body>" + c.Html + "</body>").Body!.TextContent, @"\s+", ""))
            .ToList();

    private static byte[] PictureOf(EpubBook book)
    {
        var src = new HtmlParser().ParseDocument("<body>" + book.BodyHtml + "</body>").QuerySelector("img")!.GetAttribute("src")!;
        return BookHtml.FromDataUri(src)!.Value.Bytes;
    }

    private static byte[] RandomBytes(int count)
    {
        var bytes = new byte[count];
        new Random(42).NextBytes(bytes);
        return bytes;
    }
}

/// <summary>Calibre's command-line tools, when Calibre is installed on this PC (nothing is ever installed by the tests).</summary>
internal static class Calibre
{
    public static string? Folder { get; } = Find();

    private static string? Find()
    {
        var candidates = new List<string>();
        candidates.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));
        foreach (var root in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.LocalApplicationData })
        {
            var folder = Environment.GetFolderPath(root);
            if (folder.Length == 0) continue;
            candidates.Add(Path.Combine(folder, "Calibre2"));
            candidates.Add(Path.Combine(folder, "Programs", "Calibre2"));
        }
        var exe = OperatingSystem.IsWindows() ? "ebook-meta.exe" : "ebook-meta";
        return candidates.FirstOrDefault(c => File.Exists(Path.Combine(c, exe)));
    }

    public static string Run(string tool, params string[] arguments)
    {
        var exe = Path.Combine(Folder!, OperatingSystem.IsWindows() ? tool + ".exe" : tool);
        var start = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, StandardOutputEncoding = Encoding.UTF8 };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(120_000), $"{tool} did not finish");
        Assert.True(process.ExitCode == 0, $"{tool} failed: {error.Result}");
        return output.Result;
    }
}

/// <summary>A fact that runs only when Calibre is installed; otherwise it is skipped with the reason shown.</summary>
public sealed class CalibreFactAttribute : FactAttribute
{
    public CalibreFactAttribute()
    {
        if (Calibre.Folder is null)
            Skip = "Calibre (ebook-meta / ebook-convert) is not installed on this PC; the MOBI files were checked with MobiInspector only.";
    }
}
