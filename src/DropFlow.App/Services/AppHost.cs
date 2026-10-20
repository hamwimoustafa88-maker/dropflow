using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DropFlow.App.Views;
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
using DropFlow.Core.Wheel;
using DropFlow.Engines.Common;
using DropFlow.Hooks;
using DropFlow.Overlay;
using H.NotifyIcon;
using H.NotifyIcon.Core;

namespace DropFlow.App.Services;

/// <summary>Which converter modules the app loads. One line per engine project.</summary>
internal static class Modules
{
    public static IReadOnlyList<IConverter> Create(IEngineProvider engines) =>
    [
        .. Engines.Images.ImagesModule.Create(engines),
    ];
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
        var items = wedges.Select(w => new WheelItem(w.Id, w.Label, w.State == WedgeState.NeedsEngine ? WheelItemState.NeedsEngine : WheelItemState.Ready, w.NeedsOptions)).ToList();
        window.Wheel.SetItems(items, loc.IsRightToLeft);
        var hint = fileCount == 0 ? loc["wheel.drop"] : fileCount == 1 ? loc["wheel.file"] : loc.Format("wheel.files", fileCount);
        window.Wheel.SetCenter(items.Count == 0 && fileCount > 0 ? loc["wheel.nothing"] : loc["wheel." + kind.ToString().ToLowerInvariant()], items.Count == 0 && fileCount > 0 ? "" : hint);
    }

    public void Hide() => window.HideWheel();
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
    private readonly GlobalHotkey _hotkey = new();
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
    private HistoryWindow? _historyWindow;
    private Task<IReadOnlyList<string>>? _selectionTask;

    public AppHost(Dispatcher ui)
    {
        _ui = ui;
        _scheduler = new DispatcherScheduler(ui);

        SettingsStore = new SettingsStore(new AppPaths(AppPaths.DefaultDir).SettingsFile);
        Settings = SettingsStore.Load();
        Paths = new AppPaths(Settings.DataDirectory ?? AppPaths.DefaultDir);
        FileLog.Init(Paths.LogDir);
        FileLog.Info($"DropFlow {Version} starting");

        Loc = JsonLocalizer.Load(LanguageResolver.Resolve(Settings.Language, CultureInfo.CurrentUICulture.Name));
        Catalog = ToolCatalog.LoadEmbedded();
        Engines = new EngineLocator(Paths.DataDir);
        Installer = new EngineInstaller(Engines);
        Registry = new ToolRegistry().Register(Modules.Create(Engines));
        Queue = new JobQueue(Registry, Catalog, Settings.MaxConcurrency);
        History = new HistoryStore(Paths.HistoryFile);
        Kit = new UiKit(WheelTheme.FromSystem(), Loc);
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
    public ToolRegistry Registry { get; }
    public JobQueue Queue { get; }
    public HistoryStore History { get; }
    public UiKit Kit { get; }
    public IFileDisposer Disposer { get; }
    public WheelSession Session { get; private set; } = null!;
    public WheelPresenter Presenter { get; private set; } = null!;

    public static string Version => Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.1.0";
    private static string ExePath => Environment.ProcessPath ?? "DropFlow.exe";

    // ------------------------------------------------------------------------------------------------------------------ start

    public void Start()
    {
        _wheel = new WheelWindow();
        _wheel.Prepare();
        Presenter = new WheelPresenter(_wheel, Loc);

        var builder = new WheelBuilder(Catalog, Registry, Engines);
        Session = new WheelSession(builder, Presenter, _scheduler, () => Settings, () => Loc.Language, () => ActionService.SupportedIds, OnSelectedAsync);
        Presenter.Session = Session;

        _wheel.FilesEntered += (_, files) => Session.OnFilesEntered(files);
        _wheel.ItemDropped += (_, e) => _ = Session.OnItemDroppedAsync(e.Files, e.Index);
        _wheel.ItemClicked += (_, index) => _ = Session.OnItemDroppedAsync(_wheel.Files, index);
        _wheel.DropCancelled += (_, _) => Session.Dismiss();

        Queue.Changed += batch => _ui.BeginInvoke(() => OnBatchChanged(batch));
        Queue.BatchCompleted += (batch, result) => _ui.BeginInvoke(() => OnBatchCompleted(batch, result));

        CreateTray();
        StartHooks();
        RegisterHotkey();

        if (Settings.ShowFirstRunTips)
        {
            ShowToast(Loc["firstrun.title"], Loc["firstrun.text"], null, [], TimeSpan.FromSeconds(8));
            Update(Settings with { ShowFirstRunTips = false });
        }
    }

    private void StartHooks()
    {
        if (Settings.Paused || _hook is not null) return;

        // Hook callbacks run on the hook thread: these handlers only post to the UI thread.
        _detector.DragStarted += OnDragStartedFromHook;
        _detector.KeyTapped += OnKeyTappedFromHook;
        _detector.DragEnded += OnDragEndedFromHook;
        _detector.DragCancelled += OnDragCancelledFromHook;
        try
        {
            _hook = new LowLevelInputHook(_detector);
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
            Session.OnKeyTapped(ToTrigger(key), p.X, p.Y);
            ApplyExplorerSelectionWhenReady();
        });

    private void OnDragEndedFromHook(ScreenPoint p) => _ui.BeginInvoke(Session.OnDragEnded);
    private void OnDragCancelledFromHook() => _ui.BeginInvoke(Session.OnDragCancelled);

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
                if (selection.Kind == WheelKind.Actions)
                {
                    await CreateActions().ExecuteAsync(selection.Wedge.Id, selection.Files);
                    return;
                }
                var batch = await new OperationRunnerFactory(this).Create().RunAsync(selection);
                if (batch is not null) _tracked.Add(batch);
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
            });

    /// <summary>Builds the runner on demand so it always sees the current prompts/toast.</summary>
    private sealed class OperationRunnerFactory(AppHost host)
    {
        public OperationRunner Create() => new(
            host.Registry, host.Queue, host.Engines,
            new EngineInstallService(host._ui, host.Kit, host.Installer, host.Engines),
            new UserPrompts(host._ui, host.Loc, msg => { host.ShowToast(msg, null, null, [], TimeSpan.FromSeconds(8)); return Task.CompletedTask; }),
            EngineInfo.ApproxSizeMb,
            op => host.Catalog.Tool(op)?.Name(host.Loc.Language) ?? op);
    }

    private void OnBatchChanged(BatchHandle batch)
    {
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
        if (!_tracked.Remove(batch)) return;
        _lastToast.Remove(batch);
        var entry = History.Add(batch.Job.Operation, batch.Job.Inputs, result);

        if (!result.Success && result.Outputs.Count == 0)
        {
            FileLog.Error($"{batch.Job.Operation} failed: {result.Error}");
            ShowToast(Loc.Format("result.failed", result.Error ?? ""), null, null, [], TimeSpan.FromSeconds(10));
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
            : Loc.Format("result.failed", result.Error ?? "");
        ShowToast(title, result.Note ?? (result.Outputs.Count == 1 ? Path.GetFileName(result.Outputs[0]) : null), null, actions, TimeSpan.FromSeconds(9));
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
        if (_settingsWindow is { IsVisible: true }) { _settingsWindow.Activate(); return; }
        var engines = Enum.GetValues<EngineId>().Where(e => e != EngineId.None && e != EngineId.Ffprobe).ToList();
        _settingsWindow = new SettingsWindow(Kit, Settings, SystemIntegration.IsStartupEnabled(), SystemIntegration.IsContextMenuInstalled(),
            ApplyFromWindow, engines, e => Engines.Locate(e) is not null,
            e => new EngineInstallService(_ui, Kit, Installer, Engines).InstallAsync(e, new Progress<string>(), CancellationToken.None), Version);
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    private void ShowHistory()
    {
        if (_historyWindow is { IsVisible: true }) { _historyWindow.Activate(); return; }
        _historyWindow = new HistoryWindow(Kit, History, Disposer);
        _historyWindow.Show();
        _historyWindow.Activate();
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

        Update(s);
        return RegisterHotkey() ? [] : ["hotkey.invalid"];
    }

    private void Update(AppSettings s)
    {
        Settings = s;
        SettingsStore.Save(s);
    }

    private bool RegisterHotkey()
    {
        var parsed = HotkeyParser.Parse(Settings.GlobalHotkey);
        if (parsed is null) return false;
        _hotkey.Pressed -= OnHotkey;
        _hotkey.Pressed += OnHotkey;
        return _hotkey.Register(parsed.Value);
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
        menu.Items.Add(pause);
        menu.Items.Add(Item("tray.shelf", ToggleShelf));
        menu.Items.Add(Item("tray.history", ShowHistory));
        menu.Items.Add(Item("tray.settings", ShowSettings));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("tray.quit", () => Application.Current.Shutdown()));

        _tray = new TaskbarIcon
        {
            ToolTipText = Settings.Paused ? Loc["tray.paused"] : Loc["tray.tooltip"],
            IconSource = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/dropflow.ico")),
            ContextMenu = menu,
            MenuActivation = PopupActivationMode.LeftOrRightClick,
        };
        _tray.TrayMouseDoubleClick += (_, _) => ShowSettings();
        _tray.ForceCreate(enablesEfficiencyMode: false);
    }

    public void Dispose()
    {
        StopHooks();
        _hotkey.Dispose();
        _tray?.Dispose();
        FileLog.Info("DropFlow stopped");
    }
}
