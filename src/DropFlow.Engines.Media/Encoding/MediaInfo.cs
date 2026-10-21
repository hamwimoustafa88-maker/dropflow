using System.Globalization;
using System.Text.Json;

namespace DropFlow.Engines.Media.Encoding;

public enum StreamKind { Video, Audio, Subtitle, Data, Attachment, Unknown }

/// <summary>One stream of a media file as reported by ffprobe. Unknown values are null.</summary>
public sealed record StreamInfo
{
    public int Index { get; init; }
    public StreamKind Kind { get; init; }
    public string? CodecName { get; init; }
    public string? Profile { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public double? FrameRate { get; init; }
    public bool IsVariableFrameRate { get; init; }
    public string? PixelFormat { get; init; }
    public int? SampleRate { get; init; }
    public int? Channels { get; init; }
    public string? ChannelLayout { get; init; }
    public long? BitRate { get; init; }
    public double? DurationSeconds { get; init; }
    public int? BitDepth { get; init; }
    /// <summary>Display rotation in degrees (0, 90, 180, 270) from the container's rotation metadata.</summary>
    public int Rotation { get; init; }
    /// <summary>True for cover art stored as a video stream.</summary>
    public bool IsAttachedPicture { get; init; }
    public bool IsDefault { get; init; }
    public string? Language { get; init; }
    public string? FieldOrder { get; init; }
    public string? ColorTransfer { get; init; }
    public IReadOnlyDictionary<string, string> Tags { get; init; } = EmptyTags;

    internal static readonly IReadOnlyDictionary<string, string> EmptyTags =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public bool IsInterlaced => FieldOrder is "tt" or "bb" or "tb" or "bt";

    /// <summary>HDR transfer functions (PQ or HLG). Converting these to 8-bit SDR without tone mapping looks washed out.</summary>
    public bool IsHdr => ColorTransfer is "smpte2084" or "arib-std-b67";

    /// <summary>Width as the viewer sees it (rotation applied).</summary>
    public int? DisplayWidth => Rotation is 90 or 270 ? Height : Width;
    public int? DisplayHeight => Rotation is 90 or 270 ? Width : Height;
}

/// <summary>What ffprobe knows about a file. Immutable; produced by <see cref="FfprobeParser"/>.</summary>
public sealed record MediaInfo
{
    public string? Path { get; init; }
    public string? FormatName { get; init; }
    public double? DurationSeconds { get; init; }
    public long? SizeBytes { get; init; }
    public long? BitRate { get; init; }
    public IReadOnlyList<StreamInfo> Streams { get; init; } = [];
    public IReadOnlyDictionary<string, string> Tags { get; init; } = StreamInfo.EmptyTags;

    /// <summary>First real video stream (cover art excluded).</summary>
    public StreamInfo? Video => Streams.FirstOrDefault(s => s.Kind == StreamKind.Video && !s.IsAttachedPicture);
    public StreamInfo? Audio => Streams.FirstOrDefault(s => s.Kind == StreamKind.Audio);
    public StreamInfo? CoverArt => Streams.FirstOrDefault(s => s.Kind == StreamKind.Video && s.IsAttachedPicture);
    public IEnumerable<StreamInfo> SubtitleStreams => Streams.Where(s => s.Kind == StreamKind.Subtitle);

    public bool HasVideo => Video is not null;
    public bool HasAudio => Audio is not null;
    public double? FrameRate => Video?.FrameRate;
}

/// <summary>Thrown when ffprobe output cannot be understood.</summary>
public sealed class MediaProbeException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Parses the JSON that <c>ffprobe -print_format json -show_format -show_streams</c> prints.</summary>
public static class FfprobeParser
{
    public static MediaInfo Parse(string json, string? path = null)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new MediaProbeException("ffprobe returned output that is not valid JSON.", ex);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new MediaProbeException("ffprobe returned an unexpected result.");

            var streams = new List<StreamInfo>();
            if (root.TryGetProperty("streams", out var streamsEl) && streamsEl.ValueKind == JsonValueKind.Array)
                foreach (var s in streamsEl.EnumerateArray()) streams.Add(ParseStream(s));

            string? formatName = null;
            double? duration = null;
            long? size = null, bitRate = null;
            IReadOnlyDictionary<string, string> tags = StreamInfo.EmptyTags;
            if (root.TryGetProperty("format", out var fmt) && fmt.ValueKind == JsonValueKind.Object)
            {
                formatName = Str(fmt, "format_name");
                duration = Dbl(fmt, "duration");
                size = Lng(fmt, "size");
                bitRate = Lng(fmt, "bit_rate");
                tags = ParseTags(fmt);
                path ??= Str(fmt, "filename");
            }

            if (streams.Count == 0 && formatName is null)
                throw new MediaProbeException("ffprobe found no media in this file.");

            // Matroska/WebM often have no container duration; fall back to the longest stream.
            duration ??= streams.Select(s => s.DurationSeconds).Where(d => d is > 0).DefaultIfEmpty(null).Max();

            return new MediaInfo
            {
                Path = path,
                FormatName = formatName,
                DurationSeconds = duration,
                SizeBytes = size,
                BitRate = bitRate,
                Streams = streams,
                Tags = tags,
            };
        }
    }

    private static StreamInfo ParseStream(JsonElement s)
    {
        var kind = Str(s, "codec_type") switch
        {
            "video" => StreamKind.Video,
            "audio" => StreamKind.Audio,
            "subtitle" => StreamKind.Subtitle,
            "data" => StreamKind.Data,
            "attachment" => StreamKind.Attachment,
            _ => StreamKind.Unknown,
        };

        var tags = ParseTags(s);
        double? avg = Rate(Str(s, "avg_frame_rate"));
        double? raw = Rate(Str(s, "r_frame_rate"));
        var fps = avg is > 0 ? avg : raw is > 0 ? raw : null;
        var vfr = avg is > 0 && raw is > 0 && Math.Abs(avg.Value - raw.Value) / raw.Value > 0.02;

        var disposition = s.TryGetProperty("disposition", out var d) && d.ValueKind == JsonValueKind.Object ? d : (JsonElement?)null;

        // Duration lives in the stream, or (Matroska) in a DURATION tag like "00:00:10.000000000".
        var sDuration = Dbl(s, "duration");
        if (sDuration is null && tags.TryGetValue("DURATION", out var tagDur) && TryClock(tagDur, out var tagSeconds))
            sDuration = tagSeconds;

        return new StreamInfo
        {
            Index = (int)(Lng(s, "index") ?? 0),
            Kind = kind,
            CodecName = Str(s, "codec_name"),
            Profile = Str(s, "profile"),
            Width = (int?)Lng(s, "width"),
            Height = (int?)Lng(s, "height"),
            FrameRate = kind == StreamKind.Video ? fps : null,
            IsVariableFrameRate = kind == StreamKind.Video && vfr,
            PixelFormat = Str(s, "pix_fmt"),
            SampleRate = (int?)Lng(s, "sample_rate"),
            Channels = (int?)Lng(s, "channels"),
            ChannelLayout = Str(s, "channel_layout"),
            BitRate = Lng(s, "bit_rate") ?? (tags.TryGetValue("BPS", out var bps) && long.TryParse(bps, NumberStyles.Integer, CultureInfo.InvariantCulture, out var b) ? b : null),
            DurationSeconds = sDuration,
            BitDepth = NonZero((int?)(Lng(s, "bits_per_raw_sample") ?? Lng(s, "bits_per_sample"))),
            Rotation = ReadRotation(s, tags),
            IsAttachedPicture = disposition is { } dp && Lng(dp, "attached_pic") == 1,
            IsDefault = disposition is { } dd && Lng(dd, "default") == 1,
            Language = tags.TryGetValue("language", out var lang) ? lang : null,
            FieldOrder = Str(s, "field_order"),
            ColorTransfer = Str(s, "color_transfer"),
            Tags = tags,
        };
    }

    private static int? NonZero(int? v) => v is > 0 ? v : null;

    private static int ReadRotation(JsonElement s, IReadOnlyDictionary<string, string> tags)
    {
        double? degrees = null;
        if (s.TryGetProperty("side_data_list", out var list) && list.ValueKind == JsonValueKind.Array)
            foreach (var item in list.EnumerateArray())
                if (Dbl(item, "rotation") is { } r) degrees = r;
        if (degrees is null && tags.TryGetValue("rotate", out var tag) && double.TryParse(tag, NumberStyles.Float, CultureInfo.InvariantCulture, out var t))
            degrees = t;
        if (degrees is null) return 0;
        var normalized = (int)(((Math.Round(degrees.Value / 90.0) * 90 % 360) + 360) % 360);
        return normalized;
    }

    private static IReadOnlyDictionary<string, string> ParseTags(JsonElement owner)
    {
        if (!owner.TryGetProperty("tags", out var t) || t.ValueKind != JsonValueKind.Object) return StreamInfo.EmptyTags;
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in t.EnumerateObject())
            dict[p.Name] = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : p.Value.GetRawText();
        return dict;
    }

    /// <summary>"30000/1001" or "25/1" or "0/0" to frames per second; null when invalid.</summary>
    internal static double? Rate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var slash = text.IndexOf('/');
        if (slash < 0) return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var plain) && plain > 0 ? plain : null;
        if (!double.TryParse(text[..slash], NumberStyles.Float, CultureInfo.InvariantCulture, out var n)) return null;
        if (!double.TryParse(text[(slash + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out var den) || den == 0) return null;
        var v = n / den;
        return v > 0 && double.IsFinite(v) ? v : null;
    }

    private static bool TryClock(string text, out double seconds)
    {
        seconds = 0;
        var parts = text.Split(':');
        if (parts.Length != 3) return false;
        if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var h)) return false;
        if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var m)) return false;
        if (!double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var sec)) return false;
        seconds = h * 3600 + m * 60 + sec;
        return true;
    }

    // ffprobe prints most numbers as strings ("duration": "12.345000") but some as numbers; accept both.
    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) ? p.ValueKind switch
        {
            JsonValueKind.String => p.GetString(),
            JsonValueKind.Number => p.GetRawText(),
            _ => null,
        } : null;

    private static double? Dbl(JsonElement e, string name)
    {
        var s = Str(e, name);
        return s is not null && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) ? v : null;
    }

    private static long? Lng(JsonElement e, string name)
    {
        var s = Str(e, name);
        if (s is null) return null;
        if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)) return v;
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d) ? (long)d : null;
    }
}
