// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using System.Text.Json.Serialization;
using DropFlow.Desktop.Platform;

namespace DropFlow.Desktop.App;

/// <summary>Where finished files go. Originals are never touched in any case.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<OutputLocation>))]
public enum OutputLocation { BesideSource, Downloads, Desktop }

public static class OutputPolicy
{
    /// <summary>The folder to pass as <c>ConversionJob.OutputDirectory</c>, or null for "beside the source" (the engines' default).</summary>
    public static string? DirectoryFor(OutputLocation location, AppDirectories dirs) => location switch
    {
        OutputLocation.Downloads => dirs.Downloads,
        OutputLocation.Desktop => dirs.Desktop,
        _ => null,
    };
}

/// <summary>
/// Settings that only the macOS/Linux app has. The shared ones (language, wheel keys, pins, pause) stay in Core's
/// <see cref="Core.Settings.AppSettings"/> so the format matches the Windows app.
/// </summary>
public sealed record DesktopSettings
{
    public OutputLocation Output { get; init; } = OutputLocation.BesideSource;

    /// <summary>Start DropFlow when the user signs in (LaunchAgent / SMAppService on macOS, ~/.config/autostart on Linux).</summary>
    public bool StartAtLogin { get; init; }

    /// <summary>Linux: put "Open with DropFlow" into the file managers' right-click menus.</summary>
    public bool FileManagerEntries { get; init; } = true;

    /// <summary>The welcome hint was shown once.</summary>
    public bool WelcomeShown { get; init; }
}

public sealed class DesktopSettingsStore(string path)
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public DesktopSettings Load()
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<DesktopSettings>(File.ReadAllText(path), Options) ?? new() : new();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new DesktopSettings();
        }
    }

    public void Save(DesktopSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, Options));
        File.Move(temp, path, overwrite: true);
    }
}
