using System.Globalization;
using DropFlow.Core.Localization;

namespace DropFlow.Core.Sources;

public enum ClipboardTextKind
{
    /// <summary>Nothing but white space.</summary>
    Empty,
    /// <summary>Exactly one http(s) address (surrounding white space is ignored).</summary>
    SingleUrl,
    /// <summary>Several http(s) addresses and nothing else.</summary>
    MultipleUrls,
    /// <summary>Anything else, including a sentence that contains an address.</summary>
    Text,
}

/// <summary>Decides what copied text means to "open from link".</summary>
public static class LinkText
{
    public static ClipboardTextKind Classify(string? text, out Uri? url)
    {
        url = null;
        if (string.IsNullOrWhiteSpace(text)) return ClipboardTextKind.Empty;
        if (text.Length > UrlDownloader.MaxUrlLength * 4) return ClipboardTextKind.Text;

        var tokens = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var uris = new List<Uri>(tokens.Length);
        foreach (var token in tokens)
        {
            if (token.Length > UrlDownloader.MaxUrlLength
                || !Uri.TryCreate(token, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                || string.IsNullOrEmpty(uri.Host))
                return ClipboardTextKind.Text;
            uris.Add(uri);
        }

        if (uris.Count > 1) return ClipboardTextKind.MultipleUrls;
        url = uris[0];
        return ClipboardTextKind.SingleUrl;
    }
}

/// <summary>Human-readable sizes: <c>1.5 MB</c>.</summary>
public static class ByteSize
{
    public static string Format(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = Math.Max(0, bytes);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return unit == 0
            ? $"{bytes.ToString(CultureInfo.InvariantCulture)} B"
            : $"{value.ToString(value >= 100 ? "0" : "0.#", CultureInfo.InvariantCulture)} {units[unit]}";
    }
}

/// <summary>Translated texts for download problems and progress (the exceptions themselves carry English messages).</summary>
public static class LinkMessages
{
    public static string Describe(this UrlDownloadException ex, ILocalizer loc) => ex.Kind switch
    {
        UrlDownloadError.Invalid => loc["link.invalid"],
        UrlDownloadError.UnsupportedScheme => loc["link.scheme"],
        UrlDownloadError.HasCredentials => loc["link.credentials"],
        UrlDownloadError.HttpStatus => loc.Format("link.error.status", ex.StatusCode.ToString(CultureInfo.InvariantCulture), ex.Reason ?? ""),
        UrlDownloadError.TooManyRedirects => loc["link.error.redirects"],
        UrlDownloadError.TooLarge => loc.Format("link.error.tooLarge", ByteSize.Format(ex.Limit)),
        UrlDownloadError.Storage => loc.Format("link.error.storage", ex.Reason ?? ""),
        _ => loc.Format("link.error.network", ex.Reason ?? ""),
    };

    /// <summary>"3.2 MB of 10 MB" or "3.2 MB downloaded".</summary>
    public static string Describe(this DownloadProgress progress, ILocalizer loc) =>
        progress.TotalBytes is > 0
            ? loc.Format("link.progress", ByteSize.Format(progress.BytesReceived), ByteSize.Format(progress.TotalBytes.Value))
            : loc.Format("link.progressUnknown", ByteSize.Format(progress.BytesReceived));
}
