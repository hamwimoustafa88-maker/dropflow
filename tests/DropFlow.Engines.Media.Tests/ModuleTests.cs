using DropFlow.Core.Catalog;
using DropFlow.Core.Registry;
using DropFlow.Engines.Media.Subtitles;

namespace DropFlow.Engines.Media.Tests;

/// <summary>Checks the module against catalog/tools.json: what is registered must exist there, and what the catalog promises must be supported.</summary>
public class ModuleTests
{
    private static readonly ToolCatalog Catalog = ToolCatalog.LoadEmbedded();
    private static readonly IReadOnlyList<IConverter> Converters = MediaModule.Create(new FakeEngines(EngineId.Ffmpeg, EngineId.Ffprobe));

    private static IEnumerable<string> Ids => Converters.SelectMany(c => c.Operations).Distinct();

    // ------------------------------------------------------------------ registration vs catalog

    [Fact]
    public void Every_tool_id_exists_in_the_catalog_with_the_matching_engine()
    {
        var tools = Ids.Where(i => !Operation.IsConvert(i)).ToList();
        Assert.NotEmpty(tools);
        foreach (var id in tools)
        {
            var tool = Catalog.Tool(id);
            Assert.True(tool is not null, $"{id} is registered but missing from catalog/tools.json");
            Assert.Equal("ffmpeg", tool!.Engine);
        }
    }

    [Fact]
    public void Every_convert_target_is_an_output_of_an_audio_video_or_subtitle_category()
    {
        var outputs = new[] { "audio", "video", "subtitle" }.SelectMany(c => Catalog.Category(c)!.Outputs).ToHashSet();
        foreach (var id in Ids.Where(Operation.IsConvert))
            Assert.Contains(Operation.TargetOf(id), outputs);
    }

    [Fact]
    public void Every_ffmpeg_tool_of_the_audio_and_video_categories_is_implemented_except_the_encoder_window()
    {
        var implemented = Ids.ToHashSet();
        var expected = Catalog.Tools
            .Where(t => t.Cat is "audio" or "video" && t.Engine == "ffmpeg" && t.Id != "vid.encoder")
            .Select(t => t.Id)
            .ToList();
        Assert.Contains("vid.target-size", expected);
        Assert.Contains("vid.burn-subs", expected);
        Assert.Contains("aud.merge", expected);
        foreach (var id in expected) Assert.Contains(id, implemented);
        // Speech to text belongs to the AI project; the Encoder window is UI.
        Assert.DoesNotContain("vid.transcribe", implemented);
        Assert.DoesNotContain("aud.transcribe", implemented);
        Assert.DoesNotContain("vid.encoder", implemented);
    }

    [Fact]
    public void Every_catalog_conversion_in_the_three_categories_is_supported()
    {
        var registry = new ToolRegistry().Register(Converters);
        foreach (var categoryId in new[] { "audio", "video", "subtitle" })
        {
            var category = Catalog.Category(categoryId)!;
            foreach (var input in category.Inputs)
                foreach (var output in category.Outputs)
                {
                    if (input == output) continue;
                    var expected = categoryId == "video" && input == "gif" ? output is "mp4" or "mkv" or "webm" or "avi" or "wmv" or "mov" : true;
                    Assert.True(registry.Supports(Operation.Convert(output), input) == expected,
                        $"{categoryId}: {input} -> {output} should be {(expected ? "supported" : "unsupported")}");
                }
        }
    }

    [Fact]
    public void Nothing_converts_a_format_to_itself()
    {
        foreach (var id in Ids.Where(Operation.IsConvert))
        {
            var ext = Operation.TargetOf(id);
            Assert.DoesNotContain(Converters, c => c.Supports(id, ext));
        }
    }

    [Fact]
    public void Supports_never_claims_input_formats_the_catalog_does_not_list_for_the_category()
    {
        var videoIn = Catalog.Category("video")!.Inputs.ToHashSet();
        var audioIn = Catalog.Category("audio")!.Inputs.ToHashSet();
        var every = Catalog.Categories.SelectMany(c => c.Inputs).Distinct().Concat(["txt", "xyz", "mp5"]).ToList();

        foreach (var tool in Ids.Where(i => i.StartsWith("vid.", StringComparison.Ordinal)))
            foreach (var ext in every)
                if (Converters.Any(c => c.Supports(tool, ext))) Assert.Contains(ext, videoIn);

        foreach (var tool in Ids.Where(i => i.StartsWith("aud.", StringComparison.Ordinal)))
            foreach (var ext in every)
                if (Converters.Any(c => c.Supports(tool, ext))) Assert.Contains(ext, audioIn);
    }

    [Fact]
    public void Video_tools_cover_the_same_inputs_except_gif_and_audio_tools_cover_all_audio_inputs()
    {
        var videoTools = Ids.Where(i => i.StartsWith("vid.", StringComparison.Ordinal)).ToList();
        foreach (var tool in videoTools)
        {
            foreach (var ext in new[] { "mp4", "mov", "mkv", "webm", "avi", "wmv", "flv", "m4v" })
                Assert.True(Converters.Any(c => c.Supports(tool, ext)), $"{tool} .{ext}");
            Assert.DoesNotContain(Converters, c => c.Supports(tool, "gif"));   // gif is an image to the catalog
            Assert.DoesNotContain(Converters, c => c.Supports(tool, "mp3"));
        }
        foreach (var tool in Ids.Where(i => i.StartsWith("aud.", StringComparison.Ordinal)))
        {
            foreach (var ext in Catalog.Category("audio")!.Inputs)
                Assert.True(Converters.Any(c => c.Supports(tool, ext)), $"{tool} .{ext}");
            Assert.DoesNotContain(Converters, c => c.Supports(tool, "mp4"));
        }
    }

    [Fact]
    public void Tools_are_never_claimed_by_two_converters_for_the_same_input()
    {
        foreach (var id in Ids.Where(i => !Operation.IsConvert(i)))
            foreach (var ext in new[] { "mp4", "mp3", "wav", "mkv" })
                Assert.True(Converters.Count(c => c.Supports(id, ext)) <= 1, $"{id} .{ext}");
    }

    [Fact]
    public void Convert_operations_have_exactly_one_owner_per_input_extension()
    {
        foreach (var id in Ids.Where(Operation.IsConvert))
            foreach (var ext in Catalog.Categories.SelectMany(c => c.Inputs).Distinct())
                Assert.True(Converters.Count(c => c.Supports(id, ext)) <= 1, $"{id} .{ext}");
    }

    // ------------------------------------------------------------------ engines and options

    [Fact]
    public void Ffmpeg_operations_declare_the_ffmpeg_engine_and_subtitles_none()
    {
        foreach (var c in Converters)
            foreach (var id in c.Operations)
            {
                var expected = c is SubtitleConverter ? EngineId.None : EngineId.Ffmpeg;
                Assert.Equal(expected, c.EngineFor(id));
            }
    }

    [Fact]
    public void Plain_conversions_run_immediately_without_asking()
    {
        foreach (var c in Converters)
            foreach (var id in c.Operations.Where(Operation.IsConvert))
                Assert.Empty(c.OptionsFor(id));
    }

    [Fact]
    public void Tools_that_need_input_ask_and_those_that_do_not_run_immediately()
    {
        var asking = new[]
        {
            "vid.compress", "vid.metadata", "vid.trim", "vid.crop", "vid.speed", "vid.snapshot", "vid.split", "vid.join", "vid.redact",
            "vid.target-size", "vid.burn-subs", "aud.compress", "aud.metadata", "aud.normalize", "aud.trim", "aud.channels", "aud.visualize", "aud.bleep", "aud.merge",
        };
        foreach (var id in asking)
            Assert.NotEmpty(Converters.Single(c => c.Operations.Contains(id)).OptionsFor(id));
        Assert.Empty(Converters.Single(c => c.Operations.Contains("vid.mute")).OptionsFor("vid.mute"));
    }

    [Fact]
    public void Option_schemas_are_internally_consistent()
    {
        foreach (var c in Converters)
            foreach (var id in c.Operations)
            {
                var options = c.OptionsFor(id);
                Assert.Equal(options.Count, options.Select(o => o.Key).Distinct().Count());   // unique keys
                foreach (var o in options)
                {
                    var where = $"{id}.{o.Key}";
                    Assert.False(string.IsNullOrWhiteSpace(o.Key), where);
                    switch (o.Kind)
                    {
                        case ToolOptionKind.Choice:
                            Assert.NotNull(o.Choices);
                            Assert.NotEmpty(o.Choices!);
                            Assert.Contains(Convert.ToString(o.Default, System.Globalization.CultureInfo.InvariantCulture), o.Choices);
                            break;
                        case ToolOptionKind.Number:
                            Assert.NotNull(o.Default);
                            var value = Convert.ToDouble(o.Default, System.Globalization.CultureInfo.InvariantCulture);
                            Assert.True(o.Min is not null && o.Max is not null, $"{where} needs a range");
                            Assert.True(o.Min <= o.Max, where);
                            Assert.InRange(value, o.Min!.Value, o.Max!.Value);
                            break;
                        case ToolOptionKind.Bool:
                            Assert.IsType<bool>(o.Default);
                            break;
                        case ToolOptionKind.Text:
                            Assert.IsType<string>(o.Default);
                            break;
                    }
                }
            }
    }

    [Fact]
    public void Options_of_an_unrelated_operation_are_empty()
    {
        var compress = Converters.Single(c => c.Operations.Contains("vid.compress"));
        Assert.Empty(compress.OptionsFor("vid.trim"));
        Assert.Empty(compress.OptionsFor("nonsense"));
    }

    [Fact]
    public void Documented_option_keys_are_the_ones_in_the_schema()
    {
        string[] Keys(string id) => Converters.Single(c => c.Operations.Contains(id)).OptionsFor(id).Select(o => o.Key).ToArray();

        Assert.Equal(["crf", "preset", "codec", "maxHeight", "audioBitrate"], Keys("vid.compress"));
        Assert.Equal(["title", "artist", "comment", "strip"], Keys("vid.metadata"));
        Assert.Equal(["start", "end", "reencode"], Keys("vid.trim"));
        Assert.Equal(["aspect", "x", "y", "w", "h"], Keys("vid.crop"));
        Assert.Equal(["speed"], Keys("vid.speed"));
        Assert.Equal(["times", "every"], Keys("vid.snapshot"));
        Assert.Equal(["points", "segment", "reencode"], Keys("vid.split"));
        Assert.Equal(["mode"], Keys("vid.join"));
        Assert.Equal(["x", "y", "w", "h", "mode", "strength", "color", "start", "end"], Keys("vid.redact"));
        Assert.Equal(["sizeMb", "audioBitrate", "codec", "preset"], Keys("vid.target-size"));
        Assert.Equal(["subtitle", "fontSize", "position"], Keys("vid.burn-subs"));
        Assert.Equal(["bitrate", "format"], Keys("aud.compress"));
        Assert.Equal(["title", "artist", "album", "comment", "strip"], Keys("aud.metadata"));
        Assert.Equal(["target", "truePeak", "lra"], Keys("aud.normalize"));
        Assert.Equal(["start", "end", "autoSilence", "silenceDb", "minSilence"], Keys("aud.trim"));
        Assert.Equal(["channels"], Keys("aud.channels"));
        Assert.Equal(["layout", "color", "background"], Keys("aud.visualize"));
        Assert.Equal(["ranges", "frequency", "volume"], Keys("aud.bleep"));
        Assert.Equal(["format"], Keys("aud.merge"));
    }

    [Fact]
    public void The_spec_options_have_the_documented_defaults_and_ranges()
    {
        ToolOption Opt(string id, string key) => Converters.Single(c => c.Operations.Contains(id)).OptionsFor(id).Single(o => o.Key == key);

        Assert.Equal((26, 16.0, 38.0), (Convert.ToInt32(Opt("vid.compress", "crf").Default), Opt("vid.compress", "crf").Min!.Value, Opt("vid.compress", "crf").Max!.Value));
        Assert.Equal("medium", Opt("vid.compress", "preset").Default);
        Assert.Equal((0.25, 4.0), (Opt("vid.speed", "speed").Min!.Value, Opt("vid.speed", "speed").Max!.Value));
        Assert.True(Opt("vid.target-size", "sizeMb").Required);
        Assert.True(Opt("aud.bleep", "ranges").Required);
        Assert.Equal(-16, Convert.ToDouble(Opt("aud.normalize", "target").Default));
        Assert.Equal(new[] { "mono", "stereo" }, Opt("aud.channels", "channels").Choices);
        Assert.Equal(new[] { "landscape", "portrait", "square" }, Opt("aud.visualize", "layout").Choices);
        Assert.Equal(new[] { "blur", "solid" }, Opt("vid.redact", "mode").Choices);
    }

    // ------------------------------------------------------------------ multi-file tools

    [Fact]
    public async Task Multi_file_tools_in_the_catalog_demand_at_least_two_inputs()
    {
        using var h = new MediaHarness();
        foreach (var tool in Catalog.Tools.Where(t => t.Multi && t.Id is "vid.join" or "aud.merge"))
        {
            var input = tool.Id == "vid.join" ? h.Video("a.mp4") : h.Audio("a.mp3");
            var result = await h.Run(tool.Id, input);
            Assert.False(result.Success, tool.Id);
            Assert.Contains("two", result.Error);
        }
    }

    [Fact]
    public void Multi_flags_in_the_catalog_match_the_tools_this_module_treats_as_multi()
    {
        var multiIds = Catalog.Tools.Where(t => t.Multi && Ids.Contains(t.Id)).Select(t => t.Id).Order().ToList();
        Assert.Equal(["aud.merge", "vid.join"], multiIds);
    }

    // ------------------------------------------------------------------ module level

    [Fact]
    public async Task A_module_created_without_any_engine_fails_jobs_with_a_message_naming_ffmpeg()
    {
        var converters = MediaModule.Create(new FakeEngines());
        using var dir = new DropFlow.TestSupport.TempDir();
        var input = DropFlow.TestSupport.Samples.Wav(Path.Combine(dir.Path, "a.wav"));

        var converter = converters.First(c => c.Supports("convert:mp3", "wav"));
        var result = await converter.RunAsync(new ConversionJob([input], "convert:mp3"), new Progress<JobProgress>(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("FFmpeg", result.Error);
        Assert.Single(Directory.GetFiles(dir.Path));
    }

    [Fact]
    public void Creating_the_module_twice_gives_independent_converters()
    {
        var a = MediaModule.Create(new FakeEngines());
        var b = MediaModule.Create(new FakeEngines());
        Assert.Equal(a.Count, b.Count);
        Assert.NotSame(a[0], b[0]);
    }

    [Fact]
    public void The_module_contains_one_subtitle_converter_and_the_ffmpeg_operations()
    {
        Assert.Single(Converters, c => c is SubtitleConverter);
        Assert.Equal(22, Converters.Count); // 1 conversion family + 12 video tools + 8 audio tools + subtitles
    }
}
