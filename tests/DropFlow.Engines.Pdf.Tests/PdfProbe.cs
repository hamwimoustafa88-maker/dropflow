using System.IO.Compression;
using System.Text;
using DropFlow.Engines.Pdf.Infrastructure;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using Pig = UglyToad.PdfPig.PdfDocument;

namespace DropFlow.Engines.Pdf.Tests;

/// <summary>Reads facts about a produced PDF with readers other than the one that wrote it (PdfPig and PDFium).</summary>
internal static class PdfProbe
{
    /// <summary>Visible page size in points as PDFium sees it (CropBox and /Rotate applied).</summary>
    public static (double Width, double Height) PdfiumSize(string pdf, int page = 1)
    {
        using var doc = PdfiumDocument.Open(pdf);
        var size = doc.PageSize(page - 1);
        return (Math.Round(size.Width, 2), Math.Round(size.Height, 2));
    }

    /// <summary>The CropBox as PdfPig reads it, in PDF user space (y up). PdfPig reports the MediaBox when there is none.</summary>
    public static (double Left, double Bottom, double Right, double Top) CropBox(string pdf, int page = 1)
    {
        using var doc = Pig.Open(pdf);
        var b = doc.GetPage(page).CropBox.Bounds;
        return (Math.Round(b.Left, 2), Math.Round(b.Bottom, 2), Math.Round(b.Right, 2), Math.Round(b.Top, 2));
    }

    public static (double Left, double Bottom, double Right, double Top) MediaBox(string pdf, int page = 1)
    {
        using var doc = Pig.Open(pdf);
        var b = doc.GetPage(page).MediaBox.Bounds;
        return (Math.Round(b.Left, 2), Math.Round(b.Bottom, 2), Math.Round(b.Right, 2), Math.Round(b.Top, 2));
    }

    /// <summary>Page size in points as PdfPig reports it (it applies the CropBox and /Rotate as well).</summary>
    public static (double Width, double Height) PdfPigSize(string pdf, int page = 1)
    {
        using var doc = Pig.Open(pdf);
        var p = doc.GetPage(page);
        return (Math.Round(p.Width, 2), Math.Round(p.Height, 2));
    }

    /// <summary>Copies a PDF and adds an XMP metadata packet (document-level /Metadata stream) carrying <paramref name="xml"/>.</summary>
    public static string WithXmp(string source, string target, string xml)
    {
        using var doc = PdfSharpSupport.OpenForModify(source);
        var stream = new PdfDictionary(doc);
        stream.Elements.SetName("/Type", "/Metadata");
        stream.Elements.SetName("/Subtype", "/XML");
        stream.CreateStream(System.Text.Encoding.UTF8.GetBytes(xml));
        doc.Internals.AddObject(stream);
        doc.Internals.Catalog.Elements["/Metadata"] = stream.Reference;
        doc.Save(target);
        return target;
    }

    /// <summary>Copies a PDF and rewrites the page boxes / rotation of every page (PDFsharp), to build files with unusual geometry.</summary>
    public static string WithBoxes(string source, string target, double[]? media = null, double[]? crop = null, int? rotate = null)
    {
        PdfSharpSupport.EnsureFonts();
        using var doc = PdfSharpSupport.OpenForModify(source);
        foreach (var page in doc.Pages.Cast<PdfPage>())
        {
            if (media is not null) page.MediaBox = new PdfRectangle(new XPoint(media[0], media[1]), new XPoint(media[2], media[3]));
            if (crop is not null) page.CropBox = new PdfRectangle(new XPoint(crop[0], crop[1]), new XPoint(crop[2], crop[3]));
            if (rotate is not null) page.Rotate = rotate.Value;
        }
        doc.Save(target);
        return target;
    }
}

/// <summary>Searches a PDF file for a word the way a forensic tool would: plain bytes, hex strings, UTF-16 and every FlateDecode stream.</summary>
internal static class RawPdf
{
    /// <summary>True when the word (ASCII or Unicode) can be found anywhere in the file, including inside decompressed streams.</summary>
    public static bool Contains(byte[] file, string word)
    {
        var needles = Needles(word);
        foreach (var haystack in Haystacks(file))
            if (needles.Any(n => IndexOf(haystack, n) >= 0))
                return true;
        return false;
    }

    private static List<byte[]> Needles(string word)
    {
        var needles = new List<byte[]>
        {
            Encoding.UTF8.GetBytes(word),
            Encoding.Latin1.GetBytes(word),
            Encoding.BigEndianUnicode.GetBytes(word),
            Encoding.Unicode.GetBytes(word),
        };
        // PDF hex strings: <48656C6C6F> and the two-byte glyph form <00480065...>
        foreach (var bytes in needles.ToList())
        {
            needles.Add(Encoding.ASCII.GetBytes(Convert.ToHexString(bytes)));
            needles.Add(Encoding.ASCII.GetBytes(Convert.ToHexString(bytes).ToLowerInvariant()));
        }
        return needles;
    }

    private static IEnumerable<byte[]> Haystacks(byte[] file)
    {
        yield return file;
        var text = Encoding.Latin1.GetString(file);
        var from = 0;
        while (true)
        {
            var start = text.IndexOf("stream", from, StringComparison.Ordinal);
            if (start < 0) yield break;
            var begin = start + "stream".Length;
            if (begin < text.Length && text[begin] == '\r') begin++;
            if (begin < text.Length && text[begin] == '\n') begin++;
            var end = text.IndexOf("endstream", begin, StringComparison.Ordinal);
            if (end < 0) yield break;
            from = end + "endstream".Length;

            var inflated = TryInflate(file.AsSpan(begin, end - begin).ToArray());
            if (inflated is not null)
            {
                yield return inflated;
                // Content streams hold text as hex or escaped strings; make plain-text variants searchable too.
                yield return Encoding.Latin1.GetBytes(Unhex(Encoding.Latin1.GetString(inflated)));
            }
        }
    }

    private static byte[]? TryInflate(byte[] data)
    {
        try
        {
            using var input = new MemoryStream(data);
            using var zlib = new ZLibStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            zlib.CopyTo(output);
            return output.ToArray();
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            return null;
        }
    }

    /// <summary>Replaces every &lt;4865...&gt; hex string with the characters it encodes (single- and double-byte).</summary>
    private static string Unhex(string text)
    {
        var sb = new StringBuilder();
        var i = 0;
        while (i < text.Length)
        {
            if (text[i] == '<' && i + 1 < text.Length && text[i + 1] != '<')
            {
                var close = text.IndexOf('>', i);
                if (close > i + 1)
                {
                    var hex = text[(i + 1)..close].Replace(" ", "");
                    if (hex.Length % 2 == 0 && hex.All(Uri.IsHexDigit))
                    {
                        var bytes = Convert.FromHexString(hex);
                        sb.Append(Encoding.Latin1.GetString(bytes));
                        sb.Append(Encoding.BigEndianUnicode.GetString(bytes));
                        i = close + 1;
                        continue;
                    }
                }
            }
            sb.Append(text[i]);
            i++;
        }
        return sb.ToString();
    }

    private static int IndexOf(byte[] haystack, byte[] needle) => haystack.AsSpan().IndexOf(needle);
}

