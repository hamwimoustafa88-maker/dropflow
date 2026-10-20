using DropFlow.Core.App;

namespace DropFlow.Core.Settings;

public static class SettingsValidator
{
    /// <summary>Error codes (matching resource keys <c>settings.*</c>): "keys.duplicate", "hotkey.invalid", "language.invalid", "concurrency.invalid".</summary>
    public static IReadOnlyList<string> Validate(AppSettings s)
    {
        var errors = new List<string>();
        if (!s.Keys.IsValid) errors.Add("keys.duplicate");
        if (HotkeyParser.Parse(s.GlobalHotkey) is null) errors.Add("hotkey.invalid");
        if (s.Language is not ("system" or "ar" or "en")) errors.Add("language.invalid");
        if (s.MaxConcurrency is < 1 or > 16) errors.Add("concurrency.invalid");
        return errors;
    }
}
