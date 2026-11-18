namespace DropFlow.Hooks;

/// <summary>A rectangle in physical screen pixels (the wheel window while it is shown).</summary>
public readonly record struct ScreenRect(int X, int Y, int Width, int Height)
{
    public bool Contains(int x, int y) => x >= X && y >= Y && x < X + Width && y < Y + Height;
}

/// <summary>What the hook should do with one input event.</summary>
public readonly record struct GestureDecision(bool Swallow, bool Flip)
{
    public static readonly GestureDecision Pass = new(false, false);
}

/// <summary>
/// Turns the wheel over during a file drag. While a drag is in progress Windows sends keys and the mouse wheel to the window under the
/// pointer, not to the wheel, so the low-level hook asks this filter first. It swallows input ONLY while the wheel is armed (shown during a
/// drag): plain Tab / Shift+Tab, and mouse-wheel turns over the wheel's own rectangle. Everything else passes untouched, including any key
/// with Ctrl, Alt or Win held (Alt+Tab keeps working mid-drag). A swallowed key-down also swallows its key-up so the other application
/// never sees half a key press, and auto-repeat never flips twice. Pure and thread-agnostic: the hook thread is the only caller.
/// </summary>
public sealed class WheelGestureFilter
{
    public const int VkTab = 0x09;
    public const int WheelNotch = 120;
    public static readonly TimeSpan MinFlipInterval = TimeSpan.FromMilliseconds(250);

    private volatile ArmedState? _armed;
    private bool _tabDown;
    private bool _swallowTabUp;
    private int _wheelSum;
    private long _lastFlipMs = long.MinValue / 2;

    private sealed record ArmedState(ScreenRect Rect);

    /// <summary>False turns the whole feature off (settings kill switch).</summary>
    public bool Enabled { get; set; } = true;

    public bool IsArmed => _armed is not null;

    /// <summary>The wheel is on screen during a drag at this rectangle. May be called from any thread.</summary>
    public void Arm(ScreenRect wheel) => _armed = new ArmedState(wheel);

    /// <summary>The wheel was hidden, or the drag ended. May be called from any thread.</summary>
    public void Disarm()
    {
        _armed = null;
        _wheelSum = 0;
    }

    /// <summary>A key event. <paramref name="modifierHeld"/> = Ctrl, Alt or Win is down.</summary>
    public GestureDecision OnKey(int vk, bool down, bool modifierHeld, bool dragInProgress)
    {
        if (vk != VkTab) return GestureDecision.Pass;
        if (!down)
        {
            _tabDown = false;
            if (!_swallowTabUp) return GestureDecision.Pass;
            _swallowTabUp = false;
            return new GestureDecision(true, false);
        }

        var repeat = _tabDown;
        _tabDown = true;
        if (!Enabled || _armed is null || !dragInProgress || modifierHeld)
            return repeat && _swallowTabUp ? new GestureDecision(true, false) : GestureDecision.Pass;

        _swallowTabUp = true;
        return new GestureDecision(true, !repeat);
    }

    /// <summary>A vertical mouse-wheel event at screen pixel (x, y). <paramref name="nowMs"/> is a monotonic clock in milliseconds.</summary>
    public GestureDecision OnWheel(int x, int y, int delta, long nowMs, bool dragInProgress)
    {
        var armed = _armed;
        if (!Enabled || armed is null || !dragInProgress || !armed.Rect.Contains(x, y)) return GestureDecision.Pass;

        if (Math.Sign(delta) != Math.Sign(_wheelSum)) _wheelSum = 0;
        _wheelSum += delta;
        if (Math.Abs(_wheelSum) < WheelNotch || nowMs - _lastFlipMs < MinFlipInterval.TotalMilliseconds)
            return new GestureDecision(true, false);

        _wheelSum = 0;
        _lastFlipMs = nowMs;
        return new GestureDecision(true, true);
    }
}
