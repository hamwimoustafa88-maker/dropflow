using System.Text;
using DropFlow.Engines.Archive.Images;

namespace DropFlow.Engines.Archive.Sharing;

/// <summary>Builds <c>Content-Disposition</c> values that keep Arabic and other Unicode file names intact (RFC 6266 with RFC 5987 encoding).</summary>
public static class ContentDispositionHeader
{
    /// <summary>
    /// <c>attachment; filename="ascii-fallback"; filename*=UTF-8''percent-encoded</c>. Modern browsers use the second form; the first
    /// is a safe ASCII fallback for old clients. Always "attachment", so a shared .html is saved, never rendered.
    /// </summary>
    public static string Attachment(string fileName) =>
        $"attachment; filename=\"{AsciiFallback(fileName)}\"; filename*=UTF-8''{Rfc5987Encode(fileName)}";

    internal static string AsciiFallback(string fileName)
    {
        var extension = Path.GetExtension(fileName);
        var stem = fileName[..^extension.Length];

        static string Ascii(string text)
        {
            var sb = new StringBuilder(text.Length);
            foreach (var c in text)
                sb.Append(c is >= ' ' and < '\u007F' && c is not ('"' or '\\' or '%' or ';' or '/' or ':') ? c : '_');
            return sb.ToString();
        }

        var fallbackStem = Ascii(stem);
        var fallbackExtension = Ascii(extension);
        if (fallbackStem.All(c => !char.IsAsciiLetterOrDigit(c)))
        {
            // an Arabic name collapses to "____": "download.pdf" reads better than "___ ____.pdf"
            fallbackStem = "download";
            if (fallbackExtension.All(c => c == '_' || c == '.')) fallbackExtension = "";
        }

        var fallback = fallbackStem + fallbackExtension;
        return fallback.Length > 150 ? fallback[..150] : fallback;
    }

    internal static string Rfc5987Encode(string value)
    {
        var sb = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            var c = (char)b;
            if (b < 0x80 && (char.IsAsciiLetterOrDigit(c) || c is '!' or '#' or '$' or '&' or '+' or '-' or '.' or '^' or '_' or '`' or '|' or '~'))
                sb.Append(c);
            else
                sb.Append('%').Append(b.ToString("X2"));
        }
        return sb.ToString();
    }
}

/// <summary>Content types for downloads. Anything a browser could run (html, svg, js, xml) is deliberately sent as opaque bytes.</summary>
internal static class MimeTypes
{
    private static readonly Dictionary<string, string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        [".txt"] = "text/plain; charset=utf-8",
        [".csv"] = "text/csv; charset=utf-8",
        [".md"] = "text/plain; charset=utf-8",
        [".json"] = "application/json",
        [".pdf"] = "application/pdf",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".bmp"] = "image/bmp",
        [".ico"] = "image/x-icon",
        [".tif"] = "image/tiff",
        [".tiff"] = "image/tiff",
        [".heic"] = "image/heic",
        [".avif"] = "image/avif",
        [".mp3"] = "audio/mpeg",
        [".m4a"] = "audio/mp4",
        [".wav"] = "audio/wav",
        [".ogg"] = "audio/ogg",
        [".flac"] = "audio/flac",
        [".aac"] = "audio/aac",
        [".mp4"] = "video/mp4",
        [".m4v"] = "video/mp4",
        [".mov"] = "video/quicktime",
        [".webm"] = "video/webm",
        [".mkv"] = "video/x-matroska",
        [".zip"] = "application/zip",
        [".7z"] = "application/x-7z-compressed",
        [".rar"] = "application/vnd.rar",
        [".gz"] = "application/gzip",
        [".tar"] = "application/x-tar",
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        [".pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        [".epub"] = "application/epub+zip",
    };

    public static string For(string fileName) =>
        Known.GetValueOrDefault(Path.GetExtension(fileName), "application/octet-stream");
}

/// <summary>The QR code a phone scans to open a share.</summary>
public static class PhoneShareLink
{
    /// <summary>A PNG QR code for <paramref name="url"/> (a http/https link), <paramref name="size"/> pixels square.</summary>
    public static byte[] CreateQrPng(string url, int size = 512)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new ArgumentException("The link must be an absolute http or https URL.", nameof(url));

        return QrCodeService.CreatePng(uri.AbsoluteUri, new QrCreateOptions(QrErrorCorrection.M, size, 4));
    }

    /// <summary>The same QR code for a running share.</summary>
    public static byte[] CreateQrPng(PhoneShareServer server, int size = 512) => CreateQrPng(server.ListingUrl.AbsoluteUri, size);
}
