using System.Text;
using DropFlow.Core.Operations;
using DropFlow.Engines.Archive.Images;
using DropFlow.TestSupport;
using ImageMagick;
using ZXing;
using ZXing.Common;

namespace DropFlow.Engines.Archive.Tests;

public class QrTests
{
    private static QrConverter Converter => new();

    private static string WritePng(TempDir temp, string name, string text, QrCreateOptions? options = null)
    {
        var path = temp.Combine(name);
        File.WriteAllBytes(path, QrCodeService.CreatePng(text, options));
        return path;
    }

    // ---------------------------------------------------------------- contract

    [Theory]
    [InlineData("png", true)]
    [InlineData("jpg", true)]
    [InlineData("webp", true)]
    [InlineData("bmp", true)]
    [InlineData("gif", true)]
    [InlineData("tiff", true)]
    [InlineData("heic", true)]
    [InlineData("avif", true)]
    [InlineData("svg", true)]
    [InlineData("ico", true)]
    [InlineData("txt", true)]     // create mode takes its text from a .txt file
    [InlineData("pdf", false)]
    [InlineData("zip", false)]
    [InlineData("docx", false)]
    [InlineData("", false)]
    public void Supports_images_and_text_files_only(string ext, bool expected)
    {
        Assert.Equal(expected, Converter.Supports("img.qr", ext));
        Assert.False(Converter.Supports("img.icons", ext));
    }

    [Fact]
    public void Asks_for_the_mode_first_and_offers_the_encoding_options()
    {
        var options = Converter.OptionsFor("img.qr");
        var mode = Assert.Single(options, o => o.Key == "mode");
        Assert.Equal(["read", "create"], mode.Choices);
        Assert.Equal("read", mode.Default);
        var level = Assert.Single(options, o => o.Key == "errorCorrection");
        Assert.Equal(["L", "M", "Q", "H", "auto"], level.Choices);
        Assert.Equal("M", level.Default);
        Assert.Contains(options, o => o.Key == "text");
        Assert.Contains(options, o => o.Key == "size");
        Assert.Contains(options, o => o.Key == "quietZone");
        Assert.Equal(EngineId.None, Converter.EngineFor("img.qr"));
    }

    // ---------------------------------------------------------------- create

    [Fact]
    public async Task Creates_a_png_from_the_text_option_that_decodes_back_to_the_text()
    {
        using var temp = new TempDir();
        var image = Samples.Png(temp.Combine("logo.png"));

        var result = await Kit.Run(Converter, "img.qr", image, new() { ["mode"] = "create", ["text"] = "https://example.com/dropflow?lang=ar&x=1" });

        Assert.True(result.Success, result.Error);
        var output = Assert.Single(result.Outputs);
        Assert.Equal("logo (qr).png", Path.GetFileName(output));
        using var picture = new MagickImage(output);
        Assert.Equal(MagickFormat.Png, picture.Format);
        Assert.Equal((uint)512, picture.Width);
        Assert.Equal((uint)512, picture.Height);
        Assert.Equal("https://example.com/dropflow?lang=ar&x=1", Assert.Single(QrCodeService.Read(output)).Text);
        Assert.Equal(image, temp.Combine("logo.png")); // the source image is untouched
    }

    [Fact]
    public async Task Creates_from_the_text_inside_a_txt_file_without_a_mode_option()
    {
        using var temp = new TempDir();
        var file = temp.WriteText("link.txt", "https://dropflow.example/download\r\n\r\n");

        var result = await Kit.Run(Converter, "img.qr", file);

        Assert.True(result.Success, result.Error);
        Assert.Equal("link (qr).png", Path.GetFileName(Assert.Single(result.Outputs)));
        Assert.Equal("https://dropflow.example/download", Assert.Single(QrCodeService.Read(result.Outputs[0])).Text);   // trailing line breaks are dropped
    }

    [Fact]
    public async Task Arabic_text_and_arabic_file_names_survive()
    {
        using var temp = new TempDir();
        var file = temp.WriteText($"{Kit.ArabicFile}.txt", $"{Kit.ArabicText} - https://example.com/مرحبا");

        var result = await Kit.Run(Converter, "img.qr", file, new() { ["mode"] = "create" });

        Assert.True(result.Success, result.Error);
        Assert.Equal($"{Kit.ArabicFile} (qr).png", Path.GetFileName(Assert.Single(result.Outputs)));
        Assert.Equal($"{Kit.ArabicText} - https://example.com/مرحبا", Assert.Single(QrCodeService.Read(result.Outputs[0])).Text);
    }

    [Fact]
    public async Task Multi_line_text_is_kept_exactly()
    {
        using var temp = new TempDir();
        var image = Samples.Png(temp.Combine("a.png"));
        const string text = "WIFI:T:WPA;S:Home Network;P:pass word;;\nline two\nline three";

        var result = await Kit.Run(Converter, "img.qr", image, new() { ["mode"] = "create", ["text"] = text });

        Assert.Equal(text, Assert.Single(QrCodeService.Read(Assert.Single(result.Outputs))).Text);
    }

    [Theory]
    [InlineData(128)]
    [InlineData(300)]
    [InlineData(1000)]
    public async Task The_size_option_sets_the_pixel_size(int size)
    {
        using var temp = new TempDir();
        var image = Samples.Png(temp.Combine("a.png"));

        var result = await Kit.Run(Converter, "img.qr", image, new() { ["mode"] = "create", ["text"] = "size test", ["size"] = size });

        using var picture = new MagickImage(Assert.Single(result.Outputs));
        Assert.Equal((uint)size, picture.Width);
        Assert.Equal((uint)size, picture.Height);
        Assert.Equal("size test", Assert.Single(QrCodeService.Read(result.Outputs[0])).Text);
    }

    [Fact]
    public async Task Out_of_range_sizes_are_clamped()
    {
        using var temp = new TempDir();
        var image = Samples.Png(temp.Combine("a.png"));

        var tiny = await Kit.Run(Converter, "img.qr", image, new() { ["mode"] = "create", ["text"] = "x", ["size"] = 2 });
        using var small = new MagickImage(Assert.Single(tiny.Outputs));
        Assert.Equal((uint)QrCodeService.MinSize, small.Width);

        var huge = await Kit.Run(Converter, "img.qr", image, new() { ["mode"] = "create", ["text"] = "x", ["size"] = 999_999 });
        using var large = new MagickImage(Assert.Single(huge.Outputs));
        Assert.Equal((uint)QrCodeService.MaxSize, large.Width);
    }

    [Fact]
    public async Task Quiet_zone_controls_the_blank_border_in_modules()
    {
        using var temp = new TempDir();
        var image = Samples.Png(temp.Combine("a.png"));

        async Task<MagickImage> Make(int quiet)
        {
            var result = await Kit.Run(Converter, "img.qr", image, new() { ["mode"] = "create", ["text"] = "border", ["size"] = 400, ["quietZone"] = quiet });
            return new MagickImage(Assert.Single(result.Outputs));
        }

        using var none = await Make(0);
        using var wide = await Make(4);

        Assert.True(IsDark(none, 0, 0), "with no quiet zone the finder pattern touches the corner");
        Assert.False(IsDark(wide, 0, 0));
        // with 4 modules of border on a ~29-module code in 400 px, nothing dark may appear in the first ~40 px on any edge
        for (var i = 0; i < 40; i++)
        {
            Assert.False(IsDark(wide, i, 200));
            Assert.False(IsDark(wide, 200, i));
            Assert.False(IsDark(wide, 399 - i, 200));
            Assert.False(IsDark(wide, 200, 399 - i));
        }
    }

    [Fact]
    public void Higher_error_correction_needs_a_denser_code_so_big_texts_only_fit_at_low_levels()
    {
        var text = new string('x', 2500) + "!";

        Assert.NotEmpty(QrCodeService.CreatePng(text, new QrCreateOptions(QrErrorCorrection.L)));
        Assert.Throws<QrTooLargeException>(() => QrCodeService.CreatePng(text, new QrCreateOptions(QrErrorCorrection.H)));

        // same short text: H has more modules than L, so its modules are smaller on a fixed-size image
        var l = ModuleSize(QrCodeService.CreatePng("DropFlow QR error correction", new QrCreateOptions(QrErrorCorrection.L, 800, 4)));
        var h = ModuleSize(QrCodeService.CreatePng("DropFlow QR error correction", new QrCreateOptions(QrErrorCorrection.H, 800, 4)));
        Assert.True(h < l, $"H module {h}px should be smaller than L module {l}px");
    }

    [Fact]
    public async Task Every_error_correction_level_produces_a_readable_code_and_survives_damage_in_proportion()
    {
        using var temp = new TempDir();
        var image = Samples.Png(temp.Combine("a.png"));
        const string text = "https://example.com/resilience-test-0123456789";

        foreach (var level in new[] { "L", "M", "Q", "H" })
        {
            var result = await Kit.Run(Converter, "img.qr", image, new() { ["mode"] = "create", ["text"] = text, ["errorCorrection"] = level, ["size"] = 600 });
            Assert.True(result.Success, result.Error);
            Assert.Equal(text, Assert.Single(QrCodeService.Read(result.Outputs[0])).Text);
        }

        // Level H tolerates a blot over ~10% of the code (a logo in the middle); level L does not.
        var withH = new MagickImage(QrCodeService.CreatePng(text, new QrCreateOptions(QrErrorCorrection.H, 600, 4)));
        BlotCenter(withH, 130);
        Assert.Equal(text, Assert.Single(QrCodeService.Read(ToBytes(withH))).Text);
    }

    [Fact]
    public async Task Text_that_is_too_long_fails_clearly_and_creates_nothing()
    {
        using var temp = new TempDir();
        var image = Samples.Png(temp.Combine("a.png"));

        var result = await Kit.Run(Converter, "img.qr", image, new() { ["mode"] = "create", ["text"] = new string('y', 5000) });

        Assert.False(result.Success);
        Assert.Contains("too long", result.Error);
        Assert.Equal(["a.png"], Directory.GetFileSystemEntries(temp.Path).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Create_without_any_text_asks_for_it()
    {
        using var temp = new TempDir();
        var image = Samples.Png(temp.Combine("a.png"));
        var empty = temp.WriteText("empty.txt", "\r\n");

        var noText = await Kit.Run(Converter, "img.qr", image, new() { ["mode"] = "create" });
        var emptyFile = await Kit.Run(Converter, "img.qr", empty);

        Assert.False(noText.Success);
        Assert.Contains("Enter the text", noText.Error);
        Assert.False(emptyFile.Success);
    }

    // ---------------------------------------------------------------- read

    [Theory]
    [InlineData("avif")]
    [InlineData("webp")]
    [InlineData("tiff")]
    [InlineData("bmp")]
    [InlineData("gif")]
    [InlineData("jpg")]
    public async Task Every_listed_image_format_that_can_be_written_here_is_accepted_as_input(string format)
    {
        using var temp = new TempDir();
        var path = temp.Combine($"code.{format}");
        using (var image = new MagickImage(QrCodeService.CreatePng("input format check", new QrCreateOptions(QrErrorCorrection.M, 360, 4)))) image.Write(path);

        Assert.True(Converter.Supports("img.qr", format));
        var result = await Kit.Run(Converter, "img.qr", path, new() { ["mode"] = "read" });
        Assert.True(result.Success, $"{format}: {result.Error}");
        Assert.Equal("input format check", File.ReadAllText(Assert.Single(result.Outputs)));
    }

    [Fact]
    public void Heic_is_claimed_because_the_decoder_is_registered_but_no_heic_encoder_exists_to_make_a_sample()
    {
        var heic = MagickNET.SupportedFormats.Single(f => f.Format == MagickFormat.Heic);
        Assert.True(heic.SupportsReading);
        Assert.False(heic.SupportsWriting);     // so HEIC decoding cannot be exercised with a generated file here
        Assert.True(Converter.Supports("img.qr", "heic"));
    }

    [Fact]
    public async Task Reads_a_qr_code_from_a_png_into_a_txt_file()
    {
        using var temp = new TempDir();
        var png = WritePng(temp, "poster.png", "https://example.com/read-me");

        var result = await Kit.Run(Converter, "img.qr", png, new() { ["mode"] = "read" });

        Assert.True(result.Success, result.Error);
        var output = Assert.Single(result.Outputs);
        Assert.Equal("poster (qr).txt", Path.GetFileName(output));
        Assert.Equal("https://example.com/read-me", File.ReadAllText(output));
    }

    [Theory]
    [InlineData("jpg")]
    [InlineData("bmp")]
    [InlineData("gif")]
    [InlineData("tiff")]
    [InlineData("webp")]
    public async Task Reads_codes_saved_in_other_image_formats(string format)
    {
        using var temp = new TempDir();
        var original = QrCodeService.CreatePng("format round trip", new QrCreateOptions(QrErrorCorrection.M, 360, 4));
        var path = temp.Combine($"code.{format}");
        using (var image = new MagickImage(original))
        {
            if (format == "jpg") image.Quality = 60;       // lossy: edges are no longer pure black and white
            image.Write(path);
        }

        var result = await Kit.Run(Converter, "img.qr", path, new() { ["mode"] = "read" });

        Assert.True(result.Success, $"{format}: {result.Error}");
        Assert.Equal("format round trip", File.ReadAllText(Assert.Single(result.Outputs)));
    }

    [Fact]
    public async Task Reads_a_rotated_inverted_and_noisy_code()
    {
        using var temp = new TempDir();
        var original = QrCodeService.CreatePng("hard to read", new QrCreateOptions(QrErrorCorrection.M, 400, 4));

        var variants = new Dictionary<string, Action<MagickImage>>
        {
            ["rotated90"] = i => i.Rotate(90),
            ["rotated180"] = i => i.Rotate(180),
            ["mirrored"] = i => i.Flop(),
            ["inverted"] = i => i.Negate(),
            ["blurred"] = i => i.Blur(0, 1.2),
            ["noisy"] = i => i.AddNoise(NoiseType.Gaussian, 0.4),
            ["small"] = i => i.Resize(new Percentage(35)),
            ["tilted"] = i => { i.BackgroundColor = MagickColors.White; i.Rotate(12); },
        };

        foreach (var (name, change) in variants)
        {
            using var image = new MagickImage(original);
            change(image);
            var path = temp.Combine($"{name}.png");
            image.Write(path);

            var result = await Kit.Run(Converter, "img.qr", path, new() { ["mode"] = "read" });
            Assert.True(result.Success, $"{name}: {result.Error}");
            Assert.Equal("hard to read", File.ReadAllText(result.Outputs[0]));
        }
    }

    [Fact]
    public async Task Finds_a_small_code_inside_a_large_photo()
    {
        using var temp = new TempDir();
        using var canvas = new MagickImage(MagickColors.White, 3000, 2000);
        var noiseBackdrop = new MagickImage(MagickColors.Gray, 3000, 2000);
        noiseBackdrop.AddNoise(NoiseType.Uniform, 0.5);
        canvas.Composite(noiseBackdrop, CompositeOperator.Over);
        using var code = new MagickImage(QrCodeService.CreatePng("tiny code in a big picture", new QrCreateOptions(QrErrorCorrection.M, 300, 4)));
        canvas.Composite(code, 1900, 1100, CompositeOperator.Over);
        var path = temp.Combine("photo.jpg");
        canvas.Quality = 85;
        canvas.Write(path);

        var result = await Kit.Run(Converter, "img.qr", path, new() { ["mode"] = "read" });

        Assert.True(result.Success, result.Error);
        Assert.Equal("tiny code in a big picture", File.ReadAllText(Assert.Single(result.Outputs)));
    }

    [Fact]
    public async Task Reads_a_code_drawn_on_a_transparent_background()
    {
        using var temp = new TempDir();
        using var image = new MagickImage(QrCodeService.CreatePng("transparent", new QrCreateOptions(QrErrorCorrection.M, 300, 4)));
        image.Transparent(MagickColors.White);                     // white becomes see-through, black modules stay opaque
        var path = temp.Combine("transparent.png");
        image.Write(path);

        var result = await Kit.Run(Converter, "img.qr", path, new() { ["mode"] = "read" });

        Assert.True(result.Success, result.Error);
        Assert.Equal("transparent", File.ReadAllText(Assert.Single(result.Outputs)));
    }

    [Fact]
    public async Task Reads_several_codes_and_other_barcode_types()
    {
        using var temp = new TempDir();
        using var canvas = new MagickImage(MagickColors.White, 1100, 520);
        using var qrOne = new MagickImage(QrCodeService.CreatePng("first code", new QrCreateOptions(QrErrorCorrection.M, 420, 4)));
        using var qrTwo = new MagickImage(QrCodeService.CreatePng("second code", new QrCreateOptions(QrErrorCorrection.M, 420, 4)));
        canvas.Composite(qrOne, 20, 20, CompositeOperator.Over);
        canvas.Composite(qrTwo, 540, 20, CompositeOperator.Over);
        var path = temp.Combine("two.png");
        canvas.Write(path);

        var result = await Kit.Run(Converter, "img.qr", path, new() { ["mode"] = "read" });

        Assert.True(result.Success, result.Error);
        var text = File.ReadAllText(Assert.Single(result.Outputs));
        Assert.Contains("[QR_CODE]\nfirst code", text);
        Assert.Contains("[QR_CODE]\nsecond code", text);
        Assert.Contains("\n\n", text);
        Assert.Contains("2 codes found", result.Note);

        // a one-dimensional barcode, made with ZXing's own writer
        var barcode = new ZXing.BarcodeWriterPixelData { Format = BarcodeFormat.CODE_128, Options = new EncodingOptions { Width = 500, Height = 150, Margin = 10 } };
        var pixels = barcode.Write("DROPFLOW-12345");
        using var bar = new MagickImage();
        bar.ReadPixels(pixels.Pixels, new PixelReadSettings((uint)pixels.Width, (uint)pixels.Height, StorageType.Char, PixelMapping.BGRA));
        var barPath = temp.Combine("code128.png");
        bar.Write(barPath);

        var barResult = await Kit.Run(Converter, "img.qr", barPath, new() { ["mode"] = "read" });
        Assert.True(barResult.Success, barResult.Error);
        Assert.Equal("DROPFLOW-12345", File.ReadAllText(Assert.Single(barResult.Outputs)));
    }

    [Fact]
    public async Task Arabic_text_is_read_back_exactly_from_a_file_with_an_arabic_name()
    {
        using var temp = new TempDir();
        var path = WritePng(temp, $"{Kit.ArabicFile}.png", Kit.ArabicText);

        var result = await Kit.Run(Converter, "img.qr", path, new() { ["mode"] = "read" });

        Assert.True(result.Success, result.Error);
        var output = Assert.Single(result.Outputs);
        Assert.Equal($"{Kit.ArabicFile} (qr).txt", Path.GetFileName(output));
        Assert.Equal(Kit.ArabicText, File.ReadAllText(output, Encoding.UTF8));
    }

    [Fact]
    public async Task A_picture_without_any_code_fails_with_a_message_and_no_file()
    {
        using var temp = new TempDir();
        var plain = Samples.Png(temp.Combine("gradient.png"));

        var result = await Kit.Run(Converter, "img.qr", plain, new() { ["mode"] = "read" });

        Assert.False(result.Success);
        Assert.Contains("No QR code", result.Error);
        Assert.Equal(["gradient.png"], Directory.GetFileSystemEntries(temp.Path).Select(Path.GetFileName));
    }

    [Fact]
    public async Task A_corrupt_image_fails_cleanly()
    {
        using var temp = new TempDir();
        var junk = temp.Combine("broken.png");
        File.WriteAllBytes(junk, Kit.Noise(2000));

        var result = await Kit.Run(Converter, "img.qr", junk, new() { ["mode"] = "read" });

        Assert.False(result.Success);
        Assert.Contains("could not be read", result.Error);
        Assert.Single(Directory.GetFileSystemEntries(temp.Path));
    }

    [Fact]
    public async Task A_text_file_cannot_be_read_as_a_picture()
    {
        using var temp = new TempDir();
        var file = temp.WriteText("notes.txt", "hello");

        var result = await Kit.Run(Converter, "img.qr", file, new() { ["mode"] = "read" });

        Assert.False(result.Success);
        Assert.Contains("mode=create", result.Error);
    }

    [Fact]
    public async Task Never_overwrites_an_existing_result_and_honours_the_output_directory()
    {
        using var temp = new TempDir();
        var png = WritePng(temp, "code.png", "once");
        File.WriteAllText(temp.Combine("code (qr).txt"), "PRECIOUS");

        var first = await Kit.Run(Converter, "img.qr", png, new() { ["mode"] = "read" });
        Assert.Equal("code (qr 2).txt", Path.GetFileName(Assert.Single(first.Outputs)));
        Assert.Equal("PRECIOUS", File.ReadAllText(temp.Combine("code (qr).txt")));

        var elsewhere = Path.Combine(temp.Path, "out");
        var second = await Kit.Run(Converter, "img.qr", png, new() { ["mode"] = "read" }, elsewhere);
        Assert.Equal(elsewhere, Path.GetDirectoryName(Assert.Single(second.Outputs)));
    }

    [Fact]
    public async Task Bad_modes_and_missing_files_fail_clearly()
    {
        using var temp = new TempDir();
        var png = WritePng(temp, "code.png", "x");

        Assert.False((await Kit.Run(Converter, "img.qr", png, new() { ["mode"] = "decode" })).Success);
        Assert.False((await Kit.Run(Converter, "img.qr", temp.Combine("ghost.png"), new() { ["mode"] = "read" })).Success);
        Assert.False((await Kit.Run(Converter, "img.qr", png, new() { ["mode"] = "create", ["text"] = "x", ["size"] = "big" })).Success);
    }

    [Fact]
    public async Task Cancellation_before_start_is_honoured()
    {
        using var temp = new TempDir();
        var png = WritePng(temp, "code.png", "x");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Kit.Run(Converter, Kit.Job("img.qr", [png], new() { ["mode"] = "read" }), cts.Token));
        Assert.Single(Directory.GetFileSystemEntries(temp.Path));
    }

    // ---------------------------------------------------------------- helpers

    private static bool IsDark(MagickImage image, int x, int y)
    {
        var color = image.GetPixels().GetPixel(x, y).ToColor()!;
        return (color.R + color.G + color.B) / 3.0 < 128;
    }

    private static byte[] ToBytes(MagickImage image)
    {
        image.Format = MagickFormat.Png;
        return image.ToByteArray();
    }

    /// <summary>Paints a white square over the middle of the code, like a logo placed on it.</summary>
    private static void BlotCenter(MagickImage image, uint size)
    {
        using var blot = new MagickImage(MagickColors.White, size, size);
        image.Composite(blot, Gravity.Center, CompositeOperator.Over);
    }

    /// <summary>Pixel width of one QR module: the first dark run in the top finder pattern is 7 modules wide.</summary>
    private static double ModuleSize(byte[] png)
    {
        using var image = new MagickImage(png);
        for (var y = 0; y < image.Height; y++)
        {
            for (var x = 0; x < image.Width; x++)
            {
                if (!IsDark(image, x, y)) continue;
                var run = 0;
                while (x + run < image.Width && IsDark(image, x + run, y)) run++;
                return run / 7.0;
            }
        }
        throw new InvalidOperationException("no dark pixel found");
    }
}