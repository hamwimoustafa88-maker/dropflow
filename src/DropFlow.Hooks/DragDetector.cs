namespace DropFlow.Hooks;

public enum ModifierKey { Shift, Ctrl, Alt }

public readonly record struct ScreenPoint(int X, int Y);

/// <summary>
/// Pure state machine that turns raw mouse/keyboard input into "a drag is in progress" and "a modifier was tapped
/// during the drag". It has no Win32 dependency so it can be unit-tested; <see cref="LowLevelInputHook"/> feeds it.
/// Every method is O(1) and non-blocking because it is called from inside a low-level hook callback.
/// </summary>
public sealed class DragDetector
{
    public const int VkEscape = 0x1B;

    private readonly int _thresholdX;
    private readonly int _thresholdY;
    private readonly HashSet<int> _keysDown = [];

    private bool _leftDown;
    private ScreenPoint _origin;
    private ScreenPoint _last;
    private ModifierKey? _pendingTap;

    /// <param name="thresholdX">Horizontal drag distance in pixels (SM_CXDRAG).</param>
    /// <param name="thresholdY">Vertical drag distance in pixels (SM_CYDRAG).</param>
    public DragDetector(int thresholdX, int thresholdY)
    {
        _thresholdX = thresholdX;
        _thresholdY = thresholdY;
    }

    public bool IsDragging { get; private set; }

    public event Action<ScreenPoint>? DragStarted;
    public event Action<ScreenPoint>? DragEnded;
    public event Action? DragCancelled;
    public event Action<ModifierKey, ScreenPoint>? KeyTapped;

    public void OnMouseDown(int x, int y)
    {
        _leftDown = true;
        _origin = _last = new ScreenPoint(x, y);
        IsDragging = false;
        _pendingTap = null;
    }

    public void OnMouseMove(int x, int y)
    {
        _last = new ScreenPoint(x, y);
        if (!_leftDown || IsDragging) return;
        if (Math.Abs(x - _origin.X) > _thresholdX || Math.Abs(y - _origin.Y) > _thresholdY)
        {
            IsDragging = true;
            DragStarted?.Invoke(_last);
        }
    }

    public void OnMouseUp(int x, int y)
    {
        _last = new ScreenPoint(x, y);
        var wasDragging = IsDragging;
        _leftDown = false;
        IsDragging = false;
        _pendingTap = null;
        if (wasDragging) DragEnded?.Invoke(_last);
    }

    public void OnKeyDown(int vk)
    {
        if (!_keysDown.Add(vk)) return; // auto-repeat of a key that is already down

        if (vk == VkEscape && IsDragging)
        {
            IsDragging = false;
            _leftDown = false;
            _pendingTap = null;
            DragCancelled?.Invoke();
            return;
        }

        // A tap is a modifier pressed on its own while dragging. Any other key (or a second modifier) makes it a chord.
        _pendingTap = IsDragging && _keysDown.Count == 1 ? ToModifier(vk) : null;
    }

    public void OnKeyUp(int vk)
    {
        _keysDown.Remove(vk);
        if (_pendingTap is { } tapped && ToModifier(vk) == tapped)
        {
            _pendingTap = null;
            KeyTapped?.Invoke(tapped, _last);
        }
    }

    private static ModifierKey? ToModifier(int vk) => vk switch
    {
        0x10 or 0xA0 or 0xA1 => ModifierKey.Shift,
        0x11 or 0xA2 or 0xA3 => ModifierKey.Ctrl,
        0x12 or 0xA4 or 0xA5 => ModifierKey.Alt,
        _ => null,
    };
}
