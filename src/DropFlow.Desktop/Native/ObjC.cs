// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;

namespace DropFlow.Desktop.Native;

[StructLayout(LayoutKind.Sequential)]
public struct CGPoint(double x, double y)
{
    public double X = x;
    public double Y = y;
}

/// <summary>
/// The few Objective-C runtime calls DropFlow needs on macOS (no binding library): look up classes and selectors and send messages.
/// Every <c>objc_msgSend</c> overload below has a signature that is passed in registers on both arm64 and x86_64 (pointers, integers,
/// doubles and the two-double <see cref="CGPoint"/>), so no <c>_stret</c> variant is needed.
/// </summary>
internal static class ObjC
{
    private const string Lib = "/usr/lib/libobjc.A.dylib";

    [DllImport(Lib, EntryPoint = "objc_getClass")] public static extern IntPtr GetClass([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(Lib, EntryPoint = "sel_registerName")] public static extern IntPtr Sel([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(Lib, EntryPoint = "objc_autoreleasePoolPush")] public static extern IntPtr PoolPush();
    [DllImport(Lib, EntryPoint = "objc_autoreleasePoolPop")] public static extern void PoolPop(IntPtr pool);

    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern IntPtr Send(IntPtr receiver, IntPtr selector);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr a);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr a, IntPtr b);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern IntPtr SendNUInt(IntPtr receiver, IntPtr selector, nuint a);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern long SendLong(IntPtr receiver, IntPtr selector);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern ulong SendULong(IntPtr receiver, IntPtr selector);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern byte SendBool(IntPtr receiver, IntPtr selector);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern byte SendBool(IntPtr receiver, IntPtr selector, IntPtr a);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern CGPoint SendPoint(IntPtr receiver, IntPtr selector);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern void SendVoid(IntPtr receiver, IntPtr selector, IntPtr a);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern void SendVoidLong(IntPtr receiver, IntPtr selector, long a);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern void SendVoidULong(IntPtr receiver, IntPtr selector, ulong a);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern void SendVoidBool(IntPtr receiver, IntPtr selector, byte a);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern void SendVoidPoint(IntPtr receiver, IntPtr selector, CGPoint a);

    public static IntPtr Class(string name) => GetClass(name);

    /// <summary>An autoreleased NSString.</summary>
    public static IntPtr NSString(string value)
    {
        var utf8 = Marshal.StringToCoTaskMemUTF8(value);
        try { return Send(GetClass("NSString"), Sel("stringWithUTF8String:"), utf8); }
        finally { Marshal.FreeCoTaskMem(utf8); }
    }

    public static string? ToManaged(IntPtr nsString)
    {
        if (nsString == IntPtr.Zero) return null;
        var utf8 = Send(nsString, Sel("UTF8String"));
        return utf8 == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(utf8);
    }

    /// <summary>The value of an exported <c>NSString *const</c> (e.g. <c>NSPasteboardNameDrag</c>) from a framework.</summary>
    public static IntPtr ExportedString(string frameworkPath, string symbol)
    {
        if (!NativeLibrary.TryLoad(frameworkPath, out var lib)) return IntPtr.Zero;
        return NativeLibrary.TryGetExport(lib, symbol, out var address) ? Marshal.ReadIntPtr(address) : IntPtr.Zero;
    }

    public const string AppKit = "/System/Library/Frameworks/AppKit.framework/AppKit";
    public const string ServiceManagement = "/System/Library/Frameworks/ServiceManagement.framework/ServiceManagement";
}

/// <summary>Disposable autorelease pool scope (objects created by convenience constructors are released at the end).</summary>
internal readonly struct AutoreleasePool : IDisposable
{
    private readonly IntPtr _pool;
    public AutoreleasePool() => _pool = ObjC.PoolPush();
    public void Dispose() => ObjC.PoolPop(_pool);
}
