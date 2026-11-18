using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DropFlow.App.Services;

/// <summary>The program whose window is in front (the one the drag started in), for Settings → "Never open the wheel in these apps".</summary>
internal static class ForegroundApp
{
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    private static (uint Pid, string Name) _cache;

    /// <summary>Process name without ".exe" (lower case), or null when it cannot be read.</summary>
    public static string? Name()
    {
        try
        {
            GetWindowThreadProcessId(GetForegroundWindow(), out var pid);
            if (pid == 0) return null;
            if (_cache.Pid == pid) return _cache.Name;
            using var p = Process.GetProcessById((int)pid);
            var name = p.ProcessName.ToLowerInvariant();
            _cache = (pid, name);
            return name;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return null; }
    }

    /// <summary>True when <paramref name="name"/> is in the list ("photoshop", "Photoshop.exe" and " photoshop " all match).</summary>
    public static bool IsExcluded(string? name, IReadOnlyList<string> excluded) =>
        name is not null && excluded.Any(e => string.Equals(Normalize(e), name, StringComparison.OrdinalIgnoreCase));

    public static string Normalize(string entry)
    {
        var t = entry.Trim();
        return t.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? t[..^4] : t;
    }
}
