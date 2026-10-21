using System.Text;
using System.Text.RegularExpressions;
using DropFlow.AI.Models;
using DropFlow.Core.Catalog;
using DropFlow.Core.Output;

namespace DropFlow.AI;

/// <summary>Operation ids implemented here. Every one of them exists in catalog/tools.json.</summary>
public static class AiOperations
{
    public const string AudioTranscribe = "aud.transcribe";
    public const string VideoTranscribe = "vid.transcribe";
    public const string ImageOcr = "img.ocr";
    public const string PdfOcr = "pdf.ocr";
    public const string RemoveBackground = "img.remove-bg";
    public const string RedactAuto = "img.redact-auto";

    public static IReadOnlyList<string> All { get; } =
        [AudioTranscribe, VideoTranscribe, ImageOcr, PdfOcr, RemoveBackground, RedactAuto];
}

/// <summary>Input extensions come from the shared catalog so Supports can never drift from what the wheel offers.</summary>
internal static class AiCatalog
{
    private static readonly Lazy<ToolCatalog> Catalog = new(ToolCatalog.LoadEmbedded);

    public static IReadOnlySet<string> Inputs(string categoryId) =>
        Catalog.Value.Category(categoryId)?.Inputs.ToHashSet(StringComparer.OrdinalIgnoreCase)
        ?? throw new InvalidOperationException($"Catalog has no category '{categoryId}'.");

    public static string Normalize(string extension) => ToolCatalog.Normalize(extension);
}

/// <summary>
/// Failure messages the app can act on. A missing model is reported as <c>Model 'id' is not installed ...</c>;
/// <see cref="TryParseMissingModel"/> recovers the id so the UI can offer the download.
/// </summary>
public static class AiFailures
{
    public static string MissingModel(ModelManager models, string modelId)
    {
        var info = models.Find(modelId);
        var detail = info is null ? "" : $" ({info.DisplayName}, {info.SizeBytes / 1_048_576.0:0.#} MB)";
        return $"Model '{modelId}' is not installed{detail}. Download it from the model manager first.";
    }

    private static readonly Regex MissingModelPattern = new(@"Model '(?<id>[^']+)' is not installed", RegexOptions.Compiled);

    public static string? TryParseMissingModel(string? error)
    {
        var m = error is null ? null : MissingModelPattern.Match(error);
        return m is { Success: true } ? m.Groups["id"].Value : null;
    }

    public static string MissingEngine(string name, string? hint = null) =>
        $"{name} is not installed.{(hint is null ? "" : " " + hint)}";
}

internal static class OutputNaming
{
    /// <summary>
    /// Output paths for several files that belong together ("talk (transcript).srt" and "talk (transcript).txt"): one collision-free
    /// numbering is chosen for the whole set so they always share the same name stem.
    /// </summary>
    public static IReadOnlyList<string> ForSet(string input, IReadOnlyList<string> extensions, string suffix, string? outputDirectory)
    {
        var resolver = new OutputPathResolver(p => extensions.Any(e => Taken(Path.ChangeExtension(p, e))));
        var first = resolver.ForFile(input, extensions[0], suffix, outputDirectory);
        return extensions.Select(e => Path.ChangeExtension(first, e)).ToList();
    }

    public static string ForOne(string input, string extension, string suffix, string? outputDirectory) =>
        new OutputPathResolver().ForFile(input, extension, suffix, outputDirectory);

    private static bool Taken(string path) => File.Exists(path) || Directory.Exists(path);

    /// <summary>Writes UTF-8 without BOM, refusing to overwrite (CreateNew), so a race with another writer cannot destroy a file.</summary>
    public static void WriteNew(string path, string content)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        var bytes = new UTF8Encoding(false).GetBytes(content);
        stream.Write(bytes, 0, bytes.Length);
    }
}

/// <summary>A scratch folder that is always removed, with an ASCII-only fallback location for tools that cannot open Unicode paths.</summary>
internal sealed class WorkDirectory : IDisposable
{
    public string Path { get; }

    private WorkDirectory(string path) => Path = path;

    /// <param name="asciiOnly">True for engines (tesseract) whose command line cannot carry non-ASCII paths on Windows.</param>
    public static WorkDirectory Create(bool asciiOnly = false)
    {
        var root = System.IO.Path.GetTempPath();
        if (asciiOnly && !IsAscii(root))
        {
            // A user name in Arabic puts %TEMP% under a non-ASCII path; C:\ProgramData is ASCII and writable for new sub-folders.
            root = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "DropFlow", "tmp");
        }
        var dir = System.IO.Path.Combine(root, "dropflow-ai-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return new WorkDirectory(dir);
    }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public static bool IsAscii(string s) => s.All(c => c < 128);

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); }
        catch (IOException) { /* best effort */ }
        catch (UnauthorizedAccessException) { /* best effort */ }
    }
}
