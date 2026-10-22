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
        IConverter converter, bool installWorks = true, IOptionsEditor? editor = null)
    {
        var registry = new ToolRegistry().Register(converter);
        var queue = new JobQueue(registry, Catalog);
        var engines = new MutableEngines();
        var installer = new Installer(e => { if (installWorks) engines.Install(e); }, installWorks);
        var prompts = new Prompts();
        var runner = new OperationRunner(registry, queue, engines, installer, prompts, _ => 100, op => "Title:" + op, editor);
        return (runner, prompts, installer, registry, queue, engines);
    }

    /// <summary>A dedicated editor window for one operation (a trim timeline, say) that answers with option values.</summary>
    private sealed class Editor(string operation, IReadOnlyDictionary<string, object?>? answer, Prompts? log = null) : IOptionsEditor
    {
        public List<string> Opened { get; } = [];
        public bool CanEdit(string op, IReadOnlyList<string> files) => op == operation;
        public Task<IReadOnlyDictionary<string, object?>?> EditAsync(string op, string title, IReadOnlyList<string> files, IReadOnlyList<ToolOption> options)
        {
            Opened.Add($"{op} '{title}' {string.Join("|", files)} {options.Count}");
            log?.Log.Add("editor " + op);
            return Task.FromResult(answer);
        }
    }

    [Fact]
    public async Task A_dedicated_editor_replaces_the_options_form_and_its_values_travel_with_the_job()
    {
        ConversionJob? seen = null;
        var converter = new FakeConverter(["vid.trim"], run: (job, _, _) => { seen = job; return Task.FromResult(ConversionResult.Ok([])); },
            options: [new ToolOption("start", ToolOptionKind.Text, "0"), new ToolOption("end", ToolOptionKind.Text, "")]);
        var editor = new Editor("vid.trim", new Dictionary<string, object?> { ["start"] = "1.5", ["end"] = "4" });
        var (runner, prompts, _, _, _, _) = Rig(converter, editor: editor);

        var batch = await runner.RunAsync(Sel("vid.trim", @"C:\a.mp4"));
        await batch!.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal([@"vid.trim 'Title:vid.trim' C:\a.mp4 2"], editor.Opened);
        Assert.DoesNotContain(prompts.Log, l => l.StartsWith("options"));
        Assert.Equal("1.5", seen!.GetString("start"));
        Assert.Equal("4", seen.GetString("end"));
    }

    [Fact]
    public async Task Closing_the_editor_without_running_starts_nothing()
    {
        var converter = new FakeConverter(["vid.trim"], options: [new ToolOption("start", ToolOptionKind.Text, "0")]);
        var (runner, _, _, _, queue, _) = Rig(converter, editor: new Editor("vid.trim", null));

        Assert.Null(await runner.RunAsync(Sel("vid.trim", @"C:\a.mp4")));
        Assert.Empty(queue.Batches);
    }

    [Fact]
    public async Task The_engine_is_installed_before_the_editor_opens_because_its_previews_need_it()
    {
        var converter = new FakeConverter(["vid.trim"], engine: EngineId.Ffmpeg, options: [new ToolOption("start", ToolOptionKind.Text, "0")]);
        var prompts = new Prompts();
        var registry = new ToolRegistry().Register(converter);
        var queue = new JobQueue(registry, Catalog);
        var engines = new MutableEngines();
        var editor = new Editor("vid.trim", new Dictionary<string, object?> { ["start"] = "2" }, prompts);
        var runner = new OperationRunner(registry, queue, engines, new Installer(engines.Install), prompts, _ => 100, op => op, editor);

        Assert.NotNull(await runner.RunAsync(Sel("vid.trim", @"C:\a.mp4")));
        Assert.Equal(["confirm Ffmpeg 100MB", "editor vid.trim"], prompts.Log);

        // declined: the editor never opens
        var declinedPrompts = new Prompts { InstallAnswer = false };
        var declinedEditor = new Editor("vid.trim", new Dictionary<string, object?>(), declinedPrompts);
        var declined = new OperationRunner(registry, queue, new MutableEngines(), new Installer(), declinedPrompts, _ => 100, op => op, declinedEditor);
        Assert.Null(await declined.RunAsync(Sel("vid.trim", @"C:\a.mp4")));
        Assert.Empty(declinedEditor.Opened);
    }

    [Fact]
    public async Task Operations_without_an_editor_still_use_the_form()
    {
        var converter = new FakeConverter(["img.compress"], options: [new ToolOption("quality", ToolOptionKind.Number, 70d)]);
        var editor = new Editor("vid.trim", new Dictionary<string, object?>());
        var (runner, prompts, _, _, _, _) = Rig(converter, editor: editor);

        Assert.NotNull(await runner.RunAsync(Sel("img.compress", @"C:\a.png")));
        Assert.Equal(["options img.compress 'Title:img.compress' 1"], prompts.Log);
        Assert.Empty(editor.Opened);
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

    /// <summary>Like PDF compression: the "extreme" level needs no engine, the others do.</summary>
    private sealed class OptionDependentEngine : IConverter
    {
        public IReadOnlyCollection<string> Operations => ["pdf.compress"];
        public bool Supports(string operation, string inputExtension) => operation == "pdf.compress";
        public EngineId EngineFor(string operation) => EngineId.Qpdf;
        public EngineId EngineFor(string operation, IReadOnlyDictionary<string, object?> options) =>
            options.TryGetValue("level", out var level) && Equals(level, "extreme") ? EngineId.None : EngineId.Qpdf;
        public IReadOnlyList<ToolOption> OptionsFor(string operation) => [new ToolOption("level", ToolOptionKind.Choice, "recommended", ["less", "recommended", "extreme"])];
        public Task<ConversionResult> RunAsync(ConversionJob job, IProgress<JobProgress> progress, CancellationToken cancellationToken) =>
            Task.FromResult(ConversionResult.Ok([job.Inputs[0] + ".out"]));
    }

    [Fact]
    public async Task A_choice_that_needs_no_engine_does_not_ask_to_install_one()
    {
        var (runner, prompts, installer, _, _, _) = Rig(new OptionDependentEngine());
        prompts.OptionsAnswer = new Dictionary<string, object?> { ["level"] = "extreme" };

        var batch = await runner.RunAsync(Sel("pdf.compress", @"C:\a.pdf"));

        Assert.NotNull(batch);
        Assert.Empty(installer.Installed);
        Assert.DoesNotContain(prompts.Log, l => l.StartsWith("confirm"));
    }

    [Fact]
    public async Task Options_are_asked_before_the_engine_install_so_the_choice_can_decide_it()
    {
        var (runner, prompts, installer, _, _, _) = Rig(new OptionDependentEngine());
        prompts.OptionsAnswer = new Dictionary<string, object?> { ["level"] = "recommended" };

        await runner.RunAsync(Sel("pdf.compress", @"C:\a.pdf"));

        Assert.Equal(["options pdf.compress 'Title:pdf.compress' 1", "confirm Qpdf 100MB"], prompts.Log);
        Assert.Equal([EngineId.Qpdf], installer.Installed);
    }

    private sealed class WindowTool(EngineId engine = EngineId.None) : IInteractiveTool
    {
        public List<string> Opened { get; } = [];
        public IReadOnlyCollection<string> Operations => ["vid.encoder"];
        public bool Supports(string operation, string inputExtension) => operation == "vid.encoder";
        public EngineId EngineFor(string operation) => engine;
        public IReadOnlyList<ToolOption> OptionsFor(string operation) => [new ToolOption("never", ToolOptionKind.Text)];
        public Task OpenAsync(string operation, IReadOnlyList<string> files) { Opened.Add(operation + ":" + string.Join("|", files)); return Task.CompletedTask; }
        public Task<ConversionResult> RunAsync(ConversionJob job, IProgress<JobProgress> progress, CancellationToken cancellationToken) =>
            Task.FromResult(ConversionResult.Fail("opens a window"));
    }

    [Fact]
    public async Task A_window_tool_is_opened_with_all_files_instead_of_being_queued_or_asking_for_options()
    {
        var tool = new WindowTool();
        var (runner, prompts, _, _, queue, _) = Rig(tool);

        var batch = await runner.RunAsync(Sel("vid.encoder", @"C:\a.mp4", @"C:\b.mkv"));

        Assert.Null(batch);
        Assert.Equal([@"vid.encoder:C:\a.mp4|C:\b.mkv"], tool.Opened);
        Assert.Empty(queue.Batches);
        Assert.Empty(prompts.Log);
    }

    [Fact]
    public async Task A_window_tool_that_needs_an_engine_installs_it_first_and_is_not_opened_when_declined()
    {
        var tool = new WindowTool(EngineId.Ffmpeg);
        var (runner, prompts, installer, _, _, _) = Rig(tool);

        await runner.RunAsync(Sel("vid.encoder", @"C:\a.mp4"));
        Assert.Equal(["confirm Ffmpeg 100MB"], prompts.Log);
        Assert.Equal([EngineId.Ffmpeg], installer.Installed);
        Assert.Single(tool.Opened);

        var declined = new WindowTool(EngineId.Ffmpeg);
        var (runner2, prompts2, installer2, _, _, _) = Rig(declined);
        prompts2.InstallAnswer = false;
        await runner2.RunAsync(Sel("vid.encoder", @"C:\a.mp4"));
        Assert.Empty(declined.Opened);
        Assert.Empty(installer2.Installed);
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

public class HotkeyParserTests
{
    [Theory]
    [InlineData("Ctrl+Alt+D", Hotkey.Ctrl | Hotkey.Alt, 'D')]
    [InlineData("ctrl + alt + d", Hotkey.Ctrl | Hotkey.Alt, 'D')]
    [InlineData("Shift+F9", Hotkey.Shift, 0x78)]
    [InlineData("Win+Space", Hotkey.Win, 0x20)]
    [InlineData("Control+5", Hotkey.Ctrl, '5')]
    [InlineData("Alt+Left", Hotkey.Alt, 0x25)]
    public void Valid_shortcuts_parse(string text, uint mods, int vk)
    {
        var h = HotkeyParser.Parse(text);
        Assert.NotNull(h);
        Assert.Equal(mods, h!.Value.Modifiers);
        Assert.Equal((uint)vk, h.Value.VirtualKey);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("D")]                  // needs a modifier
    [InlineData("Ctrl+Alt")]           // needs a key
    [InlineData("Ctrl+D+E")]           // one key only
    [InlineData("Ctrl+Ctrl+D")]
    [InlineData("Ctrl+Banana")]
    [InlineData("Ctrl+F25")]
    public void Unusable_shortcuts_are_rejected(string text) => Assert.Null(HotkeyParser.Parse(text));

    [Theory]
    [InlineData("Ctrl+Alt+D")]
    [InlineData("Shift+F9")]
    [InlineData("Win+Space")]
    [InlineData("Ctrl+Shift+Esc")]
    public void Format_round_trips(string text) => Assert.Equal(text, HotkeyParser.Format(HotkeyParser.Parse(text)!.Value));
}

public class SingleInstanceTests
{
    private static string UniqueName() => "DropFlowTest." + Guid.NewGuid().ToString("N");

    [Fact]
    public void The_first_acquirer_is_primary_and_the_second_is_not()
    {
        var name = UniqueName();
        using var first = SingleInstance.Acquire(name);
        using var second = SingleInstance.Acquire(name);

        Assert.True(first.IsPrimary);
        Assert.False(second.IsPrimary);
    }

    [Fact]
    public void After_the_primary_exits_a_new_launch_becomes_primary()
    {
        var name = UniqueName();
        var first = SingleInstance.Acquire(name);
        first.Dispose();
        using var next = SingleInstance.Acquire(name);
        Assert.True(next.IsPrimary);
    }

    [Fact]
    public async Task Arguments_with_spaces_and_arabic_are_forwarded_intact()
    {
        var name = UniqueName();
        using var primary = SingleInstance.Acquire(name);
        var received = new TaskCompletionSource<string[]>();
        primary.ArgumentsReceived += a => received.TrySetResult(a);
        primary.StartListening();

        using var secondary = SingleInstance.Acquire(name);
        var sent = new[] { "--open", @"C:\صور العائلة\a b.png", "x\"y" };
        Assert.True(secondary.Forward(sent, TimeSpan.FromSeconds(5)));

        Assert.Equal(sent, await received.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Several_launches_in_a_row_are_all_delivered_in_order()
    {
        var name = UniqueName();
        using var primary = SingleInstance.Acquire(name);
        var got = new List<string>();
        var done = new TaskCompletionSource();
        primary.ArgumentsReceived += a => { lock (got) { got.Add(a[0]); if (got.Count == 5) done.TrySetResult(); } };
        primary.StartListening();

        using var secondary = SingleInstance.Acquire(name);
        for (var i = 0; i < 5; i++) Assert.True(secondary.Forward([$"f{i}"], TimeSpan.FromSeconds(5)));

        await done.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(["f0", "f1", "f2", "f3", "f4"], got);
    }

    [Fact]
    public void Forwarding_with_nobody_listening_fails_fast_instead_of_hanging()
    {
        using var lonely = SingleInstance.Acquire(UniqueName());
        var started = DateTime.UtcNow;
        Assert.False(lonely.Forward(["x"], TimeSpan.FromMilliseconds(300)));
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(3));
    }

    [Fact]
    public void Only_the_primary_may_listen()
    {
        var name = UniqueName();
        using var first = SingleInstance.Acquire(name);
        using var second = SingleInstance.Acquire(name);
        Assert.Throws<InvalidOperationException>(() => second.StartListening());
    }
}
