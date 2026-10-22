using System.Text;
using System.Text.RegularExpressions;

namespace DropFlow.Engines.Archive.Images;

/// <summary>The text a QR code holds for the common kinds of content.</summary>
public static partial class QrContent
{
    /// <summary>Schemes written without "//" that are still links (mailto:, tel:...), so no "https://" is put in front of them.</summary>
    private static readonly HashSet<string> OpaqueSchemes = new(StringComparer.OrdinalIgnoreCase)
    {
        "mailto", "tel", "sms", "smsto", "mms", "mmsto", "geo", "facetime", "skype", "whatsapp", "market", "maps", "magnet", "bitcoin", "urn", "data", "matmsg", "wifi", "otpauth",
    };

    /// <summary>Any text, unchanged.</summary>
    public static string Text(string text) => text;

    /// <summary>Trimmed; "https://" is added when the link has no scheme ("example.com", "localhost:8080/x").</summary>
    public static string Link(string url)
    {
        var link = (url ?? "").Trim();
        if (link.Length == 0) return link;
        if (SchemeWithSlashes().IsMatch(link)) return link;

        var colon = link.IndexOf(':');
        if (colon > 0 && OpaqueSchemes.Contains(link[..colon])) return link;
        return "https://" + link;
    }

    /// <summary>
    /// The "join this network" text phones understand: <c>WIFI:T:WPA;S:name;P:password;H:true;;</c>.
    /// <c>\ ; , : "</c> in the name and password are escaped with a backslash. <paramref name="security"/> is "WPA", "WEP" or "nopass"
    /// (also "none" or empty); with nopass the password is left out.
    /// </summary>
    public static string Wifi(string ssid, string? password, string security, bool hidden)
    {
        var type = (security ?? "").Trim().ToUpperInvariant() switch
        {
            "WEP" => "WEP",
            "NOPASS" or "NONE" or "OPEN" or "" => "nopass",
            _ => "WPA",
        };

        var text = new StringBuilder("WIFI:T:").Append(type).Append(";S:").Append(Escape(ssid ?? "")).Append(';');
        if (type != "nopass") text.Append("P:").Append(Escape(password ?? "")).Append(';');
        if (hidden) text.Append("H:true;");
        return text.Append(';').ToString();
    }

    private static string Escape(string value)
    {
        var escaped = new StringBuilder(value.Length + 8);
        foreach (var c in value)
        {
            if (c is '\\' or ';' or ',' or ':' or '"') escaped.Append('\\');
            escaped.Append(c);
        }
        return escaped.ToString();
    }

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9+.\-]*://")]
    private static partial Regex SchemeWithSlashes();
}
