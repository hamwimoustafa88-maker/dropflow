namespace DropFlow.Overlay;

public enum WheelItemState
{
    Ready,
    /// <summary>Works, but the engine it needs is not installed yet (drawn with a download mark).</summary>
    NeedsEngine,
}

/// <summary>One wedge as the control draws it.</summary>
public sealed record WheelItem(string Id, string Label, WheelItemState State = WheelItemState.Ready, bool NeedsOptions = false);

public sealed class WheelItemEventArgs(int index, WheelItem item) : EventArgs
{
    public int Index { get; } = index;
    public WheelItem Item { get; } = item;
}
