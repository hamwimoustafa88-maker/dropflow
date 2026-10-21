using DropFlow.Core.Catalog;
using DropFlow.Core.Operations;
using DropFlow.Core.Registry;
using DropFlow.Core.Wheel;

namespace DropFlow.Core.Tests;

public class WheelBuilderTests
{
    private static readonly ToolCatalog Catalog = ToolCatalog.LoadEmbedded();
    private static readonly string[] Actions = ["act.copy", "act.move", "act.copy-path"];

    private static WheelBuilder Builder(ToolRegistry registry, params EngineId[] installed) =>
        new(Catalog, registry, new FakeEngines(installed));

    // A converter that turns any image into png/jpg/webp except into its own format.
    private static FakeConverter ImageConverter() => new(
        ["convert:png", "convert:jpg", "convert:webp"],
        supports: (op, ext) => Catalog.CategoryForExtension(ext) == "image" && Operation.TargetOf(op) != ext);

    [Fact]
    public void No_files_means_empty_wheels()
    {
        var set = Builder(new ToolRegistry()).Build([], "en", Actions);
        Assert.Empty(set.Convert);
        Assert.Empty(set.Tools);
        Assert.Empty(set.Actions);
    }

    [Fact]
    public void Convert_wedges_follow_catalog_order_and_skip_the_files_own_format()
    {
        var set = Builder(new ToolRegistry().Register(ImageConverter())).Build([@"C:\a.png"], "en", Actions);
        Assert.Equal(["JPG", "WEBP"], set.Convert.Select(w => w.Label));
    }

    [Fact]
    public void Only_implemented_conversions_appear()
    {
        var set = Builder(new ToolRegistry()).Build([@"C:\a.png"], "en", Actions);
        Assert.Empty(set.Convert);
    }

    [Fact]
    public void Tools_are_localised_and_filtered_by_category()
    {
        var registry = new ToolRegistry().Register(new FakeConverter(["img.compress", "vid.trim"]));
        var en = Builder(registry).Build([@"C:\a.png"], "en", Actions);
        var ar = Builder(registry).Build([@"C:\a.png"], "ar", Actions);

        Assert.Equal(["Compress images"], en.Tools.Select(w => w.Label));
        Assert.Equal(["ضغط الصور"], ar.Tools.Select(w => w.Label));
    }

    [Fact]
    public void Multi_file_tools_need_at_least_two_files()
    {
        var registry = new ToolRegistry().Register(new FakeConverter(["pdf.merge", "pdf.split"]));
        var one = Builder(registry).Build([@"C:\a.pdf"], "en", Actions);
        var two = Builder(registry).Build([@"C:\a.pdf", @"C:\b.pdf"], "en", Actions);

        Assert.DoesNotContain(one.Tools, w => w.Id == "pdf.merge");
        Assert.Contains(one.Tools, w => w.Id == "pdf.split");
        Assert.Contains(two.Tools, w => w.Id == "pdf.merge" && w.Multi);
    }

    [Fact]
    public void Mixed_categories_get_actions_but_no_conversions_or_category_tools()
    {
        var registry = new ToolRegistry().Register(ImageConverter()).Register(new FakeConverter(["img.compress"]));
        var set = Builder(registry).Build([@"C:\a.png", @"C:\b.pdf"], "en", Actions);

        Assert.Empty(set.Convert);
        Assert.Empty(set.Tools);
        Assert.Equal(3, set.Actions.Count);
    }

    [Fact]
    public void General_tools_are_offered_for_mixed_unknown_and_folder_selections()
    {
        var registry = new ToolRegistry().Register(new FakeConverter(["gen.hash", "gen.rename", "img.compress"]));

        var mixed = Builder(registry).Build([@"C:\a.png", @"C:\b.pdf"], "en", Actions);
        Assert.Equal(["gen.rename", "gen.hash"], mixed.Tools.Select(w => w.Id));   // catalog order

        var unknown = Builder(registry).Build([@"C:\thing.xyz"], "en", Actions);
        Assert.Equal(["gen.hash"], unknown.Tools.Select(w => w.Id));   // gen.rename needs two files

        var folder = Builder(registry).Build([@"C:\Users\me\Pictures"], "en", Actions);
        Assert.Equal(["gen.hash"], folder.Tools.Select(w => w.Id));
    }

    [Fact]
    public void General_tools_must_support_every_selected_file()
    {
        var registry = new ToolRegistry().Register(new FakeConverter(["gen.hash"], supports: (_, ext) => ext != "xyz"));
        var set = Builder(registry).Build([@"C:\a.png", @"C:\b.xyz"], "en", Actions);
        Assert.Empty(set.Tools);
    }

    [Fact]
    public void Zip_is_offered_for_a_single_file_or_folder_even_though_it_aggregates()
    {
        var registry = new ToolRegistry().Register(new FakeConverter(["arc.zip"]));
        var one = Builder(registry).Build([@"C:\a.png"], "en", Actions);
        var folder = Builder(registry).Build([@"C:\Users\me\Pictures"], "en", Actions);

        Assert.Equal(["arc.zip"], one.Tools.Select(w => w.Id));
        Assert.True(one.Tools.Single().Multi);
        Assert.Equal(["arc.zip"], folder.Tools.Select(w => w.Id));
    }

    [Fact]
    public void Unknown_files_still_get_actions()
    {
        var set = Builder(new ToolRegistry()).Build([@"C:\thing.xyz"], "en", Actions);
        Assert.Equal(["act.copy", "act.move", "act.copy-path"], set.Actions.Select(a => a.Id).OrderBy(x => Array.IndexOf(Actions, x)));
    }

    [Fact]
    public void Only_available_actions_are_listed()
    {
        var set = Builder(new ToolRegistry()).Build([@"C:\a.png"], "en", ["act.copy"]);
        Assert.Equal(["act.copy"], set.Actions.Select(a => a.Id));
    }

    [Fact]
    public void A_missing_engine_marks_the_wedge_instead_of_hiding_it()
    {
        var registry = new ToolRegistry().Register(new FakeConverter(["convert:mp4"], engine: EngineId.Ffmpeg));
        var without = Builder(registry).Build([@"C:\a.mov"], "en", Actions);
        var with = Builder(registry, EngineId.Ffmpeg).Build([@"C:\a.mov"], "en", Actions);

        Assert.Equal(WedgeState.NeedsEngine, without.Convert.Single().State);
        Assert.Equal(EngineId.Ffmpeg, without.Convert.Single().Engine);
        Assert.Equal(WedgeState.Ready, with.Convert.Single().State);
    }

    [Fact]
    public void Tools_that_need_options_are_flagged()
    {
        var registry = new ToolRegistry().Register(new FakeConverter(["pdf.protect"], options: [new ToolOption("password", ToolOptionKind.Password, Required: true)]));
        var set = Builder(registry).Build([@"C:\a.pdf"], "en", Actions);
        Assert.True(set.Tools.Single().NeedsOptions);
    }

    [Theory]
    [InlineData("pdfa", "PDF/A")]
    [InlineData("png", "PNG")]
    [InlineData("gz", "GZ")]
    public void Format_labels_are_uppercase_with_special_cases(string ext, string label) =>
        Assert.Equal(label, WheelBuilder.FormatLabel(ext));
}
