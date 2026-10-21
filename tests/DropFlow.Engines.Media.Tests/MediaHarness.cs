using DropFlow.Core.Operations;
using DropFlow.Engines.Media.Encoding;
using DropFlow.Engines.Media.Runtime;
using DropFlow.TestSupport;

namespace DropFlow.Engines.Media.Tests;

/// <summary>Collects <see cref="JobProgress"/> reports in order.</summary>
internal sealed class ProgressLog : IProgress<JobProgress>
{
    private readonly object _gate = new();
    public List<JobProgress> Reports { get; } = [];

    public void Report(JobProgress value)
    {
        lock (_gate) Reports.Add(value);
    }
}

/// <summary>A module wired to a <see cref="FakeToolRunner"/> and a scratch folder; the setup every operation test starts from.</summary>
internal sealed class MediaHarness : IDisposable
{
    private readonly TempDir _dir = new();

    public MediaHarness()
    {
        Converters = MediaModule.Create(new MediaServices(Runner));
    }

    public FakeToolRunner Runner { get; } = new();
    public IReadOnlyList<IConverter> Converters { get; }
    public string Dir => _dir.Path;

    public IConverter For(string operation, string extension) =>
        Converters.FirstOrDefault(c => c.Supports(operation, extension))
        ?? throw new InvalidOperationException($"No converter supports {operation} for .{extension}");

    /// <summary>Creates a placeholder input file (the fake ffmpeg never reads it) and, optionally, the media info ffprobe will report for it.</summary>
    public string Input(string relativePath, MediaInfo? info = null, string content = "input-bytes")
    {
        var path = System.IO.Path.Combine(Dir, relativePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        if (info is not null) Runner.Probe(path, info with { Path = path });
        return path;
    }

    public string Video(string relativePath = "clip.mp4", double seconds = 60, int width = 1920, int height = 1080, double fps = 30, string? audioCodec = "aac", int channels = 2, int rotation = 0)
    {
        var path = System.IO.Path.Combine(Dir, relativePath);
        return Input(relativePath, TestMedia.Video(width, height, fps, seconds, audioCodec: audioCodec, channels: channels, rotation: rotation, path: path));
    }

    public string Audio(string relativePath = "song.mp3", string codec = "mp3", double seconds = 30, int channels = 2, int sampleRate = 44100, bool cover = false)
    {
        var path = System.IO.Path.Combine(Dir, relativePath);
        return Input(relativePath, TestMedia.Audio(codec, channels, sampleRate, seconds, path, cover));
    }

    public Task<ConversionResult> Run(
        string operation, string input, IReadOnlyDictionary<string, object?>? options = null,
        ProgressLog? progress = null, CancellationToken ct = default, string? outputDirectory = null)
    {
        var ext = System.IO.Path.GetExtension(input).TrimStart('.').ToLowerInvariant();
        return For(operation, ext).RunAsync(new ConversionJob([input], operation, options, outputDirectory), progress ?? new ProgressLog(), ct);
    }

    public Task<ConversionResult> RunMulti(string operation, IReadOnlyList<string> inputs, IReadOnlyDictionary<string, object?>? options = null, ProgressLog? progress = null)
    {
        var ext = System.IO.Path.GetExtension(inputs[0]).TrimStart('.').ToLowerInvariant();
        return For(operation, ext).RunAsync(new ConversionJob(inputs, operation, options), progress ?? new ProgressLog(), CancellationToken.None);
    }

    public static Dictionary<string, object?> Opts(params (string Key, object? Value)[] values) =>
        values.ToDictionary(v => v.Key, v => v.Value);

    /// <summary>Every file and folder below the harness directory, relative, for "nothing else was left behind" checks.</summary>
    public IReadOnlyList<string> Tree() =>
        Directory.EnumerateFileSystemEntries(Dir, "*", SearchOption.AllDirectories)
            .Select(p => System.IO.Path.GetRelativePath(Dir, p)).OrderBy(p => p, StringComparer.Ordinal).ToList();

    public void Dispose() => _dir.Dispose();
}
