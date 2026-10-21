namespace DropFlow.Overlay;

public enum WheelItemState
{
    Ready,
    /// <summary>Works, but the engine it needs is not installed yet (drawn with a download mark).</summary>
    NeedsEngine,
}

public enum WheelItemRole
{
    Normal,
    /// <summary>Added by the user with "+ Add"; can be removed again (right-click, the Menu key or Delete).</summary>
    Pinned,
    /// <summary>The "+ Add" slot that opens the list of everything else that applies.</summary>
    Add,
}

/// <summary>One wedge as the control draws it.</summary>
public sealed record WheelItem(
    string Id, string Label, WheelItemState State = WheelItemState.Ready, bool NeedsOptions = false, WheelItemRole Role = WheelItemRole.Normal);

/// <summary>A titled group of rows in the "+ Add" list (Convert, Tools, Actions).</summary>
public sealed record WheelItemGroup(string Title, IReadOnlyList<WheelItem> Items);

public sealed class WheelItemEventArgs(int index, WheelItem item) : EventArgs
{
    public int Index { get; } = index;
    public WheelItem Item { get; } = item;
}
