using DropFlow.Core.Operations;

namespace DropFlow.Core.Tests;

/// <summary>Converter whose behaviour is supplied by the test.</summary>
internal sealed class FakeConverter(
    IReadOnlyCollection<string> operations,
    Func<string, string, bool>? supports = null,
    Func<ConversionJob, IProgress<JobProgress>, CancellationToken, Task<ConversionResult>>? run = null,
    EngineId engine = EngineId.None,
    IReadOnlyList<ToolOption>? options = null) : IConverter
{
    public IReadOnlyCollection<string> Operations { get; } = operations;

    public bool Supports(string operation, string inputExtension) =>
        Operations.Contains(operation) && (supports?.Invoke(operation, inputExtension) ?? true);

    public EngineId EngineFor(string operation) => engine;

    public IReadOnlyList<ToolOption> OptionsFor(string operation) => options ?? [];

    public Task<ConversionResult> RunAsync(ConversionJob job, IProgress<JobProgress> progress, CancellationToken cancellationToken) =>
        run?.Invoke(job, progress, cancellationToken) ?? Task.FromResult(ConversionResult.Ok([job.Inputs[0] + ".out"]));
}

internal sealed class FakeEngines(params EngineId[] installed) : IEngineProvider
{
    public string? Locate(EngineId engine) => installed.Contains(engine) ? $"C:\\engines\\{engine}.exe" : null;
}

internal sealed class FakeDisposer : IFileDisposer
{
    public List<string> Disposed { get; } = [];
    public void Dispose(string path) => Disposed.Add(path);
}

internal sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dropflow-test-" + Guid.NewGuid().ToString("N"));

    public TempDir() => Directory.CreateDirectory(Path);

    public string File(string name, string content = "x")
    {
        var p = System.IO.Path.Combine(Path, name);
        System.IO.File.WriteAllText(p, content);
        return p;
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { /* temp dir cleanup is best effort */ }
    }
}
