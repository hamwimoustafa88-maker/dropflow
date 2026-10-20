using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace DropFlow.Core.Localization;

/// <summary>Strings the UI needs. Implemented by <see cref="JsonLocalizer"/>.</summary>
public interface ILocalizer
{
    /// <summary>Two-letter UI language ("ar" or "en").</summary>
    string Language { get; }

    bool IsRightToLeft => Language == "ar";

    /// <summary>Text for a key, or the key itself when missing (so a missing translation is visible, not a crash).</summary>
    string this[string key] { get; }

    string Format(string key, params object[] args) => string.Format(CultureInfo.CurrentUICulture, this[key], args);
}

/// <summary>Loads flat JSON dictionaries ("tray.quit": "Quit") embedded in this assembly, or supplied directly for tests.</summary>
public sealed class JsonLocalizer : ILocalizer
{
    private readonly IReadOnlyDictionary<string, string> _strings;
    private readonly IReadOnlyDictionary<string, string> _fallback;

    public JsonLocalizer(string language, IReadOnlyDictionary<string, string> strings, IReadOnlyDictionary<string, string>? fallback = null)
    {
        Language = language;
        _strings = strings;
        _fallback = fallback ?? new Dictionary<string, string>();
    }

    public string Language { get; }

    public bool IsRightToLeft => Language == "ar";

    public string this[string key] =>
        _strings.TryGetValue(key, out var v) ? v : _fallback.TryGetValue(key, out var f) ? f : key;

    public string Format(string key, params object[] args) => string.Format(CultureInfo.CurrentUICulture, this[key], args);

    public static IReadOnlyDictionary<string, string> Parse(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? [];

    /// <summary>The embedded strings for "ar" or "en". Missing Arabic keys fall back to English rather than showing a key.</summary>
    public static JsonLocalizer Load(string language)
    {
        var en = ReadEmbedded("en");
        return language == "ar" ? new JsonLocalizer("ar", ReadEmbedded("ar"), en) : new JsonLocalizer("en", en);
    }

    public static IReadOnlyDictionary<string, string> ReadEmbedded(string language)
    {
        using var stream = typeof(JsonLocalizer).Assembly.GetManifestResourceStream($"DropFlow.Core.strings.{language}.json")
            ?? throw new InvalidOperationException($"Missing embedded strings for '{language}'.");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }
}
