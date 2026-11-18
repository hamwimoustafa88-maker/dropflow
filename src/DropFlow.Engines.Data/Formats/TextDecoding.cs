// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;

namespace DropFlow.Engines.Data.Formats;

/// <summary>
/// Text files whose encoding is not declared: byte order mark first, then strict UTF-8, then the legacy Windows code page that fits
/// the bytes (1256 when they decode to Arabic letters, otherwise 1252). Old Arabic exports from Excel and phones are Windows-1256.
/// </summary>
internal static class TextDecoding
{
    static TextDecoding() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <summary>UTF-8 with a byte order mark: Excel needs it to show Arabic in a CSV file.</summary>
    public static readonly Encoding Utf8WithBom = new UTF8Encoding(true);

    public static readonly Encoding Utf8 = new UTF8Encoding(false);

    public static string Decode(byte[] bytes)
    {
        if (bytes is [0xEF, 0xBB, 0xBF, ..]) return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (bytes is [0xFF, 0xFE, ..]) return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes is [0xFE, 0xFF, ..]) return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);

        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            // not UTF-8: one of the legacy code pages
        }

        var arabic = Encoding.GetEncoding(1256).GetString(bytes);
        var arabicLetters = arabic.Count(c => c is >= (char)0x0621 and <= (char)0x064A);
        var highBytes = bytes.Count(b => b >= 0x80);
        return highBytes > 0 && arabicLetters * 2 >= highBytes ? arabic : Encoding.GetEncoding(1252).GetString(bytes);
    }

    /// <summary>An encoding by name ("UTF-8", "windows-1256"), or UTF-8 when the name is unknown.</summary>
    public static Encoding ByName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return Utf8;
        try
        {
            return Encoding.GetEncoding(name.Trim().Trim('"'));
        }
        catch (ArgumentException)
        {
            return Utf8;
        }
    }

    public static bool HasArabic(string text) => text.Any(c => c is >= (char)0x0600 and <= (char)0x06FF or >= (char)0x0750 and <= (char)0x077F or >= (char)0xFB50 and <= (char)0xFEFC);
}
