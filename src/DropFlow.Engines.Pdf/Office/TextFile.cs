using System.Text;

namespace DropFlow.Engines.Pdf.Office;

/// <summary>
/// Reads a text file whose encoding is not declared. Order: byte order mark, valid UTF-8, then the legacy Windows code page that
/// fits the bytes (1256 for Arabic, otherwise 1252). Arabic spreadsheets exported from older Excel versions are still Windows-1256.
/// </summary>
internal static class TextFile
{
    static TextFile()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public static string Read(string path) => Decode(File.ReadAllBytes(path));

    public static string Decode(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);

        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            // not UTF-8: fall through to the legacy code pages
        }

        var arabic = Encoding.GetEncoding(1256).GetString(bytes);
        var arabicLetters = arabic.Count(c => c is >= 'ء' and <= 'ي');
        var highBytes = bytes.Count(b => b >= 0x80);
        // If most of the high bytes decode to Arabic letters, the file is Windows-1256; otherwise assume Western Europe.
        return highBytes > 0 && arabicLetters * 2 >= highBytes ? arabic : Encoding.GetEncoding(1252).GetString(bytes);
    }

    /// <summary>UTF-8 with a byte order mark: what Windows editors and Excel need to recognise Arabic text.</summary>
    public static readonly Encoding Utf8WithBom = new UTF8Encoding(true);

    public static readonly Encoding Utf8 = new UTF8Encoding(false);
}
