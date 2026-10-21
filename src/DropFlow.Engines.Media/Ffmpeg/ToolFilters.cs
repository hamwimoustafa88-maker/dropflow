using System.Text.RegularExpressions;

namespace DropFlow.Engines.Media.Ffmpeg;

/// <summary>A rectangle inside a video frame, in pixels.</summary>
internal readonly record struct PixelRect(int X, int Y, int Width, int Height);

/// <summary>Pure builders for the filter strings the video and audio tools use. Every value is escaped here, never by callers.</summary>
internal static partial class ToolFilters
{
    // ------------------------------------------------------------ subtitles

    /// <summary>
    /// The <c>subtitles</c> filter. The file name and fonts directory are escaped for both FFmpeg levels (see
    /// <see cref="FilterEscaping"/>), so Windows paths with drive colons, backslashes, commas, quotes, brackets and Arabic
    /// all survive. <paramref name="forceStyle"/> is an ASS style override such as <c>FontName=Arial,FontSize=24</c>.
    /// </summary>
    public static string Subtitles(string subtitleFile, string? fontsDirectory = null, string? forceStyle = null)
    {
        var f = "subtitles=filename=" + FilterEscaping.PathValue(subtitleFile);
        if (!string.IsNullOrEmpty(fontsDirectory)) f += ":fontsdir=" + FilterEscaping.PathValue(fontsDirectory);
        if (!string.IsNullOrEmpty(forceStyle)) f += ":force_style=" + FilterEscaping.Value(forceStyle);
        return f;
    }

    /// <summary>ASS style overrides for burned-in SRT/VTT text: readable white text with a black outline.</summary>
    public static string BurnStyle(int fontSize, string position, string fontName = "Arial")
    {
        var alignment = position switch { "top" => 8, "middle" => 5, _ => 2 }; // ASS numpad alignment
        return $"FontName={fontName},FontSize={fontSize},PrimaryColour=&H00FFFFFF,OutlineColour=&H00000000,BorderStyle=1,Outline=2,Shadow=0,Alignment={alignment},MarginV=30";
    }

    // ------------------------------------------------------------ speed

    public static string SetPts(double speed) => $"setpts=PTS/{TimeSpec.Num(speed, 6)}";

    /// <summary>
    /// <c>atempo</c> chain for a speed factor. Each stage stays inside 0.5..2 (older FFmpeg builds reject anything else), so 4x is
    /// <c>atempo=2,atempo=2</c> and 0.25x is <c>atempo=0.5,atempo=0.5</c>. Pitch is preserved. Returns null for 1x.
    /// </summary>
    public static string? AtempoChain(double speed)
    {
        if (Math.Abs(speed - 1) < 1e-9) return null;
        var stages = new List<string>();
        var s = speed;
        while (s > 2) { stages.Add("atempo=2"); s /= 2; }
        while (s < 0.5) { stages.Add("atempo=0.5"); s /= 0.5; }
        if (Math.Abs(s - 1) > 1e-9 || stages.Count == 0) stages.Add($"atempo={TimeSpec.Num(s, 6)}");
        return string.Join(",", stages);
    }

    // ------------------------------------------------------------ crop

    public static string Crop(PixelRect r) => $"crop={r.Width}:{r.Height}:{r.X}:{r.Y}";

    /// <summary>Largest centred rectangle with the aspect ratio <paramref name="aspectW"/>:<paramref name="aspectH"/>, even-sized.</summary>
    public static PixelRect CenteredAspectCrop(int frameWidth, int frameHeight, int aspectW, int aspectH)
    {
        var target = aspectW / (double)aspectH;
        int w, h;
        if (frameWidth / (double)frameHeight > target)
        {
            h = frameHeight;
            w = (int)Math.Round(frameHeight * target);
        }
        else
        {
            w = frameWidth;
            h = (int)Math.Round(frameWidth / target);
        }
        w = Math.Min(Even(w), Even(frameWidth));
        h = Math.Min(Even(h), Even(frameHeight));
        return new PixelRect(Even((frameWidth - w) / 2), Even((frameHeight - h) / 2), w, h);
    }

    /// <summary>"16:9" or "9:16" to (16, 9). Returns false for anything else.</summary>
    public static bool TryParseAspect(string? text, out int w, out int h)
    {
        w = h = 0;
        var m = AspectRegex().Match(text ?? "");
        if (!m.Success) return false;
        w = int.Parse(m.Groups[1].Value);
        h = int.Parse(m.Groups[2].Value);
        return w > 0 && h > 0;
    }

    public static int Even(int v) => Math.Max(0, v - v % 2);

    // ------------------------------------------------------------ redact

    /// <summary>
    /// Blurs a rectangle, optionally only between two times. A simple filtergraph (one input, one output) so it can sit in
    /// <c>-vf</c>. The blur radius is limited to a quarter of the box's short side because chroma planes are half size.
    /// </summary>
    public static string RedactBlur(PixelRect r, int strength, double? start, double? end)
    {
        var radius = Math.Clamp(strength, 1, Math.Max(1, Math.Min(r.Width, r.Height) / 4));
        return $"split[orig][part];[part]crop={r.Width}:{r.Height}:{r.X}:{r.Y},boxblur={radius}:2[blurred];" +
               $"[orig][blurred]overlay={r.X}:{r.Y}{EnableBetween(start, end)}";
    }

    /// <summary>Paints a solid rectangle, optionally only between two times.</summary>
    public static string RedactSolid(PixelRect r, string color, double? start, double? end) =>
        $"drawbox=x={r.X}:y={r.Y}:w={r.Width}:h={r.Height}:color={FilterEscaping.Value(color + "@1")}:t=fill{EnableBetween(start, end)}";

    /// <summary><c>:enable='between(t,s,e)'</c>, or nothing when the whole timeline is wanted.</summary>
    public static string EnableBetween(double? start, double? end)
    {
        if (start is null && end is null) return "";
        var s = TimeSpec.Num(start ?? 0, 3);
        var expr = end is { } e ? $"between(t,{s},{TimeSpec.Num(e, 3)})" : $"gte(t,{s})";
        return ":enable=" + FilterEscaping.Expression(expr);
    }

    /// <summary>Accepts FFmpeg colour names (black, red) and hex (#RRGGBB, 0xRRGGBB, RRGGBB).</summary>
    public static bool IsValidColor(string? color) => !string.IsNullOrWhiteSpace(color) && ColorRegex().IsMatch(color.Trim());

    // ------------------------------------------------------------ bleep

    /// <summary>
    /// Replaces the given time ranges with a sine tone using one sample-accurate <c>aeval</c> expression per channel
    /// (original sound outside the ranges, tone inside). Needs the channel count because <c>aeval</c> addresses channels explicitly.
    /// </summary>
    public static string Bleep(IReadOnlyList<TimeRange> ranges, int channels, double frequencyHz, double volume)
    {
        if (ranges.Count == 0) throw new ArgumentException("At least one range is required.", nameof(ranges));
        channels = Math.Clamp(channels, 1, 2);
        var inRange = "gt(" + string.Join("+", ranges.Select(r => $"between(t,{TimeSpec.Num(r.Start, 3)},{TimeSpec.Num(r.End, 3)})")) + ",0)";
        var tone = $"{TimeSpec.Num(volume, 3)}*sin(2*PI*{TimeSpec.Num(frequencyHz, 2)}*t)";
        var perChannel = Enumerable.Range(0, channels).Select(i => $"val({i})*(1-{inRange})+{tone}*{inRange}");
        return $"aeval=exprs={FilterEscaping.Expression(string.Join("|", perChannel))}:c=same";
    }

    // ------------------------------------------------------------ audio visualisation

    public static (int Width, int Height) VisualizeSize(string layout) => layout switch
    {
        "portrait" => (1080, 1920),
        "square" => (1080, 1080),
        _ => (1920, 1080),
    };

    [GeneratedRegex(@"^(\d{1,2}):(\d{1,2})$")]
    private static partial Regex AspectRegex();

    [GeneratedRegex(@"^(?:(?:#|0x)?[0-9a-fA-F]{6}|[a-zA-Z]{3,20})$")]
    private static partial Regex ColorRegex();
}
