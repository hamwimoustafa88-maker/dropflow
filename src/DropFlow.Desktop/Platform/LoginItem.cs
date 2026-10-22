// SPDX-License-Identifier: GPL-3.0-or-later
using System.Security;
using System.Text;
using DropFlow.Desktop.Native;

namespace DropFlow.Desktop.Platform;

/// <summary>
/// "Start DropFlow when I sign in". macOS: <c>SMAppService.mainApp</c> (macOS 13+, the system's Login Items list); when that is not
/// available or refuses an unsigned app, a per-user LaunchAgent in <c>~/Library/LaunchAgents</c>. Linux: an XDG autostart entry in
/// <c>~/.config/autostart</c>. Nothing needs administrator rights.
/// </summary>
public static class LoginItem
{
    public const string Label = "dev.scouthub.dropflow";
    private const string PlistName = Label + ".plist";
    private const string DesktopName = "dropflow.desktop";

    public static string LaunchAgentPlist(string appBundlePath)
    {
        var bundle = SecurityElement.Escape(appBundlePath);
        return $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
              <key>Label</key>
              <string>{Label}</string>
              <key>ProgramArguments</key>
              <array>
                <string>/usr/bin/open</string>
                <string>-a</string>
                <string>{bundle}</string>
                <string>--args</string>
                <string>--minimized</string>
              </array>
              <key>RunAtLoad</key>
              <true/>
              <key>ProcessType</key>
              <string>Interactive</string>
            </dict>
            </plist>

            """.Replace("\r\n", "\n");
    }

    public static string AutostartDesktopEntry(string command) =>
        $"""
        [Desktop Entry]
        Type=Application
        Name=DropFlow
        Comment=Drag a file, tap a key, drop it on the wheel
        Exec={QuoteExec(command)} --minimized
        Icon=dropflow
        Terminal=false
        NoDisplay=true
        X-GNOME-Autostart-enabled=true

        """.Replace("\r\n", "\n");

    /// <summary>Quotes a program path for a desktop entry's Exec key (freedesktop Desktop Entry spec, "The Exec key").</summary>
    public static string QuoteExec(string path)
    {
        if (path.All(c => char.IsAsciiLetterOrDigit(c) || c is '/' or '-' or '_' or '.' or '+')) return path;
        var sb = new StringBuilder("\"");
        foreach (var c in path)
        {
            if (c is '"' or '`' or '$' or '\\') sb.Append('\\');
            sb.Append(c);
        }
        return sb.Append('"').ToString();
    }

    /// <summary>The .app bundle that contains an executable, or null when it is not inside one.</summary>
    public static string? BundleOf(string executable)
    {
        var marker = executable.IndexOf(".app/Contents/MacOS/", StringComparison.Ordinal);
        return marker < 0 ? null : executable[..(marker + 4)];
    }

    /// <summary>The command that starts this copy: the AppImage when running from one (its mount point changes every run).</summary>
    public static string CurrentCommand =>
        Environment.GetEnvironmentVariable("APPIMAGE") is { Length: > 0 } image ? image : Environment.ProcessPath ?? "dropflow-desktop";

    public static bool IsEnabled(AppDirectories dirs)
    {
        if (OperatingSystem.IsMacOS())
            return MacLoginItem.Status() == MacLoginItem.Enabled || File.Exists(AppDirectories.Join(dirs.LaunchAgents, PlistName));
        return File.Exists(AppDirectories.Join(dirs.Autostart, DesktopName));
    }

    /// <summary>Turns start-at-login on or off. Returns a short description of what was done (for the log).</summary>
    public static string Apply(bool enable, AppDirectories dirs)
    {
        if (OperatingSystem.IsMacOS())
        {
            var plist = AppDirectories.Join(dirs.LaunchAgents, PlistName);
            if (!enable)
            {
                MacLoginItem.Unregister();
                if (File.Exists(plist)) File.Delete(plist);
                return "login item removed";
            }
            if (MacLoginItem.Register()) return "registered with SMAppService";
            var bundle = BundleOf(CurrentCommand);
            if (bundle is null) return "not inside an app bundle: login item not added";
            Directory.CreateDirectory(dirs.LaunchAgents);
            File.WriteAllText(plist, LaunchAgentPlist(bundle));
            return "LaunchAgent written";
        }

        var entry = AppDirectories.Join(dirs.Autostart, DesktopName);
        if (!enable)
        {
            if (File.Exists(entry)) File.Delete(entry);
            return "autostart entry removed";
        }
        Directory.CreateDirectory(dirs.Autostart);
        File.WriteAllText(entry, AutostartDesktopEntry(CurrentCommand));
        return "autostart entry written";
    }
}
