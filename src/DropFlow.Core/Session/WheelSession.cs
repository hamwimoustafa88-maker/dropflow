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

    /// <summary>
    /// "+ Add" was chosen: replace the wheel with the list of items that can be added to it (grouped by <see cref="Wedge.Wheel"/>). The
    /// view takes focus so the list works with the mouse and the keyboard; it reports back through
    /// <see cref="WheelSession.OnAddPickedAsync"/> and <see cref="WheelSession.CloseAddList"/>.
    /// </summary>
    void ShowAddList(WheelKind kind, IReadOnlyList<Wedge> addable);

    /// <summary>Which face is shown and whether the wheel has a back at all (draws the face dots and the "Tab" hint). Optional.</summary>
    void ShowFace(WheelFace face, bool hasBack) { }

    void Hide();
}

/// <summary>Runs an action later. Abstracted so the grace period after a drag ends is testable without real timers.</summary>
public interface IScheduler
{
    IDisposable After(TimeSpan delay, Action action);
}

/// <summary>
/// The life of one wheel: a key tap shows it, the drag entering it supplies the files, a drop on a wedge selects, and releasing
/// elsewhere (or Esc) dismisses it. Dropping on "+ Add" opens a second level listing what else applies; picking there runs the item
/// and pins it to this wheel for this kind of input. No UI types here — everything goes through <see cref="IWheelView"/>.
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
    private readonly Action<WheelPins> _savePins;
    private readonly TimeSpan _grace;
    private IDisposable? _pendingHide;
    private WheelSet _set = WheelSet.Empty;

    /// <param name="savePins">Stores changed pins (the host saves them in the settings, which <paramref name="settings"/> then returns).</param>
    public WheelSession(
        WheelBuilder builder, IWheelView view, IScheduler scheduler,
        Func<AppSettings> settings, Func<string> language, Func<IReadOnlyCollection<string>> actionIds,
        Func<WheelSelection, Task> onSelected, TimeSpan? grace = null, Action<WheelPins>? savePins = null)
    {
        _builder = builder;
        _view = view;
        _scheduler = scheduler;
        _settings = settings;
        _language = language;
        _actionIds = actionIds;
        _onSelected = onSelected;
        _savePins = savePins ?? (_ => { });
        _grace = grace ?? DefaultGrace;
    }

    public bool IsVisible { get; private set; }
    public WheelKind Kind { get; private set; } = WheelKind.Convert;

    /// <summary>The face of the current wheel that is shown.</summary>
    public WheelFace Face { get; private set; } = WheelFace.Front;

    /// <summary>True when the current wheel has more items than fit on its front.</summary>
    public bool HasBack => Faces.HasBack;

    private WheelFaces Faces => WheelFaces.Split(_set.Of(Kind), _settings().WheelFrontMax);
    public IReadOnlyList<string> Files { get; private set; } = [];

    /// <summary>True while the "+ Add" list replaces the wheel.</summary>
    public bool IsChoosingAddition { get; private set; }

    /// <summary>The wedges of the face currently shown.</summary>
    public IReadOnlyList<Wedge> Items => Faces.Of(Face);

    /// <summary>What "+ Add" offers on the wheel currently shown.</summary>
    public IReadOnlyList<Wedge> Addable => _set.AddableFor(Kind);

    /// <summary>A trigger key was tapped during a drag at screen pixel (x, y).</summary>
    public void OnKeyTapped(TriggerKey key, int x, int y)
    {
        if (_settings().Paused) return;
        var kind = KindFor(key);
        if (kind is null) return;

        CancelPendingHide();
        IsChoosingAddition = false;
        if (Kind != kind.Value) Face = WheelFace.Front;
        Kind = kind.Value;
        if (!IsVisible)
        {
            Face = WheelFace.Front;
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
        IsChoosingAddition = false;
        Kind = kind;
        Face = WheelFace.Front;
        Files = files;
        Rebuild();
        IsVisible = true;
        _view.Show(kind, x, y);
        Apply();
    }

    /// <summary>Windows delivered the dragged files to the wheel.</summary>
    public void OnFilesEntered(IReadOnlyList<string> files)
    {
        if (!IsVisible || IsChoosingAddition) return;
        CancelPendingHide();
        Files = files;
        Rebuild();
        Apply();
    }

    /// <summary>The user switched wheels while it is open (a different trigger key or the arrow keys).</summary>
    public void SwitchTo(WheelKind kind)
    {
        if (!IsVisible) return;
        IsChoosingAddition = false;
        Kind = kind;
        Face = WheelFace.Front;
        Apply();
    }

    /// <summary>Turns the wheel over (Tab, Shift+Tab, PageUp/PageDown or one mouse-wheel notch). Nothing happens on a wheel without a back.</summary>
    public void Flip()
    {
        if (!IsVisible || IsChoosingAddition || !HasBack) return;
        Face = Face == WheelFace.Front ? WheelFace.Back : WheelFace.Front;
        Apply();
    }

    /// <summary>The drag was released over wedge <paramref name="index"/> (or it was clicked / chosen with the keyboard).</summary>
    public async Task OnItemDroppedAsync(IReadOnlyList<string> files, int index)
    {
        CancelPendingHide();
        var items = Items;
        if (index < 0 || index >= items.Count || files.Count == 0)
        {
            Dismiss();
            return;
        }

        if (items[index].IsAdd)
        {
            // The drop is over, but the wheel stays: the second level lists everything that can be added.
            Files = files;
            IsChoosingAddition = true;
            _view.ShowAddList(Kind, _set.AddableFor(Kind));
            return;
        }

        var selection = new WheelSelection(Kind, items[index], files);
        Dismiss();
        await _onSelected(selection).ConfigureAwait(false);
    }

    /// <summary>
    /// An item was picked in the "+ Add" list: pin it to this wheel for this kind of input (so it appears directly next time) and run it
    /// on the files right away, like any wedge.
    /// </summary>
    public async Task OnAddPickedAsync(string wedgeId)
    {
        if (!IsVisible || !IsChoosingAddition) return;
        var wedge = _set.AddableFor(Kind).FirstOrDefault(w => w.Id == wedgeId);
        if (wedge is null || Files.Count == 0)
        {
            CloseAddList();
            return;
        }

        _savePins(_settings().PinnedItems.With(Kind, _set.Category, wedge.Id));
        var selection = new WheelSelection(Kind, wedge with { Pinned = true }, Files);
        Dismiss();
        await _onSelected(selection).ConfigureAwait(false);
    }

    /// <summary>Back from the "+ Add" list to the wheel (Esc or the Back button).</summary>
    public void CloseAddList()
    {
        if (!IsChoosingAddition) return;
        IsChoosingAddition = false;
        if (IsVisible) Apply();
    }

    /// <summary>True when wedge <paramref name="index"/> of the current wheel was added by the user and can be removed again.</summary>
    public bool CanUnpin(int index) => index >= 0 && index < Items.Count && Items[index].Pinned;

    /// <summary>"Remove from wheel" on a pinned wedge: forget the pin and redraw the wheel without it.</summary>
    public void Unpin(int index)
    {
        if (!CanUnpin(index)) return;
        _savePins(_settings().PinnedItems.Without(Kind, _set.Category, Items[index].Id));
        Rebuild();
        Apply();
    }

    /// <summary>The mouse button was released. If no drop arrives within the grace period the wheel hides.</summary>
    public void OnDragEnded()
    {
        // While the "+ Add" list is open the drag that opened it is over; its late mouse-up must not close the list.
        if (!IsVisible || IsChoosingAddition) return;
        CancelPendingHide();
        _pendingHide = _scheduler.After(_grace, Dismiss);
    }

    public void OnDragCancelled()
    {
        if (IsChoosingAddition) return;
        Dismiss();
    }

    public void Dismiss()
    {
        CancelPendingHide();
        IsChoosingAddition = false;
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

    private void Rebuild() => _set = _builder.Build(Files, _language(), _actionIds(), _settings().PinnedItems ?? WheelPins.None, _settings().Layouts);

    private void Apply()
    {
        if (!HasBack) Face = WheelFace.Front; // the selection changed and the back is gone
        _view.SetItems(Kind, Items, Files.Count);
        _view.ShowFace(Face, HasBack);
    }

    private void CancelPendingHide()
    {
        _pendingHide?.Dispose();
        _pendingHide = null;
    }
}
