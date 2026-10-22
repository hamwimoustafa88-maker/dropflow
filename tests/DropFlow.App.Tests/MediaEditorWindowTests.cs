using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DropFlow.App.Views;
using DropFlow.App.Views.Editors.Media;
using DropFlow.Core.Localization;
using DropFlow.Core.Operations;
using DropFlow.Core.Runner;
using DropFlow.Engines.Common;
using DropFlow.Engines.Media;
using DropFlow.Engines.Media.Encoding;
using DropFlow.Overlay;
using DropFlow.TestSupport;
using TestEngines = DropFlow.TestSupport.Engines;

namespace DropFlow.App.Tests;

/// <summary>Sample media made once by the real FFmpeg for the editor tests (skipped when FFmpeg is missing).</summary>
internal static class EditorSamples
{
    private static readonly Lazy<Task<(string Video, string Video2, string Audio, string Audio2, string Dir)>> Made = new(MakeAsync);

    public static (string Video, string Video2, string Audio, string Audio2, string Dir) Get() => Made.Value.GetAwaiter().GetResult();

    /// <summary>Where the rendered editor pictures go, to be looked at by a person.</summary>
    public static string Renders { get; } = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "dropflow-media-editors")).FullName;

    private static async Task<(string, string, string, string, string)> MakeAsync()
    {
        var ffmpeg = TestEngines.Locator.Locate(EngineId.Ffmpeg) ?? throw new InvalidOperationException("ffmpeg missing");
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "dropflow-editor-samples-" + Guid.NewGuid().ToString("N")[..8])).FullName;
        async Task Run(params string[] args)
        {
            var r = await ProcessRunner.RunAsync(ffmpeg, ["-hide_banner", "-nostdin", "-y", .. args]);
            if (!r.Success) throw new InvalidOperationException(r.LastMessage);
        }
        var video = Path.Combine(dir, "رحلة البحر.mp4");
        await Run("-f", "lavfi", "-i", "testsrc2=duration=8:size=640x360:rate=25", "-f", "lavfi", "-i", "sine=frequency=330:duration=8",
            "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-g", "25", "-c:a", "aac", "-shortest", video);
        var video2 = Path.Combine(dir, "second clip.mp4");
        await Run("-f", "lavfi", "-i", "smptebars=duration=3:size=640x360:rate=25", "-f", "lavfi", "-i", "sine=frequency=550:duration=3",
            "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", video2);
        var audio = Path.Combine(dir, "voice note.wav");   // stereo: speech-like bursts on the left, a quieter tone on the right
        await Run("-f", "lavfi", "-i", "sine=frequency=440:duration=10:sample_rate=44100", "-f", "lavfi", "-i", "sine=frequency=880:duration=10:sample_rate=44100",
            "-filter_complex", "[0:a]volume='if(lt(mod(t,2),1.2),1,0.05)':eval=frame[l];[1:a]volume=0.3[r];[l][r]join=inputs=2:channel_layout=stereo[a]", "-map", "[a]", audio);
        var audio2 = Path.Combine(dir, "intro.mp3");
        await Run("-f", "lavfi", "-i", "sine=frequency=660:duration=4", "-c:a", "libmp3lame", "-q:a", "4", audio2);
        return (video, video2, audio, audio2, dir);
    }
}

public class MediaEditorWindowTests
{
    private static readonly IReadOnlyList<IConverter> Converters = MediaModule.Create(TestEngines.Locator);

    private static IConverter Converter(string op, string file) =>
        Converters.First(c => c.Supports(op, Path.GetExtension(file).TrimStart('.').ToLowerInvariant()));

    private static MediaEditorContext Context(string op, IReadOnlyList<string> files, string language, string scratch) =>
        new(new UiKit(WheelTheme.Light, JsonLocalizer.Load(language)), TestEngines.Locator, op, op, files, Converter(op, files[0]).OptionsFor(op), scratch);

    private static MediaSession Session(string file, string scratch)
    {
        var info = MediaEncoder.ProbeAsync(TestEngines.Locator, file).GetAwaiter().GetResult();
        return new MediaSession(TestEngines.Locator, file, info, scratch);
    }

    /// <summary>Runs the dispatcher (so background loads land) until <paramref name="done"/> or the timeout.</summary>
    private static void PumpUntil(Func<bool> done, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (!done() && DateTime.UtcNow < until)
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
            timer.Start();
            Dispatcher.PushFrame(frame);
        }
    }

    private static string Render(Window window, string name)
    {
        window.UpdateLayout();
        PumpUntil(() => false, TimeSpan.FromMilliseconds(300));
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var file = Path.Combine(EditorSamples.Renders, name + ".png");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(file)) encoder.Save(stream);
        return file;
    }

    private static string Scratch() => Path.Combine(Path.GetTempPath(), "dropflow-editor-scratch-" + Guid.NewGuid().ToString("N")[..8]);

    /// <summary>Opens the editor, lets the strip load, applies <paramref name="act"/>, renders it, and returns its values.</summary>
    private static (Dictionary<string, object?>? Values, string? Problem, string Png) Open(
        string op, string file, string language, Action<MediaEditorWindow>? act = null, Func<MediaSession, bool>? loaded = null)
    {
        return Sta.Run(() =>
        {
            // as in the app: continuations of awaits started on this thread come back to its dispatcher
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            var scratch = Scratch();
            var session = Session(file, scratch);
            var window = MediaEditors.Create(op, Context(op, [file], language, scratch), session);
            window.Show();
            PumpUntil(() => (loaded ?? (s => s.HasVideo ? s.Thumbnails.Length > 0 && s.Thumbnails.All(t => t is not null) && s.Still.Source is not null : s.Waveform.Length > 0))(session), TimeSpan.FromSeconds(25));
            act?.Invoke(window);
            Assert.Equal(language == "ar" ? FlowDirection.RightToLeft : FlowDirection.LeftToRight, window.FlowDirection);
            var png = Render(window, $"{op}-{language}");
            var result = (window.CurrentValues, window.CurrentProblem, png);
            window.Close();
            return result;
        });
    }

    private static async Task<ConversionResult> RunTool(string op, string file, IReadOnlyDictionary<string, object?> values, string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        return await Converter(op, file).RunAsync(new ConversionJob([file], op, values, outputDir), new Progress<JobProgress>(), CancellationToken.None);
    }

    private static async Task<MediaInfo> Probe(string file) => await MediaEncoder.ProbeAsync(TestEngines.Locator, file);

    // ------------------------------------------------------------------------------------------------------------------ trim

    [EngineFact(EngineId.Ffmpeg)]
    public async Task Trim_renders_in_both_languages_and_its_values_trim_the_real_file()
    {
        var s = EditorSamples.Get();
        foreach (var lang in new[] { "en", "ar" })
        {
            var (values, problem, png) = Open("vid.trim", s.Video, lang, w =>
            {
                var trim = (MediaTrimEditor)w;
                trim.StartBox.Type("2");
                trim.EndBox.Type("0:05.50");
            });
            Assert.Null(problem);
            Assert.Equal("2", values!["start"]);
            Assert.Equal("5.5", values["end"]);
            Assert.True(new FileInfo(png).Length > 20_000);
            if (lang != "en") continue;

            values["reencode"] = true;
            var result = await RunTool("vid.trim", s.Video, values, Path.Combine(s.Dir, "out-trim"));
            Assert.True(result.Success, result.Error);
            Assert.InRange((await Probe(result.Outputs.Single())).DurationSeconds!.Value, 3.3, 3.7);
        }
    }

    [EngineFact(EngineId.Ffmpeg)]
    public void An_untouched_trim_cannot_run_and_closing_returns_nothing()
    {
        var s = EditorSamples.Get();
        var (values, problem) = Sta.Run(() =>
        {
            var scratch = Scratch();
            var window = MediaEditors.Create("aud.trim", Context("aud.trim", [s.Audio], "en", scratch), Session(s.Audio, scratch));
            window.Show();
            var p = window.CurrentProblem;
            window.Close();
            return (window.Completion.IsCompleted ? window.Completion.Result : new OptionsEdit(new Dictionary<string, object?>()), p);
        });
        Assert.Equal("media.trim.nothing", problem);
        Assert.Null(values);
    }

    // ------------------------------------------------------------------------------------------------------------------ split

    [EngineFact(EngineId.Ffmpeg)]
    public async Task Split_with_a_removed_clip_joins_the_rest_for_real()
    {
        var s = EditorSamples.Get();
        Dictionary<string, object?>? values = null;
        foreach (var lang in new[] { "en", "ar" })
        {
            (values, var problem, _) = Open("vid.split", s.Video, lang, w =>
            {
                var split = (MediaSplitEditor)w;
                split.Model.AddPoint(2);
                split.Model.AddPoint(5);
                split.SelectClip(1);
                split.Model.ToggleRemoved(1);
                split.SetJoin(true);
            });
            Assert.Null(problem);
        }
        Assert.Equal("2, 5", values!["points"]);
        Assert.Equal("2", values["remove"]);
        Assert.Equal("join", values["output"]);

        var result = await RunTool("vid.split", s.Video, values, Path.Combine(s.Dir, "out-split"));
        Assert.True(result.Success, result.Error);
        Assert.InRange((await Probe(result.Outputs.Single())).DurationSeconds!.Value, 4.5, 5.5);   // 8 s minus the 3 s middle clip
    }

    // ------------------------------------------------------------------------------------------------------------------ speed

    [EngineFact(EngineId.Ffmpeg)]
    public async Task Speed_snaps_shows_the_new_length_and_speeds_up_the_real_file()
    {
        var s = EditorSamples.Get();
        Dictionary<string, object?>? values = null;
        foreach (var lang in new[] { "en", "ar" })
            (values, _, _) = Open("vid.speed", s.Video, lang, w => ((MediaSpeedEditor)w).SetSpeed(1.97));
        Assert.Equal(2.0, values!["speed"]);
        var result = await RunTool("vid.speed", s.Video, values, Path.Combine(s.Dir, "out-speed"));
        Assert.True(result.Success, result.Error);
        Assert.InRange((await Probe(result.Outputs.Single())).DurationSeconds!.Value, 3.7, 4.3);
    }

    // ------------------------------------------------------------------------------------------------------------------ crop

    [EngineFact(EngineId.Ffmpeg)]
    public async Task Crop_box_from_an_aspect_preset_crops_the_real_file()
    {
        var s = EditorSamples.Get();
        Dictionary<string, object?>? values = null;
        foreach (var lang in new[] { "en", "ar" })
            (values, _, _) = Open("vid.crop", s.Video, lang, w => ((MediaCropEditor)w).ChooseAspect("9:16"));
        Assert.Equal("custom", values!["aspect"]);
        Assert.Equal(360, values["h"]);
        var result = await RunTool("vid.crop", s.Video, values, Path.Combine(s.Dir, "out-crop"));
        Assert.True(result.Success, result.Error);
        var v = (await Probe(result.Outputs.Single())).Video!;
        Assert.Equal(((int)values["w"]!, 360), (v.Width!.Value, v.Height!.Value));
    }

    // ------------------------------------------------------------------------------------------------------------------ frames

    [EngineFact(EngineId.Ffmpeg)]
    public async Task Frames_added_at_the_playhead_are_saved_by_the_real_tool()
    {
        var s = EditorSamples.Get();
        Dictionary<string, object?>? values = null;
        foreach (var lang in new[] { "en", "ar" })
            (values, _, _) = Open("vid.snapshot", s.Video, lang, w =>
            {
                var frames = (MediaFramesEditor)w;
                frames.AddAtPlayhead();
                w.Session.Seek(3);
                frames.AddAtPlayhead();
                PumpUntil(() => false, TimeSpan.FromSeconds(1.5));
            });
        Assert.Equal("0, 3", values!["times"]);
        var result = await RunTool("vid.snapshot", s.Video, values, Path.Combine(s.Dir, "out-frames"));
        Assert.True(result.Success, result.Error);
        Assert.Equal(2, Directory.GetFiles(result.Outputs.Single(), "*.png").Length);
    }

    // ------------------------------------------------------------------------------------------------------------------ redact

    [EngineFact(EngineId.Ffmpeg)]
    public async Task Redact_draws_boxes_with_time_ranges_and_one_box_runs_through_the_installed_tool()
    {
        var s = EditorSamples.Get();
        var supportsBoxes = Converter("vid.redact", s.Video).OptionsFor("vid.redact").Any(o => o.Key == "boxes");
        string? twoBoxProblem = null;
        foreach (var lang in new[] { "en", "ar" })
        {
            (_, twoBoxProblem, _) = Open("vid.redact", s.Video, lang, w =>
            {
                var redact = (MediaRedactEditor)w;
                redact.SetMode("solid");
                redact.DrawBox(new PixelBox(40, 40, 160, 90));
                redact.DrawBox(new PixelBox(380, 200, 200, 120));
                redact.SetBoxTimes(1, 1, 4);
            });
        }
        Assert.Equal(supportsBoxes ? null : "media.redact.oneBoxOnly", twoBoxProblem);

        var (values, problem, _) = Open("vid.redact", s.Video, "en", w =>
        {
            var redact = (MediaRedactEditor)w;
            redact.SetMode("solid");
            redact.DrawBox(new PixelBox(64, 36, 128, 72));
            redact.SetBoxTimes(0, 1, 6);
        });
        Assert.Null(problem);
        var result = await RunTool("vid.redact", s.Video, values!, Path.Combine(s.Dir, "out-redact"));
        Assert.True(result.Success, result.Error);
        Assert.True((await Probe(result.Outputs.Single())).HasVideo);
    }

    // ------------------------------------------------------------------------------------------------------------------ bleep

    [EngineFact(EngineId.Ffmpeg)]
    public async Task Bleep_ranges_from_the_lane_bleep_the_real_file()
    {
        var s = EditorSamples.Get();
        Dictionary<string, object?>? values = null;
        foreach (var lang in new[] { "en", "ar" })
            (values, _, _) = Open("aud.bleep", s.Audio, lang, w =>
            {
                var bleep = (MediaBleepEditor)w;
                bleep.AddRange(1.5, 2.25);
                bleep.AddRange(6, 7);
            });
        Assert.Equal("1.5-2.25; 6-7", values!["ranges"]);
        var result = await RunTool("aud.bleep", s.Audio, values, Path.Combine(s.Dir, "out-bleep"));
        Assert.True(result.Success, result.Error);
    }

    // ------------------------------------------------------------------------------------------------------------------ normalize

    [EngineFact(EngineId.Ffmpeg)]
    public async Task Normalize_measures_on_open_and_its_targets_run_the_real_tool()
    {
        var s = EditorSamples.Get();
        Dictionary<string, object?>? values = null;
        foreach (var lang in new[] { "en", "ar" })
        {
            (values, _, _) = Open("aud.normalize", s.Audio, lang, w =>
            {
                var n = (MediaNormalizeEditor)w;
                PumpUntil(() => n.Measured is not null, TimeSpan.FromSeconds(20));
                Assert.NotNull(n.Measured);
                n.SetTarget(-14);
            });
        }
        Assert.Equal(-14.0, values!["target"]);
        var result = await RunTool("aud.normalize", s.Audio, values, Path.Combine(s.Dir, "out-normalize"));
        Assert.True(result.Success, result.Error);
    }

    // ------------------------------------------------------------------------------------------------------------------ channels

    [EngineFact(EngineId.Ffmpeg)]
    public async Task Channels_with_a_side_gain_run_the_real_tool()
    {
        var s = EditorSamples.Get();
        Dictionary<string, object?>? values = null;
        string? sameProblem = null;
        foreach (var lang in new[] { "en", "ar" })
            (values, _, _) = Open("aud.channels", s.Audio, lang, w =>
            {
                var c = (MediaChannelsEditor)w;
                c.SetTarget("stereo");
                sameProblem = w.CurrentProblem;
                c.SetGains(-6, 0);
            });
        Assert.Equal("media.channels.alreadyStereo", sameProblem);
        Assert.Equal("stereo", values!["channels"]);
        Assert.Equal(-6.0, values["leftGain"]);
        var result = await RunTool("aud.channels", s.Audio, values, Path.Combine(s.Dir, "out-channels"));
        Assert.True(result.Success, result.Error);
    }

    // ------------------------------------------------------------------------------------------------------------------ join / merge

    [EngineFact(EngineId.Ffmpeg)]
    public void Join_and_merge_render_their_rows_and_return_the_new_order()
    {
        var s = EditorSamples.Get();
        foreach (var (op, files) in new[] { ("vid.join", new[] { s.Video, s.Video2 }), ("aud.merge", new[] { s.Audio, s.Audio2 }) })
            foreach (var lang in new[] { "en", "ar" })
            {
                var edit = Sta.Run(() =>
                {
                    var window = new MediaJoinEditor(Context(op, files, lang, Scratch()));
                    window.Show();
                    PumpUntil(() => window.FactsLoaded, TimeSpan.FromSeconds(25));
                    window.MoveForTest(1, 0);
                    Render(window, $"{op}-{lang}");
                    window.PressRun();
                    return window.Completion.Result;
                });
                Assert.Equal([files[1], files[0]], edit!.Files);
                Assert.True(edit.Values.ContainsKey(op == "vid.join" ? "mode" : "format"));
            }
    }

    // ------------------------------------------------------------------------------------------------------------------ player fallback

    [EngineFact(EngineId.Ffmpeg)]
    public async Task A_video_the_windows_player_cannot_show_falls_back_to_ffmpeg_stills_that_follow_the_playhead()
    {
        var s = EditorSamples.Get();
        var ffv1 = Path.Combine(s.Dir, "lossless.mkv");   // FFV1 has no Media Foundation decoder
        var r = await ProcessRunner.RunAsync(TestEngines.Locator.Locate(EngineId.Ffmpeg)!, ["-hide_banner", "-nostdin", "-y", "-f", "lavfi", "-i", "testsrc=duration=3:size=320x240:rate=10", "-c:v", "ffv1", ffv1]);
        Assert.True(r.Success, r.LastMessage);

        var (stills, changed) = Sta.Run(() =>
        {
            var scratch = Scratch();
            var session = Session(ffv1, scratch);
            var stage = new System.Windows.Controls.Grid { Children = { session.Player, session.Still } };
            var host = new Window { Content = stage, Width = 400, Height = 300, ShowInTaskbar = false };
            host.Show();
            PumpUntil(() => session.UsesStills, TimeSpan.FromSeconds(12));
            PumpUntil(() => session.Still.Source is not null, TimeSpan.FromSeconds(10));
            var first = session.Still.Source;
            session.Seek(2);
            PumpUntil(() => !ReferenceEquals(session.Still.Source, first), TimeSpan.FromSeconds(10));
            var result = (session.UsesStills, !ReferenceEquals(session.Still.Source, first) && session.Still.Visibility == Visibility.Visible);
            host.Close();
            session.Dispose();
            return result;
        });

        Assert.True(stills, "the player was expected to give up on FFV1");
        Assert.True(changed, "seeking should bring a new still");
    }

    // ------------------------------------------------------------------------------------------------------------------ wiring

    [Fact]
    public void Editors_open_for_one_file_of_their_tools_and_join_or_merge_need_two()
    {
        var editors = new MediaEditors(Dispatcher.CurrentDispatcher, new UiKit(WheelTheme.Light, JsonLocalizer.Load("en")), TestEngines.Locator, _ => Task.CompletedTask);
        Assert.True(editors.CanEdit("vid.trim", [@"C:\a.mp4"]));
        Assert.False(editors.CanEdit("vid.trim", [@"C:\a.mp4", @"C:\b.mp4"]));   // several files: the form
        Assert.True(editors.CanEdit("aud.bleep", [@"C:\a.wav"]));
        Assert.False(editors.CanEdit("vid.join", [@"C:\a.mp4"]));
        Assert.True(editors.CanEdit("vid.join", [@"C:\a.mp4", @"C:\b.mp4"]));
        Assert.False(editors.CanEdit("vid.compress", [@"C:\a.mp4"]));
        Assert.False(editors.CanEdit("vid.trim", [Path.GetTempPath()]));          // a folder
    }

    [Theory]
    [InlineData(8, 900, 1)]        // eight seconds across 900 px: a label every second
    [InlineData(600, 900, 60)]     // ten minutes: every minute
    [InlineData(1, 900, 0.1)]
    public void Timeline_ticks_use_round_steps(double duration, double width, double expected) =>
        Assert.Equal(expected, MediaTimelineView.TickStep(duration, width));

    [Fact]
    public void Every_editor_operation_is_a_real_media_tool()
    {
        foreach (var op in MediaEditors.Operations)
            Assert.Contains(Converters, c => c.Operations.Contains(op));
    }

    [Fact]
    public void Every_media_editor_string_exists_in_both_languages()
    {
        var keys = Directory.GetFiles(Path.Combine(RepoRoot(), "src", "DropFlow.App", "Views", "Editors", "Media"), "*.cs")
            .SelectMany(f => System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(f), "\"(media\\.[A-Za-z0-9.]+[A-Za-z0-9])\"").Select(m => m.Groups[1].Value))
            .Concat(["media.color.black", "media.color.white", "media.color.gray", "media.color.red"])
            .Distinct().ToList();
        var problems = new List<string>();
        foreach (var lang in new[] { "en", "ar" })
        {
            var strings = JsonLocalizer.ReadEmbedded(lang);
            problems.AddRange(keys.Where(k => !strings.ContainsKey(k)).Select(k => $"{lang}: {k}"));
        }
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DropFlow.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
