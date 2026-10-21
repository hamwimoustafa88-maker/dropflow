using System.Globalization;
using System.Text;

namespace DropFlow.Core.Sources;

/// <summary>
/// Decides what a downloaded file is called. Everything here is untrusted input from a server, so the result is always a plain
/// file name (no folders, no reserved Windows names, no characters Windows rejects, bounded length).
/// </summary>
public static class DownloadNaming
{
    public const string Fallback = "download";
    private const int MaxStem = 120;
    private const int MaxExtension = 32;

    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private static readonly Dictionary<string, string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["application/pdf"] = ".pdf",
        ["image/png"] = ".png",
        ["image/jpeg"] = ".jpg",
        ["image/jpg"] = ".jpg",
        ["image/gif"] = ".gif",
        ["image/webp"] = ".webp",
        ["image/avif"] = ".avif",
        ["image/heic"] = ".heic",
        ["image/bmp"] = ".bmp",
        ["image/tiff"] = ".tiff",
        ["image/svg+xml"] = ".svg",
        ["image/x-icon"] = ".ico",
        ["image/vnd.microsoft.icon"] = ".ico",
        ["video/mp4"] = ".mp4",
        ["video/webm"] = ".webm",
        ["video/quicktime"] = ".mov",
        ["video/x-matroska"] = ".mkv",
        ["audio/mpeg"] = ".mp3",
        ["audio/mp4"] = ".m4a",
        ["audio/wav"] = ".wav",
        ["audio/x-wav"] = ".wav",
        ["audio/ogg"] = ".ogg",
        ["audio/flac"] = ".flac",
        ["audio/webm"] = ".weba",
        ["application/zip"] = ".zip",
        ["application/x-7z-compressed"] = ".7z",
        ["application/vnd.rar"] = ".rar",
        ["application/gzip"] = ".gz",
        ["application/x-tar"] = ".tar",
        ["application/json"] = ".json",
        ["application/xml"] = ".xml",
        ["text/xml"] = ".xml",
        ["text/plain"] = ".txt",
        ["text/html"] = ".html",
        ["text/csv"] = ".csv",
        ["text/markdown"] = ".md",
        ["application/epub+zip"] = ".epub",
        ["application/msword"] = ".doc",
        ["application/vnd.openxmlformats-officedocument.wordprocessingml.document"] = ".docx",
        ["application/vnd.ms-excel"] = ".xls",
        ["application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"] = ".xlsx",
        ["application/vnd.ms-powerpoint"] = ".ppt",
        ["application/vnd.openxmlformats-officedocument.presentationml.presentation"] = ".pptx",
    };

    /// <summary>The extension (with dot) for a Content-Type such as <c>image/png; charset=x</c>, or null when it is not one we know.</summary>
    public static string? ExtensionFor(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType)) return null;
        var mediaType = contentType.Split(';')[0].Trim();
        return Extensions.TryGetValue(mediaType, out var ext) ? ext : null;
    }

    /// <summary>
    /// The best file name the server and the address offer: Content-Disposition, then the last path segment of the final address,
    /// then of the address the user gave, then <c>download</c>. A name with no extension gets one from the Content-Type when known.
    /// </summary>
    public static string Choose(string? contentDisposition, Uri finalUri, Uri requestedUri, string? contentType)
    {
        var name = Sanitize(FromContentDisposition(contentDisposition))
                   ?? Sanitize(FromUrl(finalUri))
                   ?? Sanitize(FromUrl(requestedUri));
        var extension = ExtensionFor(contentType);
        if (name is null) return Fallback + (extension ?? "");
        return Path.HasExtension(name) || extension is null ? name : name + extension;
    }

    /// <summary>The file name from a <c>Content-Disposition</c> value: <c>filename*</c> (RFC 5987) wins over <c>filename</c>.</summary>
    public static string? FromContentDisposition(string? header)
    {
        if (string.IsNullOrWhiteSpace(header)) return null;

        string? plain = null, extended = null;
        foreach (var part in SplitParameters(header).Skip(1))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0) continue;
            var key = part[..eq].Trim().ToLowerInvariant();
            var value = part[(eq + 1)..].Trim();
            if (key == "filename*") extended ??= DecodeExtended(value);
            else if (key == "filename") plain ??= Unquote(value);
        }
        return !string.IsNullOrWhiteSpace(extended) ? extended : plain;
    }

    /// <summary>The URL-decoded last non-empty path segment (the query string is never used), or null.</summary>
    public static string? FromUrl(Uri uri)
    {
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0) return null;
        try { return Uri.UnescapeDataString(segments[^1]); }
        catch (UriFormatException) { return segments[^1]; }
    }

    /// <summary>
    /// Makes <paramref name="name"/> safe to create in a folder: drops any folder part, replaces characters Windows rejects, removes
    /// bidirectional controls (they can disguise an extension), avoids reserved device names and caps the length. Null when nothing is left.
    /// </summary>
    public static string? Sanitize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;

        var slash = name.LastIndexOfAny(['/', '\\']);
        if (slash >= 0) name = name[(slash + 1)..];

        var clean = new StringBuilder(name.Length);
        foreach (var ch in name)
        {
            if (IsBidiControl(ch)) continue;
            clean.Append(ch < ' ' || "\"<>|:*?\\/".Contains(ch) ? '_' : ch);
        }

        var text = clean.ToString().Trim().TrimEnd('.', ' ');
        if (text.Length == 0 || text.All(c => c is '_' or '.' or ' ')) return null;

        var extension = Path.GetExtension(text);
        var stem = text[..^extension.Length];
        if (stem.Length == 0) { stem = extension; extension = ""; } // ".bashrc": a name, not an extension
        if (extension.Length > MaxExtension) extension = extension[..MaxExtension];
        stem = Cut(stem, MaxStem).TrimEnd('.', ' ');
        if (stem.Length == 0) stem = Fallback;

        var firstPart = stem.Split('.')[0].Trim();
        if (Reserved.Contains(firstPart)) stem = "_" + stem;

        return (stem + extension).TrimEnd('.', ' ');
    }

    /// <summary>Appends <c> (n)</c> before the extension: <c>report.pdf</c> becomes <c>report (2).pdf</c>.</summary>
    public static string Numbered(string fileName, int n)
    {
        if (n <= 1) return fileName;
        var extension = Path.GetExtension(fileName);
        var stem = fileName[..^extension.Length];
        return $"{stem} ({n.ToString(CultureInfo.InvariantCulture)}){extension}";
    }

    private static bool IsBidiControl(char c) =>
        c is >= '‪' and <= '‮' or >= '⁦' and <= '⁩' or '‎' or '‏' or '؜';

    private static string Cut(string text, int max)
    {
        if (text.Length <= max) return text;
        var cut = text[..max];
        return char.IsHighSurrogate(cut[^1]) ? cut[..^1] : cut;
    }

    /// <summary>Splits on ';' outside double quotes.</summary>
    private static IEnumerable<string> SplitParameters(string header)
    {
        var current = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < header.Length; i++)
        {
            var c = header[i];
            if (c == '\\' && quoted && i + 1 < header.Length && header[i + 1] is '"' or '\\') { current.Append(c).Append(header[++i]); continue; }
            if (c == '"') quoted = !quoted;
            if (c == ';' && !quoted) { yield return current.ToString(); current.Clear(); continue; }
            current.Append(c);
        }
        yield return current.ToString();
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            var inner = value[1..^1];
            var sb = new StringBuilder(inner.Length);
            for (var i = 0; i < inner.Length; i++)
            {
                // only \" and \\ are escapes; servers often send Windows paths with single backslashes
                if (inner[i] == '\\' && i + 1 < inner.Length && inner[i + 1] is '"' or '\\') i++;
                sb.Append(inner[i]);
            }
            return sb.ToString();
        }
        return value;
    }

    /// <summary><c>UTF-8''%D8%AA%D9%82%D8%B1%D9%8A%D8%B1.pdf</c> → <c>تقرير.pdf</c>. Only UTF-8 and ISO-8859-1 are accepted (RFC 6266).</summary>
    private static string? DecodeExtended(string value)
    {
        var first = value.IndexOf('\'');
        var second = first < 0 ? -1 : value.IndexOf('\'', first + 1);
        if (second < 0) return null;

        var charset = value[..first].Trim();
        Encoding encoding;
        if (charset.Equals("utf-8", StringComparison.OrdinalIgnoreCase)) encoding = new UTF8Encoding(false, throwOnInvalidBytes: false);
        else if (charset.Equals("iso-8859-1", StringComparison.OrdinalIgnoreCase)) encoding = Encoding.Latin1;
        else return null;

        var encoded = value[(second + 1)..];
        var bytes = new List<byte>(encoded.Length);
        for (var i = 0; i < encoded.Length; i++)
        {
            if (encoded[i] == '%' && i + 2 < encoded.Length &&Uri.IsHexDigit(encoded[i + 1]) && Uri.IsHexDigit(encoded[i + 2]))
            {
                bytes.Add(Convert.ToByte(encoded.Substring(i + 1, 2), 16));
                i += 2;
            }
            else
            {
                bytes.AddRange(Encoding.UTF8.GetBytes(encoded[i].ToString()));
            }
        }
        return encoding.GetString(bytes.ToArray());
    }
}
