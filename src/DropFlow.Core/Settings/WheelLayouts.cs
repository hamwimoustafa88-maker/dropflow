using System.Text.Json;
using System.Text.Json.Serialization;
using DropFlow.Core.Wheel;

namespace DropFlow.Core.Settings;

/// <summary>
/// How the user arranged one wheel for one kind of input: the order of its items (ids; items not listed keep their catalog order after the
/// listed ones, so a tool added by an update still appears) and the items hidden from it (they move to the "+ Add" list). The front of the
/// wheel is simply the first <see cref="AppSettings.WheelFrontMax"/> items in this order, so "move to the back" is a move past that line.
/// </summary>
public sealed record WheelLayout(IReadOnlyList<string> Order, IReadOnlyList<string> Hidden)
{
    public static readonly WheelLayout Default = new([], []);

    public bool IsDefault => Order.Count == 0 && Hidden.Count == 0;

    /// <summary>Applies the layout: hidden items removed, the rest in the user's order (unlisted items after, in their own order).</summary>
    public IReadOnlyList<Wedge> Apply(IReadOnlyList<Wedge> items)
    {
        if (IsDefault) return items;
        var hidden = Hidden.ToHashSet(StringComparer.Ordinal);
        var rank = Order.Select((id, i) => (id, i)).GroupBy(p => p.id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().i, StringComparer.Ordinal);
        return items
            .Where(w => !hidden.Contains(w.Id))
            .Select((w, i) => (w, key: rank.TryGetValue(w.Id, out var r) ? r : int.MaxValue / 2 + i))
            .OrderBy(p => p.key)
            .Select(p => p.w)
            .ToList();
    }
}

/// <summary>Every wheel layout, per wheel and input category ("image", "pdf"… or <see cref="WheelBuilder.OtherCategory"/>). Immutable.</summary>
[JsonConverter(typeof(WheelLayoutsJsonConverter))]
public sealed class WheelLayouts
{
    private readonly IReadOnlyDictionary<WheelKind, IReadOnlyDictionary<string, WheelLayout>> _map;

    private WheelLayouts(IReadOnlyDictionary<WheelKind, IReadOnlyDictionary<string, WheelLayout>> map) => _map = map;

    public static WheelLayouts None { get; } = new(new Dictionary<WheelKind, IReadOnlyDictionary<string, WheelLayout>>());

    public WheelLayout For(WheelKind wheel, string category) =>
        _map.TryGetValue(wheel, out var c) && c.TryGetValue(category, out var l) ? l : WheelLayout.Default;

    public WheelLayouts With(WheelKind wheel, string category, WheelLayout layout)
    {
        var map = _map.ToDictionary(p => p.Key, p => p.Value);
        var categories = map.TryGetValue(wheel, out var existing)
            ? existing.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal)
            : new Dictionary<string, WheelLayout>(StringComparer.Ordinal);
        if (layout.IsDefault) categories.Remove(category); else categories[category] = layout;
        if (categories.Count == 0) map.Remove(wheel); else map[wheel] = categories;
        return new WheelLayouts(map);
    }

    internal Dictionary<string, Dictionary<string, LayoutDto>> ToMap() =>
        _map.ToDictionary(p => p.Key.ToString(), p => p.Value.ToDictionary(c => c.Key, c => new LayoutDto([.. c.Value.Order], [.. c.Value.Hidden])));

    /// <summary>Reads a hand-editable map; unknown wheels, empty ids and duplicates are dropped.</summary>
    internal static WheelLayouts FromMap(Dictionary<string, Dictionary<string, LayoutDto?>?>? map)
    {
        var result = None;
        foreach (var (wheelName, categories) in map ?? [])
        {
            if (!Enum.TryParse<WheelKind>(wheelName, ignoreCase: true, out var wheel) || categories is null) continue;
            foreach (var (category, dto) in categories)
            {
                if (string.IsNullOrWhiteSpace(category) || dto is null) continue;
                static List<string> Clean(List<string?>? ids) => (ids ?? []).Where(i => !string.IsNullOrWhiteSpace(i)).Select(i => i!).Distinct(StringComparer.Ordinal).ToList();
                result = result.With(wheel, category, new WheelLayout(Clean(dto.Order), Clean(dto.Hidden)));
            }
        }
        return result;
    }

    internal sealed record LayoutDto(List<string?>? Order, List<string?>? Hidden);
}

internal sealed class WheelLayoutsJsonConverter : JsonConverter<WheelLayouts>
{
    public override WheelLayouts Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        WheelLayouts.FromMap(JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, WheelLayouts.LayoutDto?>?>>(ref reader));

    public override void Write(Utf8JsonWriter writer, WheelLayouts value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, value.ToMap());
}
