using DropFlow.Core.Operations;

namespace DropFlow.Core.Routing;

/// <summary>A chain of conversions: the formats to convert through, in order; the last one is the target ("pdf", "png").</summary>
public sealed record Route(IReadOnlyList<string> Steps, double Cost)
{
    public string Target => Steps[^1];
}

/// <summary>
/// Finds the cheapest chain of direct conversions from one format to another (Dijkstra over formats, at most <see cref="MaxSteps"/> steps).
/// An edge a → b exists when a converter runs "convert:b" on a with no required option, and the engine it needs is installed (a route never
/// offers a download). Costs: 1 per step, plus the engine's weight (LibreOffice is slow and heavy), plus a penalty for passing through a
/// lossy format (a JPEG in the middle loses quality) and for turning text into a picture before the end. Archives are never a step.
/// Ties are broken by the format name, so the same route is chosen every time.
/// </summary>
public sealed class RoutePlanner(Func<string, string, IConverter?> find, IEngineProvider engines, IReadOnlyCollection<string> formats)
{
    public const int MaxSteps = 3;

    private static readonly HashSet<string> Lossy = new(StringComparer.Ordinal) { "jpg", "webp", "avif", "heic", "gif", "mp3", "m4a", "aac", "ogg", "opus", "wma" };
    private static readonly HashSet<string> Raster = new(StringComparer.Ordinal) { "jpg", "png", "webp", "avif", "heic", "gif", "bmp", "tiff", "ico" };
    private static readonly HashSet<string> Text = new(StringComparer.Ordinal) { "txt", "md", "html", "docx", "odt", "rtf", "epub", "csv", "tsv", "json" };
    private static readonly HashSet<string> NeverThrough = new(StringComparer.Ordinal) { "zip", "tar", "gz", "tgz", "7z", "rar", "pdfa" };

    private static double EngineWeight(EngineId engine) => engine switch
    {
        EngineId.None => 0,
        EngineId.Qpdf => 0.2,
        EngineId.Ffmpeg or EngineId.Ffprobe => 0.5,
        EngineId.LibreOffice => 3,
        _ => 1,
    };

    /// <summary>The cost of one direct step, or null when the step cannot run unattended right now.</summary>
    public double? StepCost(string from, string to)
    {
        if (from == to) return null;
        var op = Operation.Convert(to);
        var converter = find(op, from);
        if (converter is null) return null;
        if (converter.OptionsFor(op).Any(o => o.Required && o.Default is null)) return null;
        var engine = converter.EngineFor(op);
        if (engine != EngineId.None && engines.Locate(engine) is null) return null;
        return 1 + EngineWeight(engine);
    }

    /// <summary>The cheapest route of 1 to <see cref="MaxSteps"/> steps, or null.</summary>
    public Route? Plan(string from, string to)
    {
        if (from == to) return null;
        // state = (format, steps taken); best known cost and the route to it
        var best = new Dictionary<(string, int), (double Cost, List<string> Path)> { [(from, 0)] = (0, []) };
        var open = new PriorityQueue<(string Format, int Depth), (double, string)>();
        open.Enqueue((from, 0), (0, ""));
        Route? found = null;

        while (open.TryDequeue(out var state, out var priority))
        {
            var (cost, path) = best[state];
            if (priority.Item1 > cost) continue; // stale entry
            if (state.Format == to) { found ??= new Route(path, cost); break; }
            if (state.Depth == MaxSteps) continue;

            foreach (var next in formats.Order(StringComparer.Ordinal))
            {
                if (next == from || path.Contains(next)) continue;
                if (next != to && NeverThrough.Contains(next)) continue;
                if (StepCost(state.Format, next) is not { } step) continue;
                var extra = 0.0;
                if (next != to && Lossy.Contains(next)) extra += 1.5;                                  // quality lost in the middle
                if (next != to && Raster.Contains(next) && Text.Contains(from) && !Raster.Contains(to)) extra += 2; // text flattened to pixels
                var total = cost + step + extra;
                var key = (next, state.Depth + 1);
                if (best.TryGetValue(key, out var known) && known.Cost <= total) continue;
                best[key] = (total, [.. path, next]);
                open.Enqueue(key, (total, string.Join(">", path.Append(next))));
            }
        }
        return found;
    }
}
