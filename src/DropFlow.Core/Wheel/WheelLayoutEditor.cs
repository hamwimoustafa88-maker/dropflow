using DropFlow.Core.Settings;

namespace DropFlow.Core.Wheel;

/// <summary>
/// The pure operations behind Settings → Arrange the wheels. <paramref name="items"/> is everything that can sit on the wheel
/// (<see cref="WheelBuilder.ArrangeableFor"/>); each operation returns the new layout, which always lists the full visible order so the result
/// does not depend on catalog order later.
/// </summary>
public static class WheelLayoutEditor
{
    /// <summary>The items shown on the wheel, in order, and the hidden ones.</summary>
    public static (IReadOnlyList<Wedge> Visible, IReadOnlyList<Wedge> Hidden) View(IReadOnlyList<Wedge> items, WheelLayout layout)
    {
        var visible = layout.Apply(items);
        var hidden = items.Where(w => layout.Hidden.Contains(w.Id)).ToList();
        return (visible, hidden);
    }

    /// <summary>Moves a visible item by <paramref name="delta"/> places (clamped at the ends).</summary>
    public static WheelLayout Move(IReadOnlyList<Wedge> items, WheelLayout layout, string id, int delta)
    {
        var order = layout.Apply(items).Select(w => w.Id).ToList();
        var from = order.IndexOf(id);
        if (from < 0) return layout;
        return MoveTo(items, layout, id, Math.Clamp(from + delta, 0, order.Count - 1));
    }

    /// <summary>Puts a visible item at <paramref name="index"/> in the order (drag and drop).</summary>
    public static WheelLayout MoveTo(IReadOnlyList<Wedge> items, WheelLayout layout, string id, int index)
    {
        var order = layout.Apply(items).Select(w => w.Id).ToList();
        if (!order.Remove(id)) return layout;
        order.Insert(Math.Clamp(index, 0, order.Count), id);
        return new WheelLayout(order, layout.Hidden);
    }

    /// <summary>Hides a visible item, or shows a hidden one again (at the end of the wheel).</summary>
    public static WheelLayout ToggleHidden(IReadOnlyList<Wedge> items, WheelLayout layout, string id)
    {
        if (items.All(w => w.Id != id)) return layout;
        var order = layout.Apply(items).Select(w => w.Id).ToList();
        if (layout.Hidden.Contains(id))
            return new WheelLayout([.. order, id], layout.Hidden.Where(h => h != id).ToList());
        return new WheelLayout(order.Where(o => o != id).ToList(), [.. layout.Hidden, id]);
    }
}
