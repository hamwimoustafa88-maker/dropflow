using DropFlow.AI.Speech;

namespace DropFlow.AI.Tests;

public class FfmpegAudioFailureTests
{
    // What FFmpeg 9.0 really prints for a video without sound: the cause is NOT the last line.
    private const string Ffmpeg9NoAudio = """
        Stream map '' matches no streams.
        To ignore this, add a trailing '?' to the map.
        Failed to set value '0:a:0' for option 'map': Invalid argument
        Error parsing options for output file C:\x\out.wav.
        Error opening output files: Invalid argument
        """;

    [Fact]
    public void A_video_without_sound_is_recognised_from_the_whole_error_text()
    {
        var message = FfmpegAudio.ExplainFailure(Ffmpeg9NoAudio, "Error opening output files: Invalid argument");
        Assert.Contains("no audio track", message);
    }

    [Fact]
    public void A_damaged_file_is_recognised_anywhere_in_the_text()
    {
        var message = FfmpegAudio.ExplainFailure("moov atom not found\nInvalid data found when processing input\nsomething else", "something else");
        Assert.Contains("damaged", message);
    }

    [Fact]
    public void An_unknown_failure_shows_the_last_line_not_the_whole_log()
    {
        var message = FfmpegAudio.ExplainFailure("line one\nline two\nthe real reason", "the real reason");
        Assert.Equal("ffmpeg failed to extract the audio: the real reason", message);
    }

    [Fact]
    public void Old_single_argument_use_still_works()
    {
        Assert.Contains("no audio track", FfmpegAudio.ExplainFailure("Output file #0 does not contain any stream"));
        Assert.Equal("ffmpeg failed to extract the audio.", FfmpegAudio.ExplainFailure(null));
    }
}
