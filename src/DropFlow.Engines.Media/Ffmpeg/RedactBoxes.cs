// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text.Json;

namespace DropFlow.Engines.Media.Ffmpeg;

/// <summary>One area to hide, optionally only between two times (seconds; null = from the start / to the end).</summary>
internal readonly record struct RedactBox(PixelRect Rect, double? Start, double? End);

/// <summary>
/// The <c>boxes</c> option of <c>vid.redact</c>. Two spellings are accepted, so a person can type it and the visual editor can fill it:
/// <list type="bullet">
/// <item><b>Compact text</b>: boxes separated by <c>;</c> or new lines, each <c>x,y,w,h</c> in pixels of the picture as displayed, optionally followed by
///   <c>@start-end</c> (seconds or mm:ss / hh:mm:ss; either side may be empty). Example: <c>100,50,300,200@0:05-0:12; 0,0,160,90</c>.</item>
/// <item><b>JSON</b> (what the editor writes): an array of objects <c>{"x":100,"y":50,"w":300,"h":200,"start":5,"end":"0:12"}</c>; <c>start</c>/<c>end</c>
///   are optional and may be numbers (seconds) or time strings.</item>
/// </list>
/// At most <see cref="MaxBoxes"/> boxes. Anything malformed is a <see cref="FormatException"/> naming the box.
/// </summary>
internal static class RedactBoxes
{
    public const int MaxBoxes = 64;

    public static IReadOnlyList<RedactBox> Parse(string? text)
    {
        var trimmed = (text ?? "").Trim();
        if (trimmed.Length == 0) return [];
        var boxes = trimmed.StartsWith('[') || trimmed.StartsWith('{') ? ParseJson(trimmed) : ParseText(trimmed);
        if (boxes.Count > MaxBoxes) throw new FormatException($"At most {MaxBoxes} areas can be hidden in one run ({boxes.Count} were given).");
        return boxes;
    }

    private static List<RedactBox> ParseText(string text)
    {
        var result = new List<RedactBox>();
        var entries = text.Split([';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var i = 0; i < entries.Length; i++)
        {
            var entry = entries[i];
            var at = entry.IndexOf('@');
            var rectText = at < 0 ? entry : entry[..at];
            var parts = rectText.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length != 4) throw new FormatException($"Box {i + 1} (\"{entry}\") must be x,y,width,height.");
            var numbers = new int[4];
            for (var k = 0; k < 4; k++)
                if (!int.TryParse(parts[k], NumberStyles.Integer, CultureInfo.InvariantCulture, out numbers[k]))
                    throw new FormatException($"Box {i + 1} (\"{entry}\") must be four whole numbers: x,y,width,height.");

            double? start = null, end = null;
            if (at >= 0)
            {
                var range = entry[(at + 1)..];
                var dash = range.IndexOf('-');
                if (dash < 0) throw new FormatException($"Box {i + 1}: the time range must look like start-end, for example 0:05-0:12.");
                start = Time(range[..dash], i);
                end = Time(range[(dash + 1)..], i);
            }
            result.Add(Validate(new RedactBox(new PixelRect(numbers[0], numbers[1], numbers[2], numbers[3]), start, end), i));
        }
        return result;
    }

    private static List<RedactBox> ParseJson(string json)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new FormatException("The list of areas is not valid JSON: " + ex.Message);
        }

        using (doc)
        {
            var items = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray().ToList() : [doc.RootElement];
            var result = new List<RedactBox>();
            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (item.ValueKind != JsonValueKind.Object) throw new FormatException($"Box {i + 1} must be an object with x, y, w and h.");
                var rect = new PixelRect(Int(item, "x", i), Int(item, "y", i), Int(item, "w", i), Int(item, "h", i));
                result.Add(Validate(new RedactBox(rect, JsonTime(item, "start", i), JsonTime(item, "end", i)), i));
            }
            return result;
        }
    }

    private static int Int(JsonElement item, string name, int index)
    {
        if (item.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetDouble(out var d) && double.IsFinite(d))
            return (int)Math.Round(d);
        throw new FormatException($"Box {index + 1} needs a number for \"{name}\".");
    }

    private static double? JsonTime(JsonElement item, string name, int index)
    {
        if (!item.TryGetProperty(name, out var p) || p.ValueKind == JsonValueKind.Null) return null;
        if (p.ValueKind == JsonValueKind.Number && p.TryGetDouble(out var seconds) && double.IsFinite(seconds) && seconds >= 0) return seconds;
        if (p.ValueKind == JsonValueKind.String) return Time(p.GetString() ?? "", index);
        throw new FormatException($"Box {index + 1}: \"{name}\" must be a time in seconds or mm:ss.");
    }

    private static double? Time(string text, int index)
    {
        text = text.Trim();
        if (text.Length == 0) return null;
        return TimeSpec.TryParseSeconds(text, out var seconds)
            ? seconds
            : throw new FormatException($"Box {index + 1}: '{text}' is not a time. Use seconds (90.5) or mm:ss.");
    }

    private static RedactBox Validate(RedactBox box, int index)
    {
        var r = box.Rect;
        if (r.X < 0 || r.Y < 0) throw new FormatException($"Box {index + 1}: x and y cannot be negative.");
        if (r.Width <= 0 || r.Height <= 0) throw new FormatException($"Box {index + 1}: the width and height must be greater than zero.");
        if (box.Start is { } s && box.End is { } e && e <= s) throw new FormatException($"Box {index + 1}: the end time must be after the start time.");
        return box;
    }
}
