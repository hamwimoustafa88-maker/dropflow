namespace DropFlow.Core.Operations;

/// <summary>External programs DropFlow can drive. Resolved at runtime by an <see cref="IEngineProvider"/>.</summary>
public enum EngineId
{
    None,
    Ffmpeg,
    Ffprobe,
    Qpdf,
    LibreOffice,
    SevenZip,
    Pandoc,
    Tesseract,
}

public enum ToolOptionKind { Choice, Number, Text, Bool, Password, Folder, Range }

/// <summary>
/// One input a tool needs before it can run. The UI renders a generic panel from these, so a new tool never needs new UI.
/// Labels come from resources using the key <c>opt.{operation}.{Key}</c> (falling back to <c>opt.{Key}</c>).
/// </summary>
public sealed record ToolOption(
    string Key,
    ToolOptionKind Kind,
    object? Default = null,
    IReadOnlyList<string>? Choices = null,
    double? Min = null,
    double? Max = null,
    bool Required = false);

/// <summary>Whether a converter can run right now.</summary>
public enum Availability
{
    Ready,
    /// <summary>Implemented, but the engine it needs is not installed yet.</summary>
    NeedsEngine,
}

public sealed record ConversionJob(
    IReadOnlyList<string> Inputs,
    string Operation,
    IReadOnlyDictionary<string, object?>? Options = null,
    string? OutputDirectory = null)
{
    public static readonly IReadOnlyDictionary<string, object?> NoOptions = new Dictionary<string, object?>();

    public IReadOnlyDictionary<string, object?> Opts => Options ?? NoOptions;

    public string? GetString(string key, string? fallback = null) =>
        Opts.TryGetValue(key, out var v) && v is not null ? Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) : fallback;

    public int GetInt(string key, int fallback = 0) =>
        Opts.TryGetValue(key, out var v) && v is not null ? Convert.ToInt32(v, System.Globalization.CultureInfo.InvariantCulture) : fallback;

    public double GetDouble(string key, double fallback = 0) =>
        Opts.TryGetValue(key, out var v) && v is not null ? Convert.ToDouble(v, System.Globalization.CultureInfo.InvariantCulture) : fallback;

    public bool GetBool(string key, bool fallback = false) =>
        Opts.TryGetValue(key, out var v) && v is not null ? Convert.ToBoolean(v, System.Globalization.CultureInfo.InvariantCulture) : fallback;
}

public sealed record JobProgress(double Fraction, string? Message = null);

/// <param name="Note">Optional message for a SUCCESSFUL run the user should know about (e.g. "already optimal, original kept").</param>
public sealed record ConversionResult(bool Success, IReadOnlyList<string> Outputs, string? Error = null, TimeSpan Duration = default, string? Note = null)
{
    public static ConversionResult Ok(IReadOnlyList<string> outputs, TimeSpan duration = default, string? note = null) => new(true, outputs, null, duration, note);
    public static ConversionResult Fail(string error, TimeSpan duration = default) => new(false, [], error, duration);
}

/// <summary>
/// Implements one or more operations. An operation id is either <c>convert:{targetExtension}</c> (a format conversion)
/// or a tool id from the catalog such as <c>img.compress</c>. Converters never touch the UI.
/// </summary>
public interface IConverter
{
    /// <summary>Operation ids this converter implements, e.g. "convert:png", "img.compress".</summary>
    IReadOnlyCollection<string> Operations { get; }

    /// <summary>True when this converter can run <paramref name="operation"/> on a file with this extension (no dot).</summary>
    bool Supports(string operation, string inputExtension);

    /// <summary>Engine the operation needs, or <see cref="EngineId.None"/> for pure in-process work.</summary>
    EngineId EngineFor(string operation) => EngineId.None;

    /// <summary>Options to ask the user for. Empty means "run immediately with defaults".</summary>
    IReadOnlyList<ToolOption> OptionsFor(string operation) => [];

    /// <summary>Runs the operation. Must honour cancellation, report progress, and never overwrite or delete inputs.</summary>
    Task<ConversionResult> RunAsync(ConversionJob job, IProgress<JobProgress> progress, CancellationToken cancellationToken);
}

/// <summary>Tells the core which external engines are installed.</summary>
public interface IEngineProvider
{
    /// <summary>Full path of the engine's executable, or null when it is not installed.</summary>
    string? Locate(EngineId engine);
}

/// <summary>Deletes produced outputs on undo (Recycle Bin on Windows). Abstracted so Core stays OS-neutral and testable.</summary>
public interface IFileDisposer
{
    void Dispose(string path);
}

public static class Operation
{
    public const string ConvertPrefix = "convert:";

    public static string Convert(string targetExtension) => ConvertPrefix + targetExtension.TrimStart('.').ToLowerInvariant();

    public static bool IsConvert(string operation) => operation.StartsWith(ConvertPrefix, StringComparison.Ordinal);

    public static string TargetOf(string convertOperation) => convertOperation[ConvertPrefix.Length..];
}
