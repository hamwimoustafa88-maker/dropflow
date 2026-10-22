// SPDX-License-Identifier: GPL-3.0-or-later
using DropFlow.Core.Settings;

namespace DropFlow.Desktop.Drag;

/// <summary>
/// The three wheel keys, already translated to DropFlow's slots: on a Mac ⌘ Command fills the Ctrl slot and ⌃ Control the Alt slot
/// (see <see cref="ModifierMap"/>), so the rest of the app only ever sees Shift / Ctrl / Alt.
/// </summary>
[Flags]
public enum Mods
{
    None = 0,
    Shift = 1,
    Ctrl = 2,
    Alt = 4,
}

/// <summary>
/// One look at the input state, taken by a polling watcher (macOS pasteboard, X11 pointer). <paramref name="DragSourceActive"/> is the
/// platform's "a file drag is in progress" signal: the drag pasteboard changed and holds file URLs (macOS), or a program took the
/// XdndSelection while the button was down (X11).
/// </summary>
public readonly record struct DragSample(bool ButtonDown, Mods Modifiers, int X, int Y, bool DragSourceActive);

public enum DragSignalKind { Started, KeyTapped, Ended }

/// <summary>What the decider concluded from a sample. <see cref="Key"/> is set for <see cref="DragSignalKind.KeyTapped"/>.</summary>
public readonly record struct DragSignal(DragSignalKind Kind, int X, int Y, TriggerKey? Key = null);

/// <summary>
/// Pure decision logic shared by the polling drag watchers. A drag starts when the button is down, the pointer moved more than the
/// threshold from where it went down, and the platform reports a file drag. During a drag, a wheel key that goes down (rising edge,
/// compared with the previous sample) is a tap; keys already held when the button went down, or pressed before the drag was
/// recognised, do not count until they are released and pressed again. Releasing the button ends the drag.
/// </summary>
public sealed class DragDecider
{
    private static readonly (Mods Flag, TriggerKey Key)[] Order =
        [(Mods.Shift, TriggerKey.Shift), (Mods.Ctrl, TriggerKey.Ctrl), (Mods.Alt, TriggerKey.Alt)];

    private readonly int _threshold;
    private bool _wasDown;
    private bool _moved;
    private bool _source;
    private int _originX;
    private int _originY;
    private Mods _previous;

    /// <param name="threshold">Distance in pixels (either axis) the pointer must travel before a press counts as a drag.</param>
    public DragDecider(int threshold)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(threshold);
        _threshold = threshold;
    }

    public bool IsDragging { get; private set; }

    public IReadOnlyList<DragSignal> Feed(DragSample s)
    {
        List<DragSignal>? signals = null;
        void Emit(DragSignal signal) => (signals ??= []).Add(signal);

        if (s.ButtonDown && !_wasDown)
        {
            _originX = s.X;
            _originY = s.Y;
            _moved = false;
            _source = false;
            IsDragging = false;
            // keys held at press time are not taps: _previous already holds them (every sample stores its modifiers)
        }

        if (s.ButtonDown)
        {
            if (!_moved && (Math.Abs(s.X - _originX) > _threshold || Math.Abs(s.Y - _originY) > _threshold)) _moved = true;
            if (s.DragSourceActive) _source = true;

            if (!IsDragging && _moved && _source)
            {
                IsDragging = true;
                Emit(new DragSignal(DragSignalKind.Started, s.X, s.Y));
            }

            var pressed = s.Modifiers & ~_previous;
            if (IsDragging && pressed != Mods.None)
            {
                foreach (var (flag, key) in Order)
                {
                    if (!pressed.HasFlag(flag)) continue;
                    Emit(new DragSignal(DragSignalKind.KeyTapped, s.X, s.Y, key));
                    break;
                }
            }
            _previous = s.Modifiers;
        }
        else
        {
            if (_wasDown && IsDragging) Emit(new DragSignal(DragSignalKind.Ended, s.X, s.Y));
            IsDragging = false;
            _moved = false;
            _source = false;
            _previous = s.Modifiers;
        }

        _wasDown = s.ButtonDown;
        return (IReadOnlyList<DragSignal>?)signals ?? [];
    }
}
