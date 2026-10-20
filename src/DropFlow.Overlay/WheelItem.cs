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

/// <summary>Strings the overlay needs from the application (so this library has no resource files of its own).</summary>
public interface ILocalizer
{
    /// <summary>Two-letter UI language ("ar" or "en").</summary>
    string Language { get; }

    bool IsRightToLeft => Language == "ar";

    /// <summary>Text for a key, or the key itself when missing (so a missing translation is visible, not a crash).</summary>
    string this[string key] { get; }

    string Format(string key, params object[] args) => string.Format(System.Globalization.CultureInfo.CurrentUICulture, this[key], args);
}
