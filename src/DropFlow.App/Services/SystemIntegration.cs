using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using DropFlow.Core.App;
using Microsoft.Win32;

namespace DropFlow.App.Services;

/// <summary>Writes the registry/Start-menu entries from <see cref="ShellIntegrationPlan"/> (all per-user, no admin rights).</summary>
internal static class SystemIntegration
{
    public static bool IsStartupEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(ShellIntegrationPlan.StartupKey);
        return key?.GetValue(ShellIntegrationPlan.StartupValue) is string;
    }

    public static void SetStartup(bool enabled, string exePath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(ShellIntegrationPlan.StartupKey);
        if (enabled)
        {
            var e = ShellIntegrationPlan.Startup(exePath);
            key.SetValue(e.ValueName, e.Value);
        }
        else
        {
            key.DeleteValue(ShellIntegrationPlan.StartupValue, throwOnMissingValue: false);
        }
    }

    public static bool IsContextMenuInstalled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(ShellIntegrationPlan.ContextMenuKeys()[0]);
        return key is not null;
    }

    public static void SetContextMenu(bool enabled, string exePath, string menuText)
    {
        if (enabled)
        {
            foreach (var e in ShellIntegrationPlan.ContextMenu(exePath, menuText))
            {
                using var key = Registry.CurrentUser.CreateSubKey(e.KeyPath);
                key.SetValue(e.ValueName, e.Value);
            }
        }
        else
        {
            foreach (var path in ShellIntegrationPlan.ContextMenuKeys())
                Registry.CurrentUser.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
        }
    }

    /// <summary>"Send to → DropFlow": a shortcut in the SendTo folder. Unlike the context-menu verb, Windows passes ALL selected files in one call.</summary>
    public static void SetSendTo(bool enabled, string exePath)
    {
        var link = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "Windows", "SendTo", "DropFlow.lnk");
        if (!enabled)
        {
            if (File.Exists(link)) File.Delete(link);
            return;
        }
        var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("WScript.Shell is not available.");
        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            dynamic shortcut = shell.CreateShortcut(link);
            shortcut.TargetPath = exePath;
            shortcut.Arguments = "--open";
            shortcut.IconLocation = exePath + ",0";
            shortcut.Description = "DropFlow";
            shortcut.Save();
        }
        finally { Marshal.ReleaseComObject(shell); }
    }
}

/// <summary>A system-wide shortcut (RegisterHotKey) delivered through a hidden message window.</summary>
internal sealed class GlobalHotkey : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const int Id = 0xD0F1;

    [DllImport("user32.dll")] private static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(nint hWnd, int id);

    private HwndSource? _source;

    public event Action? Pressed;

    public bool IsRegistered { get; private set; }

    /// <summary>Registers (or re-registers) the shortcut. Returns false when another program already owns it.</summary>
    public bool Register(Hotkey hotkey)
    {
        Unregister();
        _source ??= CreateWindow();
        // MOD_NOREPEAT (0x4000) so holding the keys does not fire repeatedly
        IsRegistered = RegisterHotKey(_source.Handle, Id, hotkey.Modifiers | 0x4000, hotkey.VirtualKey);
        return IsRegistered;
    }

    public void Unregister()
    {
        if (_source is not null && IsRegistered) UnregisterHotKey(_source.Handle, Id);
        IsRegistered = false;
    }

    private HwndSource CreateWindow()
    {
        var src = new HwndSource(new HwndSourceParameters("DropFlow.HotkeySink") { Width = 0, Height = 0, WindowStyle = 0 });
        src.AddHook((nint hwnd, int msg, nint wParam, nint lParam, ref bool handled) =>
        {
            if (msg == WM_HOTKEY && wParam == Id)
            {
                Pressed?.Invoke();
                handled = true;
            }
            return 0;
        });
        return src;
    }

    public void Dispose()
    {
        Unregister();
        _source?.Dispose();
        _source = null;
    }
}
