using DropFlow.Core.Catalog;
using DropFlow.Core.Operations;
using DropFlow.Core.Queue;
using DropFlow.Core.Registry;
using DropFlow.Core.Session;
using DropFlow.Core.Wheel;
using DropFlow.Desktop.App;

namespace DropFlow.Desktop.Tests;

public class DesktopRunnerTests
{
    private sealed class FakeConverter(EngineId engine = EngineId.None, IReadOnlyList<ToolOption>? options = null) : IConverter
    {
        public List<ConversionJob> Jobs { get; } = [];
        public IReadOnlyCollection<string> Operations => ["convert:png"];
        public bool Supports(string operation, string inputExtension) => operation == "convert:png" && inputExtension == "jpg";
        public EngineId EngineFor(string operation) => engine;
        public IReadOnlyList<ToolOption> OptionsFor(string operation) => options ?? [];
        public Task<ConversionResult> RunAsync(ConversionJob job, IProgress<JobProgress> progress, CancellationToken cancellationToken)
        {
            lock (Jobs) Jobs.Add(job);
            return Task.FromResult(ConversionResult.Ok(["/out/x.png"]));
        }
    }

    private sealed class Window : IInteractiveTool
    {
        public IReadOnlyCollection<string> Operations => ["media.encoder"];
        public bool Supports(string operation, string inputExtension) => operation == "media.encoder";
        public Task<ConversionResult> RunAsync(ConversionJob job, IProgress<JobProgress> progress, CancellationToken ct) => Task.FromResult(ConversionResult.Fail("window"));
        public Task OpenAsync(string operation, IReadOnlyList<string> files) => Task.CompletedTask;
    }

    private sealed class Prompts(IReadOnlyDictionary<string, object?>? answer = null) : IDesktopPrompts
    {
        public List<string> Messages { get; } = [];
        public int Asked { get; private set; }
        public Task<IReadOnlyDictionary<string, object?>?> AskOptionsAsync(string operation, string title, IReadOnlyList<ToolOption> options)
        {
            Asked++;
            return Task.FromResult(answer);
        }
        public Task ShowMessageAsync(string message)
        {
            Messages.Add(message);
            return Task.CompletedTask;
        }
    }

    private sealed class Engines(params EngineId[] installed) : IEngineProvider
    {
        public string? Locate(EngineId engine) => installed.Contains(engine) ? "/usr/bin/" + engine : null;
    }

    private static readonly ToolCatalog Catalog = ToolCatalog.LoadEmbedded();

    private static (DesktopRunner Runner, Prompts Prompts) Make(IConverter converter, Prompts prompts, IEngineProvider engines, string? outputDir)
    {
        var registry = new ToolRegistry().Register(converter);
        var queue = new JobQueue(registry, Catalog);
        var runner = new DesktopRunner(registry, queue, engines, prompts, op => op, () => outputDir,
            e => $"missing {e}", op => $"unsupported {op}", (op, ext) => $"cannot {op} .{ext}");
        return (runner, prompts);
    }

    private static WheelSelection Pick(string op, params string[] files) =>
        new(WheelKind.Convert, new Wedge(op, WheelKind.Convert, "PNG", op), files);

    [Fact]
    public async Task The_job_carries_the_chosen_output_folder()
    {
        var converter = new FakeConverter();
        var (runner, _) = Make(converter, new Prompts(), new Engines(), "/home/u/Downloads");
        var batch = await runner.RunAsync(Pick("convert:png", "/tmp/a.jpg"));
        Assert.NotNull(batch);
        await batch!.Completion;
        Assert.Equal("/home/u/Downloads", Assert.Single(converter.Jobs).OutputDirectory);
    }

    [Fact]
    public async Task Beside_the_source_leaves_the_output_folder_empty()
    {
        var converter = new FakeConverter();
        var (runner, _) = Make(converter, new Prompts(), new Engines(), null);
        await (await runner.RunAsync(Pick("convert:png", "/tmp/a.jpg")))!.Completion;
        Assert.Null(Assert.Single(converter.Jobs).OutputDirectory);
    }

    [Fact]
    public async Task A_missing_engine_is_explained_and_nothing_is_installed_or_queued()
    {
        var converter = new FakeConverter(EngineId.Ffmpeg);
        var (runner, prompts) = Make(converter, new Prompts(), new Engines(), null);
        Assert.Null(await runner.RunAsync(Pick("convert:png", "/tmp/a.jpg")));
        Assert.Equal(["missing Ffmpeg"], prompts.Messages);
        Assert.Empty(converter.Jobs);
    }

    [Fact]
    public async Task An_installed_engine_runs()
    {
        var converter = new FakeConverter(EngineId.Ffmpeg);
        var (runner, _) = Make(converter, new Prompts(), new Engines(EngineId.Ffmpeg), null);
        Assert.NotNull(await runner.RunAsync(Pick("convert:png", "/tmp/a.jpg")));
    }

    [Fact]
    public async Task Options_are_asked_first_and_cancel_stops_everything()
    {
        var converter = new FakeConverter(options: [new ToolOption("quality", ToolOptionKind.Number, 80d)]);
        var (runner, prompts) = Make(converter, new Prompts(answer: null), new Engines(), null);
        Assert.Null(await runner.RunAsync(Pick("convert:png", "/tmp/a.jpg")));
        Assert.Equal(1, prompts.Asked);
        Assert.Empty(converter.Jobs);
    }

    [Fact]
    public async Task Chosen_options_reach_the_converter()
    {
        var converter = new FakeConverter(options: [new ToolOption("quality", ToolOptionKind.Number, 80d)]);
        var (runner, _) = Make(converter, new Prompts(new Dictionary<string, object?> { ["quality"] = 55d }), new Engines(), null);
        await (await runner.RunAsync(Pick("convert:png", "/tmp/a.jpg")))!.Completion;
        Assert.Equal(55d, Assert.Single(converter.Jobs).Opts["quality"]);
    }

    [Fact]
    public async Task Window_tools_are_reported_as_not_available_yet()
    {
        var (runner, prompts) = Make(new Window(), new Prompts(), new Engines(), null);
        Assert.Null(await runner.RunAsync(new WheelSelection(WheelKind.Tools, new Wedge("media.encoder", WheelKind.Tools, "Encoder", "media.encoder"), ["/tmp/a.mp4"])));
        Assert.Equal(["unsupported media.encoder"], prompts.Messages);
    }

    [Fact]
    public async Task No_converter_for_the_file_is_reported()
    {
        var (runner, prompts) = Make(new FakeConverter(), new Prompts(), new Engines(), null);
        Assert.Null(await runner.RunAsync(Pick("convert:png", "/tmp/a.gif")));
        Assert.Equal(["cannot convert:png .gif"], prompts.Messages);
    }
}
