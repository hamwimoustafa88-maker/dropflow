using DropFlow.Core.Catalog;
using DropFlow.Core.Operations;
using DropFlow.Core.Registry;
using DropFlow.Core.Routing;
using DropFlow.Core.Settings;
using DropFlow.Core.Wheel;

namespace DropFlow.Core.Tests;

public class RouteTests
{
    private static readonly ToolCatalog Catalog = ToolCatalog.LoadEmbedded();

    /// <summary>A converter that writes "name (converted).target" into the job's output folder (or beside the input).</summary>
    private static FakeConverter Writes(string from, string to, EngineId engine = EngineId.None, List<string>? log = null) => new(
        [Operation.Convert(to)],
        supports: (op, ext) => ext == from,
        run: (job, progress, ct) =>
        {
            var dir = job.OutputDirectory ?? Path.GetDirectoryName(job.Inputs[0])!;
            var output = Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(job.Inputs[0])} (converted).{to}");
            File.WriteAllText(output, $"{from}->{to}");
            log?.Add($"{Path.GetFileName(job.Inputs[0])} -> {to} @ {(job.OutputDirectory is null ? "beside" : Path.GetFileName(job.OutputDirectory))}");
            progress.Report(new JobProgress(1));
            return Task.FromResult(ConversionResult.Ok([output]));
        },
        engine: engine);

    private static (ToolRegistry Registry, RouteConverter Route) Rig(params IConverter[] converters) => Rig(new FakeEngines(), null, converters);

    private static (ToolRegistry Registry, RouteConverter Route) Rig(FakeEngines engines, string? temp, params IConverter[] converters)
    {
        var registry = new ToolRegistry().Register(converters);
        var route = new RouteConverter(registry, Catalog, engines, temp);
        registry.Register(route);
        return (registry, route);
    }

    [Fact]
    public void The_planner_finds_the_shortest_chain()
    {
        var (_, route) = Rig(Writes("epub", "pdf"), Writes("pdf", "png"), Writes("epub", "docx"), Writes("docx", "pdf"));
        Assert.Equal(["pdf", "png"], route.Plan("epub", "png")!.Steps);   // not epub > docx > pdf > png
    }

    [Fact]
    public void A_route_is_offered_only_for_routed_targets_without_a_direct_converter()
    {
        var (registry, _) = Rig(Writes("epub", "pdf"), Writes("pdf", "png"));
        Assert.IsType<RouteConverter>(registry.Find("convert:png", "epub"));
        Assert.Null(registry.Find("convert:webp", "epub"));              // not in the catalog's routes for epub

        var (direct, _) = Rig(Writes("epub", "pdf"), Writes("pdf", "png"), Writes("epub", "png"));
        Assert.IsNotType<RouteConverter>(direct.Find("convert:png", "epub")); // a direct converter always wins
    }

    [Fact]
    public void A_step_whose_engine_is_missing_or_that_needs_an_answer_is_not_used()
    {
        var (_, noEngine) = Rig(Writes("epub", "pdf", EngineId.LibreOffice), Writes("pdf", "png"));
        Assert.Null(noEngine.Plan("epub", "png"));

        var (_, withEngine) = Rig(new FakeEngines(EngineId.LibreOffice), null, Writes("epub", "pdf", EngineId.LibreOffice), Writes("pdf", "png"));
        Assert.NotNull(withEngine.Plan("epub", "png"));

        var asks = new FakeConverter(["convert:pdf"], (_, ext) => ext == "epub", options: [new ToolOption("password", ToolOptionKind.Text, Required: true)]);
        var (_, needsAnswer) = Rig(asks, Writes("pdf", "png"));
        Assert.Null(needsAnswer.Plan("epub", "png"));
    }

    [Fact]
    public void Archives_are_never_a_step_and_chains_stop_at_three()
    {
        var (_, viaZip) = Rig(Writes("epub", "zip"), Writes("zip", "png"));
        Assert.Null(viaZip.Plan("epub", "png"));

        var (_, four) = Rig(Writes("epub", "txt"), Writes("txt", "md"), Writes("md", "html"), Writes("html", "png"));
        Assert.Null(four.Plan("epub", "png"));
        var (_, three) = Rig(Writes("epub", "txt"), Writes("txt", "html"), Writes("html", "png"));
        Assert.Equal(["txt", "html", "png"], three.Plan("epub", "png")!.Steps);
    }

    [Fact]
    public async Task Running_a_route_writes_only_the_final_file_beside_the_source_and_cleans_up()
    {
        using var dir = new TempDir();
        var temp = Path.Combine(dir.Path, "routes");
        var book = dir.File("novel.epub");
        var log = new List<string>();
        var (registry, _) = Rig(new FakeEngines(), temp, Writes("epub", "pdf", log: log), Writes("pdf", "png", log: log));

        var result = await registry.Find("convert:png", "epub")!.RunAsync(new ConversionJob([book], "convert:png"), new Progress<JobProgress>(), CancellationToken.None);

        Assert.True(result.Success, result.Error);
        Assert.Equal([Path.Combine(dir.Path, "novel (converted).png")], result.Outputs);
        Assert.True(File.Exists(result.Outputs[0]));
        Assert.Equal("novel.epub -> pdf", log[0][..17]);
        Assert.Equal($"novel.pdf -> png @ {Path.GetFileName(dir.Path)}", log[1]); // named like the source, written to its folder
        Assert.False(File.Exists(Path.Combine(dir.Path, "novel (converted).pdf")));
        Assert.Empty(Directory.GetDirectories(temp));                         // no intermediate left behind
        Assert.True(File.Exists(book));
    }

    [Fact]
    public async Task A_failing_step_cleans_up_and_says_which_step()
    {
        using var dir = new TempDir();
        var temp = Path.Combine(dir.Path, "routes");
        var broken = new FakeConverter(["convert:png"], (_, ext) => ext == "pdf", run: (_, _, _) => Task.FromResult(ConversionResult.Fail("bad page")));
        var (registry, _) = Rig(new FakeEngines(), temp, Writes("epub", "pdf"), broken);

        var result = await registry.Find("convert:png", "epub")!.RunAsync(new ConversionJob([dir.File("a.epub")], "convert:png"), new Progress<JobProgress>(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("Step 2 of 2 (PNG)", result.Error);
        Assert.Empty(Directory.GetDirectories(temp));
    }

    [Fact]
    public void Routed_targets_appear_on_the_convert_wheel()
    {
        var (registry, _) = Rig(Writes("epub", "pdf"), Writes("pdf", "png"));
        var set = new WheelBuilder(Catalog, registry, new FakeEngines()).Build([@"C:\b.epub"], "en", [], WheelPins.None);
        Assert.Contains(set.Convert, w => w.Id == "convert:png");
        Assert.DoesNotContain(set.Convert, w => w.Id == "convert:jpg");      // routed in the catalog, but no pdf → jpg converter here
    }
}
