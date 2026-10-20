using DropFlow.Core.Settings;
using DropFlow.Core.Wheel;

namespace DropFlow.Core.Session;

/// <summary>What the user picked: which wheel, which wedge, on which files.</summary>
public sealed record WheelSelection(WheelKind Kind, Wedge Wedge, IReadOnlyList<string> Files);

/// <summary>The on-screen wheel as the session sees it. Implemented by the WPF overlay; faked in tests.</summary>
public interface IWheelView
{
    /// <summary>Show an (initially empty) wheel centred at screen pixel (x, y).</summary>
    void Show(WheelKind kind, int x, int y);

    /// <summary>Fill the wheel with wedges for the given files (may be empty while waiting for a drag to enter).</summary>
    void SetItems(WheelKind kind, IReadOnlyList<Wedge> wedges, int fileCount);

    void Hide();
}

/// <summary>Runs an action later. Abstracted so the grace period after a drag ends is testable without real timers.</summary>
public interface IScheduler
{
    IDisposable After(TimeSpan delay, Action action);
}

/// <summary>
/// The life of one wheel: a key tap shows it, the drag entering it supplies the files, a drop on a wedge selects, and releasing
/// elsewhere (or Esc) dismisses it. No UI types here — everything goes through <see cref="IWheelView"/>.
/// </summary>
public sealed class WheelSession
{
    /// <summary>Time to wait after the mouse button is released before hiding: Windows delivers the drop a moment later.</summary>
    public static readonly TimeSpan DefaultGrace = TimeSpan.FromMilliseconds(450);

    private readonly WheelBuilder _builder;
    private readonly IWheelView _view;
    private readonly IScheduler _scheduler;
    private readonly Func<AppSettings> _settings;
    private readonly Func<string> _language;
    private readonly Func<IReadOnlyCollection<string>> _actionIds;
    private readonly Func<WheelSelection, Task> _onSelected;
    private readonly TimeSpan _grace;
    private IDisposable? _pendingHide;
    private WheelSet _set = WheelSet.Empty;

    public WheelSession(
        WheelBuilder builder, IWheelView view, IScheduler scheduler,
        Func<AppSettings> settings, Func<string> language, Func<IReadOnlyCollection<string>> actionIds,
        Func<WheelSelection, Task> onSelected, TimeSpan? grace = null)
    {
        _builder = builder;
        _view = view;
        _scheduler = scheduler;
        _settings = settings;
        _language = language;
        _actionIds = actionIds;
        _onSelected = onSelected;
        _grace = grace ?? DefaultGrace;
    }

    public bool IsVisible { get; private set; }
    public WheelKind Kind { get; private set; } = WheelKind.Convert;
    public IReadOnlyList<string> Files { get; private set; } = [];

    /// <summary>A trigger key was tapped during a drag at screen pixel (x, y).</summary>
    public void OnKeyTapped(TriggerKey key, int x, int y)
    {
        if (_settings().Paused) return;
        var kind = KindFor(key);
        if (kind is null) return;

        CancelPendingHide();
        Kind = kind.Value;
        if (!IsVisible)
        {
            IsVisible = true;
            Files = [];
            _set = WheelSet.Empty;
            _view.Show(Kind, x, y);
        }
        Apply();
    }

    /// <summary>Open the wheel for files that are already known (command line, global hotkey, Explorer selection).</summary>
    public void ShowFor(IReadOnlyList<string> files, WheelKind kind, int x, int y)
    {
        CancelPendingHide();
        Kind = kind;
        Files = files;
        _set = _builder.Build(files, _language(), _actionIds());
        IsVisible = true;
        _view.Show(kind, x, y);
        Apply();
    }

    /// <summary>Windows delivered the dragged files to the wheel.</summary>
    public void OnFilesEntered(IReadOnlyList<string> files)
    {
        if (!IsVisible) return;
        CancelPendingHide();
        Files = files;
        _set = _builder.Build(files, _language(), _actionIds());
        Apply();
    }

    /// <summary>The user switched wheels while it is open (a different trigger key or the arrow keys).</summary>
    public void SwitchTo(WheelKind kind)
    {
        if (!IsVisible) return;
        Kind = kind;
        Apply();
    }

    /// <summary>The drag was released over wedge <paramref name="index"/>.</summary>
    public async Task OnItemDroppedAsync(IReadOnlyList<string> files, int index)
    {
        CancelPendingHide();
        var items = _set.Of(Kind);
        if (index < 0 || index >= items.Count || files.Count == 0)
        {
            Dismiss();
            return;
        }

        var selection = new WheelSelection(Kind, items[index], files);
        Dismiss();
        await _onSelected(selection).ConfigureAwait(false);
    }

    /// <summary>The mouse button was released. If no drop arrives within the grace period the wheel hides.</summary>
    public void OnDragEnded()
    {
        if (!IsVisible) return;
        CancelPendingHide();
        _pendingHide = _scheduler.After(_grace, Dismiss);
    }

    public void OnDragCancelled() => Dismiss();

    public void Dismiss()
    {
        CancelPendingHide();
        if (!IsVisible) return;
        IsVisible = false;
        _view.Hide();
    }

    private WheelKind? KindFor(TriggerKey key)
    {
        var keys = _settings().Keys;
        if (key == keys.Convert) return WheelKind.Convert;
        if (key == keys.Tools) return WheelKind.Tools;
        if (key == keys.Actions) return WheelKind.Actions;
        return null;
    }

    private void Apply() => _view.SetItems(Kind, _set.Of(Kind), Files.Count);

    private void CancelPendingHide()
    {
        _pendingHide?.Dispose();
        _pendingHide = null;
    }
}
