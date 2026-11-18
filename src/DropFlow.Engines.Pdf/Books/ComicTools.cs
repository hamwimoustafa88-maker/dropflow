// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using DropFlow.Core.Operations;
using DropFlow.Engines.Pdf.Infrastructure;
using DropFlow.Engines.Pdf.Tools;
using ImageMagick;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using SharpCompress.Archives;

namespace DropFlow.Engines.Pdf.Books;

/// <summary>A comic book: its pages in reading order and what ComicInfo.xml says about it.</summary>
internal sealed record Comic(IReadOnlyList<(string Name, byte[] Bytes)> Pages, string? Title, string? Author, string? Language, bool RightToLeft);

/// <summary>
/// Comic books: CBZ (zip), CBR (rar) and CB7 (7z) archives of pictures, read with SharpCompress, to PDF (one page per picture),
/// fixed-layout EPUB, and CBZ; and a PDF to CBZ (each page drawn as a picture). Pages follow the natural order of their names
/// ("page2" before "page10"). A ComicInfo.xml that says "YesAndRightToLeft" (manga) makes the PDF and the EPUB read right to left.
/// </summary>
internal static partial class ComicTools
{
    private static readonly string[] PictureExtensions = ["jpg", "jpeg", "png", "gif", "webp", "bmp", "tif", "tiff", "avif", "heic", "jxl"];
    private const long MaxPageBytes = 200L * 1024 * 1024;
    private const long MaxTotalBytes = 2L * 1024 * 1024 * 1024;
    private const int MaxPages = 10_000;
    private const int CbzDpi = 200;

    public static IEnumerable<OperationSpec> Specs()
    {
        yield return new OperationSpec("convert:pdf", BookFormats.Comics, ctx => Task.FromResult(ToPdf(ctx)));
        yield return new OperationSpec("convert:epub", BookFormats.Comics, ctx => Task.FromResult(ToEpub(ctx)));
        yield return new OperationSpec("convert:cbz", ["cbr", "cb7", "pdf"], ctx => Task.FromResult(ToCbz(ctx)));
    }

    // ---- reading ------------------------------------------------------------------------------------------------------------

    internal static Comic Read(string path, CancellationToken ct)
    {
        var name = Path.GetFileName(path);
        var pages = new List<(string Name, byte[] Bytes)>();
        byte[]? comicInfo = null;
        long total = 0;
        try
        {
            using var archive = ArchiveFactory.OpenArchive(path, new SharpCompress.Readers.ReaderOptions { LeaveStreamOpen = false });
            void Take(string key, bool directory, bool encrypted, long size, Func<Stream> open)
            {
                ct.ThrowIfCancellationRequested();
                if (directory) return;
                var entryName = key.Replace('\\', '/');
                var leaf = entryName[(entryName.LastIndexOf('/') + 1)..];
                if (entryName.StartsWith("__MACOSX/", StringComparison.OrdinalIgnoreCase) || leaf.StartsWith('.')) return;
                var isInfo = leaf.Equals("ComicInfo.xml", StringComparison.OrdinalIgnoreCase);
                var extension = Path.GetExtension(leaf).TrimStart('.').ToLowerInvariant();
                if (!isInfo && !PictureExtensions.Contains(extension)) return;
                if (encrypted) throw new ConversionFailure($"{name} is protected with a password and cannot be converted.");
                if (size > MaxPageBytes) throw new ConversionFailure($"{name}: the page {leaf} is too large.");
                using var stream = open();
                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                total += buffer.Length;
                if (total > MaxTotalBytes) throw new ConversionFailure($"{name} is too large.");
                if (isInfo) comicInfo = buffer.ToArray();
                else pages.Add((entryName, buffer.ToArray()));
                if (pages.Count > MaxPages) throw new ConversionFailure($"{name} has too many pages.");
            }

            if (archive.IsSolid || archive.Type == SharpCompress.Common.ArchiveType.SevenZip)
            {
                using var reader = archive.ExtractAllEntries();
                while (reader.MoveToNextEntry())
                    Take(reader.Entry.Key ?? "", reader.Entry.IsDirectory, Encrypted(reader.Entry), reader.Entry.Size, () => reader.OpenEntryStream());
            }
            else
            {
                foreach (var entry in archive.Entries)
                {
                    var current = entry;
                    Take(current.Key ?? "", current.IsDirectory, Encrypted(current), current.Size, () => current.OpenEntryStream());
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or IOException or FormatException or NotSupportedException
                                       || ex.GetType().Namespace?.StartsWith("SharpCompress", StringComparison.Ordinal) == true)
        {
            if (ex.GetType().Name.Contains("Password", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("password", StringComparison.OrdinalIgnoreCase))
                throw new ConversionFailure($"{name} is protected with a password and cannot be converted.");
            throw new ConversionFailure($"{name} is not a readable comic book archive ({ex.Message}).");
        }

        if (pages.Count == 0) throw new ConversionFailure($"{name} has no pictures.");
        pages.Sort((a, b) => NaturalCompare(a.Name, b.Name));
        var (title, author, language, rtl) = ComicInfo(comicInfo);
        return new Comic(pages, title ?? Path.GetFileNameWithoutExtension(path), author, language, rtl);
    }

    private static bool Encrypted(SharpCompress.Common.IEntry entry)
    {
        try { return entry.IsEncrypted; } catch (NotImplementedException) { return false; }
    }

    private static (string? Title, string? Author, string? Language, bool RightToLeft) ComicInfo(byte[]? xml)
    {
        if (xml is null) return (null, null, null, false);
        try
        {
            using var reader = XmlReader.Create(new MemoryStream(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null });
            var document = XDocument.Load(reader);
            string? Value(string local) => document.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == local)?.Value.Trim() is { Length: > 0 } v ? v : null;
            var series = Value("Series");
            var number = Value("Number");
            var title = Value("Title") ?? (series is null ? null : number is null ? series : $"{series} {number}");
            var language = Value("LanguageISO");
            var rtl = string.Equals(Value("Manga"), "YesAndRightToLeft", StringComparison.OrdinalIgnoreCase) || BookLanguage.IsRtl(language);
            return (title, Value("Writer"), language, rtl);
        }
        catch (XmlException)
        {
            return (null, null, null, false); // the pictures are what matters
        }
    }

    [GeneratedRegex(@"\d+|\D+")]
    private static partial Regex Runs();

    /// <summary>Compares names the way people number pages: runs of digits by value ("2" before "10"), the rest without regard to case.</summary>
    internal static int NaturalCompare(string a, string b)
    {
        var left = Runs().Matches(a);
        var right = Runs().Matches(b);
        for (var i = 0; i < Math.Min(left.Count, right.Count); i++)
        {
            var x = left[i].Value;
            var y = right[i].Value;
            int result;
            if (char.IsAsciiDigit(x[0]) && char.IsAsciiDigit(y[0]))
            {
                var xt = x.TrimStart('0');
                var yt = y.TrimStart('0');
                result = xt.Length != yt.Length ? xt.Length.CompareTo(yt.Length) : string.CompareOrdinal(xt, yt);
                if (result == 0) result = x.Length.CompareTo(y.Length);
            }
            else
            {
                result = string.Compare(x, y, StringComparison.OrdinalIgnoreCase);
            }
            if (result != 0) return result;
        }
        var count = left.Count.CompareTo(right.Count);
        return count != 0 ? count : string.CompareOrdinal(a, b);
    }

    // ---- writing ------------------------------------------------------------------------------------------------------------

    private static ConversionResult ToPdf(JobContext ctx)
    {
        var comic = Read(ctx.Input, ctx.Ct);
        using var document = new PdfDocument();
        PdfSharpSupport.ApplyDefaultOptions(document);
        document.Info.Title = comic.Title ?? "";
        if (comic.Author is not null) document.Info.Author = comic.Author;
        if (comic.RightToLeft) document.ViewerPreferences.Direction = PdfReadingDirection.RightToLeft;
        var images = new List<XImage>();
        try
        {
            for (var i = 0; i < comic.Pages.Count; i++)
            {
                ctx.Ct.ThrowIfCancellationRequested();
                ctx.Report(0.05 + 0.9 * i / comic.Pages.Count, comic.Pages[i].Name);
                ImageToPdfTools.AddPicturePages(document, comic.Pages[i].Bytes, comic.Pages[i].Name, images);
            }
            var path = ctx.ReserveFile(ctx.Input, "pdf", "converted");
            document.Save(path);
            ctx.Report(1);
            return ctx.Ok(path);
        }
        finally
        {
            foreach (var image in images) image.Dispose();
        }
    }

    private static ConversionResult ToEpub(JobContext ctx)
    {
        var comic = Read(ctx.Input, ctx.Ct);
        var pages = new List<(byte[] Bytes, string Mime, int Width, int Height)>();
        for (var i = 0; i < comic.Pages.Count; i++)
        {
            ctx.Ct.ThrowIfCancellationRequested();
            ctx.Report(0.05 + 0.85 * i / comic.Pages.Count, comic.Pages[i].Name);
            pages.Add(EpubPicture(comic.Pages[i].Bytes, comic.Pages[i].Name));
        }
        var path = ctx.ReserveFile(ctx.Input, "epub", "converted");
        BookEpubWriter.WritePictureBook(path, comic.Title ?? Path.GetFileNameWithoutExtension(ctx.Input), comic.Author, comic.Language ?? "en", comic.RightToLeft, pages, ctx.Ct);
        ctx.Report(1);
        return ctx.Ok(path);
    }

    /// <summary>A page as an EPUB core picture type (JPEG, PNG, GIF, WebP), with its size; other formats are re-encoded.</summary>
    private static (byte[] Bytes, string Mime, int Width, int Height) EpubPicture(byte[] bytes, string name)
    {
        try
        {
            var info = new MagickImageInfo(bytes);
            var mime = info.Format switch
            {
                MagickFormat.Jpeg or MagickFormat.Jpg => "image/jpeg",
                MagickFormat.Png or MagickFormat.Png8 or MagickFormat.Png24 or MagickFormat.Png32 => "image/png",
                MagickFormat.Gif => "image/gif",
                MagickFormat.WebP => "image/webp",
                _ => null,
            };
            var upright = info.Orientation is Orientation.Undefined or Orientation.TopLeft;
            if (mime is not null && upright) return (bytes, mime, (int)info.Width, (int)info.Height);

            using var image = new MagickImage(bytes);
            image.AutoOrient();
            image.Format = image.HasAlpha ? MagickFormat.Png : MagickFormat.Jpeg;
            if (!image.HasAlpha) image.Quality = 92;
            return (image.ToByteArray(), image.HasAlpha ? "image/png" : "image/jpeg", (int)image.Width, (int)image.Height);
        }
        catch (MagickException ex)
        {
            throw new ConversionFailure($"The page {name} could not be read as a picture ({ex.Message}).");
        }
    }

    private static ConversionResult ToCbz(JobContext ctx)
    {
        var extension = Path.GetExtension(ctx.Input).TrimStart('.').ToLowerInvariant();
        var path = ctx.ReserveFile(ctx.Input, "cbz", "converted");
        using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            if (extension == "pdf") PdfPages(ctx, zip);
            else ArchivePages(ctx, zip);
        }
        ctx.Report(1);
        return ctx.Ok(path);
    }

    /// <summary>Repacks the pages of a CBR/CB7 in reading order, numbered so every reader keeps that order.</summary>
    private static void ArchivePages(JobContext ctx, ZipArchive zip)
    {
        var comic = Read(ctx.Input, ctx.Ct);
        var digits = Math.Max(3, comic.Pages.Count.ToString().Length);
        for (var i = 0; i < comic.Pages.Count; i++)
        {
            ctx.Ct.ThrowIfCancellationRequested();
            ctx.Report(0.1 + 0.85 * i / comic.Pages.Count);
            var ext = Path.GetExtension(comic.Pages[i].Name).ToLowerInvariant();
            using var entry = zip.CreateEntry((i + 1).ToString().PadLeft(digits, '0') + ext, CompressionLevel.NoCompression).Open();
            entry.Write(comic.Pages[i].Bytes);
        }
        AddComicInfo(zip, comic.Title, comic.Author, comic.Language, comic.RightToLeft, comic.Pages.Count);
    }

    /// <summary>Draws every page of a PDF as a JPEG (200 DPI) into the CBZ.</summary>
    private static void PdfPages(JobContext ctx, ZipArchive zip)
    {
        using var document = PdfiumDocument.Open(ctx.Input);
        if (document.PageCount < 1) throw new ConversionFailure("The PDF has no pages.");
        var digits = Math.Max(3, document.PageCount.ToString().Length);
        for (var i = 0; i < document.PageCount; i++)
        {
            ctx.Ct.ThrowIfCancellationRequested();
            ctx.Report((double)i / document.PageCount, $"Page {i + 1}");
            var rendered = document.Render(i, CbzDpi);
            var bytes = RasterEncoder.ToJpeg(rendered, PdfImageTools.DefaultQuality);
            using var entry = zip.CreateEntry((i + 1).ToString().PadLeft(digits, '0') + ".jpg", CompressionLevel.NoCompression).Open();
            entry.Write(bytes);
        }
        AddComicInfo(zip, Path.GetFileNameWithoutExtension(ctx.Input), null, null, false, document.PageCount);
    }

    private static void AddComicInfo(ZipArchive zip, string? title, string? author, string? language, bool rtl, int pages)
    {
        var info = new XElement("ComicInfo",
            new XAttribute(XNamespace.Xmlns + "xsi", "http://www.w3.org/2001/XMLSchema-instance"),
            title is null ? null : new XElement("Title", title),
            author is null ? null : new XElement("Writer", author),
            new XElement("PageCount", pages),
            language is null ? null : new XElement("LanguageISO", language),
            rtl ? new XElement("Manga", "YesAndRightToLeft") : null);
        using var writer = new StreamWriter(zip.CreateEntry("ComicInfo.xml", CompressionLevel.Optimal).Open(), new UTF8Encoding(false));
        writer.Write(new XDeclaration("1.0", "utf-8", null) + "\n" + info);
    }
}
