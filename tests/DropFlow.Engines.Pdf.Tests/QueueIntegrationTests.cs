using DropFlow.Core.Catalog;
using DropFlow.Core.Operations;
using DropFlow.Core.Queue;
using DropFlow.Core.Registry;
using DropFlow.TestSupport;

namespace DropFlow.Engines.Pdf.Tests;

/// <summary>The module as the app uses it: registered in a ToolRegistry and driven by the real JobQueue.</summary>
public class QueueIntegrationTests
{
    private static JobQueue NewQueue() =>
        new(new ToolRegistry().Register(PdfModule.Create(new NoEngines())), ToolCatalog.LoadEmbedded());

    [Fact]
    public async Task A_multi_file_tool_gets_all_inputs_in_one_job()
    {
        using var dir = new TempDir();
        var a = Samples.Pdf(dir.Combine("a.pdf"), 1, _ => "A");
        var b = Samples.Pdf(dir.Combine("b.pdf"), 2, n => $"B{n}");

        var batch = NewQueue().Enqueue(new ConversionJob([a, b], "pdf.merge"));
        var result = await batch.Completion;

        Assert.True(result.Success, result.Error);
        Assert.Single(batch.Jobs);
        Assert.Equal(["A", "B1", "B2"], Kit.PageTexts(Assert.Single(result.Outputs)));
    }

    [Fact]
    public async Task A_single_file_tool_runs_once_per_dropped_file()
    {
        using var dir = new TempDir();
        var a = Samples.Pdf(dir.Combine("a.pdf"), 1);
        var b = Samples.Pdf(dir.Combine("b.pdf"), 1);

        var batch = NewQueue().Enqueue(new ConversionJob([a, b], "convert:png"));
        var result = await batch.Completion;

        Assert.True(result.Success, result.Error);
        Assert.Equal(2, batch.Jobs.Count);
        Assert.Equal(2, result.Outputs.Count);
        Assert.All(result.Outputs, o => Assert.Equal("png", PixelKit.Signature(o)));
    }

    [Fact]
    public async Task Mixed_success_and_failure_in_one_drop_is_reported_per_file()
    {
        using var dir = new TempDir();
        var good = Samples.Pdf(dir.Combine("good.pdf"), 1);
        var bad = dir.WriteText("bad.pdf", "not a pdf");

        var batch = NewQueue().Enqueue(new ConversionJob([good, bad], "convert:txt"));
        var result = await batch.Completion;

        Assert.False(result.Success);
        Assert.Contains("bad.pdf", result.Error);
        Assert.Single(result.Outputs); // the good file still produced its text
    }

    [Fact]
    public async Task Cancelling_a_batch_cancels_the_running_conversion()
    {
        using var dir = new TempDir();
        var pdf = Samples.Pdf(dir.Combine("many.pdf"), 30);
        var queue = NewQueue();

        var batch = queue.Enqueue(new ConversionJob([pdf], "pdf.to-images", Kit.Opts(("dpi", 150), ("format", "png"))));
        batch.Cancel();
        var result = await batch.Completion;

        Assert.False(result.Success);
        Assert.Equal(JobState.Cancelled, batch.Jobs[0].State);
        Assert.Equal(["many.pdf"], Kit.FilesIn(dir.Path));
        Assert.Empty(Directory.GetDirectories(dir.Path));
    }

    [Fact]
    public async Task Pdf_in_and_image_in_use_the_same_registry_without_clashing()
    {
        using var dir = new TempDir();
        var png = Samples.Png(dir.Combine("pic.png"));
        var queue = NewQueue();

        var toPdf = await queue.Enqueue(new ConversionJob([png], "convert:pdf")).Completion;
        var back = await queue.Enqueue(new ConversionJob([toPdf.Outputs[0]], "convert:png")).Completion;

        Assert.True(toPdf.Success && back.Success);
        Assert.Equal("png", PixelKit.Signature(back.Outputs[0]));
    }
}
