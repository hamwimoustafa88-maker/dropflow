using System.Text.RegularExpressions;

namespace DropFlow.Core.Sources;

/// <summary>
/// Reads a link out of what a web browser puts in a drag: a picture dragged from a page, a link, or a download from the downloads bar.
/// Chrome and Edge send <c>UniformResourceLocatorW</c> and <c>HTML Format</c> (an <c>&lt;img src&gt;</c> for pictures), Firefox
/// <c>text/x-moz-url</c> and <c>text/uri-list</c>. Pure: nothing is fetched here. The wheel is chosen from the link's file name
/// (<see cref="PlaceholderName"/>), and the file is downloaded only after the user picks a slice (<see cref="UrlDownloader"/>, which allows
/// only public addresses).
/// </summary>
public static partial class DropFormats
{
    /// <summary>
    /// The first http(s) address among the drag's formats, preferring the picture inside an HTML fragment (dragging a picture also carries
    /// the page's address, and the picture is what the user meant). Null when the drag carries no web link.
    /// </summary>
    public static Uri? LinkFrom(string? html = null, string? uriList = null, string? mozUrl = null, string? urlW = null, string? text = null)
    {
        if (html is not null)
        {
            var img = ImgSrc().Match(html);
            if (img.Success && Http(System.Net.WebUtility.HtmlDecode(img.Groups[1].Value)) is { } picture) return picture;
        }
        foreach (var candidate in new[]
                 {
                     FirstLine(uriList, skipComments: true), FirstLine(mozUrl), FirstLine(urlW), FirstLine(text),
                     html is null ? null : Href().Match(html) is { Success: true } a ? System.Net.WebUtility.HtmlDecode(a.Groups[1].Value) : null,
                 })
        {
            if (Http(candidate) is { } uri) return uri;
        }
        return null;
    }

    /// <summary>
    /// A file name for the link, so the wheel can offer what fits ("photo.jpg" → the picture wheel). Taken from the last part of the path;
    /// cleaned like a download name. Links without a usable name get "download" (general tools only).
    /// </summary>
    public static string PlaceholderName(Uri uri)
    {
        var last = Uri.UnescapeDataString(uri.AbsolutePath.TrimEnd('/').Split('/').LastOrDefault() ?? "");
        var name = DownloadNaming.Sanitize(last);
        return string.IsNullOrWhiteSpace(name) || !Path.HasExtension(name) ? "download" : name;
    }

    private static Uri? Http(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        return UrlDownloader.TryValidate(text.Trim(), out var uri, out _) ? uri : null;
    }

    private static string? FirstLine(string? text, bool skipComments = false) =>
        text?.Split(['\r', '\n', '\0'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(l => !(skipComments && l.StartsWith('#')));

    [GeneratedRegex("<img\\b[^>]*?\\bsrc\\s*=\\s*[\"']([^\"']+)[\"']", RegexOptions.IgnoreCase)]
    private static partial Regex ImgSrc();

    [GeneratedRegex("<a\\b[^>]*?\\bhref\\s*=\\s*[\"']([^\"']+)[\"']", RegexOptions.IgnoreCase)]
    private static partial Regex Href();
}
