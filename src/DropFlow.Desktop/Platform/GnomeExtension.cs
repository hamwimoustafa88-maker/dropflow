// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;

namespace DropFlow.Desktop.Platform;

public enum GnomeExtensionState { NotNeeded, Active, NeedsSignOut, Disabled, Error, Missing }

/// <summary>
/// DropFlow's GNOME Shell extension (<c>linux/gnome-extension/dropflow@scouthub.dev</c>). On Wayland no app can see the pointer or the
/// keyboard outside its own windows; the Shell can, so the extension tells DropFlow when a wheel key goes down during a drag. The app
/// copies it to the user's extensions folder (unless a package installed it system-wide) and enables it with
/// <c>gnome-extensions enable</c>; GNOME on Wayland loads a new extension only after the user signs out and in once.
/// </summary>
public static class GnomeExtension
{
    public const string Uuid = "dropflow@scouthub.dev";
    private const string SystemFolder = "/usr/share/gnome-shell/extensions/" + Uuid;

    public static string Bundled => Path.Combine(AppContext.BaseDirectory, "gnome-extension", Uuid);

    public static string UserFolder(AppDirectories dirs) => AppDirectories.Join(dirs.GnomeExtensions, Uuid);

    /// <summary>Reads <c>gnome-extensions info</c> output ("State: ACTIVE", "State: INITIALIZED"…).</summary>
    public static GnomeExtensionState ParseState(string? info, bool filesPresent = false)
    {
        if (string.IsNullOrWhiteSpace(info)) return filesPresent ? GnomeExtensionState.NeedsSignOut : GnomeExtensionState.Missing;
        var state = info.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("State:", StringComparison.OrdinalIgnoreCase));
        var value = state?["State:".Length..].Trim().ToUpperInvariant() ?? "";
        return value switch
        {
            "ACTIVE" or "ENABLED" => GnomeExtensionState.Active,
            "INITIALIZED" or "DOWNLOADING" => GnomeExtensionState.NeedsSignOut,
            "INACTIVE" or "DISABLED" => GnomeExtensionState.Disabled,
            "ERROR" or "OUT OF DATE" => GnomeExtensionState.Error,
            _ => filesPresent ? GnomeExtensionState.NeedsSignOut : GnomeExtensionState.Missing,
        };
    }

    public static GnomeExtensionState Current(HostSession session, AppDirectories dirs)
    {
        if (!(session.IsLinux && session.IsWayland && session.IsGnome)) return GnomeExtensionState.NotNeeded;
        var present = Directory.Exists(UserFolder(dirs)) || Directory.Exists(SystemFolder);
        return ParseState(Run("info", Uuid), present);
    }

    /// <summary>Copies the extension for this user (when no package installed it) and enables it. Returns the new state.</summary>
    public static GnomeExtensionState Install(HostSession session, AppDirectories dirs)
    {
        if (!(session.IsLinux && session.IsWayland && session.IsGnome)) return GnomeExtensionState.NotNeeded;
        var user = UserFolder(dirs);
        try
        {
            if (Directory.Exists(SystemFolder))
            {
                // a user copy would shadow the packaged one (and its updates)
                if (Directory.Exists(user) && File.Exists(Path.Combine(user, ".dropflow-copy"))) Directory.Delete(user, recursive: true);
            }
            else if (Directory.Exists(Bundled))
            {
                Directory.CreateDirectory(user);
                foreach (var file in Directory.GetFiles(Bundled))
                {
                    var target = Path.Combine(user, Path.GetFileName(file));
                    if (!File.Exists(target) || !File.ReadAllBytes(target).AsSpan().SequenceEqual(File.ReadAllBytes(file))) File.Copy(file, target, overwrite: true);
                }
                File.WriteAllText(Path.Combine(user, ".dropflow-copy"), "copied by the DropFlow app\n");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"GNOME extension copy: {ex.Message}");
        }
        Run("enable", Uuid);
        return Current(session, dirs);
    }

    private static string? Run(params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("gnome-extensions") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p is null) return null;
            var output = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(5000)) { try { p.Kill(); } catch (InvalidOperationException) { } return null; }
            return p.ExitCode == 0 ? output.GetAwaiter().GetResult() : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }
}
