using System.Text.RegularExpressions;

namespace DropFlow.Engines.Media.Ffmpeg;

/// <summary>Pieces shared by every FFmpeg command line DropFlow builds.</summary>
internal static class FfmpegCommand
{
    /// <summary>Where analysis and first-pass output goes.</summary>
    public static string NullDevice => OperatingSystem.IsWindows() ? "NUL" : "/dev/null";

    /// <summary>
    /// Global options for a command that writes a file: banner off, never read stdin, never overwrite an existing output
    /// (<c>-n</c>), machine-readable progress on stdout. Log level stays at <c>info</c> because loudnorm and silencedetect
    /// print their results there.
    /// </summary>
    public static List<string> Global(bool noOverwrite = true)
    {
        var args = new List<string> { "-hide_banner", "-nostdin" };
        if (noOverwrite) args.Add("-n");
        args.AddRange(["-nostats", "-progress", "pipe:1"]);
        return args;
    }

    /// <summary>Global options for an analysis run that writes nothing (<c>-f null</c>): no <c>-n</c>, no progress.</summary>
    public static List<string> GlobalAnalysis() => ["-hide_banner", "-nostdin", "-nostats"];

    /// <summary>The ffprobe command that returns everything the app needs as JSON.</summary>
    public static IReadOnlyList<string> Probe(string input) =>
        ["-v", "error", "-print_format", "json", "-show_format", "-show_streams", "-i", input];

    public static IReadOnlyList<string> ListEncoders() => ["-hide_banner", "-encoders"];

    /// <summary>
    /// Escapes a literal for use in an image-sequence / segment output pattern, where <c>%</c> starts a format code.
    /// "100% done" would otherwise be read as a broken <c>% d</c> pattern.
    /// </summary>
    public static string EscapePattern(string literal) => literal.Replace("%", "%%");
}

/// <summary>Turns FFmpeg's stderr into one short, readable reason.</summary>
internal static partial class FfmpegErrors
{
    private const string Corrupt = "The file looks corrupt, incomplete, or is not a media file FFmpeg understands.";

    public static string Summarize(string stderr, int exitCode = 1)
    {
        var lines = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(StripLogPrefix)
            .Where(l => l.Length > 0)
            .ToList();

        // Pass 1: specific causes, searching from the end (FFmpeg ends with the generic "Conversion failed!").
        for (var i = lines.Count - 1; i >= 0; i--)
        {
            var l = lines[i];

            if (UnknownEncoder().Match(l) is { Success: true } enc)
                return $"This FFmpeg build has no '{enc.Groups[1].Value}' encoder.";
            if (l.Contains("Encoder (codec", StringComparison.OrdinalIgnoreCase) && l.Contains("not found", StringComparison.OrdinalIgnoreCase))
                return "This FFmpeg build is missing a required encoder (" + l + ").";
            if (l.Contains("moov atom not found", StringComparison.OrdinalIgnoreCase)
                || l.Contains("Invalid data found when processing input", StringComparison.OrdinalIgnoreCase)
                || (l.Contains("Error while decoding stream", StringComparison.OrdinalIgnoreCase) && l.Contains("Invalid data", StringComparison.OrdinalIgnoreCase)))
                return Corrupt;
            if (l.Contains("No such file or directory", StringComparison.OrdinalIgnoreCase))
                return "FFmpeg could not open a file (does it still exist?): " + l;
            if (l.Contains("Permission denied", StringComparison.OrdinalIgnoreCase))
                return "Permission denied while reading or writing a file: " + l;
            if (l.Contains("No space left on device", StringComparison.OrdinalIgnoreCase))
                return "The disk is full.";
            if (l.Contains("matches no streams", StringComparison.OrdinalIgnoreCase)
                || l.Contains("does not contain any stream", StringComparison.OrdinalIgnoreCase))
                return "The file does not contain the audio or video stream this tool needs.";
            if (l.Contains("Output file is empty", StringComparison.OrdinalIgnoreCase)
                || l.Contains("Output file #0 does not contain any stream", StringComparison.OrdinalIgnoreCase))
                return "Nothing was produced (the requested time may be past the end of the file).";
            if (l.Contains("Error initializing filter", StringComparison.OrdinalIgnoreCase)
                || l.Contains("Error opening filters", StringComparison.OrdinalIgnoreCase)
                || l.Contains("Error reinitializing filters", StringComparison.OrdinalIgnoreCase)
                || l.Contains("Failed to inject frame into filter network", StringComparison.OrdinalIgnoreCase)
                || l.Contains("No such filter", StringComparison.OrdinalIgnoreCase))
                return "FFmpeg could not build the filter for this file: " + CauseBefore(lines, i);
            if (l.Contains("Unrecognized option", StringComparison.OrdinalIgnoreCase)
                || l.Contains("Option not found", StringComparison.OrdinalIgnoreCase))
                return "This FFmpeg version does not support an option DropFlow needs (" + l + "). Update FFmpeg.";
            if (l.Contains("out of range", StringComparison.OrdinalIgnoreCase)
                || l.Contains("Error setting option", StringComparison.OrdinalIgnoreCase)
                || l.Contains("Unable to parse option value", StringComparison.OrdinalIgnoreCase))
                return "FFmpeg did not accept a setting: " + Truncate(l);
        }

        // Pass 2: generic rejections.
        for (var i = lines.Count - 1; i >= 0; i--)
        {
            if (lines[i].Contains("Invalid argument", StringComparison.OrdinalIgnoreCase)
                || lines[i].Contains("Could not write header", StringComparison.OrdinalIgnoreCase))
                return "FFmpeg rejected the output settings: " + CauseBefore(lines, i);
        }

        var last = lines.LastOrDefault(l => !l.StartsWith("Conversion failed", StringComparison.OrdinalIgnoreCase));
        return last is null ? $"FFmpeg failed (exit code {exitCode})." : "FFmpeg failed: " + Truncate(last);
    }

    /// <summary>
    /// FFmpeg prints the real reason a few lines before its generic complaint ("Error reinitializing filters!", "Invalid argument").
    /// Looks back a few lines for one that names a cause; falls back to the complaint itself.
    /// </summary>
    private static string CauseBefore(List<string> lines, int index)
    {
        for (var j = index - 1; j >= Math.Max(0, index - 6); j--)
            if (CauseHint().IsMatch(lines[j]) && !GenericComplaint().IsMatch(lines[j])) return Truncate(lines[j]);
        return Truncate(lines[index]);
    }

    private static string StripLogPrefix(string line) => LogPrefix().Replace(line, "").Trim();

    private static string Truncate(string s) => s.Length <= 300 ? s : s[..300] + "...";

    // "[libx264 @ 0000020a5b3f1a40] message" -> "message"
    [GeneratedRegex(@"^\[[^\]@]+@\s*[0-9a-fA-Fx]+\]\s*")]
    private static partial Regex LogPrefix();

    [GeneratedRegex(@"Unknown encoder '([^']+)'", RegexOptions.IgnoreCase)]
    private static partial Regex UnknownEncoder();

    [GeneratedRegex(@"out of range|invalid|cannot|can't|unable|not found|unrecognized|unsupported|does not|failed|error", RegexOptions.IgnoreCase)]
    private static partial Regex CauseHint();

    [GeneratedRegex(@"Error reinitializing filters|Error opening filters|Failed to inject frame|Error while processing the decoded data|Conversion failed", RegexOptions.IgnoreCase)]
    private static partial Regex GenericComplaint();
}
