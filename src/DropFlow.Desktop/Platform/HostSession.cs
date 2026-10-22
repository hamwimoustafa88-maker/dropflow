// SPDX-License-Identifier: GPL-3.0-or-later
namespace DropFlow.Desktop.Platform;

public enum OsKind { Windows, MacOS, Linux }

/// <summary>Which desktop the app runs in. Built from environment variables so it is testable; <see cref="Current"/> reads the real ones.</summary>
public sealed record HostSession(OsKind Os, bool IsWayland, bool IsX11, string Desktop)
{
    public bool IsMac => Os == OsKind.MacOS;
    public bool IsLinux => Os == OsKind.Linux;

    /// <summary>GNOME, including Ubuntu's and Pop!_OS's variants ("ubuntu:GNOME", "pop:GNOME").</summary>
    public bool IsGnome => Desktop.Split(':').Any(d => d.Equals("GNOME", StringComparison.OrdinalIgnoreCase)
                                                    || d.StartsWith("GNOME-", StringComparison.OrdinalIgnoreCase));

    public static OsKind CurrentOs =>
        OperatingSystem.IsMacOS() ? OsKind.MacOS : OperatingSystem.IsLinux() ? OsKind.Linux : OsKind.Windows;

    public static HostSession Current { get; } = Detect(CurrentOs, Environment.GetEnvironmentVariable);

    public static HostSession Detect(OsKind os, Func<string, string?> env)
    {
        if (os != OsKind.Linux) return new HostSession(os, false, false, os.ToString());

        var type = (env("XDG_SESSION_TYPE") ?? "").Trim().ToLowerInvariant();
        var wayland = type == "wayland" || (type != "x11" && !string.IsNullOrEmpty(env("WAYLAND_DISPLAY")));
        var x11 = !wayland && (type == "x11" || !string.IsNullOrEmpty(env("DISPLAY")));
        var desktop = env("XDG_CURRENT_DESKTOP") ?? env("DESKTOP_SESSION") ?? "";
        return new HostSession(os, wayland, x11, desktop);
    }
}
