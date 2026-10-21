using DropFlow.Core.Catalog;
using DropFlow.Core.Operations;
using DropFlow.Core.Registry;
using DropFlow.Engines.Archive;
using DropFlow.Core.Wheel;

namespace DropFlow.Engines.Archive.Tests;

public class ModuleTests
{
    private static readonly string[] ExpectedToolIds =
        ["arc.extract", "arc.zip", "gen.hash", "gen.base64", "gen.rename", "img.qr", "img.icons"];

    [Fact]
    public void The_module_provides_every_operation_this_project_owns()
    {
        var converters = ArchiveModule.Create(Kit.RealEngines);
        var operations = converters.SelectMany(c => c.Operations).ToList();

        foreach (var id in ExpectedToolIds) Assert.Contains(id, operations);
        foreach (var target in new[] { "zip", "tar", "gz", "7z" }) Assert.Contains($"convert:{target}", operations);
        Assert.Equal(operations.Count, operations.Distinct().Count());   // no operation is claimed twice
    }

    [Fact]
    public void Every_operation_matches_the_catalog()
    {
        var catalog = ToolCatalog.LoadEmbedded();
        var archive = catalog.Category("archive")!;

        foreach (var operation in ArchiveModule.Create(Kit.RealEngines).SelectMany(c => c.Operations))
        {
            if (Operation.IsConvert(operation))
                Assert.Contains(Operation.TargetOf(operation), archive.Outputs);
            else
                Assert.NotNull(catalog.Tool(operation));
        }
    }

    [Fact]
    public void Convert_support_matches_the_catalog_category_exactly()
    {
        var catalog = ToolCatalog.LoadEmbedded();
        var archive = catalog.Category("archive")!;
        var registry = new ToolRegistry().Register(ArchiveModule.Create(Kit.RealEngines));

        foreach (var input in archive.Inputs)
            foreach (var output in archive.Outputs)
                Assert.Equal(input != output, registry.Supports(Operation.Convert(output), input));
    }

    [Fact]
    public void Tool_flags_agree_with_the_catalog()
    {
        var catalog = ToolCatalog.LoadEmbedded();
        Assert.True(catalog.Tool("arc.zip")!.Multi);
        Assert.True(catalog.Tool("gen.rename")!.Multi);
        Assert.False(catalog.Tool("arc.extract")!.Multi);
        Assert.False(catalog.Tool("gen.hash")!.Multi);
        Assert.False(catalog.Tool("gen.base64")!.Multi);
        Assert.False(catalog.Tool("img.qr")!.Multi);
        Assert.False(catalog.Tool("img.icons")!.Multi);
    }

    [Fact]
    public void The_wheel_offers_archive_conversions_and_tools_for_an_archive_selection()
    {
        var catalog = ToolCatalog.LoadEmbedded();
        var registry = new ToolRegistry().Register(ArchiveModule.Create(Kit.RealEngines));
        var wheel = new WheelBuilder(catalog, registry, Kit.RealEngines).Build([@"C:\data\backup.zip"], "en", []);

        Assert.Equal(new[] { "tar", "gz", "7z" }.Select(WheelBuilder.FormatLabel).Order(), wheel.Convert.Select(w => w.Label).Order());
        var tools = wheel.Tools.Select(w => w.Id).ToList();
        Assert.Contains("arc.extract", tools);
        Assert.Contains("gen.hash", tools);                 // "general" tools appear for every kind of file
        Assert.Contains("gen.base64", tools);
        Assert.DoesNotContain("img.qr", tools);
    }

    [Fact]
    public void Tools_that_need_options_are_marked_so_the_ui_asks_first()
    {
        var catalog = ToolCatalog.LoadEmbedded();
        var registry = new ToolRegistry().Register(ArchiveModule.Create(Kit.RealEngines));
        var wheel = new WheelBuilder(catalog, registry, Kit.RealEngines).Build([@"C:\data\a.txt", @"C:\data\b.txt"], "en", []);

        var byId = wheel.Tools.ToDictionary(w => w.Id);
        Assert.True(byId["gen.rename"].NeedsOptions);
        Assert.True(byId["gen.hash"].NeedsOptions);
        Assert.True(byId["gen.base64"].NeedsOptions);
        Assert.True(byId["gen.rename"].Multi);
    }

    [Fact]
    public void The_image_wheel_shows_qr_and_icons_for_a_picture()
    {
        var catalog = ToolCatalog.LoadEmbedded();
        var registry = new ToolRegistry().Register(ArchiveModule.Create(Kit.RealEngines));
        var wheel = new WheelBuilder(catalog, registry, Kit.RealEngines).Build([@"C:\pics\logo.png"], "en", []);

        var tools = wheel.Tools.Select(w => w.Id).ToList();
        Assert.Contains("img.qr", tools);
        Assert.Contains("img.icons", tools);
        Assert.DoesNotContain("arc.extract", tools);
    }

    [Fact]
    public void Wedges_show_needs_engine_only_for_7z_output_when_7z_is_missing()
    {
        var catalog = ToolCatalog.LoadEmbedded();
        var registry = new ToolRegistry().Register(ArchiveModule.Create(new NoEngines()));
        var wheel = new WheelBuilder(catalog, registry, new NoEngines()).Build([@"C:\data\backup.zip"], "en", []);

        var states = wheel.Convert.ToDictionary(w => w.Operation, w => w.State);
        Assert.Equal(WedgeState.NeedsEngine, states["convert:7z"]);
        Assert.Equal(WedgeState.Ready, states["convert:tar"]);
        Assert.Equal(WedgeState.Ready, states["convert:gz"]);
        Assert.All(wheel.Tools, t => Assert.Equal(WedgeState.Ready, t.State));
    }
}
