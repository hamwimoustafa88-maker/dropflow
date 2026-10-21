using DropFlow.Core.Operations;
using DropFlow.Engines.Pdf.Infrastructure;
using UglyToad.PdfPig;

namespace DropFlow.Engines.Pdf.Tests;

/// <summary>An engine provider where nothing is installed (the real situation on the dev machine).</summary>
internal sealed class NoEngines : IEngineProvider
{
    public string? Locate(EngineId engine) => null;
}

/// <summary>An engine provider that "finds" the given engines at fake paths (used with <see cref="FakeRunner"/>).</summary>
internal sealed class FakeEngines(params EngineId[] installed) : IEngineProvider
{
    public string? Locate(EngineId engine) => installed.Contains(engine) ? $@"C:\fake\{engine}.exe" : null;
}

internal sealed class RecordingProgress : IProgress<JobProgress>
{
    public List<JobProgress> Reports { get; } = [];
    public void Report(JobProgress value) => Reports.Add(value);
    public string? LastMessage => Reports.LastOrDefault(r => r.Message is not null)?.Message;
}

internal static class Kit
{
    public static readonly IReadOnlyList<IConverter> Converters = PdfModule.Create(new NoEngines());

    public static IConverter Converter(string operation, string ext, IReadOnlyList<IConverter>? converters = null) =>
        (converters ?? Converters).FirstOrDefault(c => c.Supports(operation, ext))
        ?? throw new InvalidOperationException($"Nothing supports {operation} for .{ext}");

    public static Task<ConversionResult> RunAsync(
        string operation,
        IEnumerable<string> inputs,
        IReadOnlyDictionary<string, object?>? options = null,
        string? outputDirectory = null,
        CancellationToken ct = default,
        IProgress<JobProgress>? progress = null,
        IReadOnlyList<IConverter>? converters = null)
    {
        var list = inputs.ToList();
        var ext = Path.GetExtension(list[0]).TrimStart('.').ToLowerInvariant();
        var converter = Converter(operation, ext, converters);
        return converter.RunAsync(new ConversionJob(list, operation, options, outputDirectory), progress ?? new RecordingProgress(), ct);
    }

    public static Dictionary<string, object?> Opts(params (string Key, object? Value)[] pairs) =>
        pairs.ToDictionary(p => p.Key, p => p.Value);

    public static async Task<ConversionResult> Ok(Task<ConversionResult> task)
    {
        var result = await task;
        Assert.True(result.Success, result.Error);
        return result;
    }

    public static int PageCount(string pdf)
    {
        using var doc = PdfDocument.Open(pdf);
        return doc.NumberOfPages;
    }

    /// <summary>Text of every page as read by PdfPig (a different reader than the PDFsharp writer).</summary>
    public static List<string> PageTexts(string pdf)
    {
        using var doc = PdfDocument.Open(pdf);
        return doc.GetPages().Select(p => p.Text.Trim()).ToList();
    }

    public static string[] FilesIn(string folder) =>
        Directory.GetFiles(folder).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToArray()!;
}
