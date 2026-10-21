using DropFlow.Core.Catalog;
using DropFlow.Core.Operations;
using DropFlow.Core.Queue;
using DropFlow.Core.Registry;
using DropFlow.TestSupport;

namespace DropFlow.Engines.Pdf.Tests;

/// <summary>Crop, sign, redact and compare as the app runs them: registered in a ToolRegistry, scheduled by the real JobQueue with the real catalog.</summary>
public class NewToolsQueueTests
{
    private static JobQueue NewQueue() =>
        new(new ToolRegistry().Register(PdfModule.Create(new NoEngines())), ToolCatalog.LoadEmbedded());

    private static string Doc(TempDir dir, string name, string body)
    {
        var b = new PdfBuilder();
        b.AddPage().Text("Quarterly report", 72, 100, 14).Text(body, 72, 200, 24);
        return b.Save(dir.Combine(name));
    }

    [Fact]
    public async Task Compare_is_a_multi_file_tool_one_job_with_both_files_and_two_outputs()
    {
        using var dir = new TempDir();
        var a = Doc(dir, "a.pdf", "Alpha");
        var b = Doc(dir, "b.pdf", "Beta");

        var batch = NewQueue().Enqueue(new ConversionJob([a, b], "pdf.compare"));
        var result = await batch.Completion;

        Assert.True(result.Success, result.Error);
        Assert.Single(batch.Jobs);
        Assert.Equal(2, result.Outputs.Count);
        Assert.Equal([".pdf", ".txt"], result.Outputs.Select(o => Path.GetExtension(o) ?? "").OrderBy(e => e, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task Crop_runs_once_per_dropped_file()
    {
        using var dir = new TempDir();
        var a = Doc(dir, "a.pdf", "Alpha");
        var b = Doc(dir, "b.pdf", "Beta");

        var batch = NewQueue().Enqueue(new ConversionJob([a, b], "pdf.crop", Kit.Opts(("mode", "auto"))));
        var result = await batch.Completion;

        Assert.True(result.Success, result.Error);
        Assert.Equal(2, batch.Jobs.Count);
        Assert.Equal([dir.Combine("a (cropped).pdf"), dir.Combine("b (cropped).pdf")], result.Outputs.OrderBy(o => o, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task The_signature_notice_reaches_the_batch_result_once_even_for_several_files()
    {
        using var dir = new TempDir();
        var a = Doc(dir, "a.pdf", "Alpha");
        var b = Doc(dir, "b.pdf", "Beta");

        var result = await NewQueue().Enqueue(new ConversionJob([a, b], "pdf.sign", Kit.Opts(("text", "Ahmed")))).Completion;

        Assert.True(result.Success, result.Error);
        Assert.Equal(2, result.Outputs.Count);
        Assert.Contains("not a digital signature", result.Note, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(result.Note!.IndexOf("not a digital signature", StringComparison.OrdinalIgnoreCase), result.Note.LastIndexOf("not a digital signature", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Redact_reports_what_it_rasterized_through_the_queue()
    {
        using var dir = new TempDir();
        var a = Doc(dir, "a.pdf", "Alpha beta");

        var result = await NewQueue().Enqueue(new ConversionJob([a], "pdf.redact", Kit.Opts(("terms", "beta")))).Completion;

        Assert.True(result.Success, result.Error);
        Assert.Contains("rasterized", result.Note, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("", Kit.PageTexts(result.Outputs[0])[0]);
    }

    [Fact]
    public async Task Compare_with_a_single_file_dropped_fails_with_a_clear_message()
    {
        using var dir = new TempDir();
        var a = Doc(dir, "a.pdf", "Alpha");

        var result = await NewQueue().Enqueue(new ConversionJob([a], "pdf.compare")).Completion;

        Assert.False(result.Success);
        Assert.Contains("exactly two", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["a.pdf"], Kit.FilesIn(dir.Path));
    }
}
