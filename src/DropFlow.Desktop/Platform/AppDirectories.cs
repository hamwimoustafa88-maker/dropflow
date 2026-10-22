// SPDX-License-Identifier: GPL-3.0-or-later
namespace DropFlow.Desktop.Platform;

/// <summary>
/// Where the macOS/Linux app keeps its files. macOS: <c>~/Library/Application Support/DropFlow</c> (settings, history, logs and the
/// single-instance socket). Linux: the XDG base directories — data in <c>$XDG_DATA_HOME/dropflow</c>, sockets in
/// <c>$XDG_RUNTIME_DIR/dropflow</c>, autostart in <c>$XDG_CONFIG_HOME/autostart</c>. Pure: built from a home folder and environment
/// lookups, with Unix separators whatever the host, so it is tested on Windows too.
/// </summary>
public sealed record AppDirectories(
    string Home,
    string Data,
    string Runtime,
    string InstanceSocket,
    string DragSocket,
    string Logs,
    string Downloads,
    string Desktop,
    string Autostart,
    string LaunchAgents,
    string GnomeExtensions,
    string DataHome,
    string ConfigHome)
{
    public string SettingsFile => Join(Data, "settings.json");
    public string DesktopSettingsFile => Join(Data, "desktop.json");
    public string HistoryFile => Join(Data, "history.json");

    public static AppDirectories Current { get; } = ForCurrent();

    private static AppDirectories ForCurrent()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var os = HostSession.CurrentOs;
        if (os == OsKind.Windows)
        {
            // Development on Windows only: the Windows product is src/DropFlow.App. Keep a separate folder so the two never collide.
            var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DropFlow Desktop Data");
            return new AppDirectories(home, data, data, Path.Combine(data, "app.sock"), Path.Combine(data, "drag.sock"), Path.Combine(data, "logs"),
                Path.Combine(home, "Downloads"), Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), data, data, data, data, data);
        }

        string? userDirs = null;
        if (os == OsKind.Linux)
        {
            var config = XdgHome(Environment.GetEnvironmentVariable, "XDG_CONFIG_HOME", home, ".config");
            try
            {
                var file = Join(config, "user-dirs.dirs");
                if (File.Exists(file)) userDirs = File.ReadAllText(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* defaults */ }
        }
        return For(os, home, Environment.GetEnvironmentVariable, userDirs);
    }

    public static AppDirectories For(OsKind os, string home, Func<string, string?> env, string? userDirsFile = null)
    {
        home = home.TrimEnd('/');
        if (os == OsKind.MacOS)
        {
            var data = Join(home, "Library", "Application Support", "DropFlow");
            return new AppDirectories(
                home, data, data,
                Join(data, "dropflow.sock"), Join(data, "drag.sock"),
                Join(home, "Library", "Logs", "DropFlow"),
                Join(home, "Downloads"), Join(home, "Desktop"),
                Join(home, "Library", "LaunchAgents"), Join(home, "Library", "LaunchAgents"),
                data, Join(home, "Library", "Application Support"), Join(home, "Library", "Preferences"));
        }

        var dataHome = XdgHome(env, "XDG_DATA_HOME", home, ".local/share");
        var configHome = XdgHome(env, "XDG_CONFIG_HOME", home, ".config");
        var stateHome = XdgHome(env, "XDG_STATE_HOME", home, ".local/state");
        var appData = Join(dataHome, "dropflow");
        var runtimeBase = env("XDG_RUNTIME_DIR");
        var runtime = !string.IsNullOrWhiteSpace(runtimeBase) && runtimeBase.StartsWith('/') ? Join(runtimeBase.TrimEnd('/'), "dropflow") : Join(appData, "run");

        var dirs = userDirsFile is null ? new Dictionary<string, string>() : XdgUserDirs.Parse(userDirsFile, home);
        return new AppDirectories(
            home, appData, runtime,
            Join(runtime, "app.sock"), Join(runtime, "drag.sock"),
            Join(stateHome, "dropflow", "logs"),
            dirs.GetValueOrDefault("XDG_DOWNLOAD_DIR") ?? Join(home, "Downloads"),
            dirs.GetValueOrDefault("XDG_DESKTOP_DIR") ?? Join(home, "Desktop"),
            Join(configHome, "autostart"), Join(home, "Library", "LaunchAgents"),
            Join(dataHome, "gnome-shell", "extensions"), dataHome, configHome);
    }

    /// <summary>An XDG base directory: the variable when it holds an absolute path (relative values must be ignored), else the default.</summary>
    private static string XdgHome(Func<string, string?> env, string name, string home, string fallback)
    {
        var value = env(name);
        return !string.IsNullOrWhiteSpace(value) && value.StartsWith('/') ? value.TrimEnd('/') : Join(home, fallback);
    }

    /// <summary>Joins with '/' (these paths are only used on macOS and Linux).</summary>
    public static string Join(params string[] parts) =>
        string.Join('/', parts.Select((p, i) => i == 0 ? p.TrimEnd('/') : p.Trim('/')).Where(p => p.Length > 0 || parts.Length == 1));
}

/// <summary>Reads <c>~/.config/user-dirs.dirs</c> (shell syntax: <c>XDG_DOWNLOAD_DIR="$HOME/Downloads"</c>), which localised systems use.</summary>
public static class XdgUserDirs
{
    public static Dictionary<string, string> Parse(string content, string home)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in content.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();
            if (value.Length < 2 || value[0] != '"' || value[^1] != '"') continue;
            value = value[1..^1].Replace("\\\"", "\"").Replace("\\\\", "\\");
            if (value.StartsWith("$HOME", StringComparison.Ordinal)) value = home.TrimEnd('/') + value[5..];
            if (!value.StartsWith('/')) continue;
            result[key] = value.TrimEnd('/');
        }
        return result;
    }
}
