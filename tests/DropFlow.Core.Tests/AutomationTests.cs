using DropFlow.Core.Automation;
using DropFlow.Core.Catalog;
using DropFlow.Core.Operations;
using DropFlow.Core.Queue;
using DropFlow.Core.Registry;

using static DropFlow.Core.Tests.TestPaths;

namespace DropFlow.Core.Tests;

public class RecipeRunnerTests
{
    private static readonly ToolCatalog Catalog = ToolCatalog.LoadEmbedded();

    /// <summary>Appends a tag to each input: "a.png" -> "a.png.<tag>" so a chain's history is visible in the final name.</summary>
    private static FakeConverter Tagger(string op, string tag, Func<ConversionJob, ConversionResult?>? special = null) =>
        new([op], run: (job, _, _) => Task.FromResult(special?.Invoke(job) ?? ConversionResult.Ok(job.Inputs.Select(i => $"{i}.{tag}").ToList())));

    private static (RecipeRunner Runner, FakeDisposer Disposer, JobQueue Queue) Rig(params IConverter[] converters)
    {
        var registry = new ToolRegistry().Register(converters);
        var queue = new JobQueue(registry, Catalog);
        var disposer = new FakeDisposer();
        return (new RecipeRunner(queue, disposer), disposer, queue);
    }

    [Fact]
    public async Task Steps_run_in_order_each_feeding_the_next()
    {
        var (runner, _, _) = Rig(Tagger("img.strip-gps", "a"), Tagger("img.resize", "b"), Tagger("convert:webp", "c"));
        var recipe = new Recipe("r", [new("img.strip-gps"), new("img.resize"), new("convert:webp")]);

        var result = await runner.RunAsync(recipe, [@"C:\x\p.jpg"]);

        Assert.True(result.Success, result.Error);
        Assert.Equal([@"C:\x\p.jpg.a.b.c"], result.Outputs);
    }

    [Fact]
    public async Task Intermediate_files_are_disposed_but_inputs_and_the_final_result_are_kept()
    {
        var (runner, disposer, _) = Rig(Tagger("img.strip-gps", "a"), Tagger("img.resize", "b"), Tagger("convert:webp", "c"));
        await runner.RunAsync(new Recipe("r", [new("img.strip-gps"), new("img.resize"), new("convert:webp")]), [@"C:\x\p.jpg"]);

        Assert.Equal([@"C:\x\p.jpg.a", @"C:\x\p.jpg.a.b"], disposer.Disposed.OrderBy(x => x.Length));
    }

    [Fact]
    public async Task KeepIntermediates_leaves_everything_in_place()
    {
        var (runner, disposer, _) = Rig(Tagger("img.strip-gps", "a"), Tagger("convert:webp", "c"));
        await runner.RunAsync(new Recipe("r", [new("img.strip-gps"), new("convert:webp")], KeepIntermediates: true), [@"C:\x\p.jpg"]);
        Assert.Empty(disposer.Disposed);
    }

    [Fact]
    public async Task A_step_that_produces_nothing_passes_the_files_through()
    {
        // e.g. "no location data found": the chain must continue with the original file
        var noop = new FakeConverter(["img.strip-gps"], run: (_, _, _) => Task.FromResult(ConversionResult.Ok([], note: "No location data was found.")));
        var (runner, disposer, _) = Rig(noop, Tagger("convert:webp", "c"));

        var result = await runner.RunAsync(new Recipe("r", [new("img.strip-gps"), new("convert:webp")]), [@"C:\x\p.jpg"]);

        Assert.Equal([@"C:\x\p.jpg.c"], result.Outputs);
        Assert.Equal("No location data was found.", result.Note);
        Assert.Empty(disposer.Disposed);
    }

    [Fact]
    public async Task A_failing_step_stops_the_chain_and_names_the_step()
    {
        var failing = new FakeConverter(["img.resize"], run: (_, _, _) => Task.FromResult(ConversionResult.Fail("disk full")));
        var ranLast = false;
        var last = new FakeConverter(["convert:webp"], run: (j, _, _) => { ranLast = true; return Task.FromResult(ConversionResult.Ok([])); });
        var (runner, _, _) = Rig(Tagger("img.strip-gps", "a"), failing, last);

        var result = await runner.RunAsync(new Recipe("r", [new("img.strip-gps"), new("img.resize"), new("convert:webp")]), [@"C:\x\p.jpg"]);

        Assert.False(result.Success);
        Assert.Contains("Step 2 (img.resize)", result.Error);
        Assert.Contains("disk full", result.Error);
        Assert.False(ranLast);
    }

    [Fact]
    public async Task Options_and_the_output_folder_reach_each_step()
    {
        var seen = new List<ConversionJob>();
        var capture = new FakeConverter(["img.resize", "convert:webp"], run: (job, _, _) => { lock (seen) seen.Add(job); return Task.FromResult(ConversionResult.Ok([job.Inputs[0] + ".x"])); });
        var (runner, _, _) = Rig(capture);

        await runner.RunAsync(new Recipe("r", [new("img.resize", new Dictionary<string, object?> { ["width"] = 800 }), new("convert:webp")]), [@"C:\x\p.jpg"], outputDirectory: @"D:\out");

        Assert.Equal(800, seen[0].GetInt("width"));
        Assert.All(seen, j => Assert.Equal(@"D:\out", j.OutputDirectory));
    }

    [Fact]
    public async Task Several_files_flow_through_the_chain_together()
    {
        var (runner, _, _) = Rig(Tagger("img.resize", "r"), Tagger("convert:webp", "w"));
        var result = await runner.RunAsync(new Recipe("r", [new("img.resize"), new("convert:webp")]), [@"C:\a.jpg", @"C:\b.jpg"]);
        Assert.Equal([@"C:\a.jpg.r.w", @"C:\b.jpg.r.w"], result.Outputs.OrderBy(x => x));
    }

    [Fact]
    public async Task An_empty_recipe_is_rejected()
    {
        var (runner, _, _) = Rig(Tagger("img.resize", "r"));
        Assert.False((await runner.RunAsync(new Recipe("empty", []), [@"C:\a.jpg"])).Success);
    }

    [Fact]
    public async Task Cancelling_stops_before_the_next_step()
    {
        using var cts = new CancellationTokenSource();
        var second = false;
        var first = new FakeConverter(["img.resize"], run: (j, _, _) => { cts.Cancel(); return Task.FromResult(ConversionResult.Ok([j.Inputs[0] + ".1"])); });
        var next = new FakeConverter(["convert:webp"], run: (j, _, _) => { second = true; return Task.FromResult(ConversionResult.Ok([])); });
        var (runner, _, _) = Rig(first, next);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunAsync(new Recipe("r", [new("img.resize"), new("convert:webp")]), [@"C:\a.jpg"], cancellationToken: cts.Token));
        Assert.False(second);
    }
}

public class RecipeStoreTests
{
    [Fact]
    public void Recipes_round_trip_with_options_as_plain_values()
    {
        using var dir = new TempDir();
        var store = new RecipeStore(Path.Combine(dir.Path, "recipes.json"));
        store.Save([new Recipe("mine", [new("img.resize", new Dictionary<string, object?> { ["width"] = 1280, ["keepAspect"] = true, ["mode"] = "fit" })], Description: "d")]);

        var loaded = store.Load().Single();
        var opts = loaded.Steps[0].Options!;
        Assert.Equal(1280d, opts["width"]);
        Assert.Equal(true, opts["keepAspect"]);
        Assert.Equal("fit", opts["mode"]);
        var job = new ConversionJob(["a.jpg"], "img.resize", opts);
        Assert.Equal(1280, job.GetInt("width"));            // converters can read them
        Assert.True(job.GetBool("keepAspect"));
    }

    [Fact]
    public void A_missing_or_corrupt_file_gives_no_recipes()
    {
        using var dir = new TempDir();
        Assert.Empty(new RecipeStore(Path.Combine(dir.Path, "none.json")).Load());
        Assert.Empty(new RecipeStore(dir.File("bad.json", "{ nope")).Load());
    }

    [Fact]
    public void Built_in_recipes_are_offered_only_when_every_step_exists()
    {
        var onlyImages = new ToolRegistry().Register(new FakeConverter(["img.strip-gps", "img.resize", "convert:webp", "convert:jpg", "img.compress"]));
        var names = BuiltInRecipes.Available(onlyImages).Select(r => r.Name).ToList();

        Assert.Contains("web-photo", names);
        Assert.Contains("heic-to-jpg", names);
        Assert.DoesNotContain("pdf-light", names);
    }

    [Fact]
    public void Every_built_in_step_refers_to_a_real_catalog_operation()
    {
        var catalog = ToolCatalog.LoadEmbedded();
        foreach (var step in BuiltInRecipes.All.SelectMany(r => r.Steps))
            Assert.True(Operation.IsConvert(step.Operation) || catalog.Tool(step.Operation) is not null, step.Operation);
    }
}

public class WatchFilterTests
{
    private static WatchRule Rule(string[]? ext = null, bool sub = false, bool enabled = true) =>
        new("r", P(@"C:\Downloads"), ext ?? ["heic"], "heic-to-jpg", enabled, sub, "DropFlow");

    [Theory]
    [InlineData(@"C:\Downloads\IMG_001.HEIC", true)]
    [InlineData(@"C:\Downloads\img.heif", true)]               // alias of heic
    [InlineData(@"C:\Downloads\photo.jpg", false)]
    [InlineData(@"C:\Downloads\IMG.heic.crdownload", false)]   // still downloading
    [InlineData(@"C:\Downloads\~$lock.heic", false)]
    [InlineData(@"C:\Downloads\.hidden.heic", false)]
    [InlineData(@"C:\Downloads\sub\a.heic", false)]            // subfolders off
    [InlineData(@"C:\Downloads\DropFlow\a.heic", false)]       // our own output folder
    [InlineData(@"C:\Elsewhere\a.heic", false)]
    [InlineData(@"C:\DownloadsExtra\a.heic", false)]           // prefix-but-different folder
    public void Which_files_a_rule_acts_on(string path, bool expected) =>
        Assert.Equal(expected, WatchFilter.Matches(Rule(), P(path)));

    [Fact]
    public void Subfolders_can_be_included_but_the_output_folder_never_is()
    {
        var r = Rule(sub: true);
        Assert.True(WatchFilter.Matches(r, P(@"C:\Downloads\sub\a.heic")));
        Assert.False(WatchFilter.Matches(r, P(@"C:\Downloads\DropFlow\a.heic")));
        Assert.False(WatchFilter.Matches(r, P(@"C:\Downloads\dropflow\deep\a.heic")));      // case-insensitive
    }

    [Fact]
    public void Disabled_rules_and_already_produced_files_are_skipped_and_no_extension_list_means_everything()
    {
        Assert.False(WatchFilter.Matches(Rule(enabled: false), P(@"C:\Downloads\a.heic")));
        Assert.False(WatchFilter.Matches(Rule(), P(@"C:\Downloads\a.heic"), new HashSet<string> { P(@"C:\Downloads\a.heic") }));
        Assert.True(WatchFilter.Matches(Rule(ext: []), P(@"C:\Downloads\anything.xyz")));
    }
}

public class WatchServiceTests
{
    private static TimeSpan Wait => TimeSpan.FromSeconds(8);

    private static WatchRule RuleFor(string folder, string[]? ext = null) => new("w", folder, ext ?? ["txt"], "r");

    [Fact]
    public async Task A_new_matching_file_is_reported_once_after_it_stops_changing()
    {
        using var dir = new TempDir();
        var got = new TaskCompletionSource<string>();
        var calls = 0;
        using var service = new WatchService((rule, files) => { Interlocked.Increment(ref calls); got.TrySetResult(files[0]); return Task.CompletedTask; }, poll: TimeSpan.FromMilliseconds(80));
        service.Apply([RuleFor(dir.Path)]);

        var file = Path.Combine(dir.Path, "note.txt");
        await File.WriteAllTextAsync(file, "hello");

        Assert.Equal(file, await got.Task.WaitAsync(Wait));
        await Task.Delay(500);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task A_file_that_is_still_growing_is_not_reported_until_it_is_finished()
    {
        using var dir = new TempDir();
        var got = new TaskCompletionSource<long>();
        using var service = new WatchService((_, files) => { got.TrySetResult(new FileInfo(files[0]).Length); return Task.CompletedTask; }, poll: TimeSpan.FromMilliseconds(120));
        service.Apply([RuleFor(dir.Path)]);

        var file = Path.Combine(dir.Path, "big.txt");
        await using (var s = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.Read))
        {
            for (var i = 0; i < 6; i++)
            {
                await s.WriteAsync(new byte[1000]);
                await s.FlushAsync();
                await Task.Delay(100);
                Assert.False(got.Task.IsCompleted, "reported while the file was still being written");
            }
        }
        Assert.Equal(6000, await got.Task.WaitAsync(Wait));
    }

    [Fact]
    public async Task A_download_is_reported_when_the_partial_file_is_renamed_to_its_real_name()
    {
        using var dir = new TempDir();
        var got = new TaskCompletionSource<string>();
        using var service = new WatchService((_, files) => { got.TrySetResult(Path.GetFileName(files[0])); return Task.CompletedTask; }, poll: TimeSpan.FromMilliseconds(80));
        service.Apply([RuleFor(dir.Path)]);

        var partial = Path.Combine(dir.Path, "doc.txt.crdownload");
        await File.WriteAllTextAsync(partial, "data");
        await Task.Delay(400);
        Assert.False(got.Task.IsCompleted);
        File.Move(partial, Path.Combine(dir.Path, "doc.txt"));

        Assert.Equal("doc.txt", await got.Task.WaitAsync(Wait));
    }

    [Fact]
    public async Task Files_of_other_types_and_produced_outputs_are_ignored()
    {
        using var dir = new TempDir();
        var seen = new List<string>();
        using var service = new WatchService((_, files) => { lock (seen) seen.AddRange(files); return Task.CompletedTask; }, poll: TimeSpan.FromMilliseconds(80));
        service.Apply([RuleFor(dir.Path)]);

        var produced = Path.Combine(dir.Path, "ours.txt");
        service.MarkProduced([produced]);
        await File.WriteAllTextAsync(produced, "x");
        await File.WriteAllTextAsync(Path.Combine(dir.Path, "image.png"), "x");
        await File.WriteAllTextAsync(Path.Combine(dir.Path, "wanted.txt"), "x");

        await Task.Delay(1500);
        Assert.Equal([Path.Combine(dir.Path, "wanted.txt")], seen);
    }

    [Fact]
    public void Disabled_rules_and_missing_folders_do_not_throw_and_watch_nothing()
    {
        using var service = new WatchService((_, _) => Task.CompletedTask);
        service.Apply([RuleFor(@"C:\no\such\folder"), RuleFor(Path.GetTempPath()) with { Enabled = false }]);
    }

    [Fact]
    public void Watch_rules_persist()
    {
        using var dir = new TempDir();
        var store = new WatchStore(Path.Combine(dir.Path, "watch.json"));
        store.Save([new WatchRule("1", @"C:\Downloads", ["heic", "png"], "heic-to-jpg", true, true, "Out")]);
        var r = store.Load().Single();
        Assert.Equal(("1", @"C:\Downloads", "heic-to-jpg", true, "Out"), (r.Id, r.Folder, r.Recipe, r.IncludeSubfolders, r.OutputSubfolder));
        Assert.Equal(["heic", "png"], r.Extensions);
    }
}
