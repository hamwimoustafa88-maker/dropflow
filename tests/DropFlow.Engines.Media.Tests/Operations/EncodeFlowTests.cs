using DropFlow.Engines.Media.Operations;
using DropFlow.Engines.Media.Runtime;

namespace DropFlow.Engines.Media.Tests.Operations;

/// <summary>
/// The run-time behaviour wrapped around the planner: hardware fallback, trial encodes, and the measure-and-correct loop for target
/// size. Driven through the Encoder window's entry point (<see cref="EncodeSettingsOperation"/>) with a fake FFmpeg.
/// </summary>
public class EncodeFlowTests
{
    private static async Task<(ConversionResult Result, ProgressLog Log)> Encode(MediaHarness h, string input, EncoderSettings settings, string suffix = "encoded", MediaServices? services = null)
    {
        var converter = new MediaOperationConverter(new EncodeSettingsOperation(suffix), services ?? new MediaServices(h.Runner));
        var log = new ProgressLog();
        var job = new ConversionJob([input], EncodeSettingsOperation.OperationId, new Dictionary<string, object?> { [EncodeSettingsOperation.SettingsKey] = settings });
        return (await converter.RunAsync(job, log, CancellationToken.None), log);
    }

    private static bool IsTrial(FfmpegCall c) => c.Args.Contains("color=c=black:s=256x256:d=0.2:r=10");

    // ------------------------------------------------------------------ the entry point itself

    [Fact]
    public async Task Runs_the_given_settings_and_names_the_result_after_the_plan()
    {
        using var h = new MediaHarness();
        var (video, _) = await Encode(h, h.Video("holiday.mp4"), EncoderPresets.YouTube1080p);
        Assert.True(video.Success, video.Error);
        Assert.Equal(Path.Combine(h.Dir, "holiday (encoded).mp4"), video.Outputs.Single());

        var (audio, _) = await Encode(h, h.Video("talk.mp4"), EncoderPresets.AudioMp3_192k, "audio");
        Assert.Equal(Path.Combine(h.Dir, "talk (audio).mp3"), audio.Outputs.Single());
        Assert.True(Args.HasPair(h.Runner.Calls.Last().Args, "-c:a", "libmp3lame"));
    }

    [Fact]
    public async Task Plan_errors_are_returned_as_failures_before_anything_runs()
    {
        using var h = new MediaHarness();
        var (result, _) = await Encode(h, h.Video("v.mp4", seconds: 3600), EncoderPresets.Discord10MB with { TargetSizeMb = 1 });
        Assert.False(result.Success);
        Assert.Contains("too small", result.Error);
        Assert.Empty(h.Runner.Calls);
    }

    [Fact]
    public async Task Missing_settings_are_a_failure_not_a_crash()
    {
        using var h = new MediaHarness();
        var converter = new MediaOperationConverter(new EncodeSettingsOperation("encoded"), new MediaServices(h.Runner));
        var result = await converter.RunAsync(new ConversionJob([h.Video("v.mp4")], EncodeSettingsOperation.OperationId), new ProgressLog(), CancellationToken.None);
        Assert.False(result.Success);
    }

    // ------------------------------------------------------------------ hardware

    [Fact]
    public async Task A_hardware_encoder_that_fails_mid_job_is_replaced_by_software()
    {
        using var h = new MediaHarness();
        h.Runner.OnFfmpeg = (call, _, _) =>
        {
            if (IsTrial(call)) return Task.FromResult<ProcessResult?>(null);                       // the trial encode passes...
            if (!call.Has("h264_nvenc")) return Task.FromResult<ProcessResult?>(null);
            FakeToolRunner.WriteOutput(call.Output);                                                // ...the real one does not
            return Task.FromResult<ProcessResult?>(FakeToolRunner.Fail("[h264_nvenc @ 0x1] Unable to parse option value \"p5\"\n2 frames left in the queue on closing"));
        };

        var (result, log) = await Encode(h, h.Video("v.mp4"), new EncoderSettings { Hardware = HardwarePreference.Auto });

        Assert.True(result.Success, result.Error);
        var real = h.Runner.Calls.Where(c => !IsTrial(c)).ToList();
        Assert.Equal(2, real.Count);
        Assert.True(Args.HasPair(real[0].Args, "-c:v", "h264_nvenc"));
        Assert.True(Args.HasPair(real[1].Args, "-c:v", "libx264"));
        Assert.Contains(log.Reports, r => r.Message?.Contains("software", StringComparison.Ordinal) == true);
        Assert.True(File.Exists(result.Outputs.Single()));
    }

    [Fact]
    public async Task Trial_encodes_decide_which_vendor_is_used_and_are_cached_for_the_session()
    {
        using var h = new MediaHarness();
        // NVENC fails its trial, Quick Sync passes.
        h.Runner.OnFfmpeg = (call, _, _) =>
        {
            if (!IsTrial(call)) return Task.FromResult<ProcessResult?>(null);
            return Task.FromResult<ProcessResult?>(call.Has("h264_nvenc") || call.Has("h264_amf") ? FakeToolRunner.Fail("Cannot load nvcuda.dll") : null);
        };

        var services = new MediaServices(h.Runner);   // the trial cache lives in the module's services
        await Encode(h, h.Video("a.mp4"), new EncoderSettings { Hardware = HardwarePreference.Auto }, services: services);
        var first = h.Runner.Calls.Where(c => !IsTrial(c)).Single();
        Assert.True(Args.HasPair(first.Args, "-c:v", "h264_qsv"));
        var trials = h.Runner.Calls.Count(IsTrial);
        Assert.Equal(3, trials);   // nvenc, qsv and amf were each tried once

        await Encode(h, h.Video("b.mp4"), new EncoderSettings { Hardware = HardwarePreference.Auto }, services: services);
        Assert.Equal(trials, h.Runner.Calls.Count(IsTrial));   // no repeat
    }

    [Fact]
    public async Task Software_only_settings_never_run_trials()
    {
        using var h = new MediaHarness();
        await Encode(h, h.Video("a.mp4"), new EncoderSettings { Hardware = HardwarePreference.None });
        Assert.DoesNotContain(h.Runner.Calls, IsTrial);
    }

    [Fact]
    public async Task When_every_trial_fails_the_job_just_uses_software()
    {
        using var h = new MediaHarness();
        h.Runner.OnFfmpeg = (call, _, _) => Task.FromResult<ProcessResult?>(IsTrial(call) ? FakeToolRunner.Fail("No NVENC capable devices found") : null);
        var (result, _) = await Encode(h, h.Video("a.mp4"), new EncoderSettings { Hardware = HardwarePreference.Auto });
        Assert.True(result.Success, result.Error);
        Assert.True(Args.HasPair(h.Runner.Calls.Single(c => !IsTrial(c)).Args, "-c:v", "libx264"));
    }

    [Fact]
    public async Task Trial_arguments_use_the_same_vendor_options_as_the_real_encode()
    {
        using var h = new MediaHarness();
        await Encode(h, h.Video("a.mp4"), new EncoderSettings { Hardware = HardwarePreference.Auto });
        var nvencTrial = h.Runner.Calls.First(c => IsTrial(c) && c.Has("h264_nvenc"));
        Assert.True(Args.HasPair(nvencTrial.Args, "-preset", "p4"));
        Assert.True(Args.HasPair(nvencTrial.Args, "-cq", "30"));
        var qsvTrial = h.Runner.Calls.First(c => IsTrial(c) && c.Has("h264_qsv"));
        Assert.True(Args.HasPair(qsvTrial.Args, "-global_quality", "30"));
    }

    // ------------------------------------------------------------------ target size: measure and correct

    private static EncoderSettings TargetOneMb => new() { Hardware = HardwarePreference.None, RateControl = RateControl.TargetSize, TargetSizeMb = 1, AudioBitrateKbps = 96, AllowDownscaleForTargetSize = false };

    /// <summary>Makes pass 2 (and each refinement) write a file of the next size in <paramref name="sizes"/>.</summary>
    private static void WriteSizes(MediaHarness h, params long[] sizes)
    {
        var next = 0;
        h.Runner.OnFfmpeg = (call, _, _) =>
        {
            if (call.After("-pass") == "1") return Task.FromResult<ProcessResult?>(null);
            var size = sizes[Math.Min(next++, sizes.Length - 1)];
            File.WriteAllBytes(call.Output, new byte[size]);
            return Task.FromResult<ProcessResult?>(new ProcessResult(0, "", "", TimeSpan.Zero, false, false));
        };
    }

    [Fact]
    public async Task A_result_inside_the_band_is_left_alone()
    {
        using var h = new MediaHarness();
        WriteSizes(h, 975_000);
        var (result, _) = await Encode(h, h.Video("v.mp4", seconds: 30), TargetOneMb);
        Assert.True(result.Success, result.Error);
        Assert.Equal(2, h.Runner.Calls.Count);            // pass 1 and pass 2 only
        Assert.Equal(975_000, new FileInfo(result.Outputs.Single()).Length);
    }

    [Fact]
    public async Task An_overshoot_triggers_one_corrected_final_pass_that_replaces_the_file()
    {
        using var h = new MediaHarness();
        WriteSizes(h, 1_200_000, 980_000);
        var plan = EncoderPlanner.Plan(TargetOneMb, TestMedia.Video(seconds: 30), new PlanContext { OutputPath = "x" });
        var firstKbps = plan.VideoBitrateKbps!.Value;

        var (result, log) = await Encode(h, h.Video("v.mp4", seconds: 30), TargetOneMb);

        Assert.True(result.Success, result.Error);
        Assert.Equal(3, h.Runner.Calls.Count);
        var pass2 = h.Runner.Calls[1];
        var refine = h.Runner.Calls[2];
        Assert.True(Args.HasPair(refine.Args, "-pass", "2"));                         // reuses the pass-1 statistics
        Assert.Equal(pass2.WorkingDirectory, refine.WorkingDirectory);
        Assert.NotEqual(result.Outputs.Single(), refine.Output);                       // written aside first
        var corrected = int.Parse(refine.After("-b:v")!.TrimEnd('k'));
        // too big: the bitrate is scaled by (target / actual) x 0.95 = (1,000,000 / 1,200,000) x 0.95
        var expected = firstKbps * (1_000_000.0 / 1_200_000) * 0.95;
        Assert.InRange(corrected, expected - 2, expected + 2);
        Assert.True(corrected < firstKbps);
        Assert.Equal(980_000, new FileInfo(result.Outputs.Single()).Length);           // the better file won
        Assert.Contains(log.Reports, r => r.Message?.Contains("adjusting", StringComparison.OrdinalIgnoreCase) == true);
        Assert.DoesNotContain(log.Reports, r => r.Message?.Contains("above the", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task A_big_undershoot_raises_the_bitrate()
    {
        using var h = new MediaHarness();
        WriteSizes(h, 500_000, 960_000);
        var (result, _) = await Encode(h, h.Video("v.mp4", seconds: 30), TargetOneMb);
        Assert.True(result.Success, result.Error);
        var first = int.Parse(h.Runner.Calls[1].After("-b:v")!.TrimEnd('k'));
        var second = int.Parse(h.Runner.Calls[2].After("-b:v")!.TrimEnd('k'));
        Assert.True(second > first);
        Assert.True(second <= first * 1.5 + 1);   // never more than +50% in one step
        Assert.Equal(960_000, new FileInfo(result.Outputs.Single()).Length);
    }

    [Fact]
    public async Task A_file_still_too_big_after_three_corrections_is_a_clear_failure_and_is_removed()
    {
        using var h = new MediaHarness();
        WriteSizes(h, 1_120_000, 1_060_000, 1_040_000, 1_020_000);
        var (result, _) = await Encode(h, h.Video("v.mp4", seconds: 30), TargetOneMb);
        Assert.False(result.Success);
        Assert.Equal(5, h.Runner.Calls.Count);                                     // pass 1, pass 2, three corrections
        Assert.Contains("over the 1 MB target", result.Error);
        Assert.Contains("3 attempts", result.Error);
        Assert.DoesNotContain(h.Tree(), p => p.Contains("encoded", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Every_correction_of_an_overshoot_lowers_the_bitrate_further()
    {
        using var h = new MediaHarness();
        WriteSizes(h, 1_100_000, 1_400_000, 1_050_000, 980_000);
        var (result, _) = await Encode(h, h.Video("v.mp4", seconds: 30), TargetOneMb);

        Assert.True(result.Success, result.Error);
        Assert.Equal(5, h.Runner.Calls.Count);
        var rates = h.Runner.Calls.Skip(1).Select(c => int.Parse(c.After("-b:v")!.TrimEnd('k'))).ToList();
        Assert.True(rates[1] < rates[0] && rates[2] < rates[1] && rates[3] < rates[2], string.Join(", ", rates));
        // a worse attempt (1.4 MB) never replaces the file; the final one fits
        Assert.Equal(980_000, new FileInfo(result.Outputs.Single()).Length);
    }

    [Fact]
    public async Task A_failing_correction_of_an_undershoot_keeps_the_first_result()
    {
        using var h = new MediaHarness();
        var runs = 0;
        h.Runner.OnFfmpeg = (call, _, _) =>
        {
            if (call.After("-pass") == "1") return Task.FromResult<ProcessResult?>(null);
            if (++runs == 1)
            {
                File.WriteAllBytes(call.Output, new byte[600_000]);
                return Task.FromResult<ProcessResult?>(new ProcessResult(0, "", "", TimeSpan.Zero, false, false));
            }
            return Task.FromResult<ProcessResult?>(FakeToolRunner.Fail("Error while encoding"));
        };
        var (result, _) = await Encode(h, h.Video("v.mp4", seconds: 30), TargetOneMb);
        Assert.True(result.Success, result.Error);                                // 0.6 MB is within the limit
        Assert.Equal(600_000, new FileInfo(result.Outputs.Single()).Length);
    }

    [Fact]
    public async Task A_failing_correction_of_an_overshoot_is_a_failure_because_the_file_would_not_fit()
    {
        using var h = new MediaHarness();
        var runs = 0;
        h.Runner.OnFfmpeg = (call, _, _) =>
        {
            if (call.After("-pass") == "1") return Task.FromResult<ProcessResult?>(null);
            if (++runs == 1)
            {
                File.WriteAllBytes(call.Output, new byte[1_200_000]);
                return Task.FromResult<ProcessResult?>(new ProcessResult(0, "", "", TimeSpan.Zero, false, false));
            }
            return Task.FromResult<ProcessResult?>(FakeToolRunner.Fail("Error while encoding"));
        };
        var (result, _) = await Encode(h, h.Video("v.mp4", seconds: 30), TargetOneMb);
        Assert.False(result.Success);
        Assert.Contains("1.2 MB, over the 1 MB target", result.Error);
        Assert.Empty(result.Outputs);
    }

    [Fact]
    public async Task A_cancelled_correction_throws_and_removes_everything()
    {
        using var h = new MediaHarness();
        using var cts = new CancellationTokenSource();
        var runs = 0;
        h.Runner.OnFfmpeg = async (call, _, ct) =>
        {
            if (call.After("-pass") == "1") return null;
            if (++runs == 1)
            {
                File.WriteAllBytes(call.Output, new byte[1_200_000]);
                return new ProcessResult(0, "", "", TimeSpan.Zero, false, false);
            }
            await cts.CancelAsync();
            return new ProcessResult(-1, "", "", TimeSpan.Zero, false, true);
        };
        var converter = new MediaOperationConverter(new EncodeSettingsOperation("encoded"), new MediaServices(h.Runner));
        var job = new ConversionJob([h.Video("v.mp4", seconds: 30)], EncodeSettingsOperation.OperationId, new Dictionary<string, object?> { [EncodeSettingsOperation.SettingsKey] = TargetOneMb });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => converter.RunAsync(job, new ProgressLog(), cts.Token));

        Assert.DoesNotContain(h.Tree(), p => p.Contains("encoded", StringComparison.Ordinal));
    }

    [Fact]
    public void Rescaling_touches_only_the_rate_options_and_the_output_path()
    {
        string[] args = ["-i", "in.mp4", "-b:v", "3000k", "-maxrate", "4500k", "-bufsize", "6000k", "-b:a", "128k", "-crf", "23", "out.mp4"];
        var scaled = MediaContext.ScaleBitrates(args, 0.5, "out.mp4", "tmp.mp4");
        Assert.Equal(["-i", "in.mp4", "-b:v", "1500k", "-maxrate", "2250k", "-bufsize", "3000k", "-b:a", "128k", "-crf", "23", "tmp.mp4"], scaled);
        Assert.Equal("out.mp4", args[^1]);   // the original list is not modified
    }

    [Fact]
    public void Rescaling_ignores_non_numeric_values_and_other_outputs()
    {
        string[] args = ["-b:v", "0", "-maxrate", "oops", "other.mp4"];
        Assert.Equal(["-b:v", "0", "-maxrate", "oops", "other.mp4"], MediaContext.ScaleBitrates(args, 2, "out.mp4", "tmp.mp4"));
    }
}
