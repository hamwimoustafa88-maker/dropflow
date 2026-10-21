using System.Diagnostics;
using System.IO;
using System.Security.Principal;

namespace DropFlow.App.Services;

/// <summary>Keeps DropFlow unelevated: the low-level hooks and the single-instance pipe only work for normal launches that way.</summary>
internal static class Elevation
{
    public static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>
    /// Starts <paramref name="exePath"/> again through Explorer, which runs at the user's normal (medium) level, so the new process is not
    /// elevated. Returns false when that is not possible (no shell, or a built-in administrator account where Explorer is elevated too);
    /// the caller then simply keeps running.
    /// </summary>
    public static bool RelaunchUnelevated(string? exePath)
    {
        if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath)) return false;
        if (Process.GetProcessesByName("explorer").Length == 0) return false;

        // When Explorer itself is elevated (UAC off, built-in Administrator) the relaunch would be elevated again: never loop.
        var marker = Path.Combine(Path.GetTempPath(), "DropFlow.relaunch");
        try
        {
            if (File.Exists(marker) && DateTime.UtcNow - File.GetLastWriteTimeUtc(marker) < TimeSpan.FromSeconds(30)) return false;
            File.WriteAllText(marker, "");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }

        try
        {
            using var p = Process.Start(new ProcessStartInfo("explorer.exe", $"\"{exePath}\"") { UseShellExecute = false });
            return p is not null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }
}
