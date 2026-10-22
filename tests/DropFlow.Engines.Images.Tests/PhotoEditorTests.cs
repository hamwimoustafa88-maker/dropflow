using DropFlow.Core.Operations;
using DropFlow.Engines.Images;
using DropFlow.TestSupport;
using ImageMagick;
using ImageMagick.Drawing;

namespace DropFlow.Engines.Images.Tests;

/// <summary>Each adjustment of the photo editor does what its name says, and the small preview matches the saved result.</summary>
public class PhotoEditorTests
{
    /// <summary>A grey ramp from black to white (left to right), with a dull and a vivid patch at the bottom.</summary>
    private static MagickImage Ramp(uint w = 256, uint h = 96)
    {
        var img = new MagickImage("gradient:black-white", new MagickReadSettings { Width = h, Height = w });
        img.Rotate(-90);
        img.ResetPage();
        ImageIo.EnsureColor(img);
        new Drawables().FillColor(new MagickColor("#8a7f75")).Rectangle(0, h - 24, w / 2 - 1, h - 1).Draw(img);   // dull
        new Drawables().FillColor(new MagickColor("#e01010")).Rectangle(w / 2, h - 24, w - 1, h - 1).Draw(img);   // vivid
        return img;
    }

    private static MagickImage Flat(byte v, uint w = 200, uint h = 150) => new(new MagickColor(v, v, v), w, h);

    private static MagickImage Edited(MagickImage source, PhotoAdjust adjust, double scale = 1)
    {
        var copy = (MagickImage)source.Clone();
        PhotoEditor.Apply(copy, adjust, scale);
        return copy;
    }

    private static (double R, double G, double B) Mean(MagickImage img, int x = 0, int y = 0, int w = -1, int h = -1)
    {
        w = w < 0 ? (int)img.Width - x : w;
        h = h < 0 ? (int)img.Height - y : h;
        var bytes = img.GetPixels().ToByteArray(x, y, (uint)w, (uint)h, "RGB")!;
        double r = 0, g = 0, b = 0;
        for (var i = 0; i < bytes.Length; i += 3) { r += bytes[i]; g += bytes[i + 1]; b += bytes[i + 2]; }
        var n = bytes.Length / 3.0;
        return (r / n, g / n, b / n);
    }

    private static double Gray(MagickImage img, int x, int y) => img.GetPixels().GetPixel(x, y).ToColor()!.R;

    private static double StdDev(MagickImage img, int x = 0, int y = 0, int w = -1, int h = -1)
    {
        w = w < 0 ? (int)img.Width - x : w;
        h = h < 0 ? (int)img.Height - y : h;
        var bytes = img.GetPixels().ToByteArray(x, y, (uint)w, (uint)h, "R")!;
        var mean = bytes.Average(b => (double)b);
        return Math.Sqrt(bytes.Average(b => (b - mean) * (b - mean)));
    }

    private static double Saturation(MagickImage img, int x, int y)
    {
        var c = img.GetPixels().GetPixel(x, y).ToColor()!;
        return Math.Max(c.R, Math.Max(c.G, c.B)) - Math.Min(c.R, Math.Min(c.G, c.B));
    }

    [Fact]
    public void Exposure_brightens_and_darkens()
    {
        using var src = Ramp();
        using var up = Edited(src, new PhotoAdjust { Exposure = 50 });
        using var down = Edited(src, new PhotoAdjust { Exposure = -50 });
        Assert.True(Mean(up, 0, 0, 256, 60).R > Mean(src, 0, 0, 256, 60).R + 10);
        Assert.True(Mean(down, 0, 0, 256, 60).R < Mean(src, 0, 0, 256, 60).R - 10);
    }

    [Fact]
    public void Highlights_change_bright_tones_and_leave_the_darkest_alone()
    {
        using var src = Ramp();
        using var low = Edited(src, new PhotoAdjust { Highlights = -100 });
        Assert.True(Gray(low, 200, 10) < Gray(src, 200, 10) - 8, "bright tones come down");
        Assert.InRange(Gray(low, 10, 10) - Gray(src, 10, 10), -2, 2);
    }

    [Fact]
    public void Shadows_lift_dark_tones_and_leave_the_brightest_alone()
    {
        using var src = Ramp();
        using var lifted = Edited(src, new PhotoAdjust { Shadows = 100 });
        Assert.True(Gray(lifted, 70, 10) > Gray(src, 70, 10) + 8, "dark tones go up");
        Assert.InRange(Gray(lifted, 252, 10) - Gray(src, 252, 10), -2, 2);
    }

    [Fact]
    public void Whites_and_blacks_move_the_ends_of_the_range()
    {
        using var src = Ramp();
        using var whites = Edited(src, new PhotoAdjust { Whites = -100 });
        using var blacks = Edited(src, new PhotoAdjust { Blacks = 100 });
        Assert.True(Gray(whites, 250, 10) < Gray(src, 250, 10) - 20);
        Assert.True(Gray(blacks, 5, 10) > Gray(src, 5, 10) + 20);
    }

    [Fact]
    public void Gamma_brightens_the_midtones_but_keeps_black_and_white()
    {
        using var src = Ramp();
        using var g = Edited(src, new PhotoAdjust { Gamma = 60 });
        Assert.True(Gray(g, 128, 10) > Gray(src, 128, 10) + 15);
        Assert.InRange(Gray(g, 0, 10), 0, 3);
        Assert.InRange(Gray(g, 255, 10), 252, 255);
    }

    [Fact]
    public void Temperature_warms_and_tint_moves_towards_magenta()
    {
        using var src = Flat(128);
        using var warm = Edited(src, new PhotoAdjust { Temperature = 80 });
        using var magenta = Edited(src, new PhotoAdjust { Tint = 80 });
        var w = Mean(warm);
        var m = Mean(magenta);
        Assert.True(w.R > w.B + 20, $"warm {w}");
        Assert.True(m.G < m.R - 10 && m.G < m.B - 10, $"magenta {m}");
    }

    [Fact]
    public void Saturation_and_hue_change_colour()
    {
        using var src = Ramp();
        using var gray = Edited(src, new PhotoAdjust { Saturation = 0 });
        using var hue = Edited(src, new PhotoAdjust { Hue = 120 });
        Assert.True(Saturation(gray, 200, 85) < 4);
        var red = src.GetPixels().GetPixel(200, 85).ToColor()!;
        var turned = hue.GetPixels().GetPixel(200, 85).ToColor()!;
        Assert.True(turned.R < red.R - 60, $"hue rotated red to {turned}");
    }

    [Fact]
    public void Vibrance_boosts_dull_colours_more_than_vivid_ones()
    {
        using var src = Ramp();
        using var vib = Edited(src, new PhotoAdjust { Vibrance = 80 });
        var dullGain = Saturation(vib, 40, 85) / Math.Max(1, Saturation(src, 40, 85));
        var vividGain = Saturation(vib, 200, 85) / Math.Max(1, Saturation(src, 200, 85));
        Assert.True(dullGain > 1.3, $"dull gain {dullGain}");
        Assert.True(dullGain > vividGain, $"dull {dullGain} vs vivid {vividGain}");
    }

    [Fact]
    public void Clarity_and_sharpen_add_local_contrast()
    {
        using var soft = new MagickImage("gradient:#404040-#c0c0c0", new MagickReadSettings { Width = 200, Height = 200 });
        new Drawables().FillColor(MagickColors.White).Rectangle(60, 60, 140, 140).Draw(soft);
        soft.Blur(0, 4);
        using var clear = Edited(soft, new PhotoAdjust { Clarity = 100 });
        using var sharp = Edited(soft, new PhotoAdjust { Sharpen = 100 });
        using var softer = Edited(soft, new PhotoAdjust { Clarity = -100 });
        Assert.True(StdDev(softer, 40, 40, 120, 120) < StdDev(soft, 40, 40, 120, 120), "negative clarity softens");
        Assert.True(StdDev(clear, 40, 40, 120, 120) > StdDev(soft, 40, 40, 120, 120) + 1, $"clarity {StdDev(clear, 40, 40, 120, 120)} vs {StdDev(soft, 40, 40, 120, 120)}");
        Assert.True(StdDev(sharp, 50, 90, 20, 20) > StdDev(soft, 50, 90, 20, 20), $"sharp {StdDev(sharp, 50, 90, 20, 20)} vs {StdDev(soft, 50, 90, 20, 20)}");
    }

    [Fact]
    public void Noise_reduction_smooths_a_noisy_picture()
    {
        using var noisy = Flat(128);
        noisy.AddNoise(NoiseType.Gaussian, 3);
        using var clean = Edited(noisy, new PhotoAdjust { Noise = 100 });
        Assert.True(StdDev(clean) < StdDev(noisy) * 0.8, $"{StdDev(clean)} vs {StdDev(noisy)}");
    }

    [Fact]
    public void Vignette_darkens_the_corners_not_the_centre()
    {
        using var src = Flat(200);
        using var v = Edited(src, new PhotoAdjust { Vignette = 80 });
        Assert.True(Gray(v, 2, 2) < 120);
        Assert.InRange(Gray(v, 100, 75), 195, 205);
        using var light = Edited(src, new PhotoAdjust { Vignette = -80 });
        Assert.True(Gray(light, 2, 2) > 220);
    }

    [Fact]
    public void Grain_adds_texture_and_fade_lifts_the_blacks()
    {
        using var src = Flat(128);
        using var grain = Edited(src, new PhotoAdjust { Grain = 80 });
        Assert.True(StdDev(grain) > 5);
        using var black = Flat(0);
        using var faded = Edited(black, new PhotoAdjust { Fade = 100 });
        Assert.True(Gray(faded, 10, 10) > 20);
    }

    [Fact]
    public void The_preview_of_a_small_copy_matches_the_full_result_scaled_down()
    {
        using var big = Ramp(1200, 800);
        big.AddNoise(NoiseType.Gaussian, 0.4);
        var adjust = new PhotoAdjust { Exposure = 20, Shadows = 40, Clarity = 50, Sharpen = 60, Vignette = 50, Temperature = 30, Blur = 3 };

        using var full = Edited(big, adjust);
        full.Resize(300, 200);

        using var small = (MagickImage)big.Clone();
        small.Resize(300, 200);
        PhotoEditor.Apply(small, adjust, 300.0 / 1200);

        var diff = full.Compare(small, ErrorMetric.MeanAbsolute);
        Assert.True(diff < 0.03, $"preview differs from result by {diff:0.000}");
    }

    [Fact]
    public void Preview_grain_is_about_as_strong_as_the_full_size_grain_seen_scaled_down()
    {
        using var big = Flat(128, 1200, 800);
        using var full = Edited(big, new PhotoAdjust { Grain = 60 });
        full.Resize(300, 200);
        using var small = Flat(128, 300, 200);
        PhotoEditor.Apply(small, new PhotoAdjust { Grain = 60 }, 0.25);
        var ratio = StdDev(small) / StdDev(full);
        Assert.InRange(ratio, 0.5, 2.0);
    }

    [Fact]
    public void Transparency_is_kept()
    {
        using var img = new MagickImage(new MagickColor("#80808080"), 40, 40);
        PhotoEditor.Apply(img, new PhotoAdjust { Exposure = 50, Vignette = 50 });
        Assert.True(img.HasAlpha);
        var a = img.GetPixels().GetPixel(20, 20).ToColor()!.A;
        Assert.InRange(a, 120, 136);
    }

    [Fact]
    public void Options_round_trip_and_the_converter_applies_new_adjustments()
    {
        var adjust = new PhotoAdjust { Exposure = 10, Saturation = 150, Grain = 5, Rotate = 90, Filter = "sepia" };
        var job = new ConversionJob(["x.png"], "img.edit", adjust.ToOptions());
        Assert.Equal(adjust, PhotoAdjust.FromOptions(job));
        Assert.Empty(PhotoAdjust.None.ToOptions());
    }

    [Fact]
    public async Task The_converter_writes_an_edited_copy_for_a_new_adjustment()
    {
        using var dir = new TempDir();
        var png = Samples.Png(dir.Combine("صورة.png"), 40, 30);
        var tool = ImagesModule.Create(new FormatConverterTests.NoEngines()).Single(c => c.Operations.Contains("img.edit"));
        var r = await tool.RunAsync(new ConversionJob([png], "img.edit", new Dictionary<string, object?> { ["vignette"] = 60d }), new Progress<JobProgress>(), default);
        Assert.True(r.Success, r.Error);
        Assert.EndsWith("(edited).png", r.Outputs.Single());
    }

    [Fact]
    public void Every_option_key_has_a_range_and_reads_back()
    {
        foreach (var key in PhotoAdjust.LightKeys.Concat(PhotoAdjust.ColorKeys).Concat(PhotoAdjust.DetailKeys).Concat(PhotoAdjust.EffectKeys))
        {
            var (min, max, neutral) = PhotoAdjust.RangeOf(key);
            Assert.InRange(neutral, min, max);
            Assert.Equal(max, PhotoAdjust.None.With(key, max).Get(key));
        }
    }
}
