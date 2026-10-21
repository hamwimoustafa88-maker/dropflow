using DropFlow.Engines.Media.Ffmpeg;
using DropFlow.TestSupport;
using TestEngines = DropFlow.TestSupport.Engines;

namespace DropFlow.Engines.Media.Tests.Integration;

/// <summary>
/// Helpers for the tests that run the real FFmpeg (they are skipped, visibly, when it is not installed). Sample media is generated
/// by FFmpeg itself from synthetic sources, so nothing is downloaded and nothing binary is committed. Results are verified by
/// reading them back with ffprobe through <see cref="FfprobeParser"/>, a different code path from the one that wrote them.
/// </summary>
internal static class FfmpegSamples
{
    public static string FfmpegExe => TestEngines.Locator.Locate(EngineId.Ffmpeg) ?? throw new InvalidOperationException("ffmpeg is not installed");

    public static string FfprobeExe =>
        TestEngines.Locator.Locate(EngineId.Ffprobe)
        ?? Path.Combine(Path.GetDirectoryName(FfmpegExe)!, OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe");

    public static IReadOnlyList<IConverter> Converters { get; } = MediaModule.Create(TestEngines.Locator);

    public static async Task<ProcessResult> RunFfmpeg(params string[] args)
    {
        var result = await ProcessRunner.RunAsync(FfmpegExe, ["-hide_banner", "-nostdin", "-y", .. args]);
        Assert.True(result.Success, "ffmpeg failed: " + result.LastMessage);
        return result;
    }

    public static async Task<MediaInfo> Probe(string path)
    {
        var result = await ProcessRunner.RunAsync(FfprobeExe, FfmpegCommand.Probe(path));
        Assert.True(result.Success, $"ffprobe could not read {path}: {result.LastMessage}");
        return FfprobeParser.Parse(result.StdOut, path);
    }

    /// <summary>H.264 + AAC MP4 with a keyframe every second: a colour test pattern with a counter, and a 440 Hz tone.</summary>
    public static async Task<string> Video(string path, double seconds = 6, int width = 320, int height = 240, bool audio = true, int fps = 25)
    {
        var args = new List<string>
        {
            "-f", "lavfi", "-i", $"testsrc=duration={seconds.ToString(System.Globalization.CultureInfo.InvariantCulture)}:size={width}x{height}:rate={fps}",
        };
        if (audio) args.AddRange(["-f", "lavfi", "-i", $"sine=frequency=440:duration={seconds.ToString(System.Globalization.CultureInfo.InvariantCulture)}:sample_rate=44100"]);
        args.AddRange(["-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-g", fps.ToString(), "-keyint_min", fps.ToString(), "-sc_threshold", "0"]);
        if (audio) args.AddRange(["-c:a", "aac", "-b:a", "96k", "-shortest"]);
        args.Add(path);
        await RunFfmpeg([.. args]);
        return path;
    }

    /// <summary>A stereo WAV with a sine tone.</summary>
    public static async Task<string> Wav(string path, double seconds = 3, int channels = 2, int sampleRate = 44100)
    {
        await RunFfmpeg("-f", "lavfi", "-i", $"sine=frequency=440:duration={seconds.ToString(System.Globalization.CultureInfo.InvariantCulture)}:sample_rate={sampleRate}", "-ac", channels.ToString(), path);
        return path;
    }

    /// <summary>Digital silence. The length is an input option (<c>-t</c>) because older anullsrc versions have no duration option.</summary>
    public static async Task<string> Silence(string path, double seconds, string layout = "mono")
    {
        await RunFfmpeg("-t", seconds.ToString(System.Globalization.CultureInfo.InvariantCulture), "-f", "lavfi", "-i", $"anullsrc=r=44100:cl={layout}", path);
        return path;
    }

    /// <summary>One second of silence, one second of tone, one second of silence (mono, 44.1 kHz).</summary>
    public static async Task<string> SilenceToneSilence(string path) => await Compose(path, "silence", "tone", "silence");

    /// <summary>Builds a mono 44.1 kHz WAV from one-second pieces: "silence" or "tone".</summary>
    public static async Task<string> Compose(string path, params string[] pieces)
    {
        var args = new List<string>();
        foreach (var piece in pieces)
        {
            if (piece == "silence") args.AddRange(["-t", "1", "-f", "lavfi", "-i", "anullsrc=r=44100:cl=mono"]);
            else args.AddRange(["-f", "lavfi", "-i", "sine=frequency=440:duration=1:sample_rate=44100"]);
        }
        var inputs = string.Concat(pieces.Select((_, i) => $"[{i}:a]"));
        args.AddRange(["-filter_complex", $"{inputs}concat=n={pieces.Length}:v=0:a=1[a]", "-map", "[a]", path]);
        await RunFfmpeg([.. args]);
        return path;
    }

    public static async Task<ConversionResult> Run(string operation, string input, IReadOnlyDictionary<string, object?>? options = null,
        IProgress<JobProgress>? progress = null, CancellationToken ct = default, string? outputDirectory = null)
    {
        var ext = Path.GetExtension(input).TrimStart('.').ToLowerInvariant();
        var converter = Converters.First(c => c.Supports(operation, ext));
        return await converter.RunAsync(new ConversionJob([input], operation, options, outputDirectory), progress ?? new Progress<JobProgress>(), ct);
    }

    public static async Task<ConversionResult> RunMulti(string operation, IReadOnlyList<string> inputs, IReadOnlyDictionary<string, object?>? options = null)
    {
        var ext = Path.GetExtension(inputs[0]).TrimStart('.').ToLowerInvariant();
        var converter = Converters.First(c => c.Supports(operation, ext));
        return await converter.RunAsync(new ConversionJob(inputs, operation, options), new Progress<JobProgress>(), CancellationToken.None);
    }

    public static Dictionary<string, object?> Opts(params (string Key, object? Value)[] values) => values.ToDictionary(v => v.Key, v => v.Value);

    /// <summary>Measures integrated loudness (LUFS) of a file the same way aud.normalize does.</summary>
    public static async Task<double> IntegratedLoudness(string path)
    {
        var result = await ProcessRunner.RunAsync(FfmpegExe, FfmpegCommand_Analyze(path, Loudnorm.Pass1Filter(-16, -1.5, 11)));
        var stats = Loudnorm.ParseStats(result.StdErr) ?? throw new InvalidOperationException("no loudnorm output: " + result.LastMessage);
        return double.Parse(stats.InputI, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static IReadOnlyList<string> FfmpegCommand_Analyze(string path, string filter) => AudioToolArgs.Analyze(path, filter);

    /// <summary>The PNG header says width x height; used to check snapshots without a decoder.</summary>
    public static (int Width, int Height) PngSize(string path)
    {
        var bytes = File.ReadAllBytes(path);
        Assert.True(bytes.Length > 24 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47, $"{path} is not a PNG");
        int Be(int offset) => bytes[offset] << 24 | bytes[offset + 1] << 16 | bytes[offset + 2] << 8 | bytes[offset + 3];
        return (Be(16), Be(20));
    }
}
