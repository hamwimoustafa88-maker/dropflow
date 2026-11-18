using System.Net;

namespace DropFlow.Core.Settings;

/// <summary>
/// The proxy for DropFlow's own downloads (links, AI models, the update check). "" = the system's proxy, "none" = connect directly, or an
/// address: http://, https://, socks4://, socks4a:// or socks5:// with a host and port. No user name or password is stored (they would be a
/// secret in settings.json). .NET's own HTTP stack speaks all of these, so no extra library is needed.
/// </summary>
public static class ProxySetting
{
    private static readonly string[] Schemes = ["http", "https", "socks4", "socks4a", "socks5"];

    public static bool IsValid(string? value)
    {
        var v = value?.Trim() ?? "";
        if (v.Length == 0 || v.Equals("none", StringComparison.OrdinalIgnoreCase)) return true;
        return Uri.TryCreate(v, UriKind.Absolute, out var uri) && Schemes.Contains(uri.Scheme) && uri.Host.Length > 0 && !uri.IsDefaultPort
            && string.IsNullOrEmpty(uri.UserInfo) && (uri.AbsolutePath is "/" or "");
    }

    /// <summary>The proxy to hand to HttpClient: null = the system's, <see cref="DirectProxy"/> = none, else the address.</summary>
    public static IWebProxy? Create(string? value)
    {
        var v = value?.Trim() ?? "";
        if (v.Length == 0 || !IsValid(v)) return null;
        if (v.Equals("none", StringComparison.OrdinalIgnoreCase)) return DirectProxy.Instance;
        return new WebProxy(new Uri(v)) { BypassProxyOnLocal = true };
    }

    /// <summary>"No proxy": every request goes direct.</summary>
    public sealed class DirectProxy : IWebProxy
    {
        public static readonly DirectProxy Instance = new();
        public ICredentials? Credentials { get; set; }
        public Uri? GetProxy(Uri destination) => null;
        public bool IsBypassed(Uri host) => true;
    }
}
