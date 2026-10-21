namespace DropFlow.Engines.Media.Ffmpeg;

/// <summary>Outcome of <see cref="BitrateMath.PlanTargetSize"/>.</summary>
public sealed record TargetSizePlan(
    int VideoKbps,
    int AudioKbps,
    long TargetBytes,
    double DurationSeconds,
    bool Feasible,
    string? Problem)
{
    /// <summary>Size the file is expected to have (stream payload only; the container adds the safety margin on top).</summary>
    public long ExpectedBytes => (long)((VideoKbps + AudioKbps) * 1000.0 * DurationSeconds / 8);
}

/// <summary>
/// Bitrate arithmetic for "make this video X MB". One MB here is 1,000,000 bytes (not MiB), so a result that is "10 MB"
/// by this definition can never exceed a 10 MB (or 10 MiB) upload limit. 3% of the budget is held back for container
/// overhead and for rate-control error, so the file lands 1-4% under the target, comfortably inside the +/-5% promise.
/// </summary>
public static class BitrateMath
{
    public const long BytesPerMb = 1_000_000;

    /// <summary>Fraction of the budget kept back for muxing overhead and rate-control overshoot.</summary>
    public const double SafetyMargin = 0.03;

    /// <summary>Below this a video stream is not worth producing.</summary>
    public const int MinVideoKbps = 50;

    public const int MinAudioKbps = 24;

    /// <param name="targetMb">Wanted file size in MB (1 MB = 1,000,000 bytes).</param>
    /// <param name="durationSeconds">Length of the output (after any trim).</param>
    /// <param name="hasAudio">False when the output has no audio track.</param>
    /// <param name="preferredAudioKbps">Audio bitrate to reserve; reduced when it would eat too much of a small budget.</param>
    public static TargetSizePlan PlanTargetSize(double targetMb, double durationSeconds, bool hasAudio, int preferredAudioKbps)
    {
        var targetBytes = (long)Math.Round(targetMb * BytesPerMb);
        if (targetMb <= 0 || durationSeconds <= 0)
            return new TargetSizePlan(0, 0, targetBytes, durationSeconds, false, "The target size and the video length must both be greater than zero.");

        var budgetKbps = targetBytes * 8.0 * (1 - SafetyMargin) / durationSeconds / 1000.0;

        var audio = 0;
        if (hasAudio)
        {
            // Never let audio take more than a third of the budget; keep at least MinAudioKbps.
            var cap = (int)Math.Floor(budgetKbps / 3);
            audio = Math.Max(MinAudioKbps, Math.Min(preferredAudioKbps, cap));
        }

        var video = (int)Math.Floor(budgetKbps - audio);
        if (video < MinVideoKbps)
        {
            return new TargetSizePlan(Math.Max(video, 0), audio, targetBytes, durationSeconds, false,
                $"{targetMb:0.#} MB is too small for {durationSeconds:0.#} s of video: only {Math.Max(video, 0)} kbps would be left for the picture.");
        }
        return new TargetSizePlan(video, audio, targetBytes, durationSeconds, true, null);
    }

    /// <summary>Rough "average acceptable bitrate" for a source, used when an encoder has no constant-quality mode.</summary>
    public static int EstimateVideoKbps(int width, int height, double fps, double bitsPerPixel = 0.1)
    {
        if (width <= 0 || height <= 0 || fps <= 0) return 4000;
        return (int)Math.Clamp(width * (double)height * fps * bitsPerPixel / 1000.0, 200, 80_000);
    }

    /// <summary>Bits per pixel per frame; below ~0.05 (H.264) the picture falls apart, so the planner scales down instead.</summary>
    public static double BitsPerPixel(int videoKbps, int width, int height, double fps)
    {
        if (width <= 0 || height <= 0 || fps <= 0) return double.PositiveInfinity;
        return videoKbps * 1000.0 / (width * (double)height * fps);
    }
}
