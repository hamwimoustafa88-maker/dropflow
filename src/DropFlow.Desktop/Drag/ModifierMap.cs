// SPDX-License-Identifier: GPL-3.0-or-later
using DropFlow.Core.Settings;

namespace DropFlow.Desktop.Drag;

/// <summary>Translates each platform's raw modifier bits into DropFlow's three wheel-key slots.</summary>
public static class ModifierMap
{
    // NSEventModifierFlags (AppKit)
    private const ulong MacShift = 1UL << 17;
    private const ulong MacControl = 1UL << 18;
    private const ulong MacCommand = 1UL << 20;

    // X11 KeyButMask
    private const uint X11Shift = 1;
    private const uint X11Control = 4;
    private const uint X11Mod1 = 8;
    private const uint X11Button1 = 1 << 8;

    /// <summary>
    /// macOS: ⇧ Shift → Shift, ⌘ Command → Ctrl slot, ⌃ Control → Alt slot. ⌥ Option is left alone: Finder uses it to choose
    /// "copy" during a drag, so tapping it must not open a wheel.
    /// </summary>
    public static Mods FromMacFlags(ulong flags)
    {
        var m = Mods.None;
        if ((flags & MacShift) != 0) m |= Mods.Shift;
        if ((flags & MacCommand) != 0) m |= Mods.Ctrl;
        if ((flags & MacControl) != 0) m |= Mods.Alt;
        return m;
    }

    /// <summary>X11: ShiftMask, ControlMask, Mod1Mask (Alt). Lock and Num Lock (Mod2) are ignored.</summary>
    public static Mods FromX11Mask(uint mask)
    {
        var m = Mods.None;
        if ((mask & X11Shift) != 0) m |= Mods.Shift;
        if ((mask & X11Control) != 0) m |= Mods.Ctrl;
        if ((mask & X11Mod1) != 0) m |= Mods.Alt;
        return m;
    }

    public static bool X11ButtonOneDown(uint mask) => (mask & X11Button1) != 0;

    /// <summary><c>NSEvent.pressedMouseButtons</c>: bit 0 is the primary button.</summary>
    public static bool MacLeftButtonDown(ulong buttons) => (buttons & 1) != 0;

    /// <summary>The name of a wheel key as printed on the user's keyboard.</summary>
    public static string KeyName(TriggerKey key, bool mac) => (key, mac) switch
    {
        (TriggerKey.Shift, true) => "⇧ Shift",
        (TriggerKey.Ctrl, true) => "⌘ Command",
        (TriggerKey.Alt, true) => "⌃ Control",
        (TriggerKey.Shift, false) => "Shift",
        (TriggerKey.Ctrl, false) => "Ctrl",
        _ => "Alt",
    };
}
