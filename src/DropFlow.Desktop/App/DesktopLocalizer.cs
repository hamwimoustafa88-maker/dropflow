// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using DropFlow.Core.Localization;
using DropFlow.Core.Operations;

namespace DropFlow.Desktop.App;

/// <summary>
/// Strings for the macOS/Linux app: its own keys (<c>Resources/desktop.{en,ar}.json</c>) on top of the shared ones in DropFlow.Core
/// (wheel, options, history…), so tool and option labels stay identical to the Windows app. Arabic falls back to English, and a
/// missing key shows the key itself rather than crashing.
/// </summary>
public sealed class DesktopLocalizer : ILocalizer
{
    private readonly IReadOnlyList<IReadOnlyDictionary<string, string>> _layers;

    public DesktopLocalizer(string language, params IReadOnlyDictionary<string, string>[] layers)
    {
        Language = language;
        _layers = layers;
    }

    public string Language { get; }

    public bool IsRightToLeft => Language == "ar";

    public string this[string key]
    {
        get
        {
            foreach (var layer in _layers)
                if (layer.TryGetValue(key, out var value)) return value;
            return key;
        }
    }

    public bool Has(string key) => _layers.Any(l => l.ContainsKey(key));

    public string Format(string key, params object[] args) =>
        string.Format(Language == "ar" ? CultureInfo.GetCultureInfo("ar") : CultureInfo.GetCultureInfo("en"), this[key], args);

    public static DesktopLocalizer Load(string language)
    {
        var desktopEn = ReadDesktop("en");
        var coreEn = JsonLocalizer.ReadEmbedded("en");
        return language == "ar"
            ? new DesktopLocalizer("ar", ReadDesktop("ar"), JsonLocalizer.ReadEmbedded("ar"), desktopEn, coreEn)
            : new DesktopLocalizer("en", desktopEn, coreEn);
    }

    public static IReadOnlyDictionary<string, string> ReadDesktop(string language)
    {
        using var stream = typeof(DesktopLocalizer).Assembly.GetManifestResourceStream($"DropFlow.Desktop.desktop.{language}.json")
            ?? throw new InvalidOperationException($"Missing desktop strings for '{language}'.");
        using var reader = new StreamReader(stream);
        return JsonLocalizer.Parse(reader.ReadToEnd());
    }

    /// <summary>An option's label: <c>opt.{operation}.{key}</c>, then <c>opt.{key}</c>, then the raw key.</summary>
    public string OptionLabel(string operation, ToolOption option)
    {
        foreach (var key in Overlay.OptionForm.LabelKeys(operation, option))
            if (Has(key)) return this[key];
        return option.Key;
    }

    /// <summary>A choice's label: <c>opt.{operation}.{key}.{choice}</c>, then <c>opt.{key}.{choice}</c>, then the raw value.</summary>
    public string ChoiceLabel(string operation, ToolOption option, string choice)
    {
        foreach (var key in new[] { $"opt.{operation}.{option.Key}.{choice}", $"opt.{option.Key}.{choice}" })
            if (Has(key)) return this[key];
        return choice;
    }
}
