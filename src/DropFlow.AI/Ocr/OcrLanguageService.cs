namespace DropFlow.AI.Ocr;

/// <summary>One language DropFlow can OCR, with the names each engine uses for it.</summary>
/// <param name="Iso1">ISO 639-1 code used in options ("ar").</param>
/// <param name="WindowsTag">BCP-47 tag of the Windows OCR language pack ("ar-SA"), or null when Windows has no OCR pack for it.</param>
/// <param name="Tesseract">Tesseract traineddata name ("ara").</param>
public sealed record OcrLanguage(string Iso1, string DisplayName, string? WindowsTag, string Tesseract)
{
    /// <summary>Windows capability that installs the OCR pack, e.g. <c>Language.OCR~~~ar-SA~0.0.1.0</c>; null when Windows has none.</summary>
    public string? WindowsCapabilityName => WindowsTag is null ? null : $"Language.OCR~~~{WindowsTag}~0.0.1.0";

    /// <summary>PowerShell command that installs the pack. Needs an elevated (administrator) PowerShell and an internet connection.</summary>
    public string? WindowsInstallCommand => WindowsCapabilityName is null ? null : $"Add-WindowsCapability -Online -Name {WindowsCapabilityName}";

    public bool WindowsInstallRequiresAdmin => WindowsTag is not null;
}

/// <summary>The language table: which codes DropFlow understands and how each engine names them.</summary>
public static class OcrLanguages
{
    public static IReadOnlyList<OcrLanguage> All { get; } =
    [
        new("ar", "Arabic", "ar-SA", "ara"),
        new("en", "English", "en-US", "eng"),
        new("fr", "French", "fr-FR", "fra"),
        new("de", "German", "de-DE", "deu"),
        new("es", "Spanish", "es-ES", "spa"),
        new("it", "Italian", "it-IT", "ita"),
        new("pt", "Portuguese", "pt-BR", "por"),
        new("nl", "Dutch", "nl-NL", "nld"),
        new("ru", "Russian", "ru-RU", "rus"),
        new("uk", "Ukrainian", "uk-UA", "ukr"),
        new("tr", "Turkish", "tr-TR", "tur"),
        new("pl", "Polish", "pl-PL", "pol"),
        new("cs", "Czech", "cs-CZ", "ces"),
        new("sk", "Slovak", "sk-SK", "slk"),
        new("hu", "Hungarian", "hu-HU", "hun"),
        new("ro", "Romanian", "ro-RO", "ron"),
        new("bg", "Bulgarian", "bg-BG", "bul"),
        new("hr", "Croatian", "hr-HR", "hrv"),
        new("sl", "Slovenian", "sl-SI", "slv"),
        new("el", "Greek", "el-GR", "ell"),
        new("sv", "Swedish", "sv-SE", "swe"),
        new("da", "Danish", "da-DK", "dan"),
        new("fi", "Finnish", "fi-FI", "fin"),
        new("nb", "Norwegian", "nb-NO", "nor"),
        new("ja", "Japanese", "ja-JP", "jpn"),
        new("ko", "Korean", "ko-KR", "kor"),
        new("zh", "Chinese (Simplified)", "zh-CN", "chi_sim"),
        // No Windows OCR pack exists for these; Tesseract is the only engine.
        new("fa", "Persian", null, "fas"),
        new("he", "Hebrew", null, "heb"),
        new("ur", "Urdu", null, "urd"),
        new("hi", "Hindi", null, "hin"),
    ];

    /// <summary>Finds a language by "ar", "ar-SA", "ARA" (Tesseract name) or "arabic". Null when unknown.</summary>
    public static OcrLanguage? Find(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        var c = code.Trim();
        var cut = c.IndexOfAny(['-', '_']);
        var primary = cut > 0 ? c[..cut] : c;
        return All.FirstOrDefault(l => l.Iso1.Equals(primary, StringComparison.OrdinalIgnoreCase))
            ?? All.FirstOrDefault(l => l.Tesseract.Equals(c, StringComparison.OrdinalIgnoreCase))
            ?? All.FirstOrDefault(l => l.DisplayName.Equals(c, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Splits "ar+en", "ara+eng", "ar,en" into known languages (order kept, duplicates removed). Unknown parts are returned in <paramref name="unknown"/>.</summary>
    public static IReadOnlyList<OcrLanguage> ParseList(string? value, out IReadOnlyList<string> unknown)
    {
        var found = new List<OcrLanguage>();
        var bad = new List<string>();
        foreach (var part in (value ?? "").Split(['+', ',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.Equals("auto", StringComparison.OrdinalIgnoreCase)) continue;
            var lang = Find(part);
            if (lang is null) bad.Add(part);
            else if (!found.Contains(lang)) found.Add(lang);
        }
        unknown = bad;
        return found;
    }
}

/// <summary>Where the list of installed Windows OCR languages comes from. Replaced by a fake in tests.</summary>
public interface IWindowsOcrLanguageSource
{
    /// <summary>BCP-47 tags of installed OCR language packs, e.g. "en-US", "ar-SA".</summary>
    IReadOnlyList<string> InstalledTags();
}

/// <summary>Reads the real list from <c>Windows.Media.Ocr.OcrEngine.AvailableRecognizerLanguages</c>.</summary>
public sealed class WindowsOcrLanguageSource : IWindowsOcrLanguageSource
{
    public IReadOnlyList<string> InstalledTags()
    {
        try
        {
            return Windows.Media.Ocr.OcrEngine.AvailableRecognizerLanguages.Select(l => l.LanguageTag).ToList();
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or PlatformNotSupportedException)
        {
            return [];
        }
    }
}

/// <summary>What the UI shows when a Windows OCR language is missing.</summary>
public sealed record OcrLanguageInstallHint(
    OcrLanguage Language,
    string CapabilityName,
    string PowerShellCommand,
    bool RequiresAdmin,
    string Instructions);

/// <summary>
/// Knows which OCR languages this PC has. Never assumes a language is present: Arabic in particular is an optional Windows
/// feature that most installs do not have until it is added with the PowerShell command from <see cref="InstallHint"/>.
/// </summary>
public sealed class OcrLanguageService(IWindowsOcrLanguageSource? source = null)
{
    private readonly IWindowsOcrLanguageSource _source = source ?? new WindowsOcrLanguageSource();

    /// <summary>Installed Windows OCR packs (BCP-47 tags).</summary>
    public IReadOnlyList<string> InstalledTags() => _source.InstalledTags();

    /// <summary>True when a Windows OCR pack for this language ("ar", "ar-SA", "en") is installed. "ar" matches any installed Arabic tag.</summary>
    public bool IsInstalled(string languageCodeOrTag)
    {
        var language = OcrLanguages.Find(languageCodeOrTag);
        var wanted = language?.Iso1 ?? PrimarySubtag(languageCodeOrTag);
        var tags = _source.InstalledTags();

        // A full tag such as "en-GB" asks for that exact pack; a bare code accepts any region.
        if (languageCodeOrTag.Contains('-'))
            return tags.Any(t => t.Equals(languageCodeOrTag, StringComparison.OrdinalIgnoreCase));
        return tags.Any(t => PrimarySubtag(t).Equals(wanted, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The installed tag to use for a language code (prefers the table's default region), or null when none is installed.</summary>
    public string? InstalledTagFor(string languageCode)
    {
        var language = OcrLanguages.Find(languageCode);
        var wanted = language?.Iso1 ?? PrimarySubtag(languageCode);
        var tags = _source.InstalledTags().Where(t => PrimarySubtag(t).Equals(wanted, StringComparison.OrdinalIgnoreCase)).ToList();
        return tags.FirstOrDefault(t => t.Equals(language?.WindowsTag, StringComparison.OrdinalIgnoreCase)) ?? tags.FirstOrDefault();
    }

    /// <summary>The install command for a language, or null when Windows has no OCR pack for it (use Tesseract instead).</summary>
    public static OcrLanguageInstallHint? InstallHint(string languageCode)
    {
        var language = OcrLanguages.Find(languageCode);
        if (language?.WindowsCapabilityName is not { } capability) return null;
        var command = language.WindowsInstallCommand!;
        return new OcrLanguageInstallHint(
            language, capability, command, RequiresAdmin: true,
            $"Open PowerShell as administrator and run: {command}  (needs an internet connection; no restart is required). " +
            "Or: Settings > Time & language > Language & region > add the language > tick \"Text recognition\".");
    }

    private static string PrimarySubtag(string tag)
    {
        var cut = tag.IndexOfAny(['-', '_']);
        return (cut > 0 ? tag[..cut] : tag).ToLowerInvariant();
    }
}
