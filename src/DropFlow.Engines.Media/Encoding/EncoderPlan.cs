namespace DropFlow.Engines.Media.Encoding;

/// <summary>Environment the planner needs besides the settings and the probed file.</summary>
public sealed record PlanContext
{
    /// <summary>Output file path. Null leaves <see cref="EncoderPlan.OutputPlaceholder"/> in the arguments.</summary>
    public string? OutputPath { get; init; }

    /// <summary>Encoders in the FFmpeg build (<see cref="FfmpegEncoders.Parse"/>). Null assumes a standard GPL build without hardware encoders.</summary>
    public FfmpegEncoders? Encoders { get; init; }

    /// <summary>Hardware encoders in the build (<see cref="HardwareEncoders.Detect(string)"/>). Null = none.</summary>
    public HardwareCapabilities? Hardware { get; init; }

    /// <summary>Optional trial-encode check per hardware encoder name (see <see cref="HardwareEncoders.TrialArguments"/>).</summary>
    public Func<string, bool>? HardwareWorks { get; init; }

    /// <summary>
    /// Base name of the two-pass statistics files. Deliberately a bare file name: the caller runs FFmpeg with a scratch
    /// directory as working directory, which avoids drive-letter colons breaking <c>-x265-params</c>.
    /// </summary>
    public string PassLogName { get; init; } = "dropflow-pass";
}

/// <summary>
/// What to run. <see cref="Passes"/> holds one FFmpeg argument list (two for software target-size encodes), each complete
/// with global options, the input and the output. The lists contain no executable name.
/// </summary>
public sealed record EncoderPlan
{
    /// <summary>Stands in for the output path when <see cref="PlanContext.OutputPath"/> was not given.</summary>
    public const string OutputPlaceholder = "{output}";

    /// <summary>Stands in for the input path when <see cref="MediaInfo.Path"/> is empty.</summary>
    public const string InputPlaceholder = "{input}";

    public IReadOnlyList<IReadOnlyList<string>> Passes { get; init; } = [];

    /// <summary>Extension of the file the plan produces, without the dot (mp4, mkv, webm, mov, gif, m4a, mp3, opus, flac ...).</summary>
    public string OutputExtension { get; init; } = "";

    /// <summary>Non-fatal adjustments the planner made (codec swapped for the container, encoder missing, scaled down ...).</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>Set when the settings cannot be satisfied; <see cref="Passes"/> is then empty.</summary>
    public string? Error { get; init; }

    public string? VideoEncoder { get; init; }
    public string? AudioEncoder { get; init; }
    public int? VideoBitrateKbps { get; init; }
    public int? AudioBitrateKbps { get; init; }

    /// <summary>Length of the produced file in seconds (after trimming); the denominator for progress.</summary>
    public double? OutputDurationSeconds { get; init; }

    /// <summary>Output width/height after scaling, when known.</summary>
    public int? OutputWidth { get; init; }
    public int? OutputHeight { get; init; }

    public bool Ok => Error is null;
    public bool IsTwoPass => Passes.Count == 2;

    internal static EncoderPlan Fail(string error, IReadOnlyList<string>? warnings = null) =>
        new() { Error = error, Warnings = warnings ?? [] };
}
