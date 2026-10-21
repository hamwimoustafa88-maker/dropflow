using System.Text.Json;
using System.Text.Json.Serialization;
using DropFlow.Core.Wheel;

namespace DropFlow.Core.Settings;

/// <summary>One item the user added to a wheel: which wheel, for which kind of input, and the wedge id ("img.ocr", "convert:pdf").</summary>
public readonly record struct WheelPin(WheelKind Wheel, string Category, string Id);

/// <summary>
/// Items the user added to a wheel with "+ Add": wheel → input category ("image", "pdf"… or <see cref="WheelBuilder.OtherCategory"/>)
/// → wedge ids, oldest first. Immutable: every change returns a new value. Stored in settings.json as
/// <c>"PinnedItems": { "Convert": { "image": ["img.ocr"] } }</c>.
/// </summary>
[JsonConverter(typeof(WheelPinsJsonConverter))]
public sealed class WheelPins
{
    private readonly IReadOnlyDictionary<WheelKind, IReadOnlyDictionary<string, IReadOnlyList<string>>> _map;

    private WheelPins(IReadOnlyDictionary<WheelKind, IReadOnlyDictionary<string, IReadOnlyList<string>>> map) => _map = map;

    public static WheelPins None { get; } = new(new Dictionary<WheelKind, IReadOnlyDictionary<string, IReadOnlyList<string>>>());

    public bool IsEmpty => !All.Any();

    /// <summary>Every pin, wheel by wheel (Convert, Tools, Actions), categories alphabetically, items in the order they were added.</summary>
    public IEnumerable<WheelPin> All =>
        from kind in Enum.GetValues<WheelKind>()
        where _map.ContainsKey(kind)
        from category in _map[kind].Keys.Order(StringComparer.Ordinal)
        from id in _map[kind][category]
        select new WheelPin(kind, category, id);

    public IReadOnlyList<string> For(WheelKind wheel, string category) =>
        _map.TryGetValue(wheel, out var categories) && categories.TryGetValue(category, out var ids) ? ids : [];

    public bool Contains(WheelKind wheel, string category, string id) => For(wheel, category).Contains(id);

    /// <summary>Adds an item at the end (no-op when it is already there).</summary>
    public WheelPins With(WheelKind wheel, string category, string id) =>
        Contains(wheel, category, id) ? this : Replace(wheel, category, [.. For(wheel, category), id]);

    public WheelPins Without(WheelKind wheel, string category, string id) =>
        Contains(wheel, category, id) ? Replace(wheel, category, For(wheel, category).Where(x => x != id).ToList()) : this;

    private WheelPins Replace(WheelKind wheel, string category, IReadOnlyList<string> ids)
    {
        var map = _map.ToDictionary(p => p.Key, p => p.Value);
        var categories = map.TryGetValue(wheel, out var existing)
            ? existing.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal)
            : new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        if (ids.Count == 0) categories.Remove(category); else categories[category] = ids;
        if (categories.Count == 0) map.Remove(wheel); else map[wheel] = categories;
        return new WheelPins(map);
    }

    /// <summary>Builds pins from the stored shape, skipping unknown wheels, empty ids and duplicates (a hand-edited file never crashes).</summary>
    public static WheelPins FromMap(IReadOnlyDictionary<string, Dictionary<string, List<string>?>?>? raw)
    {
        var pins = None;
        if (raw is null) return pins;
        foreach (var (wheelName, categories) in raw)
        {
            if (categories is null || !Enum.TryParse<WheelKind>(wheelName, ignoreCase: true, out var wheel) || !Enum.IsDefined(wheel)) continue;
            foreach (var (category, ids) in categories)
            {
                if (string.IsNullOrWhiteSpace(category) || ids is null) continue;
                foreach (var id in ids.Where(i => !string.IsNullOrWhiteSpace(i))) pins = pins.With(wheel, category, id);
            }
        }
        return pins;
    }

    public Dictionary<string, Dictionary<string, List<string>>> ToMap() =>
        _map.OrderBy(p => p.Key).ToDictionary(
            p => p.Key.ToString(),
            p => p.Value.OrderBy(c => c.Key, StringComparer.Ordinal).ToDictionary(c => c.Key, c => c.Value.ToList()));
}

internal sealed class WheelPinsJsonConverter : JsonConverter<WheelPins>
{
    public override WheelPins Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        WheelPins.FromMap(JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, List<string>?>?>>(ref reader));

    public override void Write(Utf8JsonWriter writer, WheelPins value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, value.ToMap());
}
