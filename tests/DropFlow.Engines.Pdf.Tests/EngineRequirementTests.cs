using DropFlow.Core.Operations;

namespace DropFlow.Engines.Pdf.Tests;

/// <summary>Which engine a PDF run really needs depends on the chosen options (extreme compression runs without qpdf).</summary>
public class EngineRequirementTests
{
    private static IConverter ConverterFor(string operation) =>
        PdfModule.Create(new NoEngines()).Single(c => c.Operations.Contains(operation) && c.Supports(operation, "pdf"));

    private static IReadOnlyDictionary<string, object?> Level(string level) => new Dictionary<string, object?> { ["level"] = level };

    [Fact]
    public void Extreme_compression_needs_no_engine_the_other_levels_need_qpdf()
    {
        var compress = ConverterFor("pdf.compress");

        Assert.Equal(EngineId.Qpdf, compress.EngineFor("pdf.compress"));                       // what the wheel knows before any choice
        Assert.Equal(EngineId.Qpdf, compress.EngineFor("pdf.compress", Level("recommended")));
        Assert.Equal(EngineId.Qpdf, compress.EngineFor("pdf.compress", Level("less")));
        Assert.Equal(EngineId.None, compress.EngineFor("pdf.compress", Level("extreme")));
        Assert.Equal(EngineId.None, compress.EngineFor("pdf.compress", Level("EXTREME")));      // spelling does not matter
        Assert.Equal(EngineId.Qpdf, compress.EngineFor("pdf.compress", ConversionJob.NoOptions)); // default level is "recommended"
    }

    [Fact]
    public void Other_qpdf_tools_always_need_it()
    {
        foreach (var op in new[] { "pdf.protect", "pdf.unlock", "pdf.repair" })
            Assert.Equal(EngineId.Qpdf, ConverterFor(op).EngineFor(op, Level("extreme")));
    }
}
