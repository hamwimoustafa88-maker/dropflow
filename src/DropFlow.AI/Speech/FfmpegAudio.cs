namespace DropFlow.AI.Speech;

/// <summary>Pure helpers for the ffmpeg step that turns any audio or video file into what Whisper reads.</summary>
public static class FfmpegAudio
{
    /// <summary>
    /// Arguments that extract the first audio stream as 16 kHz, mono, signed 16-bit WAV. Video, subtitles and data streams are dropped.
    /// Arguments are a list (never a shell string), so Arabic names and spaces are safe. <paramref name="input"/> must be an absolute path
    /// so a name starting with '-' can never be read as an option.
    /// </summary>
    public static IReadOnlyList<string> BuildExtractArguments(string input, string outputWav) =>
    [
        "-hide_banner", "-loglevel", "error", "-nostdin", "-y",
        "-i", input,
        "-map", "0:a:0",
        "-vn", "-sn", "-dn",
        "-ac", "1",
        "-ar", "16000",
        "-c:a", "pcm_s16le",
        "-f", "wav",
        outputWav,
    ];

    /// <summary>Turns ffmpeg's last error line into a sentence the user can act on.</summary>
    public static string ExplainFailure(string? ffmpegMessage)
    {
        var m = ffmpegMessage ?? "";
        if (m.Contains("matches no streams", StringComparison.OrdinalIgnoreCase)
            || m.Contains("does not contain any stream", StringComparison.OrdinalIgnoreCase))
            return "The file has no audio track to transcribe.";
        if (m.Contains("Invalid data found", StringComparison.OrdinalIgnoreCase)
            || m.Contains("moov atom not found", StringComparison.OrdinalIgnoreCase)
            || m.Contains("could not find codec parameters", StringComparison.OrdinalIgnoreCase))
            return "The file is damaged or is not a valid media file.";
        if (m.Contains("No such file", StringComparison.OrdinalIgnoreCase) || m.Contains("Permission denied", StringComparison.OrdinalIgnoreCase))
            return "ffmpeg could not open the file: " + m;
        return string.IsNullOrWhiteSpace(m) ? "ffmpeg failed to extract the audio." : "ffmpeg failed to extract the audio: " + m;
    }
}
