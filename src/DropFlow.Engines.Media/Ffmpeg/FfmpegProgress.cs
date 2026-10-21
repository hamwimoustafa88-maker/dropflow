using System.Globalization;

namespace DropFlow.Engines.Media.Ffmpeg;

/// <summary>One snapshot of FFmpeg's <c>-progress pipe:1</c> output (emitted at each <c>progress=</c> line).</summary>
internal sealed record FfmpegProgress(
    long OutTimeUs,
    double? Speed,
    long? Frame,
    double? Fps,
    double? BitrateKbps,
    long? TotalSizeBytes,
    bool IsEnd)
{
    public double OutSeconds => OutTimeUs / 1_000_000.0;

    /// <summary>
    /// Fraction of <paramref name="totalSeconds"/> produced so far, clamped to 0..1. While running it never reports 1 (FFmpeg
    /// still has to finalise the file); the final snapshot (<see cref="IsEnd"/>) reports exactly 1.
    /// </summary>
    public double Fraction(double? totalSeconds)
    {
        if (IsEnd) return 1;
        if (totalSeconds is not > 0) return 0;
        return Math.Clamp(OutSeconds / totalSeconds.Value, 0, 0.999);
    }
}

/// <summary>
/// Parses FFmpeg's machine-readable progress stream. Feed it every stdout line; it returns a snapshot when a block
/// completes. Handles <c>N/A</c> values, the historic <c>out_time_ms</c> (which is really microseconds), negative placeholders
/// and unknown keys.
/// </summary>
internal sealed class FfmpegProgressParser
{
    private long _outTimeUs;
    private bool _haveTime;
    private double? _speed;
    private long? _frame;
    private double? _fps;
    private double? _bitrate;
    private long? _size;

    /// <summary>Returns a snapshot when <paramref name="line"/> is a <c>progress=</c> line, otherwise null.</summary>
    public FfmpegProgress? Feed(string line)
    {
        var eq = line.IndexOf('=');
        if (eq <= 0) return null;
        var key = line[..eq].Trim();
        var value = line[(eq + 1)..].Trim();

        switch (key)
        {
            case "out_time_us":
            case "out_time_ms": // despite the name, FFmpeg writes microseconds here too
                if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var us) && us >= 0)
                {
                    // out_time_us wins over the legacy key when both are present in a block.
                    if (key == "out_time_us" || !_haveTime) { _outTimeUs = us; _haveTime = true; }
                }
                break;
            case "out_time":
                if (!_haveTime && TimeSpec.TryParseSeconds(value, out var s)) { _outTimeUs = (long)(s * 1_000_000); _haveTime = true; }
                break;
            case "speed":
                _speed = TryNumber(value.TrimEnd('x', 'X', ' '));
                break;
            case "frame":
                _frame = long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var f) ? f : null;
                break;
            case "fps":
                _fps = TryNumber(value);
                break;
            case "bitrate":
                _bitrate = TryNumber(value.Replace("kbits/s", "", StringComparison.OrdinalIgnoreCase).Trim());
                break;
            case "total_size":
                _size = long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sz) ? sz : null;
                break;
            case "progress":
                var snapshot = new FfmpegProgress(_outTimeUs, _speed, _frame, _fps, _bitrate, _size, value == "end");
                _haveTime = false;
                _outTimeUs = 0;
                return snapshot;
        }
        return null;
    }

    private static double? TryNumber(string v) =>
        double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && !double.IsNaN(d) ? d : null;
}
