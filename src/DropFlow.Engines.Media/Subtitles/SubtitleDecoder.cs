using System.Text;
using TextEncoding = System.Text.Encoding;

namespace DropFlow.Engines.Media.Subtitles;

/// <summary>
/// Bytes to text for subtitle files in the wild: UTF-8 with or without BOM, UTF-16/32 with BOM (and BOM-less UTF-16 by
/// byte pattern), and legacy code pages when the bytes are not valid UTF-8: windows-1256 when the text reads as Arabic,
/// windows-1252 when it reads as Western European, otherwise the system's ANSI code page (the user's culture).
/// </summary>
public static class SubtitleDecoder
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, throwOnInvalidBytes: true);

    static SubtitleDecoder() => TextEncoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <summary>Decodes <paramref name="bytes"/>; <paramref name="encodingName"/> reports what was used (for logs).</summary>
    public static string Decode(byte[] bytes, out string encodingName) => Decode(bytes, out encodingName, SystemCodePage());

    /// <summary>
    /// As <see cref="Decode(byte[], out string)"/>, with the code page used when neither the Arabic nor the Western guess fits
    /// (tests pass one; the app uses the ANSI code page of the current culture).
    /// </summary>
    public static string Decode(byte[] bytes, out string encodingName, int systemCodePage)
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
            return DecodeLegacy(bytes, out encodingName, systemCodePage);
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
    /// Picks windows-1256 (Arabic) when most high bytes decode to Arabic letters, windows-1252 when they decode to Latin letters
    /// that stand alone between ASCII ("très", "garçon"), and otherwise the system code page: a run of high bytes is how double-byte
    /// legacy encodings (Korean, Chinese, Japanese) look, and only the user's own code page can read those.
    /// </summary>
    private static string DecodeLegacy(byte[] bytes, out string name, int systemCodePage)
    {
        var arabic = TextEncoding.GetEncoding(1256).GetString(bytes);
        var high = arabic.Count(c => c > 0x7F);
        var arabicLetters = arabic.Count(c => c is >= '؀' and <= 'ۿ');
        if (high > 0 && arabicLetters >= high * 0.6)
        {
            name = "windows-1256";
            return arabic;
        }

        var western = TextEncoding.GetEncoding(1252).GetString(bytes);
        if (LooksWestern(bytes, western) || systemCodePage is 0 or 1252 or 1256 or 65001 or 20127)
        {
            name = "windows-1252";
            return western;
        }

        try
        {
            var encoding = TextEncoding.GetEncoding(systemCodePage, EncoderFallback.ReplacementFallback, DecoderFallback.ReplacementFallback);
            name = "cp" + systemCodePage.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return encoding.GetString(bytes);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            name = "windows-1252";
            return western;
        }
    }

    /// <summary>Most high bytes are Latin letters or typographic punctuation, and few of them sit next to another high byte.</summary>
    private static bool LooksWestern(byte[] bytes, string decoded1252)
    {
        int high = 0, plausible = 0, paired = 0;
        for (var i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] < 0x80) continue;
            high++;
            var c = decoded1252[i];
            if (char.IsLetter(c) || c is '’' or '‘' or '“' or '”' or '–' or '—' or '…' or '«' or '»' or '°' or '€' or '£' or '·' or '¿' or '¡' or ' ') plausible++;
            if (i + 1 < bytes.Length && bytes[i + 1] >= 0x80) paired++;
        }
        return high > 0 && plausible >= high * 0.8 && paired <= high * 0.3;
    }

    /// <summary>The ANSI code page of the current culture (1252 for English, 1256 for Arabic, 949 for Korean...); 0 when unknown.</summary>
    private static int SystemCodePage()
    {
        try
        {
            return System.Globalization.CultureInfo.CurrentCulture.TextInfo.ANSICodePage;
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            return 0;
        }
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
