using System.Text.Json;
using DropFlow.Core.Operations;
using DropFlow.Engines.Archive.Images;
using DropFlow.TestSupport;
using ImageMagick;

namespace DropFlow.Engines.Archive.Tests;

public class IconsTests
{
    private static IconsConverter Converter => new();

    /// <summary>One entry of an ICO directory, parsed by hand from the bytes (no imaging library involved).</summary>
    private sealed record IcoEntry(int Width, int Height, int ColorCount, int Planes, int BitCount, int Size, int Offset);

    private static (int Type, List<IcoEntry> Entries) ParseIco(byte[] ico)
    {
        Assert.True(ico.Length >= 6, "too short for an ICO header");
        Assert.Equal(0, BitConverter.ToUInt16(ico, 0));                     // reserved
        var type = BitConverter.ToUInt16(ico, 2);
        var count = BitConverter.ToUInt16(ico, 4);
        var entries = new List<IcoEntry>();
        for (var i = 0; i < count; i++)
        {
            var at = 6 + i * 16;
            var w = ico[at] == 0 ? 256 : ico[at];
            var h = ico[at + 1] == 0 ? 256 : ico[at + 1];
            entries.Add(new IcoEntry(w, h, ico[at + 2], BitConverter.ToUInt16(ico, at + 4), BitConverter.ToUInt16(ico, at + 6),
                (int)BitConverter.ToUInt32(ico, at + 8), (int)BitConverter.ToUInt32(ico, at + 12)));
            Assert.Equal(0, ico[at + 3]);                                   // reserved byte
        }
        return (type, entries);
    }

    private static string SamplePng(TempDir temp, string name = "logo.png", int width = 64, int height = 48) =>
        Samples.Png(temp.Combine(name), width, height);

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
    [InlineData("txt", false)]
    [InlineData("pdf", false)]
    [InlineData("zip", false)]
    public void Supports_image_formats_only(string ext, bool expected) => Assert.Equal(expected, Converter.Supports("img.icons", ext));

    [Fact]
    public void Runs_immediately_without_asking_and_needs_no_engine()
    {
        Assert.Empty(Converter.OptionsFor("img.icons"));
        Assert.Equal(EngineId.None, Converter.EngineFor("img.icons"));
        Assert.Equal(["img.icons"], Converter.Operations);
    }

    // ---------------------------------------------------------------- the .ico

    [Fact]
    public async Task Writes_a_multi_size_ico_with_png_compressed_entries_that_parse_and_decode()
    {
        using var temp = new TempDir();
        var source = SamplePng(temp);

        var result = await Kit.Run(Converter, "img.icons", source);

        Assert.True(result.Success, result.Error);
        Assert.Equal(2, result.Outputs.Count);
        var icoPath = result.Outputs[0];
        Assert.Equal("logo (icons).ico", Path.GetFileName(icoPath));
        Assert.Equal(temp.Path, Path.GetDirectoryName(icoPath));

        var ico = File.ReadAllBytes(icoPath);
        var (type, entries) = ParseIco(ico);
        Assert.Equal(1, type);                                              // 1 = icon (2 would be a cursor)
        Assert.Equal([16, 24, 32, 48, 64, 128, 256], entries.Select(e => e.Width));
        Assert.All(entries, e =>
        {
            Assert.Equal(e.Width, e.Height);
            Assert.Equal(1, e.Planes);
            Assert.Equal(32, e.BitCount);
            Assert.True(e.Offset >= 6 + 16 * entries.Count && e.Offset + e.Size <= ico.Length, "entry lies inside the file");
        });
        Assert.Equal(ico.Length, entries.Max(e => e.Offset + e.Size));       // and nothing trails after the last image
        Assert.Equal(entries.Select(e => e.Offset).Order(), entries.Select(e => e.Offset));
        for (var i = 1; i < entries.Count; i++)
            Assert.Equal(entries[i - 1].Offset + entries[i - 1].Size, entries[i].Offset);       // images are packed back to back

        foreach (var entry in entries)
        {
            var data = ico.AsSpan(entry.Offset, entry.Size).ToArray();
            Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, data[..8]);     // PNG signature: PNG-compressed entry
            using var image = new MagickImage(data);
            Assert.Equal(MagickFormat.Png, image.Format);
            Assert.Equal((uint)entry.Width, image.Width);
            Assert.Equal((uint)entry.Height, image.Height);
            Assert.True(image.HasAlpha, $"{entry.Width}px entry has an alpha channel");
        }
    }

    [Fact]
    public async Task A_second_decoder_reads_the_whole_ico_as_seven_frames()
    {
        using var temp = new TempDir();
        var source = SamplePng(temp, width: 128, height: 128);

        var result = await Kit.Run(Converter, "img.icons", source);
        Assert.True(result.Success, result.Error);

        // ImageMagick's own ICO decoder (not our code) must accept the file and find every size
        using var frames = new MagickImageCollection(result.Outputs[0]);
        Assert.Equal(7, frames.Count);
        Assert.Equal([16u, 24u, 32u, 48u, 64u, 128u, 256u], frames.Select(f => f.Width).Order());
        Assert.All(frames, f => Assert.Equal(f.Width, f.Height));
    }

    [Fact]
    public void The_ico_writer_validates_what_it_is_given_and_encodes_256_as_zero()
    {
        var png = new byte[] { 1, 2, 3 };
        var ico = IcoWriter.Write([(256, png), (16, png)]);

        Assert.Equal(0, ico[6]);                                            // width 256 is stored as 0
        Assert.Equal(0, ico[7]);
        Assert.Equal(16, ico[6 + 16]);
        Assert.Equal(2, BitConverter.ToUInt16(ico, 4));
        Assert.Equal(6 + 32, (int)BitConverter.ToUInt32(ico, 6 + 12));      // first image starts right after the directory

        Assert.Throws<ArgumentException>(() => IcoWriter.Write([]));
        Assert.Throws<ArgumentOutOfRangeException>(() => IcoWriter.Write([(257, png)]));
        Assert.Throws<ArgumentOutOfRangeException>(() => IcoWriter.Write([(0, png)]));
    }

    [Fact]
    public async Task A_non_square_picture_is_fitted_on_a_transparent_square_never_stretched()
    {
        using var temp = new TempDir();
        var source = SamplePng(temp, width: 200, height: 100);              // 2:1

        var result = await Kit.Run(Converter, "img.icons", source);

        var ico = File.ReadAllBytes(result.Outputs[0]);
        var (_, entries) = ParseIco(ico);
        var big = entries.Single(e => e.Width == 256);
        using var image = new MagickImage(ico.AsSpan(big.Offset, big.Size).ToArray());
        Assert.Equal(0, Alpha(image, 128, 5));                              // top padding is transparent
        Assert.Equal(0, Alpha(image, 128, 250));                            // bottom padding too
        Assert.Equal(255, Alpha(image, 128, 128));                          // the picture itself is opaque
        Assert.Equal(255, Alpha(image, 5, 128));                            // and spans the full width
        Assert.Equal(255, Alpha(image, 250, 128));
    }

    [Fact]
    public async Task Cover_fit_fills_the_square_by_cropping()
    {
        using var temp = new TempDir();
        var source = SamplePng(temp, width: 200, height: 100);

        var result = await Kit.Run(Converter, Kit.Job("img.icons", [source], new() { ["fit"] = "cover" }));

        var ico = File.ReadAllBytes(result.Outputs[0]);
        var big = ParseIco(ico).Entries.Single(e => e.Width == 256);
        using var image = new MagickImage(ico.AsSpan(big.Offset, big.Size).ToArray());
        foreach (var (x, y) in new[] { (0, 0), (255, 0), (0, 255), (255, 255), (128, 3), (3, 128) })
            Assert.Equal(255, Alpha(image, x, y));
    }

    [Fact]
    public async Task Small_sources_are_scaled_up_and_other_formats_work()
    {
        using var temp = new TempDir();
        var tiny = SamplePng(temp, "tiny.png", 8, 8);
        var jpg = temp.Combine("photo.jpg");
        using (var image = new MagickImage(SamplePng(temp, "base.png", 300, 200))) image.Write(jpg);
        var bmp = temp.Combine("pic.bmp");
        using (var image = new MagickImage(SamplePng(temp, "base2.png", 40, 40))) image.Write(bmp);

        foreach (var input in new[] { tiny, jpg, bmp })
        {
            var result = await Kit.Run(Converter, "img.icons", input);
            Assert.True(result.Success, $"{Path.GetFileName(input)}: {result.Error}");
            Assert.Equal(7, ParseIco(File.ReadAllBytes(result.Outputs[0])).Entries.Count);
        }
    }

    [Theory]
    [InlineData("avif")]
    [InlineData("webp")]
    [InlineData("tiff")]
    [InlineData("gif")]
    [InlineData("ico")]
    public async Task Every_listed_image_format_that_can_be_written_here_is_accepted_as_input(string format)
    {
        using var temp = new TempDir();
        var path = temp.Combine($"source.{format}");
        using (var image = new MagickImage(SamplePng(temp, "base.png", 90, 90))) image.Write(path);

        Assert.True(Converter.Supports("img.icons", format));
        var result = await Kit.Run(Converter, "img.icons", path);

        Assert.True(result.Success, $"{format}: {result.Error}");
        Assert.Equal(7, ParseIco(File.ReadAllBytes(result.Outputs[0])).Entries.Count);
    }

    [Fact]
    public async Task An_svg_source_is_rendered()
    {
        using var temp = new TempDir();
        var svg = temp.WriteText("mark.svg", """
            <svg xmlns="http://www.w3.org/2000/svg" width="120" height="120" viewBox="0 0 120 120">
              <rect width="120" height="120" rx="24" fill="#3730a3"/>
              <circle cx="60" cy="60" r="30" fill="#ffffff"/>
            </svg>
            """);

        var result = await Kit.Run(Converter, "img.icons", svg);

        Assert.True(result.Success, result.Error);
        var ico = File.ReadAllBytes(result.Outputs[0]);
        var big = ParseIco(ico).Entries.Single(e => e.Width == 256);
        using var image = new MagickImage(ico.AsSpan(big.Offset, big.Size).ToArray());
        Assert.Equal(255, Alpha(image, 128, 128));
    }

    // ---------------------------------------------------------------- the favicons folder

    [Fact]
    public async Task Writes_the_favicon_set_with_exact_sizes_a_manifest_and_an_html_snippet()
    {
        using var temp = new TempDir();
        var source = SamplePng(temp, width: 120, height: 120);

        var result = await Kit.Run(Converter, Kit.Job("img.icons", [source], new() { ["appName"] = "DropFlow Demo", ["themeColor"] = "#3730a3", ["backgroundColor"] = "#101828" }));

        Assert.True(result.Success, result.Error);
        var folder = result.Outputs[1];
        Assert.Equal("logo (favicons)", Path.GetFileName(folder));
        Assert.Equal(temp.Path, Path.GetDirectoryName(folder));

        var expectedFiles = new[]
        {
            "favicon.ico", "favicon-16x16.png", "favicon-32x32.png", "favicon-48x48.png", "apple-touch-icon.png",
            "android-chrome-192x192.png", "android-chrome-512x512.png", "site.webmanifest", "favicons.html",
        };
        Assert.Equal(expectedFiles.Order(), Directory.GetFiles(folder).Select(Path.GetFileName).Order());

        foreach (var (file, size) in IconsConverter.FaviconPngs)
        {
            using var image = new MagickImage(Path.Combine(folder, file));
            Assert.Equal(MagickFormat.Png, image.Format);
            Assert.Equal((uint)size, image.Width);
            Assert.Equal((uint)size, image.Height);
        }

        Assert.Equal(File.ReadAllBytes(result.Outputs[0]), File.ReadAllBytes(Path.Combine(folder, "favicon.ico")));    // the folder is self-contained

        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "site.webmanifest")));
        var root = manifest.RootElement;
        Assert.Equal("DropFlow Demo", root.GetProperty("name").GetString());
        Assert.Equal("#3730a3", root.GetProperty("theme_color").GetString());
        Assert.Equal("#101828", root.GetProperty("background_color").GetString());
        Assert.Equal("standalone", root.GetProperty("display").GetString());
        var icons = root.GetProperty("icons").EnumerateArray().ToList();
        Assert.Equal(["192x192", "512x512"], icons.Select(i => i.GetProperty("sizes").GetString()));
        foreach (var icon in icons)
        {
            Assert.Equal("image/png", icon.GetProperty("type").GetString());
            Assert.True(File.Exists(Path.Combine(folder, icon.GetProperty("src").GetString()!)), "manifest icon exists");
        }

        var html = File.ReadAllText(Path.Combine(folder, "favicons.html"));
        Assert.Contains("rel=\"icon\" href=\"/favicon.ico\"", html);
        Assert.Contains("rel=\"apple-touch-icon\" sizes=\"180x180\" href=\"/apple-touch-icon.png\"", html);
        Assert.Contains("rel=\"manifest\" href=\"/site.webmanifest\"", html);
        Assert.Contains("<meta name=\"theme-color\" content=\"#3730a3\">", html);
        foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(html, "href=\"/([^\"]+)\""))
            Assert.True(File.Exists(Path.Combine(folder, match.Groups[1].Value)), $"snippet links to {match.Groups[1].Value}, which must exist");
    }

    [Fact]
    public async Task The_manifest_defaults_to_the_file_name_and_ignores_bad_colours()
    {
        using var temp = new TempDir();
        var source = SamplePng(temp, $"{Kit.ArabicFile}.png");

        var result = await Kit.Run(Converter, Kit.Job("img.icons", [source], new() { ["themeColor"] = "red; } body { display:none", ["backgroundColor"] = "not-a-colour" }));

        Assert.True(result.Success, result.Error);
        var folder = result.Outputs[1];
        Assert.Equal($"{Kit.ArabicFile} (favicons)", Path.GetFileName(folder));
        Assert.Equal($"{Kit.ArabicFile} (icons).ico", Path.GetFileName(result.Outputs[0]));

        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "site.webmanifest")));
        Assert.Equal(Kit.ArabicFile, manifest.RootElement.GetProperty("name").GetString());       // Arabic survives JSON
        Assert.Equal("#ffffff", manifest.RootElement.GetProperty("theme_color").GetString());
        Assert.Equal("#ffffff", manifest.RootElement.GetProperty("background_color").GetString());
        Assert.Contains("content=\"#ffffff\"", File.ReadAllText(Path.Combine(folder, "favicons.html")));
    }

    [Fact]
    public void Manifest_and_snippet_builders_are_pure_and_escape_properly()
    {
        var manifest = IconsConverter.BuildManifest("Say \"hi\" \\ مرحبا", "#abc", "#112233");
        using var doc = JsonDocument.Parse(manifest);
        Assert.Equal("Say \"hi\" \\ مرحبا", doc.RootElement.GetProperty("name").GetString());
        Assert.EndsWith("\n", manifest);

        Assert.StartsWith("<link rel=\"icon\"", IconsConverter.BuildHtmlSnippet("#fff"));
    }

    // ---------------------------------------------------------------- names, safety, failures

    [Fact]
    public async Task Never_overwrites_earlier_results_and_honours_the_output_directory()
    {
        using var temp = new TempDir();
        var source = SamplePng(temp);
        File.WriteAllText(temp.Combine("logo (icons).ico"), "PRECIOUS");
        Directory.CreateDirectory(temp.Combine("logo (favicons)"));
        File.WriteAllText(temp.Combine("logo (favicons)/keep.txt"), "PRECIOUS");

        var first = await Kit.Run(Converter, "img.icons", source);
        var second = await Kit.Run(Converter, "img.icons", source);

        Assert.Equal(["logo (icons 2).ico", "logo (favicons 2)"], first.Outputs.Select(Path.GetFileName));
        Assert.Equal(["logo (icons 3).ico", "logo (favicons 3)"], second.Outputs.Select(Path.GetFileName));
        Assert.Equal("PRECIOUS", File.ReadAllText(temp.Combine("logo (icons).ico")));
        Assert.Equal("PRECIOUS", File.ReadAllText(temp.Combine("logo (favicons)/keep.txt")));
        Assert.True(File.Exists(source));

        var elsewhere = Path.Combine(temp.Path, "site");
        var third = await Kit.Run(Converter, "img.icons", source, outputDirectory: elsewhere);
        Assert.All(third.Outputs, o => Assert.Equal(elsewhere, Path.GetDirectoryName(o)));
    }

    [Fact]
    public async Task A_corrupt_image_fails_with_a_message_and_leaves_nothing_behind()
    {
        using var temp = new TempDir();
        var broken = temp.Combine("broken.png");
        File.WriteAllBytes(broken, Kit.Noise(3000));

        var result = await Kit.Run(Converter, "img.icons", broken);

        Assert.False(result.Success);
        Assert.Contains("could not be read", result.Error);
        Assert.Equal(["broken.png"], Directory.GetFileSystemEntries(temp.Path).Select(Path.GetFileName));
    }

    [Fact]
    public async Task A_missing_input_fails_and_cancellation_stops_without_leftovers()
    {
        using var temp = new TempDir();
        var missing = await Kit.Run(Converter, "img.icons", temp.Combine("ghost.png"));
        Assert.False(missing.Success);

        var source = SamplePng(temp);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Kit.Run(Converter, Kit.Job("img.icons", [source]), cts.Token));
        Assert.Equal(["logo.png"], Directory.GetFileSystemEntries(temp.Path).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Cancelling_part_way_removes_the_ico_and_the_folder()
    {
        using var temp = new TempDir();
        var source = SamplePng(temp, width: 3000, height: 3000);             // big enough that rendering takes a moment
        using var cts = new CancellationTokenSource();
        var recorder = new Recorder { OnReport = p => { if (p.Fraction > 0.1) cts.Cancel(); } };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Kit.Run(Converter, Kit.Job("img.icons", [source]), cts.Token, recorder));

        Assert.Equal(["logo.png"], Directory.GetFileSystemEntries(temp.Path).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Progress_ends_at_one()
    {
        using var temp = new TempDir();
        var source = SamplePng(temp);
        var recorder = new Recorder();

        var result = await Kit.Run(Converter, Kit.Job("img.icons", [source]), recorder: recorder);

        Assert.True(result.Success, result.Error);
        var fractions = recorder.Items.Select(p => p.Fraction).ToList();
        Assert.Equal(fractions.Order(), fractions);
        Assert.Equal(1.0, fractions[^1]);
    }

    private static int Alpha(MagickImage image, int x, int y) => image.GetPixels().GetPixel(x, y).ToColor()!.A;
}
