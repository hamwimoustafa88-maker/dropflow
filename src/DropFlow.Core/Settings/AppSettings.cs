using System.Text.Json;
using System.Text.Json.Serialization;

namespace DropFlow.Core.Settings;

/// <summary>Key tapped during a drag to open a wheel.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<TriggerKey>))]
public enum TriggerKey { Shift, Ctrl, Alt }

/// <summary>Animations: follow Windows ("Show animations in Windows"), always off, or always on.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<MotionSetting>))]
public enum MotionSetting { Auto, Reduce, Full }

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

    /// <summary>The version whose "What's new" slides were already shown (null on installs older than this setting).</summary>
    public string? WhatsNewSeen { get; init; }

    /// <summary>
    /// When true, a tool that needs a helper program that is not installed offers to install it (winget). Off by default: the app never
    /// asks to download anything on its own; the Install buttons in Settings always work.
    /// </summary>
    public bool OfferEngineInstalls { get; init; }

    /// <summary>Look for a new version on GitHub Releases shortly after start (the only automatic network request). On by default.</summary>
    public bool CheckForUpdates { get; init; } = true;

    /// <summary>Items added to a wheel with "+ Add", per wheel and kind of input. Removed from the wheel or in Settings.</summary>
    public WheelPins PinnedItems { get; init; } = WheelPins.None;

    /// <summary>The user's order and hidden items per wheel and kind of input (Settings → Arrange the wheels).</summary>
    public WheelLayouts Layouts { get; init; } = WheelLayouts.None;

    /// <summary>Slices on the front of a wheel; the rest go to its back (Tab or the mouse wheel turns it over).</summary>
    public int WheelFrontMax { get; init; } = Wheel.WheelFaces.DefaultFrontMax;

    /// <summary>
    /// Tab and the mouse wheel turn the wheel over DURING a drag. This is the only time DropFlow swallows a key (plain Tab while its wheel is
    /// on screen in a drag; ADR-0015). Turn it off here if it ever gets in the way; in click mode the keys work regardless.
    /// </summary>
    public bool WheelFlipGestures { get; init; } = true;

    /// <summary>Accent colour of the wheel and the windows ("#RRGGBB"); null = DropFlow's violet.</summary>
    public string? Accent { get; init; }

    /// <summary>Opacity of the wheel's surfaces, 0.6-1 (text stays solid).</summary>
    public double WheelOpacity { get; init; } = 1;

    /// <summary>Size of the wheel, 0.8-1.4 (1 = default).</summary>
    public double WheelScale { get; init; } = 1;

    /// <summary>Size of the wedge labels, 0.85-1.3.</summary>
    public double LabelScale { get; init; } = 1;

    public MotionSetting Motion { get; init; } = MotionSetting.Auto;

    /// <summary>Programs (process names, e.g. "photoshop") in which a key tap never opens the wheel.</summary>
    public IReadOnlyList<string> ExcludedApps { get; init; } = [];

    /// <summary>Results of a clipboard conversion (Ctrl+Alt+V) go back onto the clipboard. Off by default.</summary>
    public bool ClipboardResults { get; init; }

    /// <summary>How results are named and dated (Settings → Output). The default changes nothing.</summary>
    public Output.OutputRules Output { get; init; } = Core.Output.OutputRules.Default;

    /// <summary>A proxy for DropFlow's own downloads (links, AI models, update check): "" = the system's, "none" = direct, or "http://host:port" / "socks5://host:port".</summary>
    public string Proxy { get; init; } = "";

    /// <summary>The appearance values brought into their allowed ranges (hand-edited files, older versions).</summary>
    public AppSettings Normalized() => this with
    {
        WheelOpacity = double.IsFinite(WheelOpacity) ? Math.Clamp(WheelOpacity, 0.6, 1) : 1,
        WheelScale = double.IsFinite(WheelScale) ? Math.Clamp(WheelScale, 0.8, 1.4) : 1,
        LabelScale = double.IsFinite(LabelScale) ? Math.Clamp(LabelScale, 0.85, 1.3) : 1,
        Accent = Accent is { } a && System.Text.RegularExpressions.Regex.IsMatch(a, "^#[0-9A-Fa-f]{6}$") ? a : null,
        ExcludedApps = (ExcludedApps ?? []).Select(e => e?.Trim() ?? "").Where(e => e.Length is > 0 and < 100).Distinct(StringComparer.OrdinalIgnoreCase).Take(50).ToList(),
        Proxy = ProxySetting.IsValid(Proxy) ? Proxy.Trim() : "",
        Output = Output is null ? Core.Output.OutputRules.Default : Core.Output.OutputRules.IsValidPattern(Output.NamePattern) ? Output : Output with { NamePattern = null },
    };

    /// <summary>
    /// When true a key tap opens the wheel only while files are really being dragged: the shell's drag picture (a visible
    /// <c>SysDragImage</c> window) is on screen, an OLE drag holds the mouse, or the wheel already received the files. This keeps Shift+drag
    /// text selection and window moves from opening the wheel. The escape hatch for a system where that check never succeeds: set it to
    /// false (Settings, or <c>"RequireFileDrag": false</c> in settings.json); every rejected tap is written to the log.
    /// Off by default until the check has been tried with real input on several PCs: the older rule (any tap during a drag) is what the
    /// owner's drag test passed with, and a wrong "no" here would mean the wheel never opens. Set to true to try the stricter rule.
    /// </summary>
    public bool RequireFileDrag { get; init; } = false;
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
            if (settings.Layouts is null) settings = settings with { Layouts = WheelLayouts.None };
            if (settings.WheelFrontMax is < Wheel.WheelFaces.MinFrontMax or > Wheel.WheelFaces.MaxFrontMax)
                settings = settings with { WheelFrontMax = Wheel.WheelFaces.DefaultFrontMax }; // hand-edited nonsense
            settings = settings.Normalized();
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
