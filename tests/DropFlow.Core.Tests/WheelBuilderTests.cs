using DropFlow.Core.Catalog;
using DropFlow.Core.Operations;
using DropFlow.Core.Registry;
using DropFlow.Core.Settings;
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
    public void Compare_is_offered_for_exactly_two_pdfs()
    {
        var registry = new ToolRegistry().Register(new FakeConverter(["pdf.compare"]));
        string[] Offered(params string[] files) => Builder(registry).Build(files, "en", Actions).Tools.Select(w => w.Id).ToArray();

        Assert.Empty(Offered(@"C:\a.pdf"));
        Assert.Equal(["pdf.compare"], Offered(@"C:\a.pdf", @"C:\b.pdf"));
        Assert.Empty(Offered(@"C:\a.pdf", @"C:\b.pdf", @"C:\c.pdf"));
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

    // ----- "+ Add" and pinned items ------------------------------------------------------------------------------------------

    private static ToolRegistry ImageRegistry() =>
        new ToolRegistry().Register(ImageConverter()).Register(new FakeConverter(["img.compress", "img.ocr", "pdf.merge"]));

    [Fact]
    public void Add_is_the_last_wedge_of_every_wheel_that_has_something_to_add()
    {
        var set = Builder(ImageRegistry()).Build([@"C:\a.png"], "en", Actions, WheelPins.None);

        foreach (var kind in Enum.GetValues<WheelKind>())
        {
            var wedges = set.Of(kind);
            Assert.True(wedges[^1].IsAdd, $"{kind} should end with + Add");
            Assert.Single(wedges, w => w.IsAdd);
        }
    }

    [Fact]
    public void Add_appears_only_when_something_is_addable()
    {
        // only conversions apply and no actions are available: the Convert wheel already shows everything
        var set = Builder(new ToolRegistry().Register(ImageConverter())).Build([@"C:\a.png"], "en", [], WheelPins.None);
        Assert.DoesNotContain(set.Convert, w => w.IsAdd);
        Assert.Empty(set.AddableFor(WheelKind.Convert));
        Assert.True(set.Tools.Single().IsAdd);          // the empty Tools wheel can take the conversions

        Assert.Empty(Builder(ImageRegistry()).Build([], "en", Actions, WheelPins.None).Convert);   // no files, no slot
    }

    [Fact]
    public void A_picture_offers_ocr_on_the_convert_wheel()
    {
        var set = Builder(ImageRegistry()).Build([@"C:\a.png"], "en", Actions, WheelPins.None);
        var ocr = Assert.Single(set.AddableFor(WheelKind.Convert), w => w.Id == "img.ocr");
        Assert.Equal(WheelKind.Tools, ocr.Wheel);       // grouped (and run) as a tool
        Assert.Equal("Extract text (OCR)", ocr.Label);
    }

    [Fact]
    public void Addable_excludes_the_wheels_own_items_and_anything_without_a_converter_or_out_of_file_limits()
    {
        var set = Builder(ImageRegistry()).Build([@"C:\a.png"], "en", Actions, WheelPins.None);
        var addable = set.AddableFor(WheelKind.Convert).Select(w => w.Id).ToList();

        Assert.Equal(["img.compress", "img.ocr", "act.copy", "act.move", "act.copy-path"], addable);   // grouped: tools, then actions
        Assert.DoesNotContain("convert:jpg", addable);  // already on this wheel
        Assert.DoesNotContain("img.resize", addable);   // in the catalog, but no converter
        Assert.DoesNotContain("pdf.merge", addable);    // has a converter, but not for pictures

        var tools = set.AddableFor(WheelKind.Tools).Select(w => w.Id).ToList();
        Assert.Equal(["convert:jpg", "convert:webp", "act.copy", "act.move", "act.copy-path"], tools);
    }

    [Fact]
    public void Multi_file_tools_are_addable_only_with_enough_files()
    {
        var registry = new ToolRegistry().Register(new FakeConverter(["pdf.merge", "pdf.split"]));
        Assert.DoesNotContain(Builder(registry).Build([@"C:\a.pdf"], "en", Actions, WheelPins.None).AddableFor(WheelKind.Convert), w => w.Id == "pdf.merge");
        Assert.Contains(Builder(registry).Build([@"C:\a.pdf", @"C:\b.pdf"], "en", Actions, WheelPins.None).AddableFor(WheelKind.Convert), w => w.Id == "pdf.merge");
    }

    [Fact]
    public void A_pin_puts_the_item_on_that_wheel_before_add_for_that_category_only()
    {
        var pins = WheelPins.None.With(WheelKind.Convert, "image", "img.ocr");
        var picture = Builder(ImageRegistry()).Build([@"C:\a.png"], "en", Actions, pins);

        Assert.Equal(["convert:jpg", "convert:webp", "img.ocr", Wedge.AddId], picture.Convert.Select(w => w.Id));
        Assert.True(picture.Convert[2].Pinned);
        Assert.DoesNotContain(picture.AddableFor(WheelKind.Convert), w => w.Id == "img.ocr");
        Assert.Contains(picture.Tools, w => w.Id == "img.ocr" && !w.Pinned);   // still on its own wheel
        Assert.DoesNotContain(picture.Actions, w => w.Id == "img.ocr");        // other wheels unchanged

        var pdfRegistry = new ToolRegistry().Register(new FakeConverter(["img.ocr", "pdf.split"]));
        var pdf = Builder(pdfRegistry).Build([@"C:\a.pdf"], "en", Actions, pins);
        Assert.DoesNotContain(pdf.Convert, w => w.Id == "img.ocr");             // pinned for pictures, not for PDFs
    }

    [Fact]
    public void A_pin_that_does_not_apply_to_the_selection_is_kept_but_not_shown()
    {
        var pins = WheelPins.None.With(WheelKind.Convert, "image", "img.collage");   // no converter in this registry
        var set = Builder(ImageRegistry()).Build([@"C:\a.png"], "en", Actions, pins);
        Assert.DoesNotContain(set.Convert, w => w.Id == "img.collage");
        Assert.Equal("image", set.Category);
    }

    [Fact]
    public void Mixed_selections_keep_their_pins_under_other()
    {
        var registry = new ToolRegistry().Register(new FakeConverter(["gen.hash"]));
        Assert.Equal(WheelBuilder.OtherCategory, Builder(registry).CategoryKey([@"C:\a.png", @"C:\b.pdf"]));
        Assert.Equal(WheelBuilder.OtherCategory, Builder(registry).CategoryKey([@"C:\Users\me\Pictures"]));

        var pins = WheelPins.None.With(WheelKind.Actions, WheelBuilder.OtherCategory, "gen.hash");
        var set = Builder(registry).Build([@"C:\a.png", @"C:\b.pdf"], "en", Actions, pins);
        Assert.Contains(set.Actions, w => w.Id == "gen.hash" && w.Pinned);
        Assert.Equal([Wedge.AddId], set.Convert.Select(w => w.Id));          // an empty Convert wheel still offers "+ Add"
    }

    [Fact]
    public void Add_stays_reachable_on_a_very_full_wheel()
    {
        // every PDF tool in the catalog: the Tools wheel is as full as it gets, and "+ Add" is still its last wedge
        var allPdfTools = Catalog.Tools.Where(t => t.Cat is "pdf" or "general").Select(t => t.Id).ToArray();
        var registry = new ToolRegistry().Register(new FakeConverter(allPdfTools)).Register(new FakeConverter(["convert:docx"]));
        var set = Builder(registry).Build([@"C:\a.pdf", @"C:\b.pdf"], "en", Actions, WheelPins.None);

        Assert.True(set.Tools.Count > 20, $"only {set.Tools.Count} wedges");
        Assert.True(set.Tools[^1].IsAdd);
        Assert.Contains(set.AddableFor(WheelKind.Tools), w => w.Id == "convert:docx");
    }

    [Fact]
    public void Labels_for_pinned_ids_are_readable()
    {
        var b = Builder(new ToolRegistry());
        Assert.Equal("PDF", b.LabelOf("convert:pdf", "en"));
        Assert.Equal("Extract text (OCR)", b.LabelOf("img.ocr", "en"));
        Assert.Equal("unknown.id", b.LabelOf("unknown.id", "en"));
    }

    [Theory]
    [InlineData("pdfa", "PDF/A")]
    [InlineData("png", "PNG")]
    [InlineData("gz", "GZ")]
    public void Format_labels_are_uppercase_with_special_cases(string ext, string label) =>
        Assert.Equal(label, WheelBuilder.FormatLabel(ext));
}
