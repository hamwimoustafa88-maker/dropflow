// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using DropFlow.Core.Operations;
using DropFlow.Engines.Common;
using DropFlow.Engines.Media.Encoding;
using DropFlow.Engines.Media.Ffmpeg;
using DropFlow.Engines.Media.Runtime;

namespace DropFlow.Engines.Media.Preview;

/// <summary>Loudness of a whole file as EBU R128 measures it (the same first pass <c>aud.normalize</c> runs).</summary>
public sealed record LoudnessReading(double IntegratedLufs, double TruePeakDb, double RangeLu);

/// <summary>A preview could not be made (FFmpeg missing, file unreadable). The message is for logs; the editors show their own text.</summary>
public sealed class MediaPreviewException(string message) : Exception(message);

/// <summary>
/// What the media editor windows need from FFmpeg besides the real tools: stills for the timeline and for players that cannot open a
/// format, the waveform, a loudness reading, and short listening renders (bleep A/B, one side of a stereo file, a playable copy).
/// All of it is read-only for the user's file; renders are written where the caller says (a scratch folder), never beside the input.
/// Every call honours its <see cref="CancellationToken"/>; nothing here blocks the calling thread.
/// </summary>
public static class MediaPreview
{
    /// <summary>Reads a file with ffprobe (see <see cref="MediaEncoder.ProbeAsync"/>).</summary>
    public static Task<MediaInfo> ProbeAsync(IEngineProvider engines, string path, CancellationToken cancellationToken = default) =>
        MediaEncoder.ProbeAsync(engines, path, cancellationToken);

    /// <summary>A PNG of the frame at <paramref name="seconds"/>, <paramref name="height"/> pixels high; null when FFmpeg could not make one.</summary>
    public static Task<byte[]?> FrameAsync(IEngineProvider engines, string path, double seconds, int height, CancellationToken cancellationToken = default) =>
        Service(engines).FrameAsync(path, seconds, height, cancellationToken);

    /// <summary>
    /// <paramref name="count"/> frames spread over the file, delivered through <paramref name="onFrame"/> (index, PNG) as each one is ready
    /// (two at a time, so the strip fills in quickly). Frames FFmpeg cannot make are skipped.
    /// </summary>
    public static Task ThumbnailsAsync(IEngineProvider engines, string path, double duration, int count, int height, Action<int, byte[]> onFrame, CancellationToken cancellationToken = default) =>
        Service(engines).ThumbnailsAsync(path, duration, count, height, onFrame, cancellationToken);

    /// <summary>The thumbnail times <see cref="ThumbnailsAsync"/> uses: the middle of each of <paramref name="count"/> equal slots.</summary>
    public static IReadOnlyList<double> ThumbnailTimes(double duration, int count) => PreviewArgs.ThumbnailTimes(duration, count);

    /// <summary><paramref name="bars"/> peak levels (0..1) across the first audio stream.</summary>
    public static Task<float[]> WaveformAsync(IEngineProvider engines, string path, int bars, CancellationToken cancellationToken = default) =>
        Service(engines).WaveformAsync(path, bars, cancellationToken);

    /// <summary>Integrated loudness, true peak and loudness range of the whole file; null when it is silent. <paramref name="progress"/> gets 0..1.</summary>
    public static Task<LoudnessReading?> MeasureLoudnessAsync(IEngineProvider engines, string path, double? duration, IProgress<double>? progress = null, CancellationToken cancellationToken = default) =>
        Service(engines).MeasureLoudnessAsync(path, duration, progress, cancellationToken);

    /// <summary>A playable AAC copy of the whole audio track, for formats the Windows player cannot open (Ogg, Opus...).</summary>
    public static Task<string> PlayableCopyAsync(IEngineProvider engines, string path, string outputFolder, CancellationToken cancellationToken = default) =>
        Service(engines).SnippetAsync(path, 0, null, null, outputFolder, "playable", cancellationToken);

    /// <summary>
    /// A listening render of <paramref name="length"/> seconds from <paramref name="start"/>, with the beeps in place when <paramref name="bleeped"/>
    /// (the A/B preview of <c>aud.bleep</c>: same filter, ranges moved to the window). Ranges are in seconds of the original file.
    /// </summary>
    public static Task<string> BleepPreviewAsync(
        IEngineProvider engines, string path, IReadOnlyList<(double Start, double End)> ranges, double frequencyHz, double volume, int channels,
        double start, double length, bool bleeped, string outputFolder, CancellationToken cancellationToken = default)
    {
        var filter = bleeped ? PreviewArgs.BleepWindowFilter(ranges, channels, frequencyHz, volume, start, length) : null;
        return Service(engines).SnippetAsync(path, start, length, filter, outputFolder, bleeped ? "bleeped" : "original", cancellationToken);
    }

    /// <summary>
    /// A listening render for <c>aud.channels</c>: <paramref name="listen"/> = "left" or "right" plays that side of the source in both ears,
    /// "result" plays what the tool would produce with these settings.
    /// </summary>
    public static Task<string> ChannelsPreviewAsync(
        IEngineProvider engines, string path, string listen, string target, double leftGainDb, double rightGainDb, int sourceChannels,
        double start, double length, string outputFolder, CancellationToken cancellationToken = default)
    {
        var filter = listen == "result"
            ? AudioToolArgs.ChannelsFilter(target, sourceChannels, leftGainDb, rightGainDb)
            : PreviewArgs.SoloFilter(listen, sourceChannels);
        return Service(engines).SnippetAsync(path, start, length, filter, outputFolder, listen, cancellationToken);
    }

    private static MediaPreviewService Service(IEngineProvider engines) => new(new ProcessMediaToolRunner(engines));
}

/// <summary>The work behind <see cref="MediaPreview"/>, on the <see cref="IMediaToolRunner"/> seam so tests can fake FFmpeg.</summary>
internal sealed class MediaPreviewService(IMediaToolRunner runner)
{
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public async Task<byte[]?> FrameAsync(string path, double seconds, int height, CancellationToken ct)
    {
        if (runner.FfmpegPath is null) return null;
        using var png = new MemoryStream();
        var result = await runner.RunFfmpegAsync(PreviewArgs.Frame(path, seconds, height), new ProcessOptions { StandardOutput = png, Timeout = TimeSpan.FromSeconds(30) }, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        var bytes = png.ToArray();
        return result.Success && bytes.AsSpan().StartsWith(PngSignature) ? bytes : null;
    }

    public async Task ThumbnailsAsync(string path, double duration, int count, int height, Action<int, byte[]> onFrame, CancellationToken ct)
    {
        var times = PreviewArgs.ThumbnailTimes(duration, count);
        using var gate = new SemaphoreSlim(2);
        var tasks = times.Select(async (t, i) =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (await FrameAsync(path, t, height, ct).ConfigureAwait(false) is { } frame) onFrame(i, frame);
            }
            finally
            {
                gate.Release();
            }
        }).ToList();
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    public async Task<float[]> WaveformAsync(string path, int bars, CancellationToken ct)
    {
        if (runner.FfmpegPath is null) throw new MediaPreviewException("FFmpeg is not installed.");
        var sink = new WaveformSink();
        var result = await runner.RunFfmpegAsync(PreviewArgs.Waveform(path), new ProcessOptions { StandardOutput = sink, Timeout = TimeSpan.FromMinutes(10) }, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (!result.Success && sink.BlockCount == 0) throw new MediaPreviewException(FfmpegErrors.Summarize(result.StdErr, result.ExitCode));
        return sink.Reduce(bars);
    }

    public async Task<LoudnessReading?> MeasureLoudnessAsync(string path, double? duration, IProgress<double>? progress, CancellationToken ct)
    {
        if (runner.FfmpegPath is null) throw new MediaPreviewException("FFmpeg is not installed.");
        var parser = new FfmpegProgressParser();
        var options = new ProcessOptions
        {
            Timeout = TimeSpan.FromMinutes(30),
            OnOutputLine = line => { if (parser.Feed(line) is { } p) progress?.Report(Math.Clamp(p.Fraction(duration), 0, 1)); },
        };
        var result = await runner.RunFfmpegAsync(AudioToolArgs.Analyze(path, Loudnorm.Pass1Filter(-16, -1.5, 11)), options, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (!result.Success) throw new MediaPreviewException(FfmpegErrors.Summarize(result.StdErr, result.ExitCode));
        var stats = Loudnorm.ParseStats(result.StdErr) ?? throw new MediaPreviewException("FFmpeg printed no loudness measurement.");
        if (!stats.IsUsable) return null;
        static double D(string s) => double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);
        return new LoudnessReading(D(stats.InputI), D(stats.InputTp), D(stats.InputLra));
    }

    /// <summary>Renders an AAC snippet into <paramref name="folder"/> under a fresh name and returns its path.</summary>
    public async Task<string> SnippetAsync(string path, double start, double? length, string? filter, string folder, string label, CancellationToken ct)
    {
        if (runner.FfmpegPath is null) throw new MediaPreviewException("FFmpeg is not installed.");
        Directory.CreateDirectory(folder);
        var output = Path.Combine(folder, $"{label}-{Guid.NewGuid().ToString("N")[..8]}.m4a");
        var result = await runner.RunFfmpegAsync(PreviewArgs.AudioSnippet(path, start, length, filter, output), new ProcessOptions { Timeout = TimeSpan.FromMinutes(10) }, ct).ConfigureAwait(false);
        if (ct.IsCancellationRequested || !result.Success || !File.Exists(output) || new FileInfo(output).Length == 0)
        {
            try { if (File.Exists(output)) File.Delete(output); }
            catch (IOException) { /* best effort */ }
            ct.ThrowIfCancellationRequested();
            throw new MediaPreviewException(FfmpegErrors.Summarize(result.StdErr, result.ExitCode));
        }
        return output;
    }
}
