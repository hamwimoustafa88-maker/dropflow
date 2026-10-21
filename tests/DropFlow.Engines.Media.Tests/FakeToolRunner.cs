using System.Globalization;
using System.Text;
using DropFlow.Core.Operations;
using DropFlow.Engines.Common;
using DropFlow.Engines.Media.Encoding;
using DropFlow.Engines.Media.Runtime;

namespace DropFlow.Engines.Media.Tests;

/// <summary>Everything one fake ffmpeg invocation can see.</summary>
internal sealed record FfmpegCall(IReadOnlyList<string> Args, string? WorkingDirectory)
{
    /// <summary>The output path: the last argument (or null for analysis runs that write to the null device).</summary>
    public string Output => Args[^1];
    public bool Has(string arg) => Args.Contains(arg);
    public string? After(string flag) => Tests.Args.After(Args, flag);
}

/// <summary>
/// A stand-in for ffmpeg/ffprobe. It records every call, answers ffprobe from a table, answers "-encoders" with a canned list,
/// and by default "succeeds" by writing a small file at the output path while streaming realistic <c>-progress</c> lines.
/// Tests override <see cref="OnFfmpeg"/> for failures, stderr content and blocking.
/// </summary>
internal sealed class FakeToolRunner : IMediaToolRunner
{
    private readonly Dictionary<string, string> _probes = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public string? FfmpegPath { get; set; } = @"C:\fake\ffmpeg.exe";
    public string? FfprobePath { get; set; } = @"C:\fake\ffprobe.exe";
    public string EncodersOutput { get; set; } = TestMedia.EncodersGpl;
    public bool EncodersFail { get; set; }

    /// <summary>Result for the call; null = default behaviour (write the output, report progress, succeed).</summary>
    public Func<FfmpegCall, ProcessOptions, CancellationToken, Task<ProcessResult?>>? OnFfmpeg { get; set; }

    public List<FfmpegCall> Calls { get; } = [];
    public List<IReadOnlyList<string>> ProbeCalls { get; } = [];
    public int EncoderListCalls { get; private set; }

    /// <summary>Directories that existed as working directories during calls, with whether they existed at call time.</summary>
    public List<(string Directory, bool Existed)> WorkingDirectories { get; } = [];

    /// <summary>Makes every non-analysis run write an output of exactly this many bytes (pass 1 of a two-pass encode writes nothing).</summary>
    public void WriteOutputSize(long bytes) =>
        OnFfmpeg = (call, _, _) =>
        {
            if (call.After("-pass") == "1") return Task.FromResult<ProcessResult?>(null);
            File.WriteAllBytes(call.Output, new byte[bytes]);
            return Task.FromResult<ProcessResult?>(new ProcessResult(0, "", "", TimeSpan.Zero, false, false));
        };

    public void Probe(string path, MediaInfo info) => _probes[path] = ProbeJson.From(info);
    public void ProbeRaw(string path, string json) => _probes[path] = json;
    public void ProbeFails(string path) => _probes[path] = "FAIL";

    public async Task<ProcessResult> RunFfmpegAsync(IReadOnlyList<string> args, ProcessOptions options, CancellationToken cancellationToken)
    {
        if (args.Contains("-encoders"))
        {
            lock (_gate) EncoderListCalls++;
            return EncodersFail
                ? new ProcessResult(1, "", "ffmpeg: error", TimeSpan.Zero, false, false)
                : new ProcessResult(0, EncodersOutput, "", TimeSpan.Zero, false, false);
        }

        var call = new FfmpegCall(args, options.WorkingDirectory);
        lock (_gate)
        {
            Calls.Add(call);
            if (options.WorkingDirectory is { } wd) WorkingDirectories.Add((wd, Directory.Exists(wd)));
        }

        if (OnFfmpeg is not null && await OnFfmpeg(call, options, cancellationToken) is { } custom) return custom;
        return DefaultSuccess(call, options);
    }

    public Task<ProcessResult> RunFfprobeAsync(IReadOnlyList<string> args, ProcessOptions options, CancellationToken cancellationToken)
    {
        lock (_gate) ProbeCalls.Add(args);
        var path = args[^1];
        if (!_probes.TryGetValue(path, out var json))
        {
            var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
            json = ProbeJson.From(AudioExt.Contains(ext) ? TestMedia.Audio(ext == "m4a" ? "aac" : ext, path: path) : TestMedia.Video(path: path));
        }
        return Task.FromResult(json == "FAIL"
            ? new ProcessResult(1, "", $"{path}: Invalid data found when processing input", TimeSpan.Zero, false, false)
            : new ProcessResult(0, json, "", TimeSpan.Zero, false, false));
    }

    private static readonly string[] AudioExt = ["mp3", "m4a", "wav", "flac", "ogg", "opus", "aiff", "wma", "aac"];

    /// <summary>Writes the output file (unless it is the null device or a pattern) and streams a short progress sequence.</summary>
    public static ProcessResult DefaultSuccess(FfmpegCall call, ProcessOptions options)
    {
        WriteOutput(call.Output);
        if (options.OnOutputLine is { } feed)
        {
            foreach (var line in new[] { "frame=10", "out_time_us=500000", "speed=2.0x", "progress=continue", "out_time_us=1000000", "speed=2.0x", "progress=end" })
                feed(line);
        }
        return new ProcessResult(0, "", "", TimeSpan.FromMilliseconds(5), false, false);
    }

    public static void WriteOutput(string path)
    {
        if (path is "NUL" or "/dev/null" or "-") return;
        if (path.Contains('%'))
        {
            // image/segment patterns: create the first numbered file
            var concrete = path.Replace("%%", "\u0001", StringComparison.Ordinal).Replace("%05d", "00001", StringComparison.Ordinal).Replace("%03d", "001", StringComparison.Ordinal).Replace("\u0001", "%", StringComparison.Ordinal);
            path = concrete;
        }
        if (Path.GetDirectoryName(path) is { Length: > 0 } dir && Directory.Exists(dir) && !File.Exists(path))
            File.WriteAllBytes(path, [1, 2, 3, 4]);
    }

    public static ProcessResult Fail(string stderr, int exitCode = 1) => new(exitCode, "", stderr, TimeSpan.Zero, false, false);
}

/// <summary>An <see cref="IEngineProvider"/> that reports nothing installed (or the given engines).</summary>
internal sealed class FakeEngines(params EngineId[] installed) : IEngineProvider
{
    public string? Locate(EngineId engine) => installed.Contains(engine) ? $@"C:\engines\{engine}.exe" : null;
}

/// <summary>Writes ffprobe-style JSON for a <see cref="MediaInfo"/>, so tests can state what a file contains in plain C#.</summary>
internal static class ProbeJson
{
    public static string From(MediaInfo info)
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder("{\"streams\":[");
        var first = true;
        foreach (var s in info.Streams)
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append($"{{\"index\":{s.Index},\"codec_type\":\"{s.Kind.ToString().ToLowerInvariant()}\"");
            if (s.CodecName is not null) sb.Append($",\"codec_name\":\"{s.CodecName}\"");
            if (s.Width is not null) sb.Append($",\"width\":{s.Width},\"height\":{s.Height}");
            if (s.FrameRate is { } fps) sb.Append($",\"avg_frame_rate\":\"{(long)Math.Round(fps * 1000)}/1000\",\"r_frame_rate\":\"{(long)Math.Round(fps * 1000)}/1000\"");
            if (s.PixelFormat is not null) sb.Append($",\"pix_fmt\":\"{s.PixelFormat}\"");
            if (s.SampleRate is not null) sb.Append($",\"sample_rate\":\"{s.SampleRate}\"");
            if (s.Channels is not null) sb.Append($",\"channels\":{s.Channels}");
            if (s.BitRate is not null) sb.Append($",\"bit_rate\":\"{s.BitRate}\"");
            if (s.BitDepth is not null) sb.Append($",\"bits_per_raw_sample\":\"{s.BitDepth}\"");
            if (s.DurationSeconds is { } d) sb.Append($",\"duration\":\"{d.ToString("0.######", inv)}\"");
            if (s.ColorTransfer is not null) sb.Append($",\"color_transfer\":\"{s.ColorTransfer}\"");
            if (s.Rotation != 0) sb.Append($",\"side_data_list\":[{{\"side_data_type\":\"Display Matrix\",\"rotation\":{-s.Rotation}}}]");
            sb.Append($",\"disposition\":{{\"default\":1,\"attached_pic\":{(s.IsAttachedPicture ? 1 : 0)}}}");
            sb.Append('}');
        }
        sb.Append("],\"format\":{");
        sb.Append($"\"filename\":\"{(info.Path ?? "").Replace("\\", "\\\\", StringComparison.Ordinal)}\"");
        sb.Append($",\"format_name\":\"{info.FormatName ?? "mov,mp4"}\"");
        if (info.DurationSeconds is { } dur) sb.Append($",\"duration\":\"{dur.ToString("0.######", inv)}\"");
        sb.Append("}}");
        return sb.ToString();
    }
}
