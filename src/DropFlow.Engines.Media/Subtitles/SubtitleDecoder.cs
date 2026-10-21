using System.Text;
using TextEncoding = System.Text.Encoding;

namespace DropFlow.Engines.Media.Subtitles;

/// <summary>
/// Bytes to text for subtitle files in the wild: UTF-8 with or without BOM, UTF-16/32 with BOM (and BOM-less UTF-16 by
/// byte pattern), and legacy Arabic/Western code pages when the bytes are not valid UTF-8.
/// </summary>
public static class SubtitleDecoder
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, throwOnInvalidBytes: true);

    static SubtitleDecoder() => TextEncoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <summary>Decodes <paramref name="bytes"/>; <paramref name="encodingName"/> reports what was used (for logs).</summary>
    public static string Decode(byte[] bytes, out string encodingName)
    {
        if (bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xFE && bytes[2] == 0 && bytes[3] == 0)
        {
            encodingName = "utf-32le";
            return new UTF32Encoding(false, true).GetString(bytes, 4, bytes.Length - 4);
        }
        if (bytes.Length >= 4 && bytes[0] == 0 && bytes[1] == 0 && bytes[2] == 0xFE && bytes[3] == 0xFF)
        {
            encodingName = "utf-32be";
            return new UTF32Encoding(true, true).GetString(bytes, 4, bytes.Length - 4);
        }
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            encodingName = "utf-8-bom";
            return StrictOrLossy(bytes, 3, out encodingName, "utf-8-bom");
        }
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            encodingName = "utf-16le";
            return TextEncoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        }
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            encodingName = "utf-16be";
            return TextEncoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        }
        if (LooksLikeUtf16(bytes, out var littleEndian))
        {
            encodingName = littleEndian ? "utf-16le" : "utf-16be";
            return (littleEndian ? TextEncoding.Unicode : TextEncoding.BigEndianUnicode).GetString(bytes);
        }

        try
        {
            encodingName = "utf-8";
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return DecodeLegacy(bytes, out encodingName);
        }
    }

    private static string StrictOrLossy(byte[] bytes, int offset, out string name, string label)
    {
        try
        {
            name = label;
            return StrictUtf8.GetString(bytes, offset, bytes.Length - offset);
        }
        catch (DecoderFallbackException)
        {
            // A BOM promised UTF-8 but the body has stray bytes: keep the file readable instead of failing.
            name = label + "-lossy";
            return new UTF8Encoding(false, false).GetString(bytes, offset, bytes.Length - offset);
        }
    }

    /// <summary>
    /// Picks windows-1256 (Arabic) when most high bytes decode to Arabic letters, otherwise windows-1252. Arabic and English
    /// are DropFlow's audience; other legacy code pages are not guessed.
    /// </summary>
    private static string DecodeLegacy(byte[] bytes, out string name)
    {
        var arabic = TextEncoding.GetEncoding(1256).GetString(bytes);
        var high = arabic.Count(c => c > 0x7F);
        var arabicLetters = arabic.Count(c => c is >= '؀' and <= 'ۿ');
        if (high > 0 && arabicLetters >= high * 0.6)
        {
            name = "windows-1256";
            return arabic;
        }
        name = "windows-1252";
        return TextEncoding.GetEncoding(1252).GetString(bytes);
    }

    /// <summary>BOM-less UTF-16 shows up as a zero byte in every other position of mostly-ASCII text.</summary>
    private static bool LooksLikeUtf16(byte[] bytes, out bool littleEndian)
    {
        littleEndian = true;
        var n = Math.Min(bytes.Length, 400) / 2 * 2;
        if (n < 8) return false;
        int zerosEven = 0, zerosOdd = 0;
        for (var i = 0; i < n; i += 2)
        {
            if (bytes[i] == 0) zerosEven++;
            if (bytes[i + 1] == 0) zerosOdd++;
        }
        var pairs = n / 2;
        if (zerosOdd > pairs * 0.3 && zerosEven == 0) { littleEndian = true; return true; }
        if (zerosEven > pairs * 0.3 && zerosOdd == 0) { littleEndian = false; return true; }
        return false;
    }

    /// <summary>"\r\n" when the text uses Windows line endings, otherwise "\n".</summary>
    public static string DetectNewLine(string text) => text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

    /// <summary>True when the bytes look like binary data (a NUL in the first 4 KB of what is not UTF-16/32).</summary>
    public static bool LooksBinary(byte[] bytes)
    {
        if (bytes.Length >= 2 && (bytes[0] == 0xFF && bytes[1] == 0xFE || bytes[0] == 0xFE && bytes[1] == 0xFF)) return false;
        if (LooksLikeUtf16(bytes, out _)) return false;
        var n = Math.Min(bytes.Length, 4096);
        for (var i = 0; i < n; i++)
            if (bytes[i] == 0) return true;
        return false;
    }
}
