// SPDX-License-Identifier: GPL-3.0-or-later
using static DropFlow.Hooks.NativeMethods;

namespace DropFlow.Hooks;

/// <summary>
/// Tells a real drag of files apart from other left-button drags (selecting text with Shift, moving a window, drawing), so that only the
/// first opens the wheel. Two signs are accepted, both cheap enough for a hook callback:
/// <list type="bullet">
/// <item><b>The shell's drag picture</b>: Explorer, the desktop and most apps that drag files show it in a top-level window of class
///   <c>SysDragImage</c>; a <b>visible</b> one means shell items are on the move (hidden ones linger from earlier drags, so every one is checked).</item>
/// <item><b>An OLE drag holding the mouse</b>: during <c>DoDragDrop</c> the source thread captures the mouse with a hidden window. A capture held by an
///   invisible window of the foreground thread is that; text selection and window moves capture with the visible window itself.</item>
/// </list>
/// Neither is a documented contract, which is why the app keeps an escape hatch (<c>AppSettings.RequireFileDrag</c>) and logs every rejected tap
/// with <see cref="Describe"/>.
/// </summary>
public static class ShellDragProbe
{
    public const string DragImageClass = "SysDragImage";

    public static bool IsFileDragInProgress() => DragImageVisible() || OleDragHoldsMouse();

    public static bool DragImageVisible()
    {
        nint window = 0;
        for (var guard = 0; guard < 64; guard++)
        {
            window = FindWindowEx(0, window, DragImageClass, null);
            if (window == 0) return false;
            if (IsWindowVisible(window)) return true;
        }
        return false;
    }

    public static bool OleDragHoldsMouse()
    {
        var capture = ForegroundCapture();
        return capture != 0 && !IsWindowVisible(capture);
    }

    /// <summary>One line for the log: what the checks saw (no window titles, so nothing private is written).</summary>
    public static string Describe()
    {
        var capture = ForegroundCapture();
        var captureText = capture == 0 ? "none" : $"{ClassOf(capture)} ({(IsWindowVisible(capture) ? "visible" : "hidden")})";
        return $"drag picture visible: {DragImageVisible()}; foreground window: {ClassOf(GetForegroundWindow())}; mouse capture: {captureText}";
    }

    private static nint ForegroundCapture()
    {
        var foreground = GetForegroundWindow();
        if (foreground == 0) return 0;
        var thread = GetWindowThreadProcessId(foreground, out _);
        var info = new GuiThreadInfo { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<GuiThreadInfo>() };
        return GetGUIThreadInfo(thread, ref info) ? info.hwndCapture : 0;
    }

    private static string ClassOf(nint window)
    {
        if (window == 0) return "none";
        var buffer = new char[256];
        var length = GetClassName(window, buffer, buffer.Length);
        return length > 0 ? new string(buffer, 0, length) : "?";
    }
}
