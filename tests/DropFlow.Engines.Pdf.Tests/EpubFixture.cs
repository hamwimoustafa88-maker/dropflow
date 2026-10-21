using System.IO.Compression;
using System.Text;
using DropFlow.TestSupport;

namespace DropFlow.Engines.Pdf.Tests;

/// <summary>Writes a small EPUB by hand (not with the production EPUB writer) in the shape real books have: nested folders, a nav document, images, scripts to strip.</summary>
internal static class EpubFixture
{
    public static string Write(string path, bool drm = false, string title = "Sample Book", string language = "en")
    {
        using var scratch = new TempDir();
        var png = Samples.Png(scratch.Combine("cover.png"), 8, 8);

        using var stream = File.Create(path);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
        Add(zip, "mimetype", "application/epub+zip", CompressionLevel.NoCompression);
        Add(zip, "META-INF/container.xml",
            "<?xml version=\"1.0\"?><container version=\"1.0\" xmlns=\"urn:oasis:names:tc:opendocument:xmlns:container\"><rootfiles>" +
            "<rootfile full-path=\"OPS/package.opf\" media-type=\"application/oebps-package+xml\"/></rootfiles></container>");
        if (drm)
        {
            Add(zip, "META-INF/encryption.xml",
                "<?xml version=\"1.0\"?><encryption xmlns=\"urn:oasis:names:tc:opendocument:xmlns:container\" xmlns:enc=\"http://www.w3.org/2001/04/xmlenc#\">" +
                "<enc:EncryptedData><enc:EncryptionMethod Algorithm=\"http://www.w3.org/2001/04/xmlenc#aes128-cbc\"/><enc:CipherData><enc:CipherReference URI=\"OPS/text/ch1.xhtml\"/></enc:CipherData></enc:EncryptedData></encryption>");
        }

        Add(zip, "OPS/package.opf",
            "<?xml version=\"1.0\" encoding=\"utf-8\"?><package xmlns=\"http://www.idpf.org/2007/opf\" version=\"3.0\" unique-identifier=\"id\">" +
            $"<metadata xmlns:dc=\"http://purl.org/dc/elements/1.1/\"><dc:identifier id=\"id\">urn:uuid:1234</dc:identifier><dc:title>{title}</dc:title><dc:creator>An Author</dc:creator><dc:language>{language}</dc:language></metadata>" +
            "<manifest>" +
            "<item id=\"nav\" href=\"nav.xhtml\" media-type=\"application/xhtml+xml\" properties=\"nav\"/>" +
            "<item id=\"c1\" href=\"text/ch1.xhtml\" media-type=\"application/xhtml+xml\"/>" +
            "<item id=\"c2\" href=\"text/ch2.xhtml\" media-type=\"application/xhtml+xml\"/>" +
            "<item id=\"notes\" href=\"text/notes.xhtml\" media-type=\"application/xhtml+xml\"/>" +
            "<item id=\"img\" href=\"images/cover%20pic.png\" media-type=\"image/png\"/>" +
            "<item id=\"css\" href=\"style.css\" media-type=\"text/css\"/>" +
            "</manifest><spine><itemref idref=\"c1\"/><itemref idref=\"c2\"/><itemref idref=\"notes\" linear=\"no\"/></spine></package>");
        Add(zip, "OPS/nav.xhtml",
            "<?xml version=\"1.0\"?><html xmlns=\"http://www.w3.org/1999/xhtml\"><body><nav><ol><li><a href=\"text/ch1.xhtml\">NAVTEXT should not be in the book</a></li></ol></nav></body></html>");
        Add(zip, "OPS/text/ch1.xhtml",
            "<?xml version=\"1.0\" encoding=\"utf-8\"?><html xmlns=\"http://www.w3.org/1999/xhtml\"><head><title>ch1</title><style>p{color:red}</style><script>alert('x')</script></head>" +
            "<body><h1>Chapter One</h1><p onclick=\"evil()\" style=\"color:blue\">First <b>bold</b> and <i>italic</i> text with a <a href=\"ch2.xhtml#sec\">link to chapter two</a> and <a href=\"javascript:alert(1)\">bad link</a>.</p>" +
            "<p><img src=\"../images/cover%20pic.png\" alt=\"A cover picture\"/></p><p><img src=\"../images/missing.png\" alt=\"Missing picture\"/></p>" +
            "<ul><li>one</li><li>two</li></ul><iframe src=\"http://evil\"></iframe></body></html>");
        Add(zip, "OPS/text/ch2.xhtml",
            "<?xml version=\"1.0\" encoding=\"utf-8\"?><html xmlns=\"http://www.w3.org/1999/xhtml\"><head><title>ch2</title></head>" +
            "<body><h1 id=\"top\">Chapter Two</h1><h2 id=\"sec\">Section</h2><p>مرحبا بالعالم - Second chapter text.</p>" +
            "<table><tr><th>A</th><th>B</th></tr><tr><td>1</td><td>2</td></tr></table></body></html>");
        Add(zip, "OPS/text/notes.xhtml",
            "<?xml version=\"1.0\"?><html xmlns=\"http://www.w3.org/1999/xhtml\"><body><p>NOTES should be skipped (linear no)</p></body></html>");
        Add(zip, "OPS/style.css", "p { color: red }");
        zip.CreateEntryFromFile(png, "OPS/images/cover pic.png");
        return path;
    }

    private static void Add(ZipArchive zip, string name, string content, CompressionLevel level = CompressionLevel.Optimal)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name, level).Open(), new UTF8Encoding(false));
        writer.Write(content);
    }
}
