using DropFlow.Core.App;
using DropFlow.Core.Catalog;
using DropFlow.Core.Operations;
using DropFlow.Core.Queue;
using DropFlow.Core.Registry;
using DropFlow.Core.Runner;
using DropFlow.Core.Session;
using DropFlow.Core.Wheel;

namespace DropFlow.Core.Tests;

public class OperationRunnerTests
{
    private static readonly ToolCatalog Catalog = ToolCatalog.LoadEmbedded();

    private sealed class Prompts : IUserPrompts
    {
        public IReadOnlyDictionary<string, object?>? OptionsAnswer { get; set; } = new Dictionary<string, object?> { ["quality"] = 80 };
        public bool InstallAnswer { get; set; } = true;
        public List<string> Log { get; } = [];
        public Task<IReadOnlyDictionary<string, object?>?> AskOptionsAsync(string operation, string title, IReadOnlyList<ToolOption> options) { Log.Add($"options {operation} '{title}' {options.Count}"); return Task.FromResult(OptionsAnswer); }
        public Task<bool> ConfirmEngineInstallAsync(EngineId engine, int approxSizeMb) { Log.Add($"confirm {engine} {approxSizeMb}MB"); return Task.FromResult(InstallAnswer); }
        public Task ShowErrorAsync(string message) { Log.Add("error " + message); return Task.CompletedTask; }
    }

    private sealed class Installer(Action<EngineId>? onInstall = null, bool result = true) : IEngineInstallService
    {
        public List<EngineId> Installed { get; } = [];
        public Task<bool> InstallAsync(EngineId engine, IProgress<string> log, CancellationToken ct) { Installed.Add(engine); onInstall?.Invoke(engine); return Task.FromResult(result); }
    }

    /// <summary>Engines that "become installed" when the installer runs.</summary>
    private sealed class MutableEngines : IEngineProvider
    {
        private readonly HashSet<EngineId> _installed = [];
        public void Install(EngineId e) => _installed.Add(e);
        public string? Locate(EngineId engine) => _installed.Contains(engine) ? "C:\\x.exe" : null;
    }

    private static (OperationRunner Runner, Prompts Prompts, Installer Installer, ToolRegistry Registry, JobQueue Queue, MutableEngines Engines) Rig(
        IConverter converter, bool installWorks = true)
    {
        var registry = new ToolRegistry().Register(converter);
        var queue = new JobQueue(registry, Catalog);
        var engines = new MutableEngines();
        var installer = new Installer(e => { if (installWorks) engines.Install(e); }, installWorks);
        var prompts = new Prompts();
        var runner = new OperationRunner(registry, queue, engines, installer, prompts, _ => 100, op => "Title:" + op);
        return (runner, prompts, installer, registry, queue, engines);
    }

    private static WheelSelection Sel(string op, params string[] files) =>
        new(WheelKind.Convert, new Wedge(op, WheelKind.Convert, "X", op), files);

    [Fact]
    public async Task A_ready_operation_with_no_options_is_enqueued_straight_away()
    {
        var (runner, prompts, installer, _, _, _) = Rig(new FakeConverter(["convert:png"]));
        var batch = await runner.RunAsync(Sel("convert:png", @"C:\a.heic"));

        Assert.NotNull(batch);
        Assert.Empty(prompts.Log);
        Assert.Empty(installer.Installed);
        Assert.True((await batch!.Completion.WaitAsync(TimeSpan.FromSeconds(5))).Success);
    }

    [Fact]
    public async Task Options_are_asked_for_and_travel_with_the_job()
    {
        ConversionJob? seen = null;
        var converter = new FakeConverter(["img.compress"], run: (job, _, _) => { seen = job; return Task.FromResult(ConversionResult.Ok([])); },
            options: [new ToolOption("quality", ToolOptionKind.Number, 70d)]);
        var (runner, prompts, _, _, _, _) = Rig(converter);

        var batch = await runner.RunAsync(Sel("img.compress", @"C:\a.png"));
        await batch!.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(["options img.compress 'Title:img.compress' 1"], prompts.Log);
        Assert.Equal(80, seen!.GetInt("quality"));
    }

    [Fact]
    public async Task Cancelling_the_options_form_starts_nothing()
    {
        var converter = new FakeConverter(["img.compress"], options: [new ToolOption("quality", ToolOptionKind.Number, 70d)]);
        var (runner, prompts, _, _, queue, _) = Rig(converter);
        prompts.OptionsAnswer = null;

        Assert.Null(await runner.RunAsync(Sel("img.compress", @"C:\a.png")));
        Assert.Empty(queue.Batches);
    }

    [Fact]
    public async Task A_missing_engine_asks_first_installs_and_then_runs()
    {
        var (runner, prompts, installer, _, _, _) = Rig(new FakeConverter(["convert:mp4"], engine: EngineId.Ffmpeg));

        var batch = await runner.RunAsync(Sel("convert:mp4", @"C:\a.mov"));

        Assert.Equal(["confirm Ffmpeg 100MB"], prompts.Log);
        Assert.Equal([EngineId.Ffmpeg], installer.Installed);
        Assert.NotNull(batch);
    }

    [Fact]
    public async Task Declining_the_install_starts_nothing_and_installs_nothing()
    {
        var (runner, prompts, installer, _, queue, _) = Rig(new FakeConverter(["convert:mp4"], engine: EngineId.Ffmpeg));
        prompts.InstallAnswer = false;

        Assert.Null(await runner.RunAsync(Sel("convert:mp4", @"C:\a.mov")));
        Assert.Empty(installer.Installed);
        Assert.Empty(queue.Batches);
    }

    [Fact]
    public async Task A_failed_install_reports_an_error_and_starts_nothing()
    {
        var (runner, prompts, _, _, queue, _) = Rig(new FakeConverter(["convert:mp4"], engine: EngineId.Ffmpeg), installWorks: false);

        Assert.Null(await runner.RunAsync(Sel("convert:mp4", @"C:\a.mov")));
        Assert.Contains(prompts.Log, l => l.StartsWith("error") && l.Contains("Ffmpeg"));
        Assert.Empty(queue.Batches);
    }

    [Fact]
    public async Task An_unsupported_file_is_reported_not_thrown()
    {
        var (runner, prompts, _, _, _, _) = Rig(new FakeConverter(["convert:png"], supports: (_, ext) => ext == "heic"));
        Assert.Null(await runner.RunAsync(Sel("convert:png", @"C:\a.xyz")));
        Assert.Single(prompts.Log, l => l.StartsWith("error"));
    }
}

public class AppLogicTests
{
    [Theory]
    [InlineData("ar", "en-US", "ar")]
    [InlineData("en", "ar-EG", "en")]
    [InlineData("system", "ar-SA", "ar")]
    [InlineData("system", "en-GB", "en")]
    [InlineData("system", "fr-FR", "en")]
    [InlineData("", "ar-EG", "ar")]
    [InlineData("klingon", "en-US", "en")]
    public void Language_resolution(string setting, string system, string expected) =>
        Assert.Equal(expected, LanguageResolver.Resolve(setting, system));

    [Fact]
    public void Bare_paths_open_the_wheel_like_send_to_does()
    {
        var c = AppCommandLine.Parse([@"C:\a b\x.png", @"C:\y.png"]);
        Assert.Equal(AppCommandKind.Open, c.Kind);
        Assert.Equal([@"C:\a b\x.png", @"C:\y.png"], c.Paths);
    }

    [Fact]
    public void Switches_select_the_command_and_wheel()
    {
        Assert.Equal(AppCommandKind.Start, AppCommandLine.Parse([]).Kind);
        Assert.True(AppCommandLine.Parse(["--minimized"]).Minimized);
        Assert.Equal(AppCommandKind.Settings, AppCommandLine.Parse(["--settings"]).Kind);
        Assert.Equal(AppCommandKind.Shelf, AppCommandLine.Parse(["--shelf"]).Kind);
        Assert.Equal(AppCommandKind.Quit, AppCommandLine.Parse(["--QUIT"]).Kind);
        var tools = AppCommandLine.Parse(["--tools", @"C:\f.pdf"]);
        Assert.Equal((AppCommandKind.Open, "tools"), (tools.Kind, tools.Wheel));
        Assert.Equal([@"C:\f.pdf"], tools.Paths);
    }

    [Fact]
    public void Unknown_switches_are_ignored_not_treated_as_paths()
    {
        var c = AppCommandLine.Parse(["--surprise", @"C:\a.png"]);
        Assert.Equal([@"C:\a.png"], c.Paths);
    }

    [Fact]
    public void Build_and_parse_round_trip_for_forwarding_to_the_running_instance()
    {
        var original = new AppCommand(AppCommandKind.Open, [@"C:\a b.png", @"D:\c.pdf"]);
        var parsed = AppCommandLine.Parse(AppCommandLine.Build(original));
        Assert.Equal(original.Kind, parsed.Kind);
        Assert.Equal(original.Paths, parsed.Paths);
    }

    [Fact]
    public void Copy_path_gives_one_full_path_per_line_optionally_quoted()
    {
        var text = FileActionPlans.PathLines([@"C:\a\b.txt", @"C:\c d\e.txt"]);
        Assert.Equal($"C:\\a\\b.txt{Environment.NewLine}C:\\c d\\e.txt", text);
        Assert.Equal("\"C:\\c d\\e.txt\"", FileActionPlans.PathLines([@"C:\c d\e.txt"], quote: true));
    }

    [Fact]
    public void Move_never_overwrites_and_numbers_clashes_even_between_the_moved_files_themselves()
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"D:\out\a.txt" };
        var steps = FileActionPlans.PlanMove([@"C:\x\a.txt", @"C:\y\a.txt", @"C:\x\b.txt"], @"D:\out", existing.Contains);

        Assert.Equal([@"D:\out\a (2).txt", @"D:\out\a (3).txt", @"D:\out\b.txt"], steps.Select(s => s.To));
        Assert.Equal([@"C:\x\a.txt", @"C:\y\a.txt", @"C:\x\b.txt"], steps.Select(s => s.From));
    }

    [Fact]
    public void Terminal_opens_the_folder_or_the_files_folder()
    {
        Assert.Equal(@"C:\docs", FileActionPlans.TerminalFolder(@"C:\docs\file.txt", _ => false));
        Assert.Equal(@"C:\docs", FileActionPlans.TerminalFolder(@"C:\docs", p => p == @"C:\docs"));
    }

    [Fact]
    public void Terminal_prefers_windows_terminal_and_escapes_quotes_for_the_powershell_fallback()
    {
        var wt = FileActionPlans.Terminal(@"C:\my docs", true);
        Assert.Equal("wt.exe", wt.FileName);
        Assert.Equal(["-d", @"C:\my docs"], wt.Arguments);

        var ps = FileActionPlans.Terminal(@"C:\it's here", false);
        Assert.Equal("powershell.exe", ps.FileName);
        Assert.Contains("'C:\\it''s here'", ps.Arguments[^1]);
    }

    [Fact]
    public void The_context_menu_plan_covers_files_and_folders_with_a_quoted_command()
    {
        var plan = ShellIntegrationPlan.ContextMenu(@"C:\Program Files\DropFlow\DropFlow.exe", "Open with DropFlow");

        Assert.Equal(6, plan.Count);
        Assert.Contains(plan, e => e.KeyPath == @"Software\Classes\*\shell\DropFlow" && e.ValueName is null && e.Value == "Open with DropFlow");
        Assert.Contains(plan, e => e.KeyPath == @"Software\Classes\Directory\shell\DropFlow\command"
            && e.Value == "\"C:\\Program Files\\DropFlow\\DropFlow.exe\" --open \"%1\"");
        Assert.All(plan, e => Assert.StartsWith(@"Software\Classes", e.KeyPath));      // HKCU only: no admin needed
    }

    [Fact]
    public void The_startup_entry_launches_minimised_with_a_quoted_path()
    {
        var e = ShellIntegrationPlan.Startup(@"C:\Program Files\DropFlow\DropFlow.exe");
        Assert.Equal(ShellIntegrationPlan.StartupKey, e.KeyPath);
        Assert.Equal("\"C:\\Program Files\\DropFlow\\DropFlow.exe\" --minimized", e.Value);
    }

    [Fact]
    public void Rapid_requests_are_merged_into_one_batch_without_duplicates()
    {
        var scheduler = new ManualScheduler();
        var batches = new List<IReadOnlyList<string>>();
        var batcher = new RequestBatcher(scheduler, batches.Add);

        batcher.Add([@"C:\a.png"]);
        batcher.Add([@"C:\b.png"]);
        batcher.Add([@"C:\A.PNG"]);                 // same file, different case
        Assert.Empty(batches);
        Assert.Equal(1, scheduler.Pending);          // only the latest timer is alive

        scheduler.RunAll();
        Assert.Equal([[@"C:\a.png", @"C:\b.png"]], batches);

        batcher.Add([@"C:\c.png"]);
        scheduler.RunAll();
        Assert.Equal(2, batches.Count);
    }
}
