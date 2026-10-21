using DropFlow.Engines.Media.Ffmpeg;
using DropFlow.TestSupport;
using static DropFlow.Engines.Media.Tests.Integration.FfmpegSamples;

namespace DropFlow.Engines.Media.Tests.Integration;

/// <summary>The video and audio tools against the real FFmpeg. Skipped when it is not installed.</summary>
public class VideoToolIntegrationTests
{
    [EngineFact(EngineId.Ffmpeg)]
    public async Task Compress_makes_a_smaller_playable_file()
    {
        using var dir = new TempDir();
        var input = await Video(Path.Combine(dir.Path, "a.mp4"), seconds: 8, width: 640, height: 360);

        var result = await Run("vid.compress", input, Opts(("crf", 36), ("preset", "veryfast")));

        Assert.True(result.Success, result.Error);
        Assert.True(new FileInfo(result.Outputs.Single()).Length < new FileInfo(input).Length);
        var info = await Probe(result.Outputs.Single());
        Assert.Equal("h264", info.Video!.CodecName);
        Assert.InRange(info.DurationSeconds!.Value, 7.5, 8.5);
        Assert.True(info.HasAudio);
    }

    [EngineFact(EngineId.Ffmpeg)]
    public async Task Compress_with_a_height_limit_scales_down()
    {
        using var dir = new TempDir();
        var input = await Video(Path.Combine(dir.Path, "a.mp4"), seconds: 2, width: 1280, height: 720);
        var result = await Run("vid.compress", input, Opts(("maxHeight", "360")));
        Assert.True(result.Success, result.Error);
        var video = (await Probe(result.Outputs.Single())).Video!;
        Assert.Equal(360, video.Height);
        Assert.Equal(640, video.Width);
    }

    [EngineFact(EngineId.Ffmpeg)]
    public async Task Mute_removes_audio_and_copies_the_video()
    {
        using var dir = new TempDir();
        var input = await Video(Path.Combine(dir.Path, "a.mp4"), seconds: 4);
        var result = await Run("vid.mute", input);
        Assert.True(result.Success, result.Error);
        var info = await Probe(result.Outputs.Single());
        Assert.False(info.HasAudio);
        Assert.Equal("h264", info.Video!.CodecName);
        Assert.InRange(info.DurationSeconds!.Value, 3.8, 4.2);
    }

    [EngineFact(EngineId.Ffmpeg)]
    public async Task Metadata_sets_unicode_tags_and_strip_removes_them()
    {
        using var dir = new TempDir();
        var input = await Video(Path.Combine(dir.Path, "a.mp4"), seconds: 2);

        var set = await Run("vid.metadata", input, Opts(("title", "عطلة الصيف"), ("artist", "موسى"), ("comment", "ملاحظة")));
        Assert.True(set.Success, set.Error);
        var tags = (await Probe(set.Outputs.Single())).Tags;
        Assert.Equal("عطلة الصيف", tags["title"]);
        Assert.Equal("موسى", tags["artist"]);
        Assert.Equal("ملاحظة", tags["comment"]);

        var stripped = await Run("vid.metadata", set.Outputs.Single(), Opts(("strip", true)));
        Assert.True(stripped.Success, stripped.Error);
        var after = (await Probe(stripped.Outputs.Single())).Tags;
        Assert.False(after.ContainsKey("title"));
        Assert.False(after.ContainsKey("artist"));
        Assert.False(after.ContainsKey("comment"));
    }

    [EngineFact(EngineId.Ffmpeg)]
    public async Task Trim_by_copy_lands_on_a_keyframe_and_reencode_is_exact()
    {
        using var dir = new TempDir();
        var input = await Video(Path.Combine(dir.Path, "a.mp4"), seconds: 8);   // a keyframe every second

        var copy = await Run("vid.trim", input, Opts(("start", "2"), ("end", "5")));
        Assert.True(copy.Success, copy.Error);
        Assert.InRange((await Probe(copy.Outputs.Single())).DurationSeconds!.Value, 2.5, 3.6);

        var exact = await Run("vid.trim", input, Opts(("start", "2.5"), ("end", "5.5"), ("reencode", true)));
        Assert.True(exact.Success, exact.Error);
        Assert.InRange((await Probe(exact.Outputs.Single())).DurationSeconds!.Value, 2.8, 3.2);
    }

    [EngineFact(EngineId.Ffmpeg)]
    public async Task Crop_by_aspect_and_by_rectangle()
    {
        using var dir = new TempDir();
        var input = await Video(Path.Combine(dir.Path, "a.mp4"), seconds: 2);

        var square = await Run("vid.crop", input, Opts(("aspect", "1:1")));
        Assert.True(square.Success, square.Error);
        var sq = (await Probe(square.Outputs.Single())).Video!;
        Assert.Equal((240, 240), (sq.Width, sq.Height));

        var custom = await Run("vid.crop", input, Opts(("aspect", "custom"), ("x", 10), ("y", 10), ("w", 160), ("h", 120)));
        Assert.True(custom.Success, custom.Error);
        var cu = (await Probe(custom.Outputs.Single())).Video!;
        Assert.Equal((160, 120), (cu.Width, cu.Height));
        Assert.True((await Probe(custom.Outputs.Single())).HasAudio);
    }

    [EngineFact(EngineId.Ffmpeg)]
    public async Task Speed_halves_the_length_at_2x_and_keeps_the_sound()
    {
        using var dir = new TempDir();
        var input = await Video(Path.Combine(dir.Path, "a.mp4"), seconds: 6);

        var fast = await Run("vid.speed", input, Opts(("speed", 2)));
        Assert.True(fast.Success, fast.Error);
        var info = await Probe(fast.Outputs.Single());
        Assert.InRange(info.DurationSeconds!.Value, 2.6, 3.4);
        Assert.True(info.HasAudio);

        var slow = await Run("vid.speed", input, Opts(("speed", 0.5)));
        Assert.True(slow.Success, slow.Error);
        Assert.InRange((await Probe(slow.Outputs.Single())).DurationSeconds!.Value, 11, 13);
    }

    [EngineFact(EngineId.Ffmpeg)]
    public async Task Snapshot_writes_full_size_png_files_into_a_new_folder()
    {
        using var dir = new TempDir();
        var input = await Video(Path.Combine(dir.Path, "a.mp4"), seconds: 6);

        var result = await Run("vid.snapshot", input, Opts(("times", "1, 2.5")));
        Assert.True(result.Success, result.Error);
        var folder = result.Outputs.Single();
        Assert.True(Directory.Exists(folder));
        var files = Directory.GetFiles(folder, "*.png").OrderBy(f => f).ToList();
        Assert.Equal(2, files.Count);
        Assert.All(files, f => Assert.Equal((320, 240), PngSize(f)));
        Assert.NotEqual(File.ReadAllBytes(files[0]), File.ReadAllBytes(files[1]));   // the test pattern has a running counter

        var every = await Run("vid.snapshot", input, Opts(("times", ""), ("every", 2)));
        Assert.True(every.Success, every.Error);
        Assert.InRange(Directory.GetFiles(every.Outputs.Single(), "*.png").Length, 3, 4);
    }

    [EngineFact(EngineId.Ffmpeg)]
    public async Task Split_by_copy_and_by_reencode_make_clips_that_add_up()
    {
        using var dir = new TempDir();
        var input = await Video(Path.Combine(dir.Path, "a.mp4"), seconds: 6);

        foreach (var reencode in new[] { false, true })
        {
            var result = await Run("vid.split", input, Opts(("points", "2"), ("reencode", reencode)));
            Assert.True(result.Success, result.Error);
            var clips = Directory.GetFiles(result.Outputs.Single()).OrderBy(f => f).ToList();
            Assert.Equal(2, clips.Count);
            var first = await Probe(clips[0]);
            var second = await Probe(clips[1]);
            Assert.InRange(first.DurationSeconds!.Value, 1.8, 2.3);
            Assert.InRange(first.DurationSeconds.Value + second.DurationSeconds!.Value, 5.5, 6.5);
        }
    }

    [EngineFact(EngineId.Ffmpeg)]
    public async Task Join_copies_identical_clips_and_re_encodes_clips_of_different_sizes()
    {
        using var dir = new TempDir();
        var a = await Video(Path.Combine(dir.Path, "a.mp4"), seconds: 4);
        var b = await Video(Path.Combine(dir.Path, "b.mp4"), seconds: 4);
        var small = await Video(Path.Combine(dir.Path, "small.mp4"), seconds: 4, width: 160, height: 120);
        var silent = await Video(Path.Combine(dir.Path, "silent.mp4"), seconds: 4, audio: false);

        var same = await RunMulti("vid.join", [a, b]);
        Assert.True(same.Success, same.Error);
        var info = await Probe(same.Outputs.Single());
        Assert.InRange(info.DurationSeconds!.Value, 7.6, 8.4);
        Assert.Equal((320, 240), (info.Video!.Width, info.Video.Height));

        var mixed = await RunMulti("vid.join", [a, small, silent]);
        Assert.True(mixed.Success, mixed.Error);
        var mix = await Probe(mixed.Outputs.Single());
        Assert.Equal((320, 240), (mix.Video!.Width, mix.Video.Height));   // everything fitted to the first clip
        Assert.InRange(mix.DurationSeconds!.Value, 11.4, 12.6);
        Assert.True(mix.HasAudio);                                       // the silent clip got silence, not a missing track
    }

    [EngineFact(EngineId.Ffmpeg)]
    public async Task Redact_changes_the_picture_but_not_the_geometry_or_length()
    {
        using var dir = new TempDir();
        var input = await Video(Path.Combine(dir.Path, "a.mp4"), seconds: 4);

        foreach (var mode in new[] { "blur", "solid" })
        {
            var result = await Run("vid.redact", input, Opts(("x", 40), ("y", 40), ("w", 160), ("h", 120), ("mode", mode)));
            Assert.True(result.Success, $"{mode}: {result.Error}");
            var info = await Probe(result.Outputs.Single());
            Assert.Equal((320, 240), (info.Video!.Width, info.Video.Height));
            Assert.InRange(info.DurationSeconds!.Value, 3.8, 4.2);
            Assert.True(info.HasAudio);
        }

        // Frame comparison: a redacted frame must differ from a plain re-encode of the same frame.
        var redacted = (await Run("vid.redact", input, Opts(("x", 0), ("y", 0), ("w", 320), ("h", 240), ("mode", "solid")))).Outputs.Single();
        var plain = (await Run("vid.compress", input, Opts(("crf", 20)))).Outputs.Single();
        var framesRedacted = (await Run("vid.snapshot", redacted, Opts(("times", "1")))).Outputs.Single();
        var framesPlain = (await Run("vid.snapshot", plain, Opts(("times", "1")))).Outputs.Single();
        Assert.NotEqual(File.ReadAllBytes(Directory.GetFiles(framesRedacted).Single()), File.ReadAllBytes(Directory.GetFiles(framesPlain).Single()));
    }

    [EngineFact(EngineId.Ffmpeg)]
    public async Task Redacting_only_a_time_range_leaves_the_rest_alone()
    {
        using var dir = new TempDir();
        var input = await Video(Path.Combine(dir.Path, "a.mp4"), seconds: 4);
        // Same encode settings both times, so any frame difference comes from the box.
        var always = (await Run("vid.redact", input, Opts(("w", 320), ("h", 240), ("mode", "solid")))).Outputs.Single();
        var later = (await Run("vid.redact", input, Opts(("w", 320), ("h", 240), ("mode", "solid"), ("start", "2.5")))).Outputs.Single();

        var a1 = (await Run("vid.snapshot", always, Opts(("times", "1")))).Outputs.Single();
        var b1 = (await Run("vid.snapshot", later, Opts(("times", "1")))).Outputs.Single();
        var a3 = (await Run("vid.snapshot", always, Opts(("times", "3.5")))).Outputs.Single();
        var b3 = (await Run("vid.snapshot", later, Opts(("times", "3.5")))).Outputs.Single();

        byte[] Frame(string folder) => File.ReadAllBytes(Directory.GetFiles(folder).Single());
        Assert.NotEqual(Frame(a1), Frame(b1));   // at 1 s only the always-on box covers the picture
        Assert.Equal(Frame(a3).Length, Frame(b3).Length);
    }

    [EngineFact(EngineId.Ffmpeg)]
    public async Task Target_size_lands_within_five_percent()
    {
        using var dir = new TempDir();
        var input = await Video(Path.Combine(dir.Path, "long.mp4"), seconds: 30);

        var result = await Run("vid.target-size", input, Opts(("sizeMb", 1.0), ("audioBitrate", 64)));

        Assert.True(result.Success, result.Error);
        var size = new FileInfo(result.Outputs.Single()).Length;
        Assert.InRange(size / 1_000_000.0, 0.95, 1.05);
        Assert.InRange((await Probe(result.Outputs.Single())).DurationSeconds!.Value, 29.5, 30.5);
    }

    [EngineFact(EngineId.Ffmpeg)]
    public async Task Burn_subs_draws_text_for_srt_with_arabic_and_a_hostile_file_name()
    {
        using var dir = new TempDir();
        var input = await Video(Path.Combine(dir.Path, "a.mp4"), seconds: 6, width: 640, height: 360);
        var folder = Path.Combine(dir.Path, "it's [1], v=2; ترجمة");
        Directory.CreateDirectory(folder);
        var early = Path.Combine(folder, "early.srt");
        var late = Path.Combine(folder, "late.srt");
        await File.WriteAllTextAsync(early, "1\n00:00:00,500 --> 00:00:03,000\nمرحبا بالعالم - Hello\n", new System.Text.UTF8Encoding(true));
        await File.WriteAllTextAsync(late, "1\n00:00:04,000 --> 00:00:05,500\nمرحبا بالعالم - Hello\n", new System.Text.UTF8Encoding(true));

        var a = await Run("vid.burn-subs", input, Opts(("subtitle", early)));
        var b = await Run("vid.burn-subs", input, Opts(("subtitle", late)));

        Assert.True(a.Success, a.Error);
        Assert.True(b.Success, b.Error);
        var info = await Probe(a.Outputs.Single());
        Assert.Equal((640, 360), (info.Video!.Width, info.Video.Height));
        Assert.InRange(info.DurationSeconds!.Value, 5.5, 6.5);

        // At 1.5 s only "early" shows text; both outputs were encoded identically otherwise.
        byte[] FrameAt(string video, string time) => File.ReadAllBytes(Directory.GetFiles(Run("vid.snapshot", video, Opts(("times", time))).GetAwaiter().GetResult().Outputs.Single()).Single());
        Assert.NotEqual(FrameAt(a.Outputs.Single(), "1.5"), FrameAt(b.Outputs.Single(), "1.5"));
        Assert.NotEqual(FrameAt(a.Outputs.Single(), "4.5"), FrameAt(b.Outputs.Single(), "4.5"));
    }

    [EngineFact(EngineId.Ffmpeg)]
    public async Task Burn_subs_finds_a_sidecar_next_to_the_video()
    {
        using var dir = new TempDir();
        var input = await Video(Path.Combine(dir.Path, "episode 1.mp4"), seconds: 3, width: 640, height: 360);
        await File.WriteAllTextAsync(Path.Combine(dir.Path, "episode 1.srt"), "1\n00:00:00,500 --> 00:00:02,000\nHello\n");
        var result = await Run("vid.burn-subs", input);
        Assert.True(result.Success, result.Error);
        Assert.EndsWith("episode 1 (subtitled).mp4", result.Outputs.Single());
    }

    [EngineFact(EngineId.Ffmpeg)]
    public async Task Burn_subs_with_a_corrupt_subtitle_fails_before_encoding()
    {
        using var dir = new TempDir();
        var input = await Video(Path.Combine(dir.Path, "a.mp4"), seconds: 2);
        var bad = dir.WriteText("bad.srt", "not subtitles");
        var result = await Run("vid.burn-subs", input, Opts(("subtitle", bad)));
        Assert.False(result.Success);
        Assert.Equal(2, Directory.GetFiles(dir.Path).Length);
    }
}

public class AudioToolIntegrationTests
{
    [EngineFact(EngineId.Ffmpeg)]
    public async Task Compress_shrinks_wav_to_m4a()
    {
        using var dir = new TempDir();
        var input = await Wav(Path.Combine(dir.Path, "a.wav"), seconds: 4);
        var result = await Run("aud.compress", input);
        Assert.True(result.Success, result.Error);
        Assert.EndsWith(".m4a", result.Outputs.Single());
        Assert.True(new FileInfo(result.Outputs.Single()).Length < new FileInfo(input).Length / 4);
    }

    [EngineFact(EngineId.Ffmpeg)]
    public async Task Metadata_writes_tags_to_mp3()
    {
        using var dir = new TempDir();
        var mp3 = (await Run("convert:mp3", await Wav(Path.Combine(dir.Path, "a.wav"), seconds: 2))).Outputs.Single();

        var result = await Run("aud.metadata", mp3, Opts(("title", "أغنية"), ("artist", "فنان"), ("album", "ألبوم"), ("comment", "ملاحظة")));

        Assert.True(result.Success, result.Error);
        var tags = (await Probe(result.Outputs.Single())).Tags;
        Assert.Equal("أغنية", tags["title"]);
        Assert.Equal("فنان", tags["artist"]);
        Assert.Equal("ألبوم", tags["album"]);
        Assert.Equal("ملاحظة", tags["comment"]);
        Assert.Equal("mp3", (await Probe(result.Outputs.Single())).Audio!.CodecName);
    }

    [EngineFact(EngineId.Ffmpeg)]
    public async Task Normalize_brings_the_loudness_to_the_target()
    {
        using var dir = new TempDir();
        var input = await Wav(Path.Combine(dir.Path, "quiet.wav"), seconds: 6);
        var before = await IntegratedLoudness(input);

        var result = await Run("aud.normalize", input, Opts(("target", -16)));

        Assert.True(result.Success, result.Error);
        var after = await IntegratedLoudness(result.Outputs.Single());
        Assert.True(Math.Abs(after - -16) < 1.5, $"before {before:0.0} LUFS, after {after:0.0} LUFS");
        Assert.Equal(44100, (await Probe(result.Outputs.Single())).Audio!.SampleRate);   // loudnorm's 192 kHz was resampled back
    }

    [EngineFact(EngineId.Ffmpeg)]
    public async Task Normalize_of_pure_silence_is_refused()
    {
        using var dir = new TempDir();
        var silent = await Silence(Path.Combine(dir.Path, "silent.wav"), 3, "stereo");
        var result = await Run("aud.normalize", silent);
        Assert.False(result.Success);
        Assert.Single(Directory.GetFiles(dir.Path));
    }

    [EngineFact(EngineId.Ffmpeg)]
    public async Task Trim_by_range_is_exact_for_wav()
    {
        using var dir = new TempDir();
        var input = await Wav(Path.Combine(dir.Path, "a.wav"), seconds: 4);
        var result = await Run("aud.trim", input, Opts(("start", "1"), ("end", "2.5")));
        Assert.True(result.Success, result.Error);
        Assert.InRange((await Probe(result.Outputs.Single())).DurationSeconds!.Value, 1.4, 1.6);
    }

    [EngineFact(EngineId.Ffmpeg)]
    public async Task Auto_silence_trims_only_the_ends()
    {
        using var dir = new TempDir();
        var input = await SilenceToneSilence(Path.Combine(dir.Path, "sts.wav"));
        var result = await Run("aud.trim", input, Opts(("autoSilence", true)));
        Assert.True(result.Success, result.Error);
        Assert.InRange((await Probe(result.Outputs.Single())).DurationSeconds!.Value, 1.0, 1.5);   // 1 s of tone plus <= 0.1 s kept on each side
    }

    [EngineFact(EngineId.Ffmpeg)]
    public async Task Auto_silence_leaves_silence_in_the_middle()
    {
        using var dir = new TempDir();
        var input = await Compose(Path.Combine(dir.Path, "tone-gap-tone.wav"), "silence", "tone", "silence", "tone", "silence");

        var result = await Run("aud.trim", input, Opts(("autoSilence", true)));

        Assert.True(result.Success, result.Error);
        Assert.InRange((await Probe(result.Outputs.Single())).DurationSeconds!.Value, 3.0, 3.5);  // tone + the 1 s gap + tone
    }

    [EngineFact(EngineId.Ffmpeg)]
    public async Task Channels_convert_in_both_directions()
    {
        using var dir = new TempDir();
        var stereo = await Wav(Path.Combine(dir.Path, "s.wav"), seconds: 2, channels: 2);
        var mono = await Wav(Path.Combine(dir.Path, "m.wav"), seconds: 2, channels: 1);

        var toMono = await Run("aud.channels", stereo, Opts(("channels", "mono")));
        Assert.True(toMono.Success, toMono.Error);
        Assert.Equal(1, (await Probe(toMono.Outputs.Single())).Audio!.Channels);

        var toStereo = await Run("aud.channels", mono, Opts(("channels", "stereo")));
        Assert.True(toStereo.Success, toStereo.Error);
        Assert.Equal(2, (await Probe(toStereo.Outputs.Single())).Audio!.Channels);

        var same = await Run("aud.channels", mono, Opts(("channels", "mono")));
        Assert.False(same.Success);
    }

    [EngineFact(EngineId.Ffmpeg)]
    public async Task Visualize_renders_each_layout_and_a_picture_background()
    {
        using var dir = new TempDir();
        var audio = await Wav(Path.Combine(dir.Path, "a.wav"), seconds: 3);
        var picture = Samples.Png(Path.Combine(dir.Path, "bg.png"), 120, 80);

        foreach (var (layout, w, h) in new[] { ("landscape", 1920, 1080), ("portrait", 1080, 1920), ("square", 1080, 1080) })
        {
            var result = await Run("aud.visualize", audio, Opts(("layout", layout)));
            Assert.True(result.Success, $"{layout}: {result.Error}");
            var info = await Probe(result.Outputs.Single());
            Assert.Equal((w, h), (info.Video!.Width, info.Video.Height));
            Assert.Equal("h264", info.Video.CodecName);
            Assert.True(info.HasAudio);
            Assert.InRange(info.DurationSeconds!.Value, 2.5, 3.6);
        }

        var withPicture = await Run("aud.visualize", audio, Opts(("layout", "square"), ("background", picture), ("color", "#FF6A00")));
        Assert.True(withPicture.Success, withPicture.Error);
        Assert.Equal(1080, (await Probe(withPicture.Outputs.Single())).Video!.Width);
    }

    [EngineFact(EngineId.Ffmpeg)]
    public async Task Bleep_replaces_the_chosen_range_with_a_tone_and_nothing_else()
    {
        using var dir = new TempDir();
        var input = await SilenceToneSilence(Path.Combine(dir.Path, "sts.wav"));   // silent 0-1 s, tone 1-2 s, silent 2-3 s

        var result = await Run("aud.bleep", input, Opts(("ranges", "0-1")));

        Assert.True(result.Success, result.Error);
        var output = result.Outputs.Single();
        Assert.InRange((await Probe(output)).DurationSeconds!.Value, 2.9, 3.1);

        var scan = await ProcessRunner.RunAsync(FfmpegExe, AudioToolArgs.Analyze(output, SilenceDetect.Filter(-50, 0.5)));
        var silences = SilenceDetect.Parse(scan.StdErr);
        Assert.DoesNotContain(silences, s => s.Start < 0.5);     // the first second is no longer silent: it holds the beep
        Assert.Contains(silences, s => Math.Abs(s.Start - 2.0) < 0.1);   // the untouched last second is still silent
    }

    [EngineFact(EngineId.Ffmpeg)]
    public async Task Merge_joins_same_format_files_by_copy_and_mixed_files_by_re_encoding()
    {
        using var dir = new TempDir();
        var a = await Wav(Path.Combine(dir.Path, "a.wav"), seconds: 2);
        var b = await Wav(Path.Combine(dir.Path, "b.wav"), seconds: 3);
        var mp3 = (await Run("convert:mp3", await Wav(Path.Combine(dir.Path, "c.wav"), seconds: 2, channels: 1))).Outputs.Single();

        var same = await RunMulti("aud.merge", [a, b]);
        Assert.True(same.Success, same.Error);
        Assert.EndsWith(".wav", same.Outputs.Single());
        Assert.InRange((await Probe(same.Outputs.Single())).DurationSeconds!.Value, 4.9, 5.1);

        var mixed = await RunMulti("aud.merge", [a, mp3]);
        Assert.True(mixed.Success, mixed.Error);
        Assert.EndsWith(".m4a", mixed.Outputs.Single());
        Assert.InRange((await Probe(mixed.Outputs.Single())).DurationSeconds!.Value, 3.8, 4.4);
    }
}
