namespace DropFlow.Hooks;

public enum ModifierKey { Shift, Ctrl, Alt }

public readonly record struct ScreenPoint(int X, int Y);

/// <summary>
/// Pure state machine that turns raw mouse/keyboard input into "a drag is in progress" and "a modifier was tapped
/// during the drag". It has no Win32 dependency so it can be unit-tested; <see cref="LowLevelInputHook"/> feeds it.
/// Every method is O(1) and non-blocking because it is called from inside a low-level hook callback.
/// <para>The rules:</para>
/// <list type="number">
/// <item>A drag is the left button held and moved past the system drag distance (SM_CXDRAG / SM_CYDRAG).</item>
/// <item>A <b>tap</b> is a modifier pressed on its own <b>during</b> the drag and released again; a key already held when the button went down
///   (including one whose key-down the hook never saw, passed in as <c>heldKeys</c>), a key pressed before the drag distance was reached, a chord
///   or a second modifier do not count.</item>
/// <item>When <see cref="ConfirmFileDrag"/> is set, a tap only counts if it says files are really being dragged (otherwise <see cref="TapRejected"/>
///   fires, so Shift+drag text selection and window moves never open the wheel). If the check itself throws, the tap counts and
///   <see cref="ConfirmFailed"/> reports why (the escape hatch is to leave the check unset).</item>
/// <item><see cref="OnPoll"/> (called on a timer while the button is down) treats the real button state as the truth, so a button-up the hook
///   missed still ends the drag, and cursor movement that bypassed the hook (remote desktop, pens) still starts one.</item>
/// </list>
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

    /// <summary>True from the left button going down until it is seen up again (by the hook or by <see cref="OnPoll"/>).</summary>
    public bool IsButtonDown => _leftDown;

    /// <summary>
    /// Asked when a tap completes: true when files are really being dragged. Null means every tap counts. Called on the hook thread, so it must be fast.
    /// </summary>
    public Func<bool>? ConfirmFileDrag { get; set; }

    public event Action<ScreenPoint>? DragStarted;
    public event Action<ScreenPoint>? DragEnded;
    public event Action? DragCancelled;
    public event Action<ModifierKey, ScreenPoint>? KeyTapped;

    /// <summary>A tap happened during a drag that <see cref="ConfirmFileDrag"/> said is not a file drag.</summary>
    public event Action<ModifierKey>? TapRejected;

    /// <summary><see cref="ConfirmFileDrag"/> threw; the tap was let through.</summary>
    public event Action<Exception>? ConfirmFailed;

    /// <param name="isKeyDown">
    /// The physical key state at the moment of the press (<c>GetAsyncKeyState</c>). When given, the remembered keys are brought in line with it:
    /// a modifier held before the hook could see its key-down counts as held (so its auto-repeat is not a new press), and a key whose release
    /// the hook missed no longer blocks taps.
    /// </param>
    public void OnMouseDown(int x, int y, Func<int, bool>? isKeyDown = null)
    {
        _leftDown = true;
        _origin = _last = new ScreenPoint(x, y);
        IsDragging = false;
        _pendingTap = null;
        if (isKeyDown is null) return;
        foreach (var vk in _keysDown.Concat(ModifierKeys).Distinct().ToList())
        {
            if (isKeyDown(vk)) _keysDown.Add(vk);
            else _keysDown.Remove(vk);
        }
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

    /// <summary>
    /// The real state, read while the button is down: <paramref name="leftButtonDown"/> from <c>GetAsyncKeyState</c> and the cursor from
    /// <c>GetCursorPos</c>. A button found up ends the drag (the hook missed the release); a cursor that moved counts as movement.
    /// </summary>
    public void OnPoll(bool leftButtonDown, int x, int y)
    {
        if (!_leftDown) return;
        if (!leftButtonDown)
        {
            OnMouseUp(x, y);
            return;
        }
        if (x != _last.X || y != _last.Y) OnMouseMove(x, y);
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
        if (_pendingTap is not { } tapped || ToModifier(vk) != tapped) return;
        _pendingTap = null;

        if (ConfirmFileDrag is { } confirm)
        {
            bool isFileDrag;
            try
            {
                isFileDrag = confirm();
            }
            catch (Exception ex)
            {
                ConfirmFailed?.Invoke(ex);
                isFileDrag = true;
            }
            if (!isFileDrag)
            {
                TapRejected?.Invoke(tapped);
                return;
            }
        }
        KeyTapped?.Invoke(tapped, _last);
    }

    /// <summary>
    /// The left/right modifier keys <see cref="OnMouseDown"/> checks. Only the sided codes: they are what a low-level keyboard hook reports, so a
    /// generic VK_SHIFT entry would never see its key-up and would block every later tap.
    /// </summary>
    public static IReadOnlyList<int> ModifierKeys { get; } = [0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5];

    private static ModifierKey? ToModifier(int vk) => vk switch
    {
        0x10 or 0xA0 or 0xA1 => ModifierKey.Shift,
        0x11 or 0xA2 or 0xA3 => ModifierKey.Ctrl,
        0x12 or 0xA4 or 0xA5 => ModifierKey.Alt,
        _ => null,
    };
}
