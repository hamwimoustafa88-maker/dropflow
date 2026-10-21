using DropFlow.Core.Operations;
using DropFlow.Engines.Common;
using DropFlow.Engines.Media.Encoding;
using DropFlow.Engines.Media.Ffmpeg;

namespace DropFlow.Engines.Media.Runtime;

/// <summary>
/// Starts ffmpeg and ffprobe. A seam for tests: the real implementation runs processes through <see cref="ProcessRunner"/>,
/// tests substitute a fake that records arguments and writes pretend outputs.
/// </summary>
internal interface IMediaToolRunner
{
    string? FfmpegPath { get; }
    string? FfprobePath { get; }
    Task<ProcessResult> RunFfmpegAsync(IReadOnlyList<string> args, ProcessOptions options, CancellationToken cancellationToken);
    Task<ProcessResult> RunFfprobeAsync(IReadOnlyList<string> args, ProcessOptions options, CancellationToken cancellationToken);
}

/// <summary>Runs the engines found by <see cref="IEngineProvider"/>. ffprobe comes from the engine list or sits next to ffmpeg (the winget package ships both).</summary>
internal sealed class ProcessMediaToolRunner(IEngineProvider engines) : IMediaToolRunner
{
    public string? FfmpegPath => engines.Locate(EngineId.Ffmpeg);

    public string? FfprobePath
    {
        get
        {
            if (engines.Locate(EngineId.Ffprobe) is { } probe) return probe;
            if (FfmpegPath is not { } ffmpeg) return null;
            var sibling = Path.Combine(Path.GetDirectoryName(ffmpeg) ?? "", "ffprobe.exe");
            return File.Exists(sibling) ? sibling : null;
        }
    }

    public Task<ProcessResult> RunFfmpegAsync(IReadOnlyList<string> args, ProcessOptions options, CancellationToken cancellationToken) =>
        ProcessRunner.RunAsync(FfmpegPath ?? throw new InvalidOperationException("FFmpeg is not installed."), args, options, cancellationToken);

    public Task<ProcessResult> RunFfprobeAsync(IReadOnlyList<string> args, ProcessOptions options, CancellationToken cancellationToken) =>
        ProcessRunner.RunAsync(FfprobePath ?? throw new InvalidOperationException("ffprobe is not installed."), args, options, cancellationToken);
}

/// <summary>Shared by every operation of one module: the tool runner and a cache of what the FFmpeg build can encode.</summary>
internal sealed class MediaServices(IMediaToolRunner runner)
{
    private readonly object _gate = new();
    private string? _encodersFor;
    private Task<FfmpegEncoders?>? _encoders;

    public IMediaToolRunner Runner { get; } = runner;

    /// <summary>
    /// The encoders of the installed FFmpeg (read once per ffmpeg path). Null when the list could not be read; callers then
    /// assume a standard GPL build and rely on FFmpeg's own error if an encoder turns out to be missing.
    /// </summary>
    public Task<FfmpegEncoders?> GetEncodersAsync(CancellationToken cancellationToken)
    {
        var path = Runner.FfmpegPath;
        if (path is null) return Task.FromResult<FfmpegEncoders?>(null);
        lock (_gate)
        {
            if (_encoders is null || _encodersFor != path)
            {
                _encodersFor = path;
                _encoders = ReadEncodersAsync(cancellationToken);
            }
            return _encoders;
        }
    }

    private readonly Dictionary<string, bool> _hardwareTrials = [];

    /// <summary>
    /// Whether a hardware encoder really works on this machine, found by encoding one black frame (result cached for the session).
    /// A start failure, a non-zero exit or a timeout all mean "no".
    /// </summary>
    public async Task<bool> IsHardwareEncoderUsableAsync(string encoderName, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_hardwareTrials.TryGetValue(encoderName, out var cached)) return cached;
        }

        bool usable;
        try
        {
            var result = await Runner.RunFfmpegAsync(HardwareEncoders.TrialArguments(encoderName), new ProcessOptions { Timeout = TimeSpan.FromSeconds(20) }, cancellationToken).ConfigureAwait(false);
            usable = result.Success;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            usable = false;
        }

        lock (_gate) _hardwareTrials[encoderName] = usable;
        return usable;
    }

    private async Task<FfmpegEncoders?> ReadEncodersAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await Runner.RunFfmpegAsync(FfmpegCommand.ListEncoders(), new ProcessOptions { Timeout = TimeSpan.FromSeconds(30) }, cancellationToken).ConfigureAwait(false);
            if (!result.Success) return null;
            var parsed = FfmpegEncoders.Parse(result.StdOut);
            return parsed.Names.Count == 0 ? null : parsed;
        }
        catch (OperationCanceledException)
        {
            lock (_gate) _encoders = null; // a cancelled read must not poison the cache
            throw;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }
}

/// <summary>An expected failure (corrupt file, bad option, missing encoder). Caught at the converter boundary and returned as <see cref="ConversionResult.Fail"/>.</summary>
internal sealed class MediaFailure(string message) : Exception(message);
