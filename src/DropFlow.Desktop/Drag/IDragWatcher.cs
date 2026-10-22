// SPDX-License-Identifier: GPL-3.0-or-later
using DropFlow.Core.Settings;
using DropFlow.Desktop.Platform;

namespace DropFlow.Desktop.Drag;

/// <summary>
/// Notices a file drag anywhere on the desktop and a wheel key tapped during it. Every implementation raises its events on the UI
/// thread (Avalonia's dispatcher), so handlers may touch windows directly.
/// </summary>
public interface IDragWatcher : IDisposable
{
    /// <summary>Short name for the log ("macOS drag pasteboard", "X11", "GNOME Shell extension", "none").</summary>
    string Name { get; }

    /// <summary>A file drag started (pointer position in the watcher's screen coordinates).</summary>
    event Action<int, int>? DragStarted;

    /// <summary>
    /// A wheel key went down during the drag at (x, y). <c>files</c> are the dragged paths when the watcher can read them (the macOS
    /// drag pasteboard), otherwise empty; the wheel then learns them when the drag enters it.
    /// </summary>
    event Action<TriggerKey, int, int, IReadOnlyList<string>>? KeyTapped;

    /// <summary>The mouse button was released (the drop, if any, reaches the wheel window separately).</summary>
    event Action? DragEnded;

    /// <summary>Starts watching. Returns false when this desktop cannot be watched (no display, missing library…).</summary>
    bool Start();
}

public enum DragWatcherKind { None, MacPasteboard, X11, GnomeShell }

public static class DragWatcherFactory
{
    /// <summary>
    /// macOS: the drag pasteboard (no Accessibility permission). Linux X11: XFixes + pointer polling. GNOME on Wayland: DropFlow's
    /// Shell extension. Other Wayland desktops cannot be watched by an app at all: DropFlow offers "Open with DropFlow" in the file
    /// manager instead (click-mode wheel).
    /// </summary>
    public static DragWatcherKind Choose(HostSession session) => session switch
    {
        { IsMac: true } => DragWatcherKind.MacPasteboard,
        { IsLinux: true, IsWayland: true, IsGnome: true } => DragWatcherKind.GnomeShell,
        { IsLinux: true, IsX11: true } => DragWatcherKind.X11,
        _ => DragWatcherKind.None,
    };
}

/// <summary>For desktops that cannot be watched: never raises anything.</summary>
public sealed class NoDragWatcher : IDragWatcher
{
    public string Name => "none";
#pragma warning disable CS0067 // never raised by design
    public event Action<int, int>? DragStarted;
    public event Action<TriggerKey, int, int, IReadOnlyList<string>>? KeyTapped;
    public event Action? DragEnded;
#pragma warning restore CS0067
    public bool Start() => false;
    public void Dispose() { }
}
