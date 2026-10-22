// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Input;

namespace DropFlow.Desktop.Wheel;

/// <summary>
/// Which drop effect the wheel answers. Copy whenever the source allows it. Finder offers only Move while ⌘ is held (⌘ is the Tools
/// key on a Mac), so Move is accepted too — DropFlow then reads the files and never moves or deletes them; the source app does not
/// delete files for a Move drop either (the target is the one that would move them).
/// </summary>
public static class DropEffectPolicy
{
    public static DragDropEffects Choose(DragDropEffects allowed)
    {
        if (allowed.HasFlag(DragDropEffects.Copy)) return DragDropEffects.Copy;
        if (allowed.HasFlag(DragDropEffects.Move)) return DragDropEffects.Move;
        if (allowed.HasFlag(DragDropEffects.Link)) return DragDropEffects.Link;
        return DragDropEffects.None;
    }
}

public static class WheelPlacement
{
    /// <summary>Top-left pixel of a square window of <paramref name="size"/> pixels centred on the pointer, kept inside the screen.</summary>
    public static (int X, int Y) TopLeft(int pointerX, int pointerY, int size, PixelRect screen)
    {
        var x = pointerX - size / 2;
        var y = pointerY - size / 2;
        x = Math.Clamp(x, screen.X, Math.Max(screen.X, screen.Right - size));
        y = Math.Clamp(y, screen.Y, Math.Max(screen.Y, screen.Bottom - size));
        return (x, y);
    }

    /// <summary>
    /// macOS: the window's frame origin (its bottom-left corner) in Cocoa screen coordinates (points, origin at the bottom-left of the
    /// main screen) for a square window of <paramref name="size"/> points centred on <c>NSEvent.mouseLocation</c>.
    /// </summary>
    public static (double X, double Y) CocoaOrigin(double pointerX, double pointerY, double size) => (pointerX - size / 2, pointerY - size / 2);
}
