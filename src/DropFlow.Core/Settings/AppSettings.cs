using System.Text.Json;
using System.Text.Json.Serialization;

namespace DropFlow.Core.Settings;

/// <summary>Key tapped during a drag to open a wheel.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<TriggerKey>))]
public enum TriggerKey { Shift, Ctrl, Alt }

public sealed record WheelKeys
{
    public TriggerKey Convert { get; init; } = TriggerKey.Shift;
    public TriggerKey Tools { get; init; } = TriggerKey.Ctrl;
    public TriggerKey Actions { get; init; } = TriggerKey.Alt;

    /// <summary>True when no two wheels share a key (a shared key would make the wheels unreachable).</summary>
    public bool IsValid => new[] { Convert, Tools, Actions }.Distinct().Count() == 3;
}

public sealed record AppSettings
{
    /// <summary>"system", "ar" or "en".</summary>
    public string Language { get; init; } = "system";

    public WheelKeys Keys { get; init; } = new();

    public bool RunAtStartup { get; init; }

    /// <summary>When true the drag hooks are off (tray "pause").</summary>
    public bool Paused { get; init; }

    public int MaxConcurrency { get; init; } = 2;

    /// <summary>Global shortcut that opens the floating shelf / converts the clipboard.</summary>
    public string GlobalHotkey { get; init; } = "Ctrl+Alt+D";

    /// <summary>Global shortcut that opens the wheel for whatever is on the clipboard (image, files or text).</summary>
    public string ClipboardHotkey { get; init; } = "Ctrl+Alt+V";

    /// <summary>Folder where downloaded engines and models live. Null = %LOCALAPPDATA%\DropFlow.</summary>
    public string? DataDirectory { get; init; }

    public bool ShowFirstRunTips { get; init; } = true;

    /// <summary>
    /// When true, a tool that needs a helper program that is not installed offers to install it (winget). Off by default: the app never
    /// asks to download anything on its own; the Install buttons in Settings always work.
    /// </summary>
    public bool OfferEngineInstalls { get; init; }

    /// <summary>Look for a new version on GitHub Releases shortly after start (the only automatic network request). On by default.</summary>
    public bool CheckForUpdates { get; init; } = true;

    /// <summary>Items added to a wheel with "+ Add", per wheel and kind of input. Removed from the wheel or in Settings.</summary>
    public WheelPins PinnedItems { get; init; } = WheelPins.None;

    /// <summary>
    /// When true (default) a key tap opens the wheel only while files are really being dragged: the shell's drag picture (a visible
    /// <c>SysDragImage</c> window) is on screen, an OLE drag holds the mouse, or the wheel already received the files. This keeps Shift+drag
    /// text selection and window moves from opening the wheel. The escape hatch for a system where that check never succeeds: set it to
    /// false (Settings, or <c>"RequireFileDrag": false</c> in settings.json); every rejected tap is written to the log.
    /// </summary>
    public bool RequireFileDrag { get; init; } = true;
}

/// <summary>Reads and writes settings as JSON. A corrupt file falls back to defaults and is kept as ".bad" for inspection.</summary>
public sealed class SettingsStore(string path)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public string Path { get; } = path;

    public AppSettings Load()
    {
        if (!File.Exists(Path)) return new AppSettings();
        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path), Options) ?? new AppSettings();
            if (settings.PinnedItems is null) settings = settings with { PinnedItems = WheelPins.None }; // "PinnedItems": null
            return settings.Keys.IsValid ? settings : settings with { Keys = new WheelKeys() };
        }
        catch (JsonException)
        {
            try { File.Copy(Path, Path + ".bad", overwrite: true); } catch (IOException) { /* best effort */ }
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!);
        var temp = Path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, Options));
        File.Move(temp, Path, overwrite: true); // atomic replace: a crash never leaves a half-written file
    }
}
