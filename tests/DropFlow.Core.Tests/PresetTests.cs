using DropFlow.Core.Automation;
using DropFlow.Core.Catalog;
using DropFlow.Core.Operations;
using DropFlow.Core.Registry;
using DropFlow.Core.Routing;
using DropFlow.Core.Settings;
using DropFlow.Core.Wheel;

namespace DropFlow.Core.Tests;

public class PresetTests
{
    private static readonly ToolCatalog Catalog = ToolCatalog.LoadEmbedded();

    private static FakeConverter Tool(string op, Func<string, bool> accepts, Func<string, string> output, List<string>? log = null) => new(
        [op], (_, ext) => accepts(ext),
        run: (job, _, _) =>
        {
            var dir = job.OutputDirectory ?? Path.GetDirectoryName(job.Inputs[0])!;
            var path = Path.Combine(dir, output(Path.GetFileNameWithoutExtension(job.Inputs[0])));
            File.WriteAllText(path, op);
            log?.Add($"{op} {Path.GetFileName(job.Inputs[0])} {string.Join(",", (job.Options ?? ConversionJob.NoOptions).Select(o => $"{o.Key}={o.Value}"))}");
            return Task.FromResult(ConversionResult.Ok([path]));
        });

    private static (ToolRegistry Registry, List<string> Log) Rig(IReadOnlyList<Recipe> presets)
    {
        var log = new List<string>();
        var registry = new ToolRegistry().Register([
            Tool("img.resize", ext => ext is "png" or "jpg", stem => $"{stem} (resized).png", log),
            Tool("convert:jpg", ext => ext == "png", stem => $"{stem} (converted).jpg", log),
            Tool("vid.target-size", ext => ext == "mp4", stem => $"{stem} (small).mp4", log),
        ]);
        registry.Register(new PresetConverter(registry, Catalog, () => presets));
        return (registry, log);
    }

    [Fact]
    public void Built_in_presets_cover_the_common_sharing_sizes()
    {
        var names = BuiltInRecipes.All.Where(r => r.OnWheel).Select(r => r.Name).ToList();
        Assert.Equal(["whatsapp-video", "discord-video", "email-video", "instagram-photo"], names);
        Assert.All(BuiltInRecipes.All.Where(r => r.OnWheel), r =>
        {
            Assert.Matches(@"\p{IsArabic}", r.LabelAr!);
            Assert.NotEmpty(r.Categories!);
        });
    }

    [Fact]
    public void Presets_appear_on_the_tools_wheel_of_their_category_only()
    {
        var (registry, _) = Rig(BuiltInRecipes.All);
        var builder = new WheelBuilder(Catalog, registry, new FakeEngines()) { Presets = () => BuiltInRecipes.All };

        var video = builder.Build([@"C:\clip.mp4"], "ar", [], WheelPins.None);
        Assert.Contains(video.Tools, w => w.Id == "preset:whatsapp-video" && w.Label == "فيديو واتساب (16MB)");
        Assert.DoesNotContain(video.Tools, w => w.Id == "preset:instagram-photo");

        var photo = builder.Build([@"C:\cat.png"], "en", [], WheelPins.None);
        Assert.Contains(photo.Tools, w => w.Id == "preset:instagram-photo" && w.Label == "Instagram photo (1080 px)");
        Assert.DoesNotContain(photo.Tools, w => w.Id.StartsWith("preset:") && w.Id != "preset:instagram-photo");
    }

    [Fact]
    public async Task A_preset_runs_its_steps_with_their_options_and_leaves_one_file_beside_the_source()
    {
        using var dir = new TempDir();
        var png = dir.File("cat.png");
        var (registry, log) = Rig(BuiltInRecipes.All);

        var result = await registry.Find("preset:instagram-photo", "png")!.RunAsync(
            new ConversionJob([png], "preset:instagram-photo"), new Progress<JobProgress>(), CancellationToken.None);

        Assert.True(result.Success, result.Error);
        Assert.Equal([Path.Combine(dir.Path, "cat (converted).jpg")], result.Outputs);
        Assert.Equal(["img.resize cat.png width=1080", "convert:jpg cat.png quality=90"], log);   // the intermediate kept the source's name
        Assert.Equal(["cat (converted).jpg", "cat.png"], Directory.GetFiles(dir.Path).Select(Path.GetFileName).Order());
    }

    [Fact]
    public async Task A_jpg_through_resize_then_jpg_skips_the_conversion_it_does_not_need()
    {
        using var dir = new TempDir();
        var (registry, log) = Rig([new Recipe("p", [new("img.resize"), new("convert:png")], OnWheel: true, Categories: ["image"])]);

        var result = await registry.Find("preset:p", "jpg")!.RunAsync(new ConversionJob([dir.File("a.jpg")], "preset:p"), new Progress<JobProgress>(), CancellationToken.None);

        Assert.True(result.Success, result.Error);
        Assert.Equal(Path.Combine(dir.Path, "a (converted).png"), Assert.Single(result.Outputs));
        Assert.Single(log);
    }

    [Fact]
    public void User_presets_in_recipes_json_load_with_their_labels()
    {
        using var dir = new TempDir();
        var store = new RecipeStore(System.IO.Path.Combine(dir.Path, "recipes.json"));
        store.Save([new Recipe("my-small", [new("vid.target-size", new Dictionary<string, object?> { ["sizeMb"] = 8d })], Label: "Small", LabelAr: "صغير", Categories: ["video"], OnWheel: true)]);

        var loaded = Assert.Single(store.Load());
        Assert.True(loaded.OnWheel);
        Assert.Equal("صغير", loaded.DisplayName("ar"));
        Assert.Equal(8d, loaded.Steps[0].Options!["sizeMb"]);
        Assert.Equal("preset:my-small", loaded.PresetOperation);
    }
}

public class PresetBookTests
{
    private static readonly ToolCatalog Catalog = ToolCatalog.LoadEmbedded();

    private static readonly ToolOption[] CompressOptions =
    [
        new("quality", ToolOptionKind.Number, 80d, Min: 10, Max: 100),
        new("mode", ToolOptionKind.Choice, "preset", ["preset", "target"]),
        new("password", ToolOptionKind.Password),
        new("folder", ToolOptionKind.Folder),
    ];

    private static ToolRegistry Registry() => new ToolRegistry().Register(new FakeConverter(["img.compress", "convert:jpg"], options: CompressOptions));

    [Fact]
    public void Saving_options_as_a_preset_never_keeps_a_password_or_a_folder()
    {
        var values = new Dictionary<string, object?> { ["quality"] = 55d, ["mode"] = "preset", ["password"] = "s3cret", ["folder"] = @"C:\Users\me" };
        var p = PresetBook.FromOptions("img.compress", CompressOptions, values, "صغير جداً", Catalog, []);

        Assert.Equal(["mode", "quality"], p.Steps[0].Options!.Keys.Order());
        Assert.True(p.OnWheel);
        Assert.Equal(["image"], p.Categories);
        Assert.Equal("صغير جداً", p.DisplayName("ar"));
        Assert.DoesNotContain("s3cret", PresetBook.Export([p]));
    }

    [Fact]
    public void Exported_presets_import_back_with_fresh_names_when_they_clash()
    {
        var p = PresetBook.FromOptions("img.compress", CompressOptions, new Dictionary<string, object?> { ["quality"] = 55d }, "Small", Catalog, []);
        var json = PresetBook.Export([p]);

        var result = PresetBook.Import(json, Catalog, Registry(), [p]);

        var back = Assert.Single(result.Presets);
        Assert.Empty(result.Rejected);
        Assert.Equal("small-2", back.Name);
        Assert.Equal(55d, back.Steps[0].Options!["quality"]);
    }

    [Theory]
    [InlineData("""{"operation":"img.compress","options":{"folder":"x"}}""", true)]                       // folders are dropped, not refused
    [InlineData("""{"operation":"img.compress","options":{"quality":5000}}""", true)]                     // clamped to its limit
    [InlineData("""{"operation":"img.compress","options":{"mode":"C:\Windows\x"}}""", false)]           // a path
    [InlineData("""{"operation":"img.compress","options":{"mode":"weird"}}""", false)]                    // not a choice
    [InlineData("""{"operation":"img.compress","options":{"evil":"1"}}""", false)]                        // undeclared option
    [InlineData("""{"operation":"sys.format-disk"}""", false)]                                            // unknown operation
    [InlineData("""{"operation":"preset:other"}""", false)]                                               // no presets inside presets
    public void Every_imported_step_is_checked_against_what_the_operation_declares(string step, bool accepted)
    {
        var json = $$"""{"format":"dropflow-presets","version":1,"presets":[{"name":"p","label":"P","steps":[{{step}}]}]}""";
        var result = PresetBook.Import(json, Catalog, Registry(), []);
        Assert.Equal(accepted, result.Presets.Count == 1);
        if (accepted)
        {
            var options = result.Presets[0].Steps[0].Options!;
            Assert.False(options.ContainsKey("folder"));
            if (options.TryGetValue("quality", out var q)) Assert.Equal(100d, q);
        }
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"format":"something-else","version":1,"presets":[]}""")]
    [InlineData("""{"format":"dropflow-presets","version":2,"presets":[]}""")]
    public void Files_that_are_not_preset_files_are_refused_whole(string json) =>
        Assert.Empty(PresetBook.Import(json, Catalog, Registry(), []).Presets);

    [Fact]
    public void An_oversized_file_is_refused_before_it_is_parsed()
    {
        var json = "{\"format\":\"dropflow-presets\",\"pad\":\"" + new string('x', PresetBook.MaxFileBytes) + "\"}";
        Assert.Equal(["file-too-large"], PresetBook.Import(json, Catalog, Registry(), []).Rejected);
    }
}
