using DropFlow.TestSupport;

namespace DropFlow.Engines.Media.Tests;

/// <summary>The public surface the Encoder window uses, exercised with no engines installed.</summary>
public class MediaEncoderApiTests
{
    private static MediaInfo Source => TestMedia.Video(path: @"C:\Videos\clip.mp4");

    [Fact]
    public async Task Planning_needs_no_ffmpeg_and_shows_what_would_run()
    {
        var plan = await MediaEncoder.PlanAsync(new FakeEngines(), Source, EncoderPresets.WhatsApp);

        Assert.True(plan.Ok, plan.Error);
        Assert.Equal("mp4", plan.OutputExtension);
        Assert.True(plan.IsTwoPass);
        Assert.Equal(EncoderPlan.OutputPlaceholder, plan.Passes[^1][^1]);
        Assert.Equal(@"C:\Videos\clip.mp4", Args.After(plan.Passes[0], "-i"));
    }

    [Fact]
    public async Task Every_preset_can_be_planned_through_the_public_api()
    {
        foreach (var preset in EncoderPresets.All)
        {
            var plan = await MediaEncoder.PlanAsync(new FakeEngines(), Source, preset.Settings);
            Assert.True(plan.Ok, $"{preset.Id}: {plan.Error}");
        }
    }

    [Fact]
    public async Task Hardware_auto_without_ffmpeg_plans_software_because_nothing_can_be_verified()
    {
        var plan = await MediaEncoder.PlanAsync(new FakeEngines(), Source, new EncoderSettings { Hardware = HardwarePreference.Auto });
        Assert.Equal("libx264", plan.VideoEncoder);
    }

    [Fact]
    public async Task Probing_without_ffprobe_throws_a_probe_exception_that_names_it()
    {
        var ex = await Assert.ThrowsAsync<MediaProbeException>(() => MediaEncoder.ProbeAsync(new FakeEngines(), @"C:\x.mp4"));
        Assert.Contains("ffprobe", ex.Message);
    }

    [Fact]
    public async Task Running_without_ffmpeg_fails_naming_it_and_touches_nothing()
    {
        using var dir = new TempDir();
        var input = dir.WriteText("clip.mp4", "not really a video");

        var result = await MediaEncoder.RunAsync(new FakeEngines(), input, EncoderPresets.YouTube1080p, new Progress<JobProgress>());

        Assert.False(result.Success);
        Assert.Contains("FFmpeg", result.Error);
        Assert.Single(Directory.GetFiles(dir.Path));
    }
}
