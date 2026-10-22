// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;

namespace DropFlow.Desktop.Native;

/// <summary>libX11 / libXfixes / libXext entry points used by the X11 drag watcher and the overlay windows.</summary>
internal static class X11
{
    private const string LibX11 = "libX11.so.6";
    private const string LibXfixes = "libXfixes.so.3";

    public const int XFixesSelectionNotify = 0;
    public const ulong XFixesSetSelectionOwnerNotifyMask = 1;
    public const ulong CWOverrideRedirect = 1 << 9;

    [DllImport(LibX11)] public static extern IntPtr XOpenDisplay(IntPtr name);
    [DllImport(LibX11)] public static extern int XCloseDisplay(IntPtr display);
    [DllImport(LibX11)] public static extern IntPtr XDefaultRootWindow(IntPtr display);
    [DllImport(LibX11)] public static extern IntPtr XInternAtom(IntPtr display, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int onlyIfExists);
    [DllImport(LibX11)] public static extern IntPtr XGetSelectionOwner(IntPtr display, IntPtr selection);
    [DllImport(LibX11)] public static extern int XFlush(IntPtr display);
    [DllImport(LibX11)] public static extern int XPending(IntPtr display);
    [DllImport(LibX11)] public static extern int XNextEvent(IntPtr display, [Out] byte[] eventReturn);

    [DllImport(LibX11)]
    public static extern int XQueryPointer(IntPtr display, IntPtr window, out IntPtr root, out IntPtr child,
        out int rootX, out int rootY, out int winX, out int winY, out uint mask);

    [DllImport(LibX11)] public static extern int XChangeWindowAttributes(IntPtr display, IntPtr window, ulong valueMask, ref XSetWindowAttributes attributes);

    [DllImport(LibXfixes)] public static extern int XFixesQueryExtension(IntPtr display, out int eventBase, out int errorBase);
    [DllImport(LibXfixes)] public static extern void XFixesSelectSelectionInput(IntPtr display, IntPtr window, IntPtr selection, ulong eventMask);

    /// <summary>Xlib's XSetWindowAttributes (LP64 layout).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct XSetWindowAttributes
    {
        public IntPtr BackgroundPixmap;
        public UIntPtr BackgroundPixel;
        public IntPtr BorderPixmap;
        public UIntPtr BorderPixel;
        public int BitGravity;
        public int WinGravity;
        public int BackingStore;
        public UIntPtr BackingPlanes;
        public UIntPtr BackingPixel;
        public int SaveUnder;
        public IntPtr EventMask;
        public IntPtr DoNotPropagateMask;
        public int OverrideRedirect;
        public IntPtr Colormap;
        public IntPtr Cursor;
    }

    private static IntPtr _sharedDisplay;

    /// <summary>
    /// Makes an X11 window override-redirect: the window manager then maps it at once, exactly where asked and above other windows —
    /// GNOME and others otherwise hold back mapping a new window while a drag has the pointer grabbed. Call before the first show.
    /// </summary>
    public static void SetOverrideRedirect(IntPtr window)
    {
        if (window == IntPtr.Zero) return;
        try
        {
            if (_sharedDisplay == IntPtr.Zero) _sharedDisplay = XOpenDisplay(IntPtr.Zero);
            if (_sharedDisplay == IntPtr.Zero) return;
            var attributes = new XSetWindowAttributes { OverrideRedirect = 1 };
            XChangeWindowAttributes(_sharedDisplay, window, CWOverrideRedirect, ref attributes);
            XFlush(_sharedDisplay);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // not on X11
        }
    }

    /// <summary>Pointer position on the root window, or null when X cannot be reached.</summary>
    public static (int X, int Y)? PointerPosition()
    {
        try
        {
            var d = XOpenDisplay(IntPtr.Zero);
            if (d == IntPtr.Zero) return null;
            try
            {
                return XQueryPointer(d, XDefaultRootWindow(d), out _, out _, out var x, out var y, out _, out _, out _) != 0 ? (x, y) : null;
            }
            finally { XCloseDisplay(d); }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }
}
