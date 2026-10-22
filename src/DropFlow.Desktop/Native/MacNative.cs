// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.Versioning;

namespace DropFlow.Desktop.Native;

/// <summary>
/// AppKit state that any app may read without the Accessibility or Input Monitoring permission: which mouse buttons are down, which
/// modifier keys are down, where the pointer is (<c>NSEvent</c> class properties) and the system drag pasteboard.
/// </summary>
[SupportedOSPlatform("macos")]
internal static class MacInput
{
    private static readonly IntPtr NSEventClass = ObjC.Class("NSEvent");
    private static readonly IntPtr SelPressedMouseButtons = ObjC.Sel("pressedMouseButtons");
    private static readonly IntPtr SelModifierFlags = ObjC.Sel("modifierFlags");
    private static readonly IntPtr SelMouseLocation = ObjC.Sel("mouseLocation");

    public static ulong PressedMouseButtons() => ObjC.SendULong(NSEventClass, SelPressedMouseButtons);

    public static ulong ModifierFlags() => ObjC.SendULong(NSEventClass, SelModifierFlags);

    /// <summary>Pointer in Cocoa screen coordinates: points, origin at the bottom-left of the main screen, y growing upwards.</summary>
    public static CGPoint MouseLocation() => ObjC.SendPoint(NSEventClass, SelMouseLocation);
}

/// <summary>The drag pasteboard (<c>NSPasteboardNameDrag</c>): every drag writes it, so its change count tells that a drag started.</summary>
[SupportedOSPlatform("macos")]
internal sealed class MacDragPasteboard
{
    private const string FileUrlType = "public.file-url";
    private readonly IntPtr _pasteboard;

    public MacDragPasteboard()
    {
        var name = ObjC.ExportedString(ObjC.AppKit, "NSPasteboardNameDrag");
        if (name == IntPtr.Zero) name = ObjC.NSString("Apple CFPasteboard drag");   // the documented value of NSDragPboard
        _pasteboard = ObjC.Send(ObjC.Class("NSPasteboard"), ObjC.Sel("pasteboardWithName:"), name);
        ObjC.Send(_pasteboard, ObjC.Sel("retain"));
    }

    public bool IsAvailable => _pasteboard != IntPtr.Zero;

    public long ChangeCount() => _pasteboard == IntPtr.Zero ? 0 : ObjC.SendLong(_pasteboard, ObjC.Sel("changeCount"));

    public bool HasFiles()
    {
        if (_pasteboard == IntPtr.Zero) return false;
        using var pool = new AutoreleasePool();
        var types = ObjC.Send(_pasteboard, ObjC.Sel("types"));
        return types != IntPtr.Zero && ObjC.SendBool(types, ObjC.Sel("containsObject:"), ObjC.NSString(FileUrlType)) != 0;
    }

    /// <summary>
    /// Paths of the dragged files. Finder writes file <i>reference</i> URLs (<c>file:///.file/id=…</c>); <c>filePathURL</c> turns
    /// them into ordinary paths.
    /// </summary>
    public IReadOnlyList<string> ReadFiles()
    {
        var files = new List<string>();
        if (_pasteboard == IntPtr.Zero) return files;
        using var pool = new AutoreleasePool();
        var items = ObjC.Send(_pasteboard, ObjC.Sel("pasteboardItems"));
        if (items == IntPtr.Zero) return files;
        var count = (long)ObjC.SendULong(items, ObjC.Sel("count"));
        var type = ObjC.NSString(FileUrlType);
        for (nuint i = 0; i < (nuint)Math.Min(count, 10_000); i++)
        {
            var item = ObjC.SendNUInt(items, ObjC.Sel("objectAtIndex:"), i);
            var text = ObjC.Send(item, ObjC.Sel("stringForType:"), type);
            if (text == IntPtr.Zero) continue;
            var url = ObjC.Send(ObjC.Class("NSURL"), ObjC.Sel("URLWithString:"), text);
            if (url == IntPtr.Zero) continue;
            var pathUrl = ObjC.Send(url, ObjC.Sel("filePathURL"));
            var path = ObjC.ToManaged(ObjC.Send(pathUrl == IntPtr.Zero ? url : pathUrl, ObjC.Sel("path")));
            if (!string.IsNullOrEmpty(path)) files.Add(path);
        }
        return files;
    }
}

/// <summary>
/// Turns Avalonia's NSWindow into an overlay: above every normal window and menu (<c>NSPopUpMenuWindowLevel</c>), on every Space and
/// next to full-screen apps, never hidden when DropFlow is not the active app, and shown with <c>orderFrontRegardless</c> so it
/// appears without activating DropFlow (the drag source keeps focus and the drag goes on).
/// </summary>
[SupportedOSPlatform("macos")]
internal static class MacWindow
{
    private const long PopUpMenuLevel = 101;                  // kCGPopUpMenuWindowLevel
    private const ulong CanJoinAllSpaces = 1 << 0;
    private const ulong Stationary = 1 << 4;
    private const ulong IgnoresCycle = 1 << 6;
    private const ulong FullScreenAuxiliary = 1 << 8;
    private const ulong NonactivatingPanel = 1 << 7;          // NSWindowStyleMaskNonactivatingPanel (NSPanel only)

    /// <summary>The NSWindow behind an Avalonia platform handle ("NSWindow", or "NSView" whose window is asked for).</summary>
    public static IntPtr FromHandle(IntPtr handle, string? descriptor)
    {
        if (handle == IntPtr.Zero) return IntPtr.Zero;
        if (descriptor == "NSView") return ObjC.Send(handle, ObjC.Sel("window"));
        return handle;
    }

    public static void MakeOverlay(IntPtr nsWindow)
    {
        if (nsWindow == IntPtr.Zero) return;
        ObjC.SendVoidLong(nsWindow, ObjC.Sel("setLevel:"), PopUpMenuLevel);
        ObjC.SendVoidULong(nsWindow, ObjC.Sel("setCollectionBehavior:"), CanJoinAllSpaces | FullScreenAuxiliary | Stationary | IgnoresCycle);
        ObjC.SendVoidBool(nsWindow, ObjC.Sel("setHidesOnDeactivate:"), 0);
        ObjC.SendVoidBool(nsWindow, ObjC.Sel("setCanHide:"), 0);

        // Only an NSPanel can be non-activating; make it one when Avalonia created a panel.
        var panelClass = ObjC.Class("NSPanel");
        if (panelClass != IntPtr.Zero && ObjC.SendBool(nsWindow, ObjC.Sel("isKindOfClass:"), panelClass) != 0)
        {
            var mask = ObjC.SendULong(nsWindow, ObjC.Sel("styleMask"));
            ObjC.SendVoidULong(nsWindow, ObjC.Sel("setStyleMask:"), mask | NonactivatingPanel);
            ObjC.SendVoidBool(nsWindow, ObjC.Sel("setBecomesKeyOnlyIfNeeded:"), 1);
        }
    }

    /// <summary>Moves the window's bottom-left corner to a Cocoa point and brings it to the front without activating the app.</summary>
    public static void ShowAt(IntPtr nsWindow, double originX, double originY)
    {
        if (nsWindow == IntPtr.Zero) return;
        ObjC.SendVoidPoint(nsWindow, ObjC.Sel("setFrameOrigin:"), new CGPoint(originX, originY));
        ObjC.Send(nsWindow, ObjC.Sel("orderFrontRegardless"));
    }
}

/// <summary>"Open at Login" through <c>SMAppService.mainAppService</c> (macOS 13+).</summary>
[SupportedOSPlatform("macos")]
internal static class MacLoginItem
{
    public const long Enabled = 1;

    private static IntPtr Service()
    {
        if (!OperatingSystem.IsMacOSVersionAtLeast(13)) return IntPtr.Zero;
        ObjC.ExportedString(ObjC.ServiceManagement, "SMAppServiceErrorDomain");     // loads the framework
        var cls = ObjC.Class("SMAppService");
        return cls == IntPtr.Zero ? IntPtr.Zero : ObjC.Send(cls, ObjC.Sel("mainAppService"));
    }

    /// <summary>0 not registered, 1 enabled, 2 requires approval, 3 not found; -1 when unavailable.</summary>
    public static long Status()
    {
        if (!OperatingSystem.IsMacOS()) return -1;
        var s = Service();
        return s == IntPtr.Zero ? -1 : ObjC.SendLong(s, ObjC.Sel("status"));
    }

    public static bool Register()
    {
        if (!OperatingSystem.IsMacOS()) return false;
        var s = Service();
        return s != IntPtr.Zero && ObjC.SendBool(s, ObjC.Sel("registerAndReturnError:"), IntPtr.Zero) != 0;
    }

    public static void Unregister()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var s = Service();
        if (s != IntPtr.Zero) ObjC.SendBool(s, ObjC.Sel("unregisterAndReturnError:"), IntPtr.Zero);
    }
}
