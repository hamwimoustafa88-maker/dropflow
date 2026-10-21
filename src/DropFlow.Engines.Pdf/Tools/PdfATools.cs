using System.Globalization;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using DropFlow.Core.Operations;
using DropFlow.Engines.Pdf.Infrastructure;
using ImageMagick;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;

namespace DropFlow.Engines.Pdf.Tools;

/// <summary>
/// <c>convert:pdfa</c> on a PDF: PDF/A-2b, made entirely in-process (no Ghostscript, nothing to download).
/// Every page is drawn with PDFium at 200 DPI and the file is written from those pictures. The pictures are plain 8-bit DeviceRGB
/// JPEGs, so the file has no fonts, no transparency, no JavaScript, no forms, no links and no encryption - nothing that PDF/A forbids
/// can be in it. On top of the pages the file gets what PDF/A-2b requires: an XMP packet declaring <c>pdfaid:part=2</c> /
/// <c>pdfaid:conformance=B</c> (with the title, author, subject and keywords of the source), an OutputIntent with an embedded sRGB
/// ICC profile, a PDF 1.7 header and a file identifier.
/// <para>The file is written by <see cref="PdfARasterWriter"/> instead of PDFsharp because PDFsharp 6.2 replaces the document's XMP packet
/// on every save and its own PDF/A mode is internal and aims at PDF/A-1a; a page that is one picture is trivial to write exactly.</para>
/// <b>The cost is that text stops being selectable</b> (the pages are pictures); the result says so. The result is checked again after
/// writing (<see cref="SelfCheck"/>); a file that fails the check is not delivered. The output was also validated with veraPDF
/// (PDF/A-2b profile) while this converter was developed, and the test suite re-runs veraPDF when <c>DROPFLOW_VERAPDF</c> points to it.
/// </summary>
internal static class PdfATools
{
    public const double Dpi = 200;
    public const int JpegQuality = 88;

    public const string Note =
        "Saved as PDF/A-2b. Pages are converted to images; text is not selectable.";

    public static IEnumerable<OperationSpec> Specs()
    {
        yield return OperationSpec.For("convert:pdfa", "pdf", ctx => Task.FromResult(Convert(ctx)));
    }

    private static ConversionResult Convert(JobContext ctx)
    {
        var name = Path.GetFileName(ctx.Input);
        using var source = PdfiumDocument.Open(ctx.Input); // refuses password-protected and damaged files with a clear message
        if (source.PageCount < 1) return ctx.Fail($"{name} has no pages.");
        if (!source.AllowsAssembly || !source.AllowsExtraction)
            return ctx.Fail($"{name} is protected: its owner does not allow copying or changing it. Unlock it first (Unlock PDF), then try again.");

        var info = ReadInfo(ctx.Input);
        var path = ctx.ReserveFile(ctx.Input, "pdf", "PDFA");

        using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var writer = new PdfARasterWriter(stream, source.PageCount, info.ToWriterInfo(), SrgbProfile());
            for (var i = 0; i < source.PageCount; i++)
            {
                ctx.Ct.ThrowIfCancellationRequested();
                ctx.Report((double)i / source.PageCount, $"Page {i + 1}");
                var (widthPoints, heightPoints) = source.PageSize(i);
                writer.AddPage(widthPoints, heightPoints, EncodeRgbJpeg(source.Render(i, Dpi), out var w, out var h), w, h);
            }
            writer.Finish();
        }

        var problems = SelfCheck(path, source.PageCount);
        if (problems.Count > 0)
            return ctx.Fail("The PDF/A file could not be produced correctly (" + string.Join("; ", problems) + ").");

        ctx.Report(1);
        return ctx.OkWithNote(Note, path);
    }

    /// <summary>JPEG that is always three-channel sRGB (Magick would otherwise write a one-channel gray JPEG for black-and-white pages).</summary>
    private static byte[] EncodeRgbJpeg(RenderedPage page, out int width, out int height)
    {
        var settings = new PixelReadSettings((uint)page.Width, (uint)page.Height, StorageType.Char, PixelMapping.BGRA);
        using var image = new MagickImage();
        image.ReadPixels(page.Bgra, settings);
        image.HasAlpha = false;
        image.ColorSpace = ColorSpace.sRGB;
        image.ColorType = ColorType.TrueColor;
        image.Density = new Density(page.Dpi, DensityUnit.PixelsPerInch);
        image.Format = MagickFormat.Jpeg;
        image.Quality = JpegQuality;
        width = page.Width;
        height = page.Height;
        return image.ToByteArray();
    }

    /// <summary>The sRGB ICC profile that ships inside Magick.NET (ImageMagick, Apache-2.0 licensed software).</summary>
    internal static byte[] SrgbProfile() => ColorProfiles.SRGB.ToByteArray();

    private sealed record SourceInfo(string? Title, string? Author, string? Subject, string? Keywords, DateTime? Created)
    {
        public PdfARasterWriter.Info ToWriterInfo() => new(Title, Author, Subject, Keywords, Created ?? DateTime.Now, DateTime.Now);
    }

    /// <summary>Title, author, subject, keywords and creation date of the source. Metadata is a courtesy: any problem reading it just means none.</summary>
    private static SourceInfo ReadInfo(string path)
    {
        try
        {
            using var doc = PdfSharpSupport.OpenForModify(path);
            var info = doc.Info;
            DateTime? created = info.CreationDate == default ? null : info.CreationDate;
            return new SourceInfo(info.Title, info.Author, info.Subject, info.Keywords, created);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            return new SourceInfo(null, null, null, null, null);
        }
    }

    /// <summary>
    /// Re-opens the written file with PDFsharp and checks the things this converter promises. This is not a PDF/A validator (veraPDF is
    /// that); it catches a broken build before the user is told the file is PDF/A. Returns the problems found, empty when all is well.
    /// </summary>
    internal static IReadOnlyList<string> SelfCheck(string path, int expectedPages)
    {
        var problems = new List<string>();
        var bytes = File.ReadAllBytes(path);
        try
        {
            using var doc = PdfSharp.Pdf.IO.PdfReader.Open(new MemoryStream(bytes), PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import);
            if (doc.PageCount != expectedPages) problems.Add($"{doc.PageCount} pages instead of {expectedPages}");

            var catalog = doc.Internals.Catalog;
            if (catalog.Elements.GetDictionary("/Metadata")?.Stream?.UnfilteredValue is not { } xmpBytes)
                problems.Add("no XMP metadata");
            else
            {
                var xmp = Encoding.UTF8.GetString(xmpBytes);
                if (!xmp.Contains("<pdfaid:part>2</pdfaid:part>") || !xmp.Contains("<pdfaid:conformance>B</pdfaid:conformance>"))
                    problems.Add("XMP does not declare PDF/A-2b");
            }

            var intents = catalog.Elements.GetArray("/OutputIntents");
            var intent = intents?.Elements.Count > 0
                ? intents.Elements[0] as PdfDictionary ?? (intents.Elements[0] as PdfReference)?.Value as PdfDictionary
                : null;
            if (intent is null || intent.Elements.GetDictionary("/DestOutputProfile")?.Stream is null) problems.Add("no output intent profile");

            foreach (var forbidden in new[] { "/OpenAction", "/AA", "/AcroForm", "/Names", "/JavaScript" })
                if (catalog.Elements.ContainsKey(forbidden)) problems.Add($"catalog contains {forbidden}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            problems.Add("the result could not be read back: " + ex.Message);
        }

        var text = Encoding.Latin1.GetString(bytes);
        if (text.Contains("/Encrypt")) problems.Add("the file is encrypted");
        if (!text.Contains("/ID")) problems.Add("no file identifier");
        return problems;
    }
}

/// <summary>
/// Writes a PDF/A-2b file whose pages are single JPEG pictures. Object layout (classic cross-reference table, no object streams):
/// 1 catalog, 2 page tree, 3 XMP packet, 4 ICC profile, 5 output intent, 6 Info, then three objects per page (page, content, picture).
/// </summary>
internal sealed class PdfARasterWriter
{
    internal sealed record Info(string? Title, string? Author, string? Subject, string? Keywords, DateTime Created, DateTime Modified);

    private const int FirstPageObject = 7;

    private readonly Stream _out;
    private readonly int _pageCount;
    private readonly List<long> _offsets = [];
    private readonly Info _info;
    private readonly byte[] _icc;
    private int _pagesWritten;
    private long _position;

    public PdfARasterWriter(Stream output, int pageCount, Info info, byte[] iccProfile)
    {
        _out = output;
        _pageCount = pageCount;
        _info = info;
        _icc = iccProfile;

        // Header, then a comment of bytes above 127 so transfer programs treat the file as binary (PDF/A requires it).
        Write(Encoding.Latin1.GetBytes("%PDF-1.7\n%âãÏÓ\n"));

        Begin(1);
        WriteAscii($"<< /Type /Catalog /Pages 2 0 R /Metadata 3 0 R /OutputIntents [5 0 R] >>\n");
        End();

        Begin(2);
        var kids = string.Join(" ", Enumerable.Range(0, pageCount).Select(i => $"{FirstPageObject + 3 * i} 0 R"));
        WriteAscii($"<< /Type /Pages /Count {pageCount} /Kids [{kids}] >>\n");
        End();

        var xmp = Encoding.UTF8.GetBytes(BuildXmp(info));
        Begin(3);
        WriteAscii($"<< /Type /Metadata /Subtype /XML /Length {xmp.Length} >>\nstream\n");
        Write(xmp);
        WriteAscii("\nendstream\n");
        End();

        Begin(4);
        WriteAscii($"<< /N 3 /Length {_icc.Length} >>\nstream\n");
        Write(_icc);
        WriteAscii("\nendstream\n");
        End();

        Begin(5);
        WriteAscii("<< /Type /OutputIntent /S /GTS_PDFA1 /OutputConditionIdentifier (sRGB) /OutputCondition (sRGB IEC61966-2.1) " +
                   "/RegistryName (http://www.color.org) /Info (sRGB IEC61966-2.1) /DestOutputProfile 4 0 R >>\n");
        End();

        Begin(6);
        WriteAscii("<< /Creator (DropFlow) /Producer (DropFlow)");
        foreach (var (key, value) in new[] { ("Title", info.Title), ("Author", info.Author), ("Subject", info.Subject), ("Keywords", info.Keywords) })
            if (Clean(value) is { } text) WriteAscii($" /{key} {TextString(text)}");
        WriteAscii($" /CreationDate ({PdfDate(info.Created)}) /ModDate ({PdfDate(info.Modified)}) >>\n");
        End();
    }

    /// <summary>Adds one page: <paramref name="jpeg"/> is a baseline 8-bit RGB JPEG of <paramref name="pixelWidth"/> x <paramref name="pixelHeight"/>.</summary>
    public void AddPage(double widthPoints, double heightPoints, byte[] jpeg, int pixelWidth, int pixelHeight)
    {
        if (_pagesWritten >= _pageCount) throw new InvalidOperationException("More pages than announced.");
        var page = FirstPageObject + 3 * _pagesWritten;
        var w = Num(widthPoints);
        var h = Num(heightPoints);

        Begin(page);
        WriteAscii($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {w} {h}] /Resources << /XObject << /Im0 {page + 2} 0 R >> >> /Contents {page + 1} 0 R >>\n");
        End();

        var content = $"q {w} 0 0 {h} 0 0 cm /Im0 Do Q";
        Begin(page + 1);
        WriteAscii($"<< /Length {content.Length} >>\nstream\n{content}\nendstream\n");
        End();

        Begin(page + 2);
        WriteAscii($"<< /Type /XObject /Subtype /Image /Width {pixelWidth} /Height {pixelHeight} /ColorSpace /DeviceRGB " +
                   $"/BitsPerComponent 8 /Filter /DCTDecode /Length {jpeg.Length} >>\nstream\n");
        Write(jpeg);
        WriteAscii("\nendstream\n");
        End();
        _pagesWritten++;
    }

    public void Finish()
    {
        if (_pagesWritten != _pageCount) throw new InvalidOperationException("Fewer pages than announced.");
        var xref = _position;
        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var sb = new StringBuilder();
        sb.Append($"xref\n0 {_offsets.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in _offsets) sb.Append(offset.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        sb.Append($"trailer\n<< /Size {_offsets.Count + 1} /Root 1 0 R /Info 6 0 R /ID [<{id}> <{id}>] >>\nstartxref\n{xref}\n%%EOF\n");
        WriteAscii(sb.ToString());
        _out.Flush();
    }

    private void Begin(int number)
    {
        // Objects are written in number order, so the offset list is the cross-reference table.
        if (number != _offsets.Count + 1) throw new InvalidOperationException("Objects must be written in order.");
        _offsets.Add(_position);
        WriteAscii($"{number} 0 obj\n");
    }

    private void End() => WriteAscii("endobj\n");

    private void WriteAscii(string text) => Write(Encoding.Latin1.GetBytes(text));

    private void Write(byte[] bytes)
    {
        _out.Write(bytes, 0, bytes.Length);
        _position += bytes.Length;
    }

    private static string Num(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);

    private static string PdfDate(DateTime value)
    {
        var offset = TimeZoneInfo.Local.GetUtcOffset(value);
        var sign = offset < TimeSpan.Zero ? '-' : '+';
        offset = offset.Duration();
        return $"D:{value.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture)}{sign}{offset.Hours:00}'{offset.Minutes:00}'";
    }

    /// <summary>A PDF text string: a plain literal for ASCII, otherwise UTF-16 big-endian with a byte-order mark as a hex string.</summary>
    private static string TextString(string text)
    {
        if (text.All(c => c is >= ' ' and <= '~'))
            return "(" + text.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)") + ")";
        return "<" + Convert.ToHexString([0xFE, 0xFF, .. Encoding.BigEndianUnicode.GetBytes(text)]) + ">";
    }

    /// <summary>Text without the control characters XML 1.0 and PDF text strings cannot carry; null when nothing is left.</summary>
    private static string? Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var kept = new StringBuilder();
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1])) { kept.Append(c).Append(value[++i]); continue; }
            if (c is '\t' or '\n' or '\r' || (c >= ' ' && !char.IsSurrogate(c) && c is not ('￾' or '￿'))) kept.Append(c);
        }
        var text = kept.ToString().Trim();
        return text.Length == 0 ? null : text;
    }

    internal static string BuildXmp(Info info)
    {
        var sb = new StringBuilder();
        sb.Append("<?xpacket begin=\"﻿\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>\n");
        sb.Append("<x:xmpmeta xmlns:x=\"adobe:ns:meta/\">\n");
        sb.Append("<rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">\n");

        sb.Append("<rdf:Description rdf:about=\"\" xmlns:pdfaid=\"http://www.aiim.org/pdfa/ns/id/\">\n");
        sb.Append("<pdfaid:part>2</pdfaid:part>\n<pdfaid:conformance>B</pdfaid:conformance>\n</rdf:Description>\n");

        sb.Append("<rdf:Description rdf:about=\"\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\">\n");
        sb.Append("<dc:format>application/pdf</dc:format>\n");
        if (Escaped(info.Title) is { } t) sb.Append("<dc:title><rdf:Alt><rdf:li xml:lang=\"x-default\">").Append(t).Append("</rdf:li></rdf:Alt></dc:title>\n");
        if (Escaped(info.Author) is { } a) sb.Append("<dc:creator><rdf:Seq><rdf:li>").Append(a).Append("</rdf:li></rdf:Seq></dc:creator>\n");
        if (Escaped(info.Subject) is { } s) sb.Append("<dc:description><rdf:Alt><rdf:li xml:lang=\"x-default\">").Append(s).Append("</rdf:li></rdf:Alt></dc:description>\n");
        sb.Append("</rdf:Description>\n");

        sb.Append("<rdf:Description rdf:about=\"\" xmlns:pdf=\"http://ns.adobe.com/pdf/1.3/\">\n");
        sb.Append("<pdf:Producer>DropFlow</pdf:Producer>\n");
        if (Escaped(info.Keywords) is { } k) sb.Append("<pdf:Keywords>").Append(k).Append("</pdf:Keywords>\n");
        sb.Append("</rdf:Description>\n");

        sb.Append("<rdf:Description rdf:about=\"\" xmlns:xmp=\"http://ns.adobe.com/xap/1.0/\">\n");
        sb.Append("<xmp:CreatorTool>DropFlow</xmp:CreatorTool>\n");
        sb.Append("<xmp:CreateDate>").Append(XmpDate(info.Created)).Append("</xmp:CreateDate>\n");
        sb.Append("<xmp:ModifyDate>").Append(XmpDate(info.Modified)).Append("</xmp:ModifyDate>\n");
        sb.Append("<xmp:MetadataDate>").Append(XmpDate(info.Modified)).Append("</xmp:MetadataDate>\n");
        sb.Append("</rdf:Description>\n");

        sb.Append("</rdf:RDF>\n</x:xmpmeta>\n");
        sb.Append(' ', 2000).Append('\n'); // padding so the packet can be edited in place
        sb.Append("<?xpacket end=\"w\"?>");
        return sb.ToString();

        static string? Escaped(string? value) => Clean(value) is { } text ? SecurityElement.Escape(text) : null;
    }

    private static string XmpDate(DateTime value)
    {
        var offset = TimeZoneInfo.Local.GetUtcOffset(value);
        var sign = offset < TimeSpan.Zero ? '-' : '+';
        offset = offset.Duration();
        return value.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture) + $"{sign}{offset.Hours:00}:{offset.Minutes:00}";
    }
}
