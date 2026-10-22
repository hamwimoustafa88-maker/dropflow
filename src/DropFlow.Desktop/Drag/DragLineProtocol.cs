// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using DropFlow.Core.Settings;

namespace DropFlow.Desktop.Drag;

/// <summary>
/// The text protocol the DropFlow GNOME Shell extension writes to <c>$XDG_RUNTIME_DIR/dropflow/drag.sock</c>, one command per line:
/// <c>begin</c> (a drag entered the compositor), <c>key shift|ctrl|alt X Y</c> (that key went down during the drag, pointer at
/// stage pixel X,Y) and <c>end</c>. Anything else is ignored, so a broken or hostile writer can never crash the app.
/// </summary>
public static class DragLineProtocol
{
    public const int MaxLineLength = 128;

    public static DragSignal? Parse(string? line)
    {
        if (line is null || line.Length > MaxLineLength) return null;
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return null;

        switch (parts[0])
        {
            case "begin" when parts.Length == 1:
                return new DragSignal(DragSignalKind.Started, 0, 0);
            case "end" when parts.Length == 1:
                return new DragSignal(DragSignalKind.Ended, 0, 0);
            case "key" when parts.Length == 4:
                TriggerKey? key = parts[1] switch
                {
                    "shift" => TriggerKey.Shift,
                    "ctrl" => TriggerKey.Ctrl,
                    "alt" => TriggerKey.Alt,
                    _ => null,
                };
                if (key is null || !TryCoordinate(parts[2], out var x) || !TryCoordinate(parts[3], out var y)) return null;
                return new DragSignal(DragSignalKind.KeyTapped, x, y, key);
            default:
                return null;
        }
    }

    private static bool TryCoordinate(string text, out int value)
    {
        value = 0;
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) || !double.IsFinite(d)) return false;
        if (d is < -1_000_000 or > 1_000_000) return false;
        value = (int)Math.Round(d, MidpointRounding.AwayFromZero);
        return true;
    }
}
