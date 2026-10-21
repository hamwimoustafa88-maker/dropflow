using DropFlow.Core.Catalog;
using DropFlow.Core.Operations;
using DropFlow.Core.Registry;

namespace DropFlow.Engines.Pdf.Tests;

/// <summary>Checks the module against the catalog: nothing is claimed that the catalog does not offer, and nothing is claimed twice.</summary>
public class ModuleContractTests
{
    private static readonly ToolCatalog Catalog = ToolCatalog.LoadEmbedded();

    private static IEnumerable<(IConverter Converter, string Operation, string Ext)> ClaimedPairs(IReadOnlyList<IConverter> converters)
    {
        var inputs = Catalog.Categories.SelectMany(c => c.Inputs).Concat(["jpeg", "tif"]).Distinct().ToList();
        foreach (var converter in converters)
            foreach (var operation in converter.Operations)
                foreach (var ext in inputs)
                    if (converter.Supports(operation, ext))
                        yield return (converter, operation, ext);
    }

    [Fact]
    public void Create_returns_converters()
    {
        Assert.NotEmpty(PdfModule.Create(new NoEngines()));
    }

    [Fact]
    public void Every_claimed_operation_exists_in_the_catalog_for_that_input()
    {
        foreach (var (_, operation, ext) in ClaimedPairs(Kit.Converters))
        {
            var category = Catalog.CategoryForExtension(ext);
            if (ext is "jpeg") category = "image";
            if (ext is "tif") category = "image";
            Assert.NotNull(category);
            var cat = Catalog.Category(category!)!;

            if (Operation.IsConvert(operation))
            {
                Assert.True(cat.Outputs.Contains(Operation.TargetOf(operation)),
                    $"{operation} on .{ext}: '{Operation.TargetOf(operation)}' is not an output of the {category} category");
            }
            else
            {
                var tool = Catalog.Tool(operation);
                Assert.True(tool is not null, $"{operation} is not in the catalog");
                Assert.True(tool!.Cat == category, $"{operation} belongs to '{tool.Cat}', not '{category}' (.{ext})");
            }
        }
    }

    [Fact]
    public void No_pair_converts_a_format_to_itself()
    {
        foreach (var (_, operation, ext) in ClaimedPairs(Kit.Converters))
        {
            if (!Operation.IsConvert(operation)) continue;
            var target = Operation.TargetOf(operation);
            Assert.False(target == ext || (target == "jpg" && ext == "jpeg") || (target == "tiff" && ext == "tif"),
                $"{operation} on .{ext} converts a format to itself");
        }
    }

    [Fact]
    public void No_operation_and_input_pair_is_claimed_by_two_converters_of_the_same_module()
    {
        var duplicates = ClaimedPairs(Kit.Converters)
            .GroupBy(p => (p.Operation, p.Ext))
            .Where(g => g.Select(p => p.Converter).Distinct().Count() > 1)
            .Select(g => $"{g.Key.Operation} .{g.Key.Ext}")
            .ToList();
        Assert.Empty(duplicates);
    }

    [Fact]
    public void Converters_are_registered_in_a_tool_registry_and_found_by_operation_and_extension()
    {
        var registry = new ToolRegistry().Register(Kit.Converters);
        Assert.NotNull(registry.Find("pdf.merge", "pdf"));
        Assert.NotNull(registry.Find("img.to-pdf", "PNG"));
        Assert.NotNull(registry.Find("convert:png", ".pdf"));
        Assert.Null(registry.Find("convert:pdf", "pdf")); // a format is never converted to itself
        Assert.Null(registry.Find("pdf.merge", "png"));
    }

    [Theory]
    [InlineData("pdf.merge", "pdf", true)]
    [InlineData("pdf.merge", "PDF", true)]
    [InlineData("pdf.merge", "png", false)]
    [InlineData("img.to-pdf", "png", true)]
    [InlineData("img.to-pdf", "heic", true)]
    [InlineData("convert:pdf", "jpg", true)]
    [InlineData("convert:pdf", "pdf", false)]
    [InlineData("convert:png", "pdf", true)]
    [InlineData("convert:png", "png", false)]
    [InlineData("convert:pdfa", "pdf", true)]
    [InlineData("convert:pdfa", "png", false)]
    [InlineData("convert:pdf", "heic", true)]
    public void Supports_is_truthful_about_known_pairs(string operation, string ext, bool expected)
    {
        Assert.Equal(expected, Kit.Converters.Any(c => c.Supports(operation, ext)));
    }

    [Fact]
    public void Managed_operations_need_no_engine()
    {
        foreach (var (operation, ext) in new[] { ("pdf.merge", "pdf"), ("pdf.split", "pdf"), ("pdf.to-images", "pdf"), ("img.to-pdf", "png"), ("pdf.extract-text", "pdf"), ("convert:txt", "pdf") })
        {
            Assert.Equal(EngineId.None, Kit.Converter(operation, ext).EngineFor(operation));
        }
    }

    [Fact]
    public void Operations_that_need_options_declare_them()
    {
        Assert.NotEmpty(Kit.Converter("pdf.rotate", "pdf").OptionsFor("pdf.rotate"));
        Assert.NotEmpty(Kit.Converter("pdf.organize", "pdf").OptionsFor("pdf.organize"));
        Assert.NotEmpty(Kit.Converter("pdf.watermark", "pdf").OptionsFor("pdf.watermark"));
        Assert.Empty(Kit.Converter("pdf.merge", "pdf").OptionsFor("pdf.merge"));
        Assert.Empty(Kit.Converter("convert:png", "pdf").OptionsFor("convert:png"));
    }

    [Fact]
    public void Option_defaults_fit_their_declared_kind()
    {
        foreach (var converter in Kit.Converters)
            foreach (var operation in converter.Operations)
                foreach (var option in converter.OptionsFor(operation))
                {
                    if (option.Kind == ToolOptionKind.Choice)
                    {
                        Assert.NotNull(option.Choices);
                        if (option.Default is not null) Assert.Contains(Convert.ToString(option.Default), option.Choices!);
                    }

                    if (option.Kind == ToolOptionKind.Number && option.Default is not null)
                    {
                        var value = Convert.ToDouble(option.Default, System.Globalization.CultureInfo.InvariantCulture);
                        if (option.Min is { } min) Assert.True(value >= min, $"{operation}.{option.Key} default below min");
                        if (option.Max is { } max) Assert.True(value <= max, $"{operation}.{option.Key} default above max");
                    }
                }
    }
}
