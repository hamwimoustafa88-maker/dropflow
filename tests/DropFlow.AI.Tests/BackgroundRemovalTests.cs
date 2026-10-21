using System.Drawing;
using System.Security.Cryptography;
using DropFlow.AI.Imaging;
using DropFlow.AI.Models;
using DropFlow.Core.Catalog;
using DropFlow.Core.Operations;
using DropFlow.TestSupport;
using ImageMagick;
using Xunit;

namespace DropFlow.AI.Tests;

/// <summary>Pre- and post-processing for segmentation networks, on synthetic tensors and masks.</summary>
public class SegmentationMathTests
{
    private static BackgroundModelSpec Spec(InputScaling scaling = InputScaling.DivideBy255, bool sigmoid = false, bool minMax = false) =>
        new("t", 2, 1, [0f, 0f, 0f], [1f, 1f, 1f], scaling, sigmoid, minMax, FilterType.Triangle);

    [Fact]
    public void Tensor_is_planar_nchw_not_interleaved()
    {
        // two pixels: red then green
        var t = SegmentationMath.ToTensor([255, 0, 0, 0, 255, 0], 2, 1, Spec());
        Assert.Equal([1f, 0f, /* G plane */ 0f, 1f, /* B plane */ 0f, 0f], t);
    }

    [Fact]
    public void Imagenet_mean_and_std_are_applied_per_channel()
    {
        var t = SegmentationMath.ToTensor([255, 255, 255, 0, 0, 0], 2, 1, BackgroundModelSpec.BiRefNetLite);
        Assert.Equal((1f - 0.485f) / 0.229f, t[0], 4);      // R plane, pixel 0 (white)
        Assert.Equal((0f - 0.485f) / 0.229f, t[1], 4);      // R plane, pixel 1 (black)
        Assert.Equal((1f - 0.456f) / 0.224f, t[2], 4);      // G plane
        Assert.Equal((1f - 0.406f) / 0.225f, t[4], 4);      // B plane
    }

    [Fact]
    public void Divide_by_max_normalises_to_the_brightest_value_in_the_image()
    {
        var t = SegmentationMath.ToTensor([100, 50, 0, 25, 0, 0], 2, 1, Spec(InputScaling.DivideByMax));
        Assert.Equal(1f, t[0], 5);     // 100/100
        Assert.Equal(0.25f, t[1], 5);  // 25/100
        Assert.Equal(0.5f, t[2], 5);   // 50/100
        // an all-black image must not divide by zero
        Assert.All(SegmentationMath.ToTensor(new byte[6], 2, 1, Spec(InputScaling.DivideByMax)), v => Assert.Equal(0f, v));
    }

    [Fact]
    public void Wrong_buffer_size_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => SegmentationMath.ToTensor(new byte[5], 2, 1, Spec()));
    }

    [Fact]
    public void Sigmoid_matches_the_definition()
    {
        Assert.Equal(0.5f, SegmentationMath.Sigmoid(0), 6);
        Assert.Equal(0.7310586f, SegmentationMath.Sigmoid(1), 6);
        Assert.Equal(0.2689414f, SegmentationMath.Sigmoid(-1), 6);
        Assert.Equal(1f, SegmentationMath.Sigmoid(100), 6);
        Assert.Equal(0f, SegmentationMath.Sigmoid(-100), 6);
    }

    [Fact]
    public void Mask_applies_sigmoid_for_logit_models()
    {
        var mask = SegmentationMath.ToMask([-100f, 0f, 100f], Spec(sigmoid: true));
        Assert.Equal([0, 128, 255], mask);
    }

    [Fact]
    public void Mask_stretches_the_output_range_for_models_that_ask_for_it()
    {
        Assert.Equal([0, 128, 255], SegmentationMath.ToMask([2f, 4f, 6f], Spec(minMax: true)));
        // a flat output cannot be stretched; it is clamped instead
        Assert.Equal([128, 128], SegmentationMath.ToMask([0.5f, 0.5f], Spec(minMax: true)));
        Assert.Equal([0, 255], SegmentationMath.ToMask([-3f, 7f], Spec()));   // no stretch: just clamped to 0..1
    }

    [Fact]
    public void A_threshold_makes_hard_edges()
    {
        Assert.Equal([0, 255, 255], SegmentationMath.ToMask([0.2f, 0.5f, 0.8f], Spec(), hardThreshold: 0.5));
        Assert.Equal([51, 128, 204], SegmentationMath.ToMask([0.2f, 0.5f, 0.8f], Spec()));
    }

    [Fact]
    public void Threshold_makes_a_hard_mask_in_place()
    {
        byte[] mask = [0, 127, 128, 255];
        SegmentationMath.Threshold(mask, 0.5);
        Assert.Equal([0, 0, 255, 255], mask);
    }

    [Fact]
    public void Alpha_is_multiplied_so_transparent_areas_stay_transparent()
    {
        byte[] rgba = [10, 20, 30, 255, 10, 20, 30, 128, 10, 20, 30, 0, 10, 20, 30, 255];
        SegmentationMath.ApplyAlpha(rgba, [255, 255, 255, 0]);
        Assert.Equal([255, 128, 0, 0], new[] { rgba[3], rgba[7], rgba[11], rgba[15] });
        Assert.Equal([10, 20, 30], rgba[..3]);   // colour untouched
        Assert.Throws<ArgumentException>(() => SegmentationMath.ApplyAlpha(new byte[8], new byte[3]));
        byte[] half = [1, 2, 3, 255];
        SegmentationMath.ApplyAlpha(half, [128]);
        Assert.Equal(128, half[3]);
    }

    [Fact]
    public void Flattening_blends_over_the_chosen_colour()
    {
        byte[] rgba = [200, 100, 50, 255, 200, 100, 50, 0, 200, 100, 50, 128];
        var onWhite = SegmentationMath.FlattenOnto(rgba, 255, 255, 255);
        Assert.Equal([200, 100, 50], onWhite[..3]);
        Assert.Equal([255, 255, 255], onWhite[3..6]);
        Assert.Equal([(byte)((200 * 128 + 255 * 127 + 127) / 255), (byte)((100 * 128 + 255 * 127 + 127) / 255), (byte)((50 * 128 + 255 * 127 + 127) / 255)], onWhite[6..9]);
        Assert.Equal([0, 0, 0], SegmentationMath.FlattenOnto(rgba, 0, 0, 0)[3..6]);
    }

    [Fact]
    public void Model_specs_follow_the_model_cards()
    {
        var u2 = BackgroundModelSpec.For("u2netp")!;
        Assert.Equal((320, 320), (u2.Width, u2.Height));
        Assert.Equal(InputScaling.DivideByMax, u2.Scaling);
        Assert.True(u2.MinMaxNormalize);
        Assert.False(u2.ApplySigmoid);

        var bi = BackgroundModelSpec.For("birefnet-lite")!;
        Assert.Equal((1024, 1024), (bi.Width, bi.Height));
        Assert.Equal(InputScaling.DivideBy255, bi.Scaling);
        Assert.True(bi.ApplySigmoid);

        Assert.Equal([0.485f, 0.456f, 0.406f], bi.Mean);
        Assert.Equal([0.229f, 0.224f, 0.225f], bi.Std);
        Assert.Null(BackgroundModelSpec.For("nope"));
        Assert.All(BackgroundModelSpec.AutoPreference, id => Assert.NotNull(BackgroundModelSpec.For(id)));
        Assert.All(ModelCatalog.Default.Where(m => m.Purpose == ModelPurpose.BackgroundRemoval), m => Assert.NotNull(BackgroundModelSpec.For(m.Id)));
    }
}

/// <summary>The complete ONNX pipeline run for real on genuine (hand-written) ONNX files.</summary>
public class BackgroundRemovalPipelineTests
{
    private static ModelInfo Entry(string id, byte[] model, string fileName) => new()
    {
        Id = id, DisplayName = id, Purpose = ModelPurpose.BackgroundRemoval, FileName = fileName,
        Url = new Uri("http://127.0.0.1/unused"), SizeBytes = model.Length, Sha256 = Hashing.Sha256Hex(model),
        License = "MIT", SourcePage = "-",
    };

    /// <summary>A manager whose "u2netp" (and optionally "birefnet-lite") are the brightness-mask test network.</summary>
    private static ModelManager ModelsWith(TempDir dir, int netHeight, int netWidth, params string[] ids)
    {
        var bytes = OnnxTestModel.BrightnessMask(netHeight, netWidth);
        var folder = dir.Combine("models");
        Directory.CreateDirectory(folder);
        var catalog = new List<ModelInfo>();
        foreach (var id in new[] { "u2netp", "birefnet-lite" })
        {
            var entry = Entry(id, bytes, id + ".onnx");
            catalog.Add(entry);
            if (ids.Contains(id)) File.WriteAllBytes(Path.Combine(folder, entry.FileName), bytes);
        }
        return new ModelManager(folder, catalog);
    }

    /// <summary>Black photo with a white ellipse in the middle.</summary>
    private static string Photo(string path, int width = 160, int height = 100)
    {
        using var bitmap = new Bitmap(width, height);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.Clear(Color.Black);
            g.FillEllipse(Brushes.White, width / 2 - 30, height / 2 - 25, 60, 50);
        }
        bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        return path;
    }

    private static ConversionJob Job(string input, Dictionary<string, object?>? options = null, string? outDir = null) => new([input], "img.remove-bg", options, outDir);

    [Fact]
    public async Task Cuts_out_the_bright_subject_and_makes_the_background_transparent()
    {
        using var dir = new TempDir();
        using var models = ModelsWith(dir, 320, 320, "u2netp");
        var folder = dir.Combine("صور");
        Directory.CreateDirectory(folder);
        var input = Photo(Path.Combine(folder, "شخص.png"));
        var before = SHA256.HashData(File.ReadAllBytes(input));

        using var sessions = new OnnxSessionCache();
        var result = await new BackgroundRemovalConverter(models, sessions).RunAsync(Job(input), new Progress<JobProgress>(), default);

        Assert.True(result.Success, result.Error);
        Assert.Equal(Path.Combine(folder, "شخص (no background).png"), result.Outputs.Single());
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(input)));

        using var png = new TextImage.Reader(result.Outputs[0]);   // decoded by GDI+, not by the Magick.NET that wrote it
        Assert.Equal((160, 100), (png.Width, png.Height));
        Assert.True(png.Pixel(80, 50).A > 230, $"centre alpha {png.Pixel(80, 50).A}");
        Assert.True(png.Pixel(2, 2).A < 25, $"corner alpha {png.Pixel(2, 2).A}");
        Assert.True(png.Pixel(157, 97).A < 25);
        // colours of kept pixels are the photo's own
        var centre = png.Pixel(80, 50);
        Assert.True(centre.R > 240 && centre.G > 240 && centre.B > 240);
    }

    [Fact]
    public async Task Soft_edges_by_default_and_hard_edges_on_request()
    {
        using var dir = new TempDir();
        using var models = ModelsWith(dir, 320, 320, "u2netp");
        var input = Photo(dir.Combine("p.png"));
        using var sessions = new OnnxSessionCache();
        var converter = new BackgroundRemovalConverter(models, sessions);

        var soft = await converter.RunAsync(Job(input), new Progress<JobProgress>(), default);
        var hard = await converter.RunAsync(Job(input, new() { ["edge"] = "hard" }), new Progress<JobProgress>(), default);

        using var s = new TextImage.Reader(soft.Outputs[0]);
        using var h = new TextImage.Reader(hard.Outputs[0]);
        var softLevels = new HashSet<int>();
        var hardLevels = new HashSet<int>();
        for (var y = 0; y < 100; y++)
            for (var x = 0; x < 160; x++) { softLevels.Add(s.Pixel(x, y).A); hardLevels.Add(h.Pixel(x, y).A); }
        Assert.True(softLevels.Count > 10, "soft edges have intermediate alpha");
        Assert.Equal([0, 255], hardLevels.Order());
    }

    [Fact]
    public async Task Can_fill_the_background_with_white_or_black_instead_of_transparency()
    {
        using var dir = new TempDir();
        using var models = ModelsWith(dir, 320, 320, "u2netp");
        var input = Photo(dir.Combine("p.png"));
        using var sessions = new OnnxSessionCache();
        var converter = new BackgroundRemovalConverter(models, sessions);

        var white = await converter.RunAsync(Job(input, new() { ["background"] = "white" }), new Progress<JobProgress>(), default);
        var black = await converter.RunAsync(Job(input, new() { ["background"] = "black" }), new Progress<JobProgress>(), default);

        using var w = new TextImage.Reader(white.Outputs[0]);
        using var b = new TextImage.Reader(black.Outputs[0]);
        Assert.Equal(255, w.Pixel(2, 2).A);
        Assert.True(w.Pixel(2, 2).R > 230);         // the originally black corner became white
        Assert.True(b.Pixel(2, 2).R < 25);
        Assert.True(w.Pixel(80, 50).R > 230 && b.Pixel(80, 50).R > 230); // subject unchanged
    }

    [Fact]
    public async Task The_models_declared_fixed_input_size_wins_over_the_spec_and_the_mask_is_scaled_to_the_photo()
    {
        using var dir = new TempDir();
        using var models = ModelsWith(dir, 48, 64, "u2netp");   // spec says 320x320, the file says 48x64
        var input = Photo(dir.Combine("big.png"), 400, 250);
        using var sessions = new OnnxSessionCache();

        var result = await new BackgroundRemovalConverter(models, sessions).RunAsync(Job(input), new Progress<JobProgress>(), default);

        Assert.True(result.Success, result.Error);
        using var png = new TextImage.Reader(result.Outputs[0]);
        Assert.Equal((400, 250), (png.Width, png.Height));
        Assert.True(png.Pixel(200, 125).A > 230);
        Assert.True(png.Pixel(5, 5).A < 25);
    }

    [Fact]
    public async Task Existing_transparency_is_kept_when_the_mask_is_applied()
    {
        using var dir = new TempDir();
        using var models = ModelsWith(dir, 320, 320, "u2netp");
        var input = dir.Combine("holey.png");
        using (var bitmap = new Bitmap(100, 100))
        {
            using (var g = Graphics.FromImage(bitmap)) { g.Clear(Color.White); }
            for (var x = 0; x < 20; x++) for (var y = 0; y < 100; y++) bitmap.SetPixel(x, y, Color.FromArgb(0, 255, 255, 255)); // fully transparent strip
            bitmap.Save(input, System.Drawing.Imaging.ImageFormat.Png);
        }
        using var sessions = new OnnxSessionCache();
        var result = await new BackgroundRemovalConverter(models, sessions).RunAsync(Job(input, new() { ["edge"] = "hard" }), new Progress<JobProgress>(), default);

        using var png = new TextImage.Reader(result.Outputs[0]);
        Assert.Equal(0, png.Pixel(5, 50).A);      // was transparent, stays transparent
        Assert.Equal(255, png.Pixel(60, 50).A);   // opaque white subject kept
    }

    [Fact]
    public async Task Never_overwrites_and_honours_the_output_directory()
    {
        using var dir = new TempDir();
        using var models = ModelsWith(dir, 320, 320, "u2netp");
        var input = Photo(dir.Combine("p.png"));
        File.WriteAllText(dir.Combine("p (no background).png"), "mine");
        using var sessions = new OnnxSessionCache();
        var converter = new BackgroundRemovalConverter(models, sessions);

        var first = await converter.RunAsync(Job(input), new Progress<JobProgress>(), default);
        Assert.Equal(dir.Combine("p (no background 2).png"), first.Outputs.Single());
        Assert.Equal("mine", File.ReadAllText(dir.Combine("p (no background).png")));

        Directory.CreateDirectory(dir.Combine("out"));
        var moved = await converter.RunAsync(Job(input, outDir: dir.Combine("out")), new Progress<JobProgress>(), default);
        Assert.Equal(Path.Combine(dir.Combine("out"), "p (no background).png"), moved.Outputs.Single());
    }

    [Fact]
    public async Task Auto_prefers_the_better_installed_model()
    {
        using var dir = new TempDir();
        using var both = ModelsWith(dir, 320, 320, "u2netp", "birefnet-lite");
        using var sessions = new OnnxSessionCache();
        var c = new BackgroundRemovalConverter(both, sessions);
        Assert.Equal("birefnet-lite", c.ResolveModel("auto", out _));

        using var dir2 = new TempDir();
        using var onlySmall = ModelsWith(dir2, 320, 320, "u2netp");
        Assert.Equal("u2netp", new BackgroundRemovalConverter(onlySmall, sessions).ResolveModel("auto", out _));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Missing_model_fails_naming_it_and_writes_nothing()
    {
        using var dir = new TempDir();
        using var models = ModelsWith(dir, 320, 320);   // nothing installed
        var input = Photo(dir.Combine("p.png"));
        using var sessions = new OnnxSessionCache();
        var converter = new BackgroundRemovalConverter(models, sessions);

        var auto = await converter.RunAsync(Job(input), new Progress<JobProgress>(), default);
        Assert.False(auto.Success);
        Assert.Equal("u2netp", AiFailures.TryParseMissingModel(auto.Error));

        var named = await converter.RunAsync(Job(input, new() { ["model"] = "birefnet-lite" }), new Progress<JobProgress>(), default);
        Assert.Equal("birefnet-lite", AiFailures.TryParseMissingModel(named.Error));

        var unknown = await converter.RunAsync(Job(input, new() { ["model"] = "rmbg-1.4" }), new Progress<JobProgress>(), default);
        Assert.False(unknown.Success);
        Assert.Contains("not a supported", unknown.Error);
        Assert.Empty(Directory.GetFiles(dir.Path, "*(no background*"));
    }

    [Fact]
    public async Task A_damaged_model_file_is_reported_not_thrown()
    {
        using var dir = new TempDir();
        var good = OnnxTestModel.BrightnessMask(320, 320);
        var garbage = Hashing.Bytes(good.Length, seed: 3);
        var folder = dir.Combine("models");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "u2netp.onnx"), garbage);
        using var models = new ModelManager(folder, [Entry("u2netp", good, "u2netp.onnx")]);
        var input = Photo(dir.Combine("p.png"));
        using var sessions = new OnnxSessionCache();

        var result = await new BackgroundRemovalConverter(models, sessions).RunAsync(Job(input), new Progress<JobProgress>(), default);
        Assert.False(result.Success);
        Assert.Contains("not a valid ONNX", result.Error);
        Assert.Empty(Directory.GetFiles(dir.Path, "*(no background*"));
    }

    [Fact]
    public async Task Unreadable_images_and_cancellation_are_handled()
    {
        using var dir = new TempDir();
        using var models = ModelsWith(dir, 320, 320, "u2netp");
        using var sessions = new OnnxSessionCache();
        var converter = new BackgroundRemovalConverter(models, sessions);

        var broken = dir.WriteText("broken.png", "not a png");
        var bad = await converter.RunAsync(Job(broken), new Progress<JobProgress>(), default);
        Assert.False(bad.Success);
        Assert.Contains("could not be read", bad.Error);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => converter.RunAsync(Job(Photo(dir.Combine("p.png"))), new Progress<JobProgress>(), cts.Token));
        Assert.Empty(Directory.GetFiles(dir.Path, "*(no background*"));
    }

    [Fact]
    public void The_session_cache_keeps_the_most_recent_models_and_recovers_after_eviction()
    {
        using var dir = new TempDir();
        var a = dir.Combine("a.onnx");
        var b = dir.Combine("b.onnx");
        File.WriteAllBytes(a, OnnxTestModel.BrightnessMask(8, 8));
        File.WriteAllBytes(b, OnnxTestModel.BrightnessMask(16, 16));
        using var cache = new OnnxSessionCache(capacity: 1);

        var firstA = cache.Run(a, s => s.GetHashCode());
        Assert.Equal(firstA, cache.Run(a, s => s.GetHashCode()));             // reused
        var forB = cache.Run(b, s => s.InputMetadata.First().Value.Dimensions[2]);
        Assert.Equal(16, forB);                                               // evicts a
        Assert.Equal(8, cache.Run(a, s => s.InputMetadata.First().Value.Dimensions[2])); // a loads again
        Assert.Throws<ImageToolException>(() => cache.Run(dir.Combine("missing.onnx"), s => 0));
    }

    [Fact]
    public void Supports_options_and_engine_are_truthful()
    {
        using var dir = new TempDir();
        using var models = ModelsWith(dir, 320, 320);
        using var sessions = new OnnxSessionCache();
        var c = new BackgroundRemovalConverter(models, sessions);

        Assert.Equal(["img.remove-bg"], c.Operations);
        Assert.NotNull(ToolCatalog.LoadEmbedded().Tool("img.remove-bg"));
        Assert.Equal(EngineId.None, c.EngineFor("img.remove-bg"));
        foreach (var ext in new[] { "png", "jpg", "webp", "tiff", "bmp" }) Assert.True(c.Supports("img.remove-bg", ext), ext);
        foreach (var ext in new[] { "mp4", "pdf", "txt", "" }) Assert.False(c.Supports("img.remove-bg", ext), ext);
        Assert.False(c.Supports("img.redact-auto", "png"));

        var options = c.OptionsFor("img.remove-bg").ToDictionary(o => o.Key);
        Assert.Equal(["background", "edge", "model"], options.Keys.Order());
        Assert.Equal(["auto", "birefnet-lite", "u2netp"], options["model"].Choices);
        Assert.Equal(["transparent", "white", "black"], options["background"].Choices);
    }

    // ---- real models (skipped unless downloaded) ---------------------------------------------------------------------

    [ModelFact("u2netp")]
    public async Task Real_u2netp_produces_a_cutout_with_an_alpha_channel()
    {
        using var dir = new TempDir();
        using var models = ModelFactAttribute.OpenManager();
        var input = Photo(dir.Combine("subject.png"), 480, 320);
        using var sessions = new OnnxSessionCache();

        var result = await new BackgroundRemovalConverter(models, sessions).RunAsync(Job(input, new() { ["model"] = "u2netp" }), new Progress<JobProgress>(), default);

        Assert.True(result.Success, result.Error);
        using var png = new TextImage.Reader(result.Outputs[0]);
        Assert.Equal((480, 320), (png.Width, png.Height));
        var alphas = new HashSet<int>();
        for (var y = 0; y < 320; y += 8) for (var x = 0; x < 480; x += 8) alphas.Add(png.Pixel(x, y).A);
        Assert.True(alphas.Count > 1, "the mask must not be constant");
    }

    [ModelFact("birefnet-lite")]
    public async Task Real_birefnet_lite_produces_a_cutout_with_an_alpha_channel()
    {
        using var dir = new TempDir();
        using var models = ModelFactAttribute.OpenManager();
        var input = Photo(dir.Combine("subject.png"), 480, 320);
        using var sessions = new OnnxSessionCache();

        var result = await new BackgroundRemovalConverter(models, sessions).RunAsync(Job(input, new() { ["model"] = "birefnet-lite" }), new Progress<JobProgress>(), default);

        Assert.True(result.Success, result.Error);
        using var png = new TextImage.Reader(result.Outputs[0]);
        Assert.Equal((480, 320), (png.Width, png.Height));
    }
}
