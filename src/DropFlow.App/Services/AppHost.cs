using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DropFlow.AI;
using DropFlow.AI.Models;
using DropFlow.App.Views;
using DropFlow.App.Views.Editors;
using DropFlow.Core.Automation;
using DropFlow.Core.App;
using DropFlow.Core.Catalog;
using DropFlow.Core.History;
using DropFlow.Core.Localization;
using DropFlow.Core.Operations;
using DropFlow.Core.Queue;
using DropFlow.Core.Registry;
using DropFlow.Core.Runner;
using DropFlow.Core.Session;
using DropFlow.Core.Settings;
using DropFlow.Core.Sources;
using DropFlow.Core.Wheel;
using DropFlow.Engines.Archive.Sharing;
using DropFlow.Engines.Common;
using DropFlow.Hooks;
using DropFlow.Overlay;
using H.NotifyIcon;
using H.NotifyIcon.Core;

namespace DropFlow.App.Services;

/// <summary>Which converter modules the app loads. One line per engine project.</summary>
internal static class Modules
{
    /// <summary>
    /// One hidden browser (WebView2) serves two things: the "HTML to PDF" tool, and the PDF engine, which uses it to print HTML, text,
    /// Markdown, Word, spreadsheet and EPUB files to PDF and pictures — so those conversions need no LibreOffice and no download.
    /// </summary>
    public static IReadOnlyList<IConverter> Create(
        IEngineProvider engines, Dispatcher ui, string webProfileFolder, Func<IReadOnlyList<string>, Task> openEncoder, ModelManager models)
    {
        var renderer = Engines.Web.WebModule.CreateRenderer(ui, webProfileFolder);
        return
        [
            .. Engines.Web.WebModule.Create(renderer),
            .. Engines.Images.ImagesModule.Create(engines),
            .. Engines.Media.MediaModule.Create(engines),
            new EncoderTool(openEncoder),
            .. Engines.Pdf.PdfModule.Create(engines, renderer),
            .. Engines.Archive.ArchiveModule.Create(engines),
            .. Engines.Data.DataModule.Create(engines),
            .. Engines.Fonts.FontsModule.Create(engines),
            .. AI.AiModule.Create(engines, models),
        ];
    }
}

/// <summary>Maps the wheel session onto the WPF wheel window.</summary>
internal sealed class WheelPresenter(WheelWindow window, ILocalizer loc) : IWheelView
{
    public WheelSession? Session { get; set; }

    /// <summary>Set by the host just before opening the wheel for known files (command line, hotkey): the wheel then takes focus.</summary>
    public bool NextInteractive { get; set; }

    public void Show(WheelKind kind, int x, int y)
    {
        var interactive = NextInteractive;
        NextInteractive = false;
        window.ShowAt(new PixelPoint(x, y), interactive);
        if (interactive && Session is not null) window.SetFiles(Session.Files);
    }

    public void SetItems(WheelKind kind, IReadOnlyList<Wedge> wedges, int fileCount)
    {
        window.HideAddList(); // back from "+ Add" (no-op otherwise)
        var items = wedges.Select(ToItem).ToList();
        window.Wheel.SetItems(items, loc.IsRightToLeft);
        var hint = fileCount == 0 ? loc["wheel.drop"] : fileCount == 1 ? loc["wheel.file"] : loc.Format("wheel.files", fileCount);
        window.Wheel.SetCenter(items.Count == 0 && fileCount > 0 ? loc["wheel.nothing"] : loc["wheel." + Key(kind)], items.Count == 0 && fileCount > 0 ? "" : hint);
    }

    /// <summary>Face dots under the hub; turning over to the other face plays the flip.</summary>
    public void ShowFace(WheelFace face, bool hasBack)
    {
        var back = face == WheelFace.Back;
        var turned = window.Wheel.HasBack && hasBack && back != window.Wheel.ShowingBack;
        window.Wheel.SetFace(back, hasBack);
        if (turned) window.Wheel.PlayFlipAnimation();
        if (hasBack) window.Wheel.SetCenter(window.Wheel.CenterText, loc[back ? "wheel.face.back" : "wheel.face.front"]);
    }

    public void ShowAddList(WheelKind kind, IReadOnlyList<Wedge> addable)
    {
        var groups = Enum.GetValues<WheelKind>()
            .Select(k => new WheelItemGroup(loc["wheel.add.group." + Key(k)], addable.Where(w => w.Wheel == k).Select(ToItem).ToList()))
            .ToList();
        window.ShowAddList(loc["wheel.add.title." + Key(kind)], loc["wheel.add.hint"], loc["wheel.add.back"], groups, loc.IsRightToLeft);
    }

    public void Hide() => window.HideWheel();

    /// <summary>Right-click (or the Menu key) on a wedge: a pinned item offers "Remove from wheel"; other wedges have no menu.</summary>
    public void OnItemContext(int index)
    {
        if (Session is not { } session || !session.CanUnpin(index)) return;
        window.ShowItemMenu(loc["wheel.unpin"], loc.IsRightToLeft, () => session.Unpin(index));
    }

    private WheelItem ToItem(Wedge w) => new(
        w.Id,
        w.IsAdd ? loc["wheel.add"] : w.Label,
        w.State == WedgeState.NeedsEngine ? WheelItemState.NeedsEngine : WheelItemState.Ready,
        w.NeedsOptions,
        w.IsAdd ? WheelItemRole.Add : w.Pinned ? WheelItemRole.Pinned : WheelItemRole.Normal);

    private static string Key(WheelKind kind) => kind.ToString().ToLowerInvariant();
}

/// <summary>
/// The composition root and the application's behaviour: builds every service once, wires the drag hooks to the wheel session,
/// runs jobs, shows progress, and owns the tray icon. UI-thread affine except where noted.
/// </summary>
internal sealed class AppHost : IDisposable
{
    private readonly Dispatcher _ui;
    private readonly DispatcherScheduler _scheduler;
    private readonly DragDetector _detector = LowLevelInputHook.CreateSystemDetector();
    private readonly GlobalHotkey _hotkey = new(0xD0F1);
    private readonly GlobalHotkey _clipboardHotkey = new(0xD0F2);
    private readonly WatchStore _watchStore;
    private readonly RecipeStore _recipeStore;
    private readonly ModelDownloadService _modelDownloads;
    private readonly RedactDetection _redact;
    private readonly HashSet<BatchHandle> _modelRetries = [];
    private WatchService? _watch;
    private readonly RequestBatcher _requests;
    private readonly ShelfModel _shelf = new();
    private readonly HashSet<BatchHandle> _tracked = [];
    private readonly Dictionary<BatchHandle, DateTime> _lastToast = [];
    private LowLevelInputHook? _hook;
    private TaskbarIcon? _tray;
    private ToastWindow? _toast;
    private WheelWindow? _wheel;
    private FloatingShelfWindow? _shelfWindow;
    private SettingsWindow? _settingsWindow;
    private OpenLinkWindow? _linkWindow;
    private WelcomeWindow? _welcomeWindow;
    private UrlDownloader? _links;
    private Task<IReadOnlyList<string>>? _selectionTask;
    private readonly UpdateService _updates = new("https://github.com/hamwimoustafa88-maker/dropflow");

    public AppHost(Dispatcher ui)
    {
        _ui = ui;
        _scheduler = new DispatcherScheduler(ui);

        // v0.1.0 kept user data in the installer's folder, which the installer replaces and uninstall deletes: move it out first.
        var migrated = DropFlow.Core.App.DataFolder.MigrateOnce(DropFlow.Core.App.DataFolder.Legacy, AppPaths.DefaultDir);
        SettingsStore = new SettingsStore(new AppPaths(AppPaths.DefaultDir).SettingsFile);
        Settings = SettingsStore.Load();
        Paths = new AppPaths(Settings.DataDirectory ?? AppPaths.DefaultDir);
        FileLog.Init(Paths.LogDir);
        if (migrated.Count > 0) FileLog.Info("moved user data out of the install folder: " + string.Join(", ", migrated));
        FileLog.Info($"DropFlow {Version} starting");

        Loc = JsonLocalizer.Load(LanguageResolver.Resolve(Settings.Language, CultureInfo.CurrentUICulture.Name));
        Catalog = ToolCatalog.LoadEmbedded();
        Engines = new EngineLocator(Paths.DataDir);
        Installer = new EngineInstaller(Engines);
        Models = new ModelManager(Paths.ModelsDir);
        Registry = new ToolRegistry().Register(Modules.Create(Engines, ui, Path.Combine(Paths.DataDir, "webview"), OpenEncoder, Models));
        Registry.Register(new Core.Routing.RouteConverter(Registry, Catalog, Engines)); // chains (EPUB → PDF → PNG); after the direct converters
        _recipeStore = new RecipeStore(Path.Combine(Paths.DataDir, "recipes.json"));
        Registry.Register(new Core.Routing.PresetConverter(Registry, Catalog, Presets)); // "WhatsApp video", the user's saved options; last
        Core.Routing.RouteConverter.SweepLeftovers();
        Queue = new JobQueue(Registry, Catalog, Settings.MaxConcurrency) { Rules = () => Settings.Output };
        History = new HistoryStore(Paths.HistoryFile);
        _watchStore = new WatchStore(Path.Combine(Paths.DataDir, "watch.json"));
        Kit = new UiKit(WheelTheme.FromSystem().With(Settings.Accent), Loc);
        _modelDownloads = new ModelDownloadService(ui, Kit, Models);
        _redact = new RedactDetection(Models, Engines);
        Disposer = new RecycleBinDisposer();

        _requests = new RequestBatcher(_scheduler, files => _ui.BeginInvoke(() => OpenWheelFor(files, null)));
    }

    public SettingsStore SettingsStore { get; }
    public AppSettings Settings { get; private set; }
    public AppPaths Paths { get; }
    public JsonLocalizer Loc { get; }
    public ToolCatalog Catalog { get; }
    public EngineLocator Engines { get; }
    public EngineInstaller Installer { get; }
    public ModelManager Models { get; }
    public ToolRegistry Registry { get; }
    public JobQueue Queue { get; }
    public HistoryStore History { get; }
    public UiKit Kit { get; }
    public IFileDisposer Disposer { get; }

    public WheelSession Session { get; private set; } = null!;
    public WheelPresenter Presenter { get; private set; } = null!;
    private WheelBuilder Builder => _builder ??= new WheelBuilder(Catalog, Registry, Engines) { Presets = Presets };

    private (DateTime Stamp, IReadOnlyList<Recipe> List)? _presetCache;

    /// <summary>Built-in presets and the user's (recipes.json), re-read only when that file changes: the wheel asks often.</summary>
    private IReadOnlyList<Recipe> Presets()
    {
        var file = Path.Combine(Paths.DataDir, "recipes.json");
        var stamp = File.Exists(file) ? File.GetLastWriteTimeUtc(file) : DateTime.MinValue;
        if (_presetCache is { } c && c.Stamp == stamp) return c.List;
        var list = BuiltInRecipes.All.Concat(_recipeStore.Load()).Where(r => r.OnWheel).ToList();
        _presetCache = (stamp, list);
        return list;
    }
    private WheelBuilder? _builder;

    public static string Version => Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.1.0";
    private static string ExePath => Environment.ProcessPath ?? "DropFlow.exe";

    // ------------------------------------------------------------------------------------------------------------------ start

    public void Start()
    {
        _wheel = new WheelWindow();
        ApplyAppearance();
        _wheel.Prepare();
        Presenter = new WheelPresenter(_wheel, Loc);

        Session = new WheelSession(Builder, Presenter, _scheduler, () => Settings, () => Loc.Language, () => ActionService.SupportedIds, OnSelectedAsync,
            savePins: pins => Update(Settings with { PinnedItems = pins }));
        Presenter.Session = Session;

        _wheel.FilesEntered += (_, files) =>
        {
            _wheelGotFiles = files.Count > 0;
            Session.OnFilesEntered(files);
        };
        _wheel.ItemDropped += (_, e) => _ = Session.OnItemDroppedAsync(e.Files, e.Index);
        _wheel.ItemClicked += (_, index) => _ = Session.OnItemDroppedAsync(_wheel.Files.Count > 0 ? _wheel.Files : Session.Files, index);
        _wheel.DropCancelled += (_, _) => Session.Dismiss();
        _wheel.ItemContextRequested += (_, index) => Presenter.OnItemContext(index);
        _wheel.FlipRequested += (_, _) => Session.Flip();
        _wheel.AddItemPicked += (_, id) => _ = Session.OnAddPickedAsync(id);
        _wheel.AddListBack += (_, _) => Session.CloseAddList();
        _wheel.Shown += (_, _) =>
        {
            _welcomeWindow?.MarkWheelOpened();
            // During a drag the wheel window has no focus: arm the hook so Tab and the mouse wheel over it turn it over.
            if (!_wheel.IsInteractive && _hook is { } hook)
            {
                var (x, y, size) = _wheel.ScreenBounds;
                hook.Gestures.Enabled = Settings.WheelFlipGestures;
                hook.Gestures.Arm(new ScreenRect(x, y, size, size));
            }
        };
        _wheel.Hidden += (_, _) => _hook?.Gestures.Disarm();

        Queue.Changed += batch => _ui.BeginInvoke(() => OnBatchChanged(batch));
        Queue.BatchCompleted += (batch, result) => _ui.BeginInvoke(() => OnBatchCompleted(batch, result));

        CreateTray();
        StartHooks();
        RegisterHotkey();
        StartWatching();

        // Quietly look for an update shortly after start (installed copies only).
        if (Settings.CheckForUpdates)
            _ = Task.Delay(TimeSpan.FromSeconds(45)).ContinueWith(_ => _ui.InvokeAsync(() => CheckForUpdatesAsync(interactive: false)), TaskScheduler.Default);

        var firstRun = Settings.ShowFirstRunTips;
        if (firstRun)
        {
            // the guide replaces the old 8-second tip; the toast stays as the fallback when the window cannot open
            if (!ShowWelcome()) ShowToast(Loc["firstrun.title"], Loc["firstrun.text"], null, [], TimeSpan.FromSeconds(8));
            Update(Settings with { ShowFirstRunTips = false });
        }
        // after an update, once: what changed (a first install records the version without showing it)
        if (WhatsNew.ShouldShow(Version, Settings.WhatsNewSeen, firstRun)) ShowWhatsNew();
        if (Settings.WhatsNewSeen != Version) Update(Settings with { WhatsNewSeen = Version });
    }

    private void ShowWhatsNew()
    {
        var window = new WhatsNewWindow(Kit, Version, WhatsNew.Slides(Loc));
        window.Show();
        BringToFront(window); // after an update DropFlow starts in the background: without this the window sat behind the user's work
    }

    private void StartHooks()
    {
        if (Settings.Paused || _hook is not null) return;

        // Hook callbacks run on the hook thread: these handlers only post to the UI thread.
        _detector.DragStarted += OnDragStartedFromHook;
        _detector.KeyTapped += OnKeyTappedFromHook;
        _detector.DragEnded += OnDragEndedFromHook;
        _detector.DragCancelled += OnDragCancelledFromHook;
        _detector.TapRejected += OnTapRejectedFromHook;
        _detector.ConfirmFailed += OnConfirmFailedFromHook;
        // Only real file drags open the wheel (Settings.RequireFileDrag is the escape hatch); read on every tap, so a settings change applies at once.
        _detector.ConfirmFileDrag = () => !Settings.RequireFileDrag || _wheelGotFiles || ShellDragProbe.IsFileDragInProgress();
        try
        {
            _hook = new LowLevelInputHook(_detector);
            _hook.FlipRequested += () => _ui.BeginInvoke(() => Session.Flip()); // hook thread: only post
            _hook.Start();
            FileLog.Info("input hooks installed");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            FileLog.Error("could not install input hooks", ex);
            _hook = null;
            ShowToast(Loc["app.name"], ex.Message, null, [], TimeSpan.FromSeconds(8));
        }
    }

    private void StopHooks()
    {
        _detector.DragStarted -= OnDragStartedFromHook;
        _detector.KeyTapped -= OnKeyTappedFromHook;
        _detector.DragEnded -= OnDragEndedFromHook;
        _detector.DragCancelled -= OnDragCancelledFromHook;
        _detector.TapRejected -= OnTapRejectedFromHook;
        _detector.ConfirmFailed -= OnConfirmFailedFromHook;
        _detector.ConfirmFileDrag = null;
        _hook?.Dispose();
        _hook = null;
        Session?.Dismiss();
    }

    // ------------------------------------------------------------------------------------------------------------------ drag

    private void OnDragStartedFromHook(ScreenPoint p) =>
        _ui.BeginInvoke(() => _selectionTask = ExplorerSelection.ReadAsync(TimeSpan.FromMilliseconds(450)));

    private void OnKeyTappedFromHook(ModifierKey key, ScreenPoint p) =>
        _ui.BeginInvoke(() =>
        {
            // Settings → never open the wheel in these apps (a game, a drawing program that uses Shift+drag itself)
            if (!Session.IsVisible && Settings.ExcludedApps.Count > 0 && ForegroundApp.IsExcluded(ForegroundApp.Name(), Settings.ExcludedApps)) return;
            Session.OnKeyTapped(ToTrigger(key), p.X, p.Y);
            ApplyExplorerSelectionWhenReady();
        });

    private void OnDragEndedFromHook(ScreenPoint p)
    {
        _wheelGotFiles = false;
        _ui.BeginInvoke(Session.OnDragEnded);
    }

    private void OnDragCancelledFromHook()
    {
        _wheelGotFiles = false;
        _ui.BeginInvoke(Session.OnDragCancelled);
    }

    /// <summary>Set when OLE handed the dragged files to the wheel during this drag: proof it is a file drag. Read on the hook thread.</summary>
    private volatile bool _wheelGotFiles;

    private void OnTapRejectedFromHook(ModifierKey key)
    {
        var seen = ShellDragProbe.Describe();
        _ui.BeginInvoke(() => FileLog.Info($"{key} tap ignored: no file drag in progress ({seen}). If the wheel never opens, turn off \"RequireFileDrag\" in Settings."));
    }

    private void OnConfirmFailedFromHook(Exception ex) =>
        _ui.BeginInvoke(() => FileLog.Error("the file-drag check failed, so the tap was allowed", ex));

    private static TriggerKey ToTrigger(ModifierKey key) => key switch
    {
        ModifierKey.Shift => TriggerKey.Shift,
        ModifierKey.Ctrl => TriggerKey.Ctrl,
        _ => TriggerKey.Alt,
    };

    /// <summary>If the drag began in Explorer/desktop we already know the files: fill the wedges before the user reaches the wheel.</summary>
    private void ApplyExplorerSelectionWhenReady()
    {
        var task = _selectionTask;
        if (task is null) return;
        void Apply(IReadOnlyList<string> files)
        {
            if (files.Count > 0 && Session.IsVisible && Session.Files.Count == 0) Session.OnFilesEntered(files);
        }
        if (task.IsCompletedSuccessfully) Apply(task.Result);
        else task.ContinueWith(t => { if (t.IsCompletedSuccessfully) _ui.BeginInvoke(() => Apply(t.Result)); }, TaskScheduler.Default);
    }

    // ------------------------------------------------------------------------------------------------------------------ run

    private Task OnSelectedAsync(WheelSelection selection) =>
        _ui.InvokeAsync(async () =>
        {
            try
            {
                // A picture or link dragged from a browser: the wheel worked on a placeholder; fetch the real file now that a slice was picked.
                if (selection.Files.Any(f => WheelWindow.LinkFor(f) is not null))
                {
                    var real = new List<string>();
                    foreach (var f in selection.Files)
                    {
                        if (WheelWindow.LinkFor(f) is not { } link) { real.Add(f); continue; }
                        ShowToast(Loc["browser.downloading"], UrlDownloader.ForLog(link), null, [], null);
                        try { real.Add(await DownloadLinkAsync(link, new Progress<DownloadProgress>(p => ShowToast(Loc["browser.downloading"], p.FileName, p.Fraction, [], null)), CancellationToken.None)); }
                        catch (UrlDownloadException ex)
                        {
                            ShowToast(ex.Describe(Loc), null, null, [], TimeSpan.FromSeconds(8));
                            return;
                        }
                    }
                    _toast?.Hide();
                    selection = selection with { Files = real };
                }

                if (selection.Wedge.Wheel == WheelKind.Actions) // an action pinned to another wheel is still an action
                {
                    await CreateActions().ExecuteAsync(selection.Wedge.Id, selection.Files);
                    return;
                }
                var anchor = _wheel?.ScreenBounds;
                var started = DateTime.UtcNow;
                var batch = await new OperationRunnerFactory(this).Create().RunAsync(selection);
                if (batch is not null)
                {
                    _tracked.Add(batch);
                    // The wheel folds into a progress ring where it was, unless a dialog came first (then the toast is enough).
                    if (anchor is { } b && b.Size > 0 && DateTime.UtcNow - started < TimeSpan.FromSeconds(1.5) && _rings.Count < 3 && !batch.IsFinished)
                    {
                        var ring = new ProgressRingWindow(_wheel!.Wheel.Theme);
                        ring.ShowAt(new PixelPoint(b.X + b.Size / 2, b.Y + b.Size / 2));
                        _rings[batch] = (ring, DateTime.UtcNow);
                    }
                }
            }
            catch (Exception ex)
            {
                FileLog.Error("operation failed to start", ex);
                ShowToast(Loc.Format("result.failed", ex.Message), null, null, [], TimeSpan.FromSeconds(8));
            }
        }).Task.Unwrap();

    private ActionService CreateActions() =>
        new(Loc, message => { ShowToast(message, null, null, [], TimeSpan.FromSeconds(4)); return Task.CompletedTask; },
            _shelf, ShowShelf, async files =>
            {
                var ok = await ShareService.ShareAsync(new System.Windows.Interop.WindowInteropHelper(_wheel!).Handle, files);
                if (!ok) ShowToast(Loc["action.shareUnavailable"], null, null, [], TimeSpan.FromSeconds(5));
            }, SendToPhone);

    /// <summary>Starts a time-limited local share of the files and shows its QR code.</summary>
    private void SendToPhone(IReadOnlyList<string> files)
    {
        if (files.Any(Directory.Exists))
        {
            ShowToast(Loc["phone.folder"], null, null, [], TimeSpan.FromSeconds(6));
            return;
        }

        PhoneShareServer? server = null;
        try
        {
            server = new PhoneShareServer(files, new PhoneShareOptions
            {
                Lifetime = TimeSpan.FromMinutes(15),
                Strings = Loc.Language == "ar" ? PhoneShareStrings.Arabic : PhoneShareStrings.English,
            });
            server.Start();
            var window = new PhoneShareWindow(Kit, server);
            window.Show();
            window.Activate();
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            server?.Dispose();
            FileLog.Error("phone share", ex);
            ShowToast(Loc.Format("phone.failed", ex.Message), null, null, [], TimeSpan.FromSeconds(8));
        }
    }

    /// <summary>Builds the runner on demand so it always sees the current prompts/toast.</summary>
    private sealed class OperationRunnerFactory(AppHost host)
    {
        public OperationRunner Create() => new(
            host.Registry, host.Queue, host.Engines,
            new EngineInstallService(host._ui, host.Kit, host.Installer, host.Engines),
            new UserPrompts(host._ui, host.Loc, msg => { host.ShowToast(msg, null, null, [], TimeSpan.FromSeconds(8)); return Task.CompletedTask; },
                () => host.Settings.OfferEngineInstalls, host.SaveUserPreset),
            EngineInfo.ApproxSizeMb,
            op => host.Catalog.Tool(op)?.Name(host.Loc.Language) ?? op,
            // Dedicated editors (images, then timelines/boxes for media) instead of the generic form.
            new CompositeOptionsEditor(
                new ImageEditors(host.Kit, host._redact),
                new Views.Editors.Media.MediaEditors(host._ui, host.Kit, host.Engines,
                    msg => { host.ShowToast(msg, null, null, [], TimeSpan.FromSeconds(8)); return Task.CompletedTask; })));
    }

    /// <summary>Progress rings shown where the wheel was (at most three at once).</summary>
    private readonly Dictionary<BatchHandle, (ProgressRingWindow Ring, DateTime Shown)> _rings = [];

    /// <summary>While a ring shows a job, the "Working…" toast waits this long (Cancel stays reachable on long jobs).</summary>
    private static readonly TimeSpan RingOnly = TimeSpan.FromSeconds(4);

    private void OnBatchChanged(BatchHandle batch)
    {
        if (_rings.TryGetValue(batch, out var ring) && !batch.IsFinished)
        {
            ring.Ring.SetProgress(batch.Progress > 0 ? batch.Progress : null);
            if (DateTime.UtcNow - ring.Shown < RingOnly) return;
        }
        if (!_tracked.Contains(batch) || batch.IsFinished) return;
        if (_lastToast.TryGetValue(batch, out var last) && DateTime.UtcNow - last < TimeSpan.FromMilliseconds(120)) return;
        _lastToast[batch] = DateTime.UtcNow;

        var done = batch.Jobs.Count(j => j.State is JobState.Completed or JobState.Failed or JobState.Cancelled);
        var title = batch.Jobs.Count > 1 ? $"{Loc["result.working"]} {done}/{batch.Jobs.Count}" : Loc["result.working"];
        var detail = batch.Jobs.FirstOrDefault(j => j.State == JobState.Running)?.Message;
        ShowToast(title, detail ?? Path.GetFileName(batch.Job.Inputs[0]), batch.Progress, [(Loc["dialog.cancel"], batch.Cancel)], null);
    }

    private void OnBatchCompleted(BatchHandle batch, ConversionResult result)
    {
        if (_rings.Remove(batch, out var ring)) ring.Ring.Finish(result.Success);
        if (result.Success && Settings.ClipboardResults && batch.Job.Inputs.Count > 0 && batch.Job.Inputs.All(_fromClipboard.Contains)) PutOnClipboard(result.Outputs);
        if (!_tracked.Remove(batch)) return;
        _lastToast.Remove(batch);

        // An AI tool that needs a model: ask, download, and run the same job again (once).
        if (!result.Success && !_modelRetries.Contains(batch)
            && AiFailures.TryParseMissingModel(result.Error) is { } modelId && Models.Find(modelId) is not null)
        {
            _ = DownloadModelAndRetryAsync(batch, modelId);
            return;
        }
        _modelRetries.Remove(batch);

        var specs = batch.Job.Inputs.Count > 0 ? Registry.Find(batch.Job.Operation, ToolCatalog.ExtensionOf(batch.Job.Inputs[0]))?.OptionsFor(batch.Job.Operation) ?? [] : [];
        var entry = History.Add(batch.Job.Operation, batch.Job.Inputs, result, HistoryEntry.SafeOptions(specs, batch.Job.Options));

        if (!result.Success && result.Outputs.Count == 0)
        {
            FileLog.Error($"{batch.Job.Operation} failed: {result.Error}");
            ShowToast(Loc.Format("result.failed", NoteText.Localize(result.Error, Loc) ?? ""), null, null, [], TimeSpan.FromSeconds(10));
            return;
        }

        var actions = new List<(string, Action)>();
        if (result.Outputs.Count > 0)
        {
            actions.Add((Loc["result.open"], () => Explorer.Reveal(result.Outputs[0])));
            actions.Add((Loc["result.undo"], () => { History.Undo(entry.Id, Disposer); _toast?.Hide(); }));
        }
        var title = result.Success
            ? Loc.Format("result.done", Loc.Format("result.files", Math.Max(1, result.Outputs.Count)))
            : Loc.Format("result.failed", NoteText.Localize(result.Error, Loc) ?? "");
        ShowToast(title, NoteText.Localize(result.Note, Loc) ?? (result.Outputs.Count == 1 ? Path.GetFileName(result.Outputs[0]) : null), null, actions, TimeSpan.FromSeconds(9));
    }

    private async Task DownloadModelAndRetryAsync(BatchHandle failed, string modelId)
    {
        _toast?.Hide();
        var tool = Catalog.Tool(failed.Job.Operation)?.Name(Loc.Language) ?? failed.Job.Operation;
        if (!await _modelDownloads.EnsureAsync(modelId, tool))
        {
            ShowToast(Loc.Format("model.declined", tool), null, null, [], TimeSpan.FromSeconds(6));
            return;
        }

        var again = Queue.Enqueue(failed.Job);
        _modelRetries.Add(again);
        _tracked.Add(again);
    }

    private Task OpenEncoder(IReadOnlyList<string> files)
    {
        var window = new EncoderWindow(Kit, Engines, files, (input, result) => History.Add(EncoderTool.ToolId, [input], result));
        window.Show();
        window.Activate();
        return Task.CompletedTask;
    }

    private void ShowToast(string title, string? detail, double? progress, IReadOnlyList<(string Label, Action Run)> actions, TimeSpan? autoHide)
    {
        _toast ??= new ToastWindow(Kit);
        _toast.Present(title, detail, progress, actions, autoHide);
    }

    // ------------------------------------------------------------------------------------------------------------------ commands

    /// <summary>A command from another launch (context menu, Send to, a second double-click).</summary>
    public void HandleForwarded(string[] args) => Handle(AppCommandLine.Parse(args));

    public void Handle(AppCommand command)
    {
        switch (command.Kind)
        {
            case AppCommandKind.Open:
                // Explorer starts one process per selected file: merge those into one batch.
                _requests.Add(command.Paths.Select(Path.GetFullPath));
                _pendingWheel = command.Wheel;
                break;
            case AppCommandKind.Settings: ShowSettings(); break;
            case AppCommandKind.Shelf: ShowShelf(); break;
            case AppCommandKind.History: ShowHistory(); break;
            case AppCommandKind.Quit: Application.Current.Shutdown(); break;
            default:
                if (!command.Minimized) ShowSettings();
                break;
        }
    }

    private string? _pendingWheel;

    private void OpenWheelFor(IReadOnlyList<string> files, WheelKind? kind)
    {
        var wanted = kind ?? _pendingWheel switch { "tools" => WheelKind.Tools, "actions" => WheelKind.Actions, _ => WheelKind.Convert };
        _pendingWheel = null;
        var p = NativeInput.CursorPosition();
        Presenter.NextInteractive = true;
        Session.ShowFor(files, wanted, p.X, p.Y);
    }

    private void OnHotkey()
    {
        _ = Task.Run(async () =>
        {
            var selection = await ExplorerSelection.ReadAsync(TimeSpan.FromMilliseconds(600));
            await _ui.InvokeAsync(() =>
            {
                if (selection.Count > 0) OpenWheelFor(selection, WheelKind.Convert);
                else ToggleShelf();
            });
        });
    }

    // ------------------------------------------------------------------------------------------------------------------ windows

    public void ShowSettings()
    {
        if (_settingsWindow is { IsVisible: true }) { BringToFront(_settingsWindow); return; }
        var engines = Enum.GetValues<EngineId>().Where(e => e != EngineId.None && e != EngineId.Ffprobe).ToList();
        _settingsWindow = new SettingsWindow(Kit, new SettingsContext(
            Settings, SystemIntegration.IsStartupEnabled(), SystemIntegration.IsContextMenuInstalled(), ApplyFromWindow,
            engines, e => Engines.Locate(e) is not null,
            e => new EngineInstallService(_ui, Kit, Installer, Engines).InstallAsync(e, new Progress<string>(), CancellationToken.None),
            Version, _watchStore.Load(), AllRecipes().Select(r => r.Name).ToList(), SaveWatchRules,
            [.. Models.Catalog.Select(m => new ModelItem(
                m.Id, m.DisplayName, m.SizeBytes, () => Models.IsInstalled(m.Id),
                () => _modelDownloads.EnsureAsync(m.Id, m.DisplayName), () => Models.Delete(m.Id)))],
            PinnedRows(), ShowWheelEditor, PresetRows()));
        _settingsWindow.Show();
        BringToFront(_settingsWindow);
    }

    /// <summary>The welcome guide (first run and tray). Returns false when it could not be shown.</summary>
    private bool ShowWelcome()
    {
        if (_welcomeWindow is { IsVisible: true }) { _welcomeWindow.Activate(); return true; }
        string photo;
        try { photo = PracticePhoto.Ensure(Paths.DataDir); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ImageMagick.MagickException)
        {
            FileLog.Error("welcome: practice photo", ex);
            photo = "";
        }
        try
        {
            _welcomeWindow = new WelcomeWindow(Kit, new WelcomeContext(photo, Settings.Keys, Settings.Language, SystemIntegration.IsStartupEnabled(),
                Settings.ClipboardHotkey, Settings.Paused, (language, startup) =>
                {
                    try { SystemIntegration.SetStartup(startup, ExePath); }
                    catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { FileLog.Error("welcome: startup", ex); }
                    Update(Settings with { Language = language });
                }));
            _welcomeWindow.Show();
            _welcomeWindow.Activate();
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            FileLog.Error("welcome window", ex);
            return false;
        }
    }

    private HomeWindow? _homeWindow;

    /// <summary>The main window: drop zone, collected files, recent conversions (tray → Open DropFlow, double-click, History).</summary>
    private void ShowHistory()
    {
        if (_homeWindow is { IsVisible: true }) { BringToFront(_homeWindow); return; }
        _homeWindow = new HomeWindow(Kit, new HomeContext(History, Disposer, files => OpenWheelFor(files, null), Retry,
            op => op.StartsWith(Recipe.PresetPrefix, StringComparison.Ordinal) ? Presets().FirstOrDefault(p => p.PresetOperation == op)?.DisplayName(Loc.Language) ?? op
                : Operation.IsConvert(op) ? Loc.Format("home.convertedTo", Operation.TargetOf(op).ToUpperInvariant()) : Catalog.Tool(op)?.Name(Loc.Language) ?? op));
        _homeWindow.Show();
        BringToFront(_homeWindow);
    }

    /// <summary>
    /// Puts a window DropFlow opened on its own (after an update, from a second launch) above the others. Windows refuses focus to a
    /// background app, so a plain Activate left these windows hidden behind whatever the user was doing; a brief Topmost raises the
    /// window without taking the keyboard away from the user.
    /// </summary>
    private static void BringToFront(Window window)
    {
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Topmost = true;
        window.Activate();
        window.Topmost = false;
    }

    /// <summary>
    /// Runs a past conversion again on the same files with the options it used. A tool that needed a password (never stored) goes through
    /// its options dialog again instead.
    /// </summary>
    private void Retry(HistoryEntry entry)
    {
        var converter = Registry.Find(entry.Operation, ToolCatalog.ExtensionOf(entry.Inputs[0]));
        if (converter is null) { ShowToast(Loc["home.retry.unavailable"], null, null, [], TimeSpan.FromSeconds(6)); return; }
        if (HistoryEntry.NeededSecret(converter.OptionsFor(entry.Operation)))
        {
            var wedge = new Wedge(entry.Operation, WheelKind.Tools, Catalog.Tool(entry.Operation)?.Name(Loc.Language) ?? entry.Operation, entry.Operation);
            _ = OnSelectedAsync(new WheelSelection(WheelKind.Tools, wedge, entry.Inputs));
            return;
        }
        _tracked.Add(Queue.Enqueue(new ConversionJob(entry.Inputs, entry.Operation, entry.Options)));
    }

    private void ShowShelf()
    {
        _shelfWindow ??= new FloatingShelfWindow(_shelf, Loc, Kit.Theme);
        if (!_shelfWindow.IsVisible) _shelfWindow.Show();
    }

    private void ToggleShelf()
    {
        if (_shelfWindow is { IsVisible: true }) _shelfWindow.Hide();
        else ShowShelf();
    }

    /// <summary>"Update and restart": the toast turns into a download bar (no button to press twice), then DropFlow restarts.</summary>
    private async Task InstallUpdateAsync()
    {
        ShowToast(Loc["update.downloading"], null, 0, [], null);
        var done = await _updates.InstallAsync(p => _ui.BeginInvoke(() => ShowToast(Loc["update.downloading"], null, p / 100.0, [], null)));
        if (done == false) ShowToast(Loc["update.installFailed"], null, null, [], TimeSpan.FromSeconds(8));
    }

    private async Task CheckForUpdatesAsync(bool interactive)
    {
        if (interactive) ShowToast(Loc["update.checking"], null, -1, [], null);
        var check = await _updates.CheckAsync();
        switch (check.State)
        {
            case UpdateState.Available:
                ShowToast(Loc.Format("update.available", check.Version!), null, null,
                    [(Loc["update.install"], () => _ = InstallUpdateAsync())], null);
                break;
            case UpdateState.UpToDate when interactive:
                ShowToast(Loc["update.none"], null, null, [], TimeSpan.FromSeconds(5));
                break;
            case UpdateState.NotInstalled when interactive:
                ShowToast(Loc["update.notInstalled"], null, null, [], TimeSpan.FromSeconds(6));
                break;
            case UpdateState.Failed when interactive:
                ShowToast(Loc["update.failed"], check.Error, null, [], TimeSpan.FromSeconds(6));
                break;
            default:
                _toast?.Hide();
                break;
        }
    }

    // ------------------------------------------------------------------------------------------------------------------ settings

    private IReadOnlyList<string> ApplyFromWindow(AppSettings s, bool startup, bool menu)
    {
        var errors = SettingsValidator.Validate(s);
        if (errors.Count > 0) return errors;

        try
        {
            SystemIntegration.SetStartup(startup, ExePath);
            SystemIntegration.SetContextMenu(menu, ExePath, Loc["app.name"] + " …");
            SystemIntegration.SetSendTo(menu, ExePath);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException or InvalidOperationException)
        {
            FileLog.Error("system integration", ex);
        }

        // Pins change outside this form (the wheel, the list in the same window): keep the current ones, not the form's snapshot.
        Update(s with { PinnedItems = Settings.PinnedItems, Layouts = Settings.Layouts });
        return RegisterHotkey() ? [] : ["hotkey.invalid"];
    }

    /// <summary>The Settings rows for items added to the wheels (Convert, Tools, Actions; oldest first within a wheel).</summary>
    private WheelEditorWindow? _wheelEditor;

    /// <summary>Settings → Arrange the wheels.</summary>
    public void ShowWheelEditor()
    {
        if (_wheelEditor is { IsVisible: true }) { _wheelEditor.Activate(); return; }
        var categories = Catalog.Categories.Select(c => (c.Id, CategoryName(c.Id))).Append((WheelBuilder.OtherCategory, Loc["settings.pins.other"])).ToList();
        _wheelEditor = new WheelEditorWindow(Kit, new WheelEditorContext(
            categories,
            (kind, category) => Builder.ArrangeableFor(kind, category, Loc.Language, ActionService.SupportedIds, Settings.PinnedItems),
            () => Settings.Layouts,
            layouts => Update(Settings with { Layouts = layouts }),
            Settings.WheelFrontMax));
        _wheelEditor.Show();
        _wheelEditor.Activate();
    }

    /// <summary>"Save as a preset" in the options dialog: the options become a slice on the Tools wheel (passwords and folders are left out).</summary>
    private void SaveUserPreset(string operation, IReadOnlyList<ToolOption> options, IReadOnlyDictionary<string, object?> values, string label)
    {
        var mine = _recipeStore.Load();
        var preset = PresetBook.FromOptions(operation, options, values, label, Catalog, mine);
        _recipeStore.Save([.. mine, preset]);
        ShowToast(Loc.Format("preset.saved", label), null, null, [], TimeSpan.FromSeconds(5));
    }

    private PresetsContext PresetRows() => new(
        () => [.. _recipeStore.Load().Where(r => r.OnWheel).Select(r => (r.Name, r.DisplayName(Loc.Language)))],
        name => _recipeStore.Save(_recipeStore.Load().Where(r => r.Name != name)),
        () => PresetBook.Export(_recipeStore.Load()),
        json =>
        {
            var mine = _recipeStore.Load();
            var result = PresetBook.Import(json, Catalog, Registry, mine);
            if (result.Presets.Count > 0) _recipeStore.Save([.. mine, .. result.Presets]);
            return (result.Presets.Count, result.Rejected);
        });

    private IReadOnlyList<PinnedRow> PinnedRows() =>
        [.. Settings.PinnedItems.All.Select(p => new PinnedRow(
            Builder.LabelOf(p.Id, Loc.Language),
            $"{Loc["wheel." + p.Wheel.ToString().ToLowerInvariant()]} · {CategoryName(p.Category)}",
            () => Update(Settings with { PinnedItems = Settings.PinnedItems.Without(p.Wheel, p.Category, p.Id) })))];

    private string CategoryName(string id) =>
        Catalog.Category(id) is { } c ? (Loc.Language == "ar" ? c.Ar : c.En) : Loc["settings.pins.other"];

    private void Update(AppSettings s)
    {
        var before = (Settings.Accent, Settings.WheelOpacity, Settings.WheelScale, Settings.LabelScale, Settings.Motion);
        if (s.Proxy != Settings.Proxy) { _links?.Dispose(); _links = null; } // the next download uses the new proxy
        Settings = s;
        SettingsStore.Save(s);
        if (before != (s.Accent, s.WheelOpacity, s.WheelScale, s.LabelScale, s.Motion)) ApplyAppearance();
    }

    /// <summary>Accent colour, wheel opacity and size, label size and animations from Settings (windows opened later use the accent too).</summary>
    private void ApplyAppearance()
    {
        var motion = Settings.Motion;
        WheelControl.Animate = () => motion switch
        {
            MotionSetting.Reduce => false,
            MotionSetting.Full => true,
            _ => SystemParameters.ClientAreaAnimation,
        };
        if (_wheel is null) return;
        _wheel.Wheel.Theme = WheelTheme.FromSystem().With(Settings.Accent, Settings.WheelOpacity);
        _wheel.Wheel.LabelScale = Settings.LabelScale;
        _wheel.Scale = Settings.WheelScale;
    }

    private bool RegisterHotkey()
    {
        var shelf = HotkeyParser.Parse(Settings.GlobalHotkey);
        var clip = HotkeyParser.Parse(Settings.ClipboardHotkey);
        if (shelf is null || clip is null) return false;

        _hotkey.Pressed -= OnHotkey;
        _hotkey.Pressed += OnHotkey;
        _clipboardHotkey.Pressed -= OnClipboardHotkey;
        _clipboardHotkey.Pressed += OnClipboardHotkey;
        var a = _hotkey.Register(shelf.Value);
        var b = _clipboardHotkey.Register(clip.Value);
        if (!a || !b) FileLog.Info($"hotkey registration: shelf={a} clipboard={b} (another program may own the shortcut)");
        return a && b;
    }

    private void OnClipboardHotkey()
    {
        // A single copied web link is downloaded first; any other clipboard content goes the way it always did.
        Uri? link = null;
        try { link = ClipboardSource.ReadSingleUrl(); }
        catch (System.Runtime.InteropServices.COMException ex) { FileLog.Error("clipboard", ex); }
        if (link is not null)
        {
            _ = OpenLinkFromClipboardAsync(link);
            return;
        }

        IReadOnlyList<string> files;
        try { files = ClipboardSource.Export(Path.Combine(Path.GetTempPath(), "DropFlow")); }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or IOException or UnauthorizedAccessException)
        {
            FileLog.Error("clipboard", ex);
            files = [];
        }
        if (files.Count == 0) ShowToast(Loc["clipboard.empty"], null, null, [], TimeSpan.FromSeconds(4));
        else
        {
            foreach (var f in files) _fromClipboard.Add(f);
            OpenWheelFor(files, WheelKind.Convert);
        }
    }

    /// <summary>Files exported from the clipboard (Ctrl+Alt+V): with Settings → "Put results back on the clipboard", their results go there.</summary>
    private readonly HashSet<string> _fromClipboard = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>One picture → the picture itself (and the file); one text file → its text (and the file); anything else → the files.</summary>
    private void PutOnClipboard(IReadOnlyList<string> outputs)
    {
        var files = outputs.Where(File.Exists).ToList();
        if (files.Count == 0) return;
        var data = new DataObject();
        var list = new System.Collections.Specialized.StringCollection();
        list.AddRange([.. files]);
        data.SetFileDropList(list);
        var ext = Path.GetExtension(files[0]).ToLowerInvariant();
        try
        {
            if (files.Count == 1 && ext is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".tif" or ".tiff")
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.UriSource = new Uri(files[0]);
                image.EndInit();
                data.SetImage(image);
            }
            else if (files.Count == 1 && ext is ".txt" or ".md" or ".srt" or ".vtt" or ".csv" or ".json" && new FileInfo(files[0]).Length < 4 * 1024 * 1024)
            {
                data.SetText(File.ReadAllText(files[0]));
            }
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UnauthorizedAccessException) { FileLog.Error("clipboard result", ex); }
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try { Clipboard.SetDataObject(data, copy: true); ShowToast(Loc["clipboard.results"], null, null, [], TimeSpan.FromSeconds(4)); return; }
            catch (System.Runtime.InteropServices.COMException) { Thread.Sleep(80); } // another program holds the clipboard
        }
    }

    // ------------------------------------------------------------------------------------------------------------------ open from link

    private UrlDownloader Links => _links ??= new UrlDownloader(proxy: ProxySetting.Create(Settings.Proxy));

    /// <summary>Downloads a web address into the Downloads\DropFlow folder. Logs the address without its query string.</summary>
    private async Task<string> DownloadLinkAsync(Uri url, IProgress<DownloadProgress> progress, CancellationToken cancellationToken)
    {
        var shown = UrlDownloader.ForLog(url);
        FileLog.Info($"download from link: {shown}");
        try
        {
            var path = await Links.DownloadAsync(url.AbsoluteUri, progress, cancellationToken);
            FileLog.Info($"downloaded {Path.GetFileName(path)} from {shown}");
            return path;
        }
        catch (OperationCanceledException)
        {
            FileLog.Info($"download cancelled: {shown}");
            throw;
        }
        catch (UrlDownloadException ex)
        {
            FileLog.Info($"download failed ({ex.Kind}): {shown}");
            throw;
        }
    }

    /// <summary>The clipboard shortcut found one web link: download it with a progress toast, then show the wheel for the file.</summary>
    private async Task OpenLinkFromClipboardAsync(Uri url)
    {
        using var cts = new CancellationTokenSource();
        var finished = false;
        IReadOnlyList<(string, Action)> cancel = [(Loc["dialog.cancel"], cts.Cancel)];
        ShowToast(Loc["link.downloading"], url.Host, -1, cancel, null);
        var progress = new Progress<DownloadProgress>(p =>
        {
            if (!finished) ShowToast(Loc["link.downloading"], $"{url.Host} — {p.Describe(Loc)}", p.Fraction ?? -1, cancel, null);
        });

        try
        {
            var path = await DownloadLinkAsync(url, progress, cts.Token);
            finished = true;
            _toast?.Hide();
            OpenWheelFor([path], WheelKind.Convert);
        }
        catch (OperationCanceledException)
        {
            finished = true;
            ShowToast(Loc["link.cancelled"], null, null, [], TimeSpan.FromSeconds(3));
        }
        catch (UrlDownloadException ex)
        {
            finished = true;
            ShowToast(Loc.Format("result.failed", ex.Describe(Loc)), null, null, [], TimeSpan.FromSeconds(10));
        }
    }

    /// <summary>The tray's "Open from link…" window.</summary>
    private void ShowOpenLink()
    {
        if (_linkWindow is { IsVisible: true }) { _linkWindow.Activate(); return; }

        string? initial = null;
        try { initial = ClipboardSource.ReadSingleUrl()?.AbsoluteUri; }
        catch (System.Runtime.InteropServices.COMException) { /* clipboard busy: start with an empty box */ }

        _linkWindow = new OpenLinkWindow(Kit, DownloadLinkAsync, path => _ui.BeginInvoke(() => OpenWheelFor([path], WheelKind.Convert)), Links.Folder, initial);
        _linkWindow.Show();
        _linkWindow.Activate();
    }

    // ------------------------------------------------------------------------------------------------------------------ automation

    private IReadOnlyList<Recipe> AllRecipes() => [.. BuiltInRecipes.Available(Registry), .. _recipeStore.Load()];

    private void SaveWatchRules(IReadOnlyList<WatchRule> rules)
    {
        _watchStore.Save(rules);
        _watch?.Apply(rules);
    }

    private void StartWatching()
    {
        _watch = new WatchService(OnWatchedFilesAsync);
        _watch.Apply(_watchStore.Load());
    }

    private async Task OnWatchedFilesAsync(WatchRule rule, IReadOnlyList<string> files)
    {
        var recipe = AllRecipes().FirstOrDefault(r => r.Name == rule.Recipe);
        if (recipe is null)
        {
            FileLog.Error($"watch rule {rule.Id}: unknown recipe '{rule.Recipe}'");
            return;
        }

        try
        {
            var outDir = Path.Combine(rule.Folder, rule.OutputSubfolder);
            Directory.CreateDirectory(outDir);
            var result = await new RecipeRunner(Queue, Disposer).RunAsync(recipe, files, outDir);
            _watch?.MarkProduced(result.Outputs);
            await _ui.InvokeAsync(() =>
            {
                History.Add(recipe.Name, files, result);
                if (result.Success) ShowToast(Loc.Format("watch.done", Path.GetFileName(files[0])), NoteText.Localize(result.Note, Loc), null, result.Outputs.Count > 0 ? [(Loc["result.open"], () => Explorer.Reveal(result.Outputs[0]))] : [], TimeSpan.FromSeconds(6));
                else FileLog.Error($"watched folder recipe '{recipe.Name}' failed: {result.Error}");
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            FileLog.Error("watched folder", ex);
        }
    }

    // ------------------------------------------------------------------------------------------------------------------ tray

    private void CreateTray()
    {
        var menu = new ContextMenu { FlowDirection = Loc.IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight };
        var pause = new MenuItem { Header = Loc[Settings.Paused ? "tray.resume" : "tray.pause"] };
        pause.Click += (_, _) =>
        {
            Update(Settings with { Paused = !Settings.Paused });
            if (Settings.Paused) StopHooks(); else StartHooks();
            pause.Header = Loc[Settings.Paused ? "tray.resume" : "tray.pause"];
            if (_tray is not null) _tray.ToolTipText = Settings.Paused ? Loc["tray.paused"] : Loc["tray.tooltip"];
        };
        MenuItem Item(string key, Action run) { var i = new MenuItem { Header = Loc[key] }; i.Click += (_, _) => run(); return i; }
        menu.Items.Add(Item("tray.home", ShowHistory));
        menu.Items.Add(pause);
        var pauseHour = new MenuItem { Header = Loc["tray.pauseHour"] };
        pauseHour.Click += (_, _) =>
        {
            if (Settings.Paused) return;
            Update(Settings with { Paused = true });
            StopHooks();
            pause.Header = Loc["tray.resume"];
            if (_tray is not null) _tray.ToolTipText = Loc["tray.paused"];
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromHours(1) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                if (!Settings.Paused) return;
                Update(Settings with { Paused = false });
                StartHooks();
                pause.Header = Loc["tray.pause"];
                if (_tray is not null) _tray.ToolTipText = Loc["tray.tooltip"];
            };
            timer.Start();
        };
        menu.Items.Add(pauseHour);
        menu.Items.Add(Item("tray.openLink", ShowOpenLink));
        menu.Items.Add(Item("tray.shelf", ToggleShelf));
        menu.Items.Add(Item("tray.history", ShowHistory));
        menu.Items.Add(Item("tray.settings", ShowSettings));
        menu.Items.Add(Item("tray.welcome", () => ShowWelcome()));
        menu.Items.Add(Item("tray.whatsnew", ShowWhatsNew));
        menu.Items.Add(Item("tray.update", () => _ = CheckForUpdatesAsync(interactive: true)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("tray.quit", () => Application.Current.Shutdown()));

        _tray = new TaskbarIcon
        {
            ToolTipText = Settings.Paused ? Loc["tray.paused"] : Loc["tray.tooltip"],
            IconSource = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/dropflow.ico")),
            ContextMenu = menu,
            MenuActivation = PopupActivationMode.LeftOrRightClick,
        };
        _tray.TrayMouseDoubleClick += (_, _) => ShowHistory();
        _tray.ForceCreate(enablesEfficiencyMode: false);
    }

    public void Dispose()
    {
        StopHooks();
        _hotkey.Dispose();
        _clipboardHotkey.Dispose();
        _watch?.Dispose();
        _links?.Dispose();
        foreach (var converter in Registry.Converters.OfType<IDisposable>()) converter.Dispose();
        Models.Dispose();
        _redact.Dispose();
        _tray?.Dispose();
        FileLog.Info("DropFlow stopped");
    }
}
