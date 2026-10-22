// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using DropFlow.Core.App;
using DropFlow.Core.Catalog;
using DropFlow.Core.History;
using DropFlow.Core.Operations;
using DropFlow.Core.Queue;
using DropFlow.Core.Registry;
using DropFlow.Core.Session;
using DropFlow.Core.Settings;
using DropFlow.Core.Wheel;
using DropFlow.Desktop.App;
using DropFlow.Desktop.Drag;
using DropFlow.Desktop.Native;
using DropFlow.Desktop.Platform;
using DropFlow.Desktop.Views;
using DropFlow.Desktop.Wheel;
using DropFlow.Overlay;

namespace DropFlow.Desktop;

/// <summary>Runs an action later on the UI thread (the wheel's grace period after a drag ends).</summary>
internal sealed class UiScheduler : IScheduler
{
    public IDisposable After(TimeSpan delay, Action action) => DispatcherTimer.RunOnce(action, delay);
}

/// <summary>Deletes outputs on undo when no trash is available (development on Windows).</summary>
internal sealed class DeletingDisposer : IFileDisposer
{
    public void Dispose(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        else File.Delete(path);
    }
}

/// <summary>
/// The composition root of the macOS/Linux app: builds the shared Core services and engine modules once, wires the drag watcher to
/// the wheel session, runs jobs with progress toasts, and owns the tray / menu-bar icon. UI-thread affine.
/// </summary>
internal sealed class AppController : IDesktopPrompts
{
    public static readonly string[] SupportedActions = ["act.float", "act.copy", "act.move", "act.copy-path", "act.terminal"];

    private readonly Application _app;
    private readonly IClassicDesktopStyleApplicationLifetime _lifetime;
    private readonly AppDirectories _dirs;
    private readonly InstanceChannel? _instance;
    private readonly HostSession _session = HostSession.Current;
    private readonly SettingsStore _settingsStore;
    private readonly DesktopSettingsStore _desktopStore;
    private readonly ShelfModel _shelf = new();
    private readonly HashSet<BatchHandle> _tracked = [];
    private readonly Dictionary<BatchHandle, DateTime> _lastToast = [];

    private AppSettings _settings;
    private DesktopSettings _desktop;
    private DesktopLocalizer _loc = null!;
    private ToolCatalog _catalog = null!;
    private IEngineProvider _engines = null!;
    private ToolRegistry _registry = null!;
    private JobQueue _queue = null!;
    private HistoryStore _history = null!;
    private IFileDisposer _disposer = null!;
    private WheelBuilder _builder = null!;
    private WheelWindow _wheel = null!;
    private WheelPresenter _presenter = null!;
    private WheelSession _wheelSession = null!;
    private IDragWatcher _watcher = new NoDragWatcher();
    private TrayIcon? _tray;
    private NativeMenuItem? _pauseItem;
    private ToastWindow? _toast;
    private SettingsWindow? _settingsWindow;
    private HistoryWindow? _historyWindow;
    private ShelfWindow? _shelfWindow;
    private WelcomeWindow? _welcomeWindow;

    public AppController(Application app, IClassicDesktopStyleApplicationLifetime lifetime, AppDirectories dirs, InstanceChannel? instance)
    {
        _app = app;
        _lifetime = lifetime;
        _dirs = dirs;
        _instance = instance;
        _settingsStore = new SettingsStore(dirs.SettingsFile);
        _desktopStore = new DesktopSettingsStore(dirs.DesktopSettingsFile);
        _settings = _settingsStore.Load();
        _desktop = _desktopStore.Load();
    }

    public static string Version => Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.1.0";

    // ------------------------------------------------------------------------------------------------------------------ start

    public void Start(DesktopCommand command)
    {
        var language = command.Language ?? LanguageResolver.Resolve(_settings.Language, CultureInfo.CurrentUICulture.Name);
        _loc = DesktopLocalizer.Load(language);
        Log.Info($"DropFlow desktop {Version} on {_session.Os} ({(_session.IsWayland ? "Wayland" : _session.IsX11 ? "X11" : "-")}, {_session.Desktop}), language {language}");

        _catalog = ToolCatalog.LoadEmbedded();
        _engines = _session.Os == OsKind.Windows
            ? new Engines.Common.EngineLocator(_dirs.Data)
            : new UnixEngineProvider(_session.Os, AppContext.BaseDirectory.TrimEnd('/', '\\'), _dirs.Data);
        _registry = new ToolRegistry().Register(CreateModules(_engines));
        _queue = new JobQueue(_registry, _catalog, _settings.MaxConcurrency);
        _history = new HistoryStore(_dirs.HistoryFile);
        _disposer = _session.Os switch
        {
            OsKind.Linux => new FreedesktopTrash(AppDirectories.Join(_dirs.DataHome, "Trash")),
            OsKind.MacOS => new FolderTrash(AppDirectories.Join(_dirs.Home, ".Trash")),
            _ => new DeletingDisposer(),
        };
        _builder = new WheelBuilder(_catalog, _registry, _engines);

        _wheel = new WheelWindow { UseOverrideRedirect = DragWatcherFactory.Choose(_session) is DragWatcherKind.X11 or DragWatcherKind.GnomeShell };
        _presenter = new WheelPresenter(_wheel, _loc);
        _wheelSession = new WheelSession(_builder, _presenter, new UiScheduler(), () => _settings, () => _loc.Language, () => SupportedActions, OnSelectedAsync,
            savePins: pins => SaveShared(_settings with { PinnedItems = pins }));
        _presenter.Session = _wheelSession;
        _wheel.FilesEntered += (_, files) => _wheelSession.OnFilesEntered(files);
        _wheel.ItemDropped += (_, e) => _ = _wheelSession.OnItemDroppedAsync(e.Files, e.Index);
        _wheel.ItemClicked += (_, index) => _ = _wheelSession.OnItemDroppedAsync(_wheel.Files.Count > 0 ? _wheel.Files : _wheelSession.Files, index);
        _wheel.DropCancelled += (_, _) => _wheelSession.Dismiss();
        _wheel.ItemContextRequested += (_, index) => _presenter.OnItemContext(index);
        _wheel.AddItemPicked += (_, id) => _ = _wheelSession.OnAddPickedAsync(id);
        _wheel.AddListBack += (_, _) => _wheelSession.CloseAddList();

        _queue.Changed += batch => Dispatcher.UIThread.Post(() => OnBatchChanged(batch));
        _queue.BatchCompleted += (batch, result) => Dispatcher.UIThread.Post(() => OnBatchCompleted(batch, result));

        if (command.Kind == DesktopCommandKind.Snapshot)
        {
            _ = new Snapshotter(this).RunAsync(command.SnapshotFolder ?? Path.Combine(Environment.CurrentDirectory, "snapshots"));
            return;
        }

        CreateTray();
        StartWatcher();
        if (_instance is not null)
        {
            _instance.ArgumentsReceived += args => Dispatcher.UIThread.Post(() => Handle(DesktopCommandLine.Parse(args, "/")));
            _instance.StartListening();
        }
        IntegrateWithDesktop();

        if (!_desktop.WelcomeShown)
        {
            ShowWelcome();
            SaveDesktop(_desktop with { WelcomeShown = true });
        }
        Handle(command);
    }

    /// <summary>The engine modules that are portable today. AI (speech, OCR, background removal) and Web (HTML → PDF) target Windows only.</summary>
    private static IReadOnlyList<IConverter> CreateModules(IEngineProvider engines) =>
    [
        .. Engines.Images.ImagesModule.Create(engines),
        .. Engines.Media.MediaModule.Create(engines),
        .. Engines.Pdf.PdfModule.Create(engines),
        .. Engines.Archive.ArchiveModule.Create(engines),
    ];

    /// <summary>GNOME Shell extension, file-manager entries and the login item, kept in step with the settings.</summary>
    private void IntegrateWithDesktop()
    {
        try
        {
            if (DragWatcherFactory.Choose(_session) == DragWatcherKind.GnomeShell)
                Log.Info("GNOME Shell extension: " + GnomeExtension.Install(_session, _dirs));
            if (_session.IsLinux)
            {
                var titles = new[] { DesktopLocalizer.ReadDesktop("en")["fm.title"], DesktopLocalizer.ReadDesktop("ar")["fm.title"] };
                if (_desktop.FileManagerEntries) FileManagerEntries.Install(LoginItem.CurrentCommand, _loc["fm.title"], titles, _dirs);
                else FileManagerEntries.Remove(titles, _dirs);
            }
            // An AppImage moves (its path changes): rewrite the login entry so it keeps pointing at this copy.
            if (_desktop.StartAtLogin) Log.Info(LoginItem.Apply(true, _dirs));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("desktop integration", ex);
        }
    }

    private void StartWatcher()
    {
        _watcher.Dispose();
        if (_settings.Paused) { _watcher = new NoDragWatcher(); return; }
        _watcher = DragWatcherFactory.Choose(_session) switch
        {
            DragWatcherKind.MacPasteboard when OperatingSystem.IsMacOS() => new MacDragWatcher(),
            DragWatcherKind.X11 => new X11DragWatcher(),
            DragWatcherKind.GnomeShell => new ShellDragWatcher(_dirs.DragSocket),
            _ => new NoDragWatcher(),
        };
        _watcher.KeyTapped += (key, x, y, files) =>
        {
            _wheelSession.OnKeyTapped(key, x, y);
            if (files.Count > 0 && _wheelSession.IsVisible && _wheelSession.Files.Count == 0) _wheelSession.OnFilesEntered(files);
        };
        _watcher.DragEnded += _wheelSession.OnDragEnded;
        Log.Info($"drag watcher: {_watcher.Name} {(_watcher.Start() ? "started" : "not available")}");
    }

    // ------------------------------------------------------------------------------------------------------------------ commands

    public void Handle(DesktopCommand command)
    {
        switch (command.Kind)
        {
            case DesktopCommandKind.Wheel when command.Paths.Count > 0: OpenWheelFor(command.Paths, command.Wheel ?? WheelKind.Convert); break;
            case DesktopCommandKind.Settings: ShowSettings(); break;
            case DesktopCommandKind.History: ShowHistory(); break;
            case DesktopCommandKind.Shelf: ShowShelf(); break;
            case DesktopCommandKind.Welcome: ShowWelcome(); break;
            case DesktopCommandKind.Quit: Quit(); break;
        }
    }

    /// <summary>Click mode: a wheel for files that are already known, at the pointer, taking focus so the keyboard works.</summary>
    private void OpenWheelFor(IReadOnlyList<string> files, WheelKind kind)
    {
        var existing = files.Where(f => File.Exists(f) || Directory.Exists(f)).ToList();
        if (existing.Count == 0) return;
        var (x, y) = PointerForClickMode();
        Log.Info($"click-mode wheel for {existing.Count} item(s) at {x},{y}");
        _presenter.NextInteractive = true;
        _wheelSession.ShowFor(existing, kind, x, y);
    }

    private (int X, int Y) PointerForClickMode()
    {
        if (OperatingSystem.IsMacOS())
        {
            var p = MacInput.MouseLocation();
            return ((int)p.X, (int)p.Y);
        }
        if (_session.IsX11 && X11.PointerPosition() is { } pointer) return pointer;
        var area = _wheel.Screens.Primary?.WorkingArea ?? new PixelRect(0, 0, 1280, 800);
        return (area.X + area.Width / 2, area.Y + area.Height / 2);
    }

    // ------------------------------------------------------------------------------------------------------------------ run

    private Task OnSelectedAsync(WheelSelection selection) =>
        Dispatcher.UIThread.InvokeAsync(async () =>
        {
            try
            {
                if (selection.Wedge.Wheel == WheelKind.Actions)
                {
                    await RunActionAsync(selection.Wedge.Id, selection.Files);
                    return;
                }
                var batch = await CreateRunner().RunAsync(selection);
                if (batch is not null) Dispatcher.UIThread.Post(() => _tracked.Add(batch));
            }
            catch (Exception ex)
            {
                Log.Error("operation failed to start", ex);
                ShowToast(_loc.Format("result.failed", ex.Message), null, null, [], TimeSpan.FromSeconds(8));
            }
        });

    private DesktopRunner CreateRunner() => new(
        _registry, _queue, _engines, this,
        op => _catalog.Tool(op)?.Name(_loc.Language) ?? op,
        () => OutputPolicy.DirectoryFor(_desktop.Output, _dirs),
        MissingEngineMessage,
        tool => _loc.Format("run.notSupported", tool),
        (op, ext) => _loc.Format("result.failed", $"{OperationNames.Of(_catalog, op, _loc.Language)} · .{ext}"));

    private string MissingEngineMessage(EngineId engine)
    {
        var name = _loc["engine.name." + engine];
        return EngineHints.InstallCommand(engine, _session.Os, EngineHints.Current) is { } command
            ? _loc.Format("engine.missing.unix", name, command)
            : _loc.Format("engine.missing.unixPlain", name);
    }

    public Task<IReadOnlyDictionary<string, object?>?> AskOptionsAsync(string operation, string title, IReadOnlyList<ToolOption> options) =>
        Dispatcher.UIThread.InvokeAsync(() => new OptionsWindow(title, operation, options, _loc).AskAsync());

    public Task ShowMessageAsync(string message) =>
        Dispatcher.UIThread.InvokeAsync(() => ShowToast(message, null, null, [], TimeSpan.FromSeconds(12))).GetTask();

    private void OnBatchChanged(BatchHandle batch)
    {
        if (!_tracked.Contains(batch) || batch.IsFinished) return;
        if (_lastToast.TryGetValue(batch, out var last) && DateTime.UtcNow - last < TimeSpan.FromMilliseconds(120)) return;
        _lastToast[batch] = DateTime.UtcNow;
        var done = batch.Jobs.Count(j => j.State is JobState.Completed or JobState.Failed or JobState.Cancelled);
        var title = batch.Jobs.Count > 1 ? $"{_loc["result.working"]} {done}/{batch.Jobs.Count}" : _loc["result.working"];
        var detail = batch.Jobs.FirstOrDefault(j => j.State == JobState.Running)?.Message;
        ShowToast(title, detail ?? Path.GetFileName(batch.Job.Inputs[0]), batch.Progress, [(_loc["dialog.cancel"], batch.Cancel)], null);
    }

    private void OnBatchCompleted(BatchHandle batch, ConversionResult result)
    {
        // The batch may finish before the runner's caller tracked it: track first, then report.
        _tracked.Remove(batch);
        _lastToast.Remove(batch);
        var entry = _history.Add(batch.Job.Operation, batch.Job.Inputs, result);
        if (!result.Success && result.Outputs.Count == 0)
        {
            Log.Error($"{batch.Job.Operation} failed: {result.Error}");
            ShowToast(_loc.Format("result.failed", result.Error ?? ""), null, null, [], TimeSpan.FromSeconds(10));
            return;
        }

        var actions = new List<(string, Action)>();
        if (result.Outputs.Count > 0)
        {
            var first = result.Outputs[0];
            actions.Add((_loc["result.open"], () => Reveal(first)));
            actions.Add((_loc["result.undo"], () => { _history.Undo(entry.Id, _disposer); _toast?.Hide(); }));
        }
        var title = result.Success
            ? _loc.Format("result.done", _loc.Format("result.files", Math.Max(1, result.Outputs.Count)))
            : _loc.Format("result.failed", result.Error ?? "");
        ShowToast(title, result.Note ?? (result.Outputs.Count == 1 ? Path.GetFileName(result.Outputs[0]) : null), null, actions, TimeSpan.FromSeconds(9));
    }

    internal void ShowToast(string title, string? detail, double? progress, IReadOnlyList<(string Label, Action Run)> actions, TimeSpan? autoHide)
    {
        _toast ??= new ToastWindow(_loc);
        _toast.Present(title, detail, progress, actions, autoHide);
    }

    private void Reveal(string path)
    {
        if (!ShellPlans.TryStart(ShellPlans.Reveal(_session.Os, path))) Log.Error("could not reveal " + path);
    }

    // ------------------------------------------------------------------------------------------------------------------ actions

    private IClipboard? Clipboard => _wheel.Clipboard;

    private async Task RunActionAsync(string id, IReadOnlyList<string> files)
    {
        try
        {
            switch (id)
            {
                case "act.copy":
                {
                    var items = new List<IStorageItem>();
                    foreach (var f in files)
                    {
                        IStorageItem? item = Directory.Exists(f)
                            ? await _wheel.StorageProvider.TryGetFolderFromPathAsync(f)
                            : await _wheel.StorageProvider.TryGetFileFromPathAsync(f);
                        if (item is not null) items.Add(item);
                    }
                    if (Clipboard is { } clip && items.Count > 0) await clip.SetFilesAsync(items);
                    ShowToast(_loc.Format("action.copied", items.Count), null, null, [], TimeSpan.FromSeconds(4));
                    break;
                }
                case "act.copy-path":
                    if (Clipboard is { } c) await c.SetTextAsync(FileActionPlans.PathLines(files));
                    ShowToast(_loc["action.pathCopied"], null, null, [], TimeSpan.FromSeconds(4));
                    break;
                case "act.move":
                {
                    var picked = await _wheel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = _loc["action.moveTo"] });
                    if (picked.Count == 0 || picked[0].TryGetLocalPath() is not { } folder) return;
                    var moved = 0;
                    foreach (var step in FileActionPlans.PlanMove(files, folder))
                    {
                        if (Directory.Exists(step.From)) Directory.Move(step.From, step.To);
                        else File.Move(step.From, step.To);
                        moved++;
                    }
                    ShowToast(_loc.Format("action.moved", moved), null, null, [], TimeSpan.FromSeconds(4));
                    break;
                }
                case "act.terminal":
                {
                    var folder = FileActionPlans.TerminalFolder(files[0]);
                    if (!ShellPlans.TryStart(ShellPlans.Terminals(_session.Os, folder), folder))
                        ShowToast(_loc["action.terminalFailed"], null, null, [], TimeSpan.FromSeconds(5));
                    break;
                }
                case "act.float":
                    _shelf.Add(files);
                    ShowShelf();
                    ShowToast(_loc["action.shelfAdded"], null, null, [], TimeSpan.FromSeconds(3));
                    break;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("action " + id, ex);
            ShowToast(ex.Message, null, null, [], TimeSpan.FromSeconds(6));
        }
    }

    // ------------------------------------------------------------------------------------------------------------------ windows

    private static void Present(Window window)
    {
        if (!window.IsVisible) window.Show();
        window.Activate();
    }

    internal SettingsWindow CreateSettingsWindow() => new(_loc, new SettingsContext(
        _settings, _desktop with { StartAtLogin = _desktop.StartAtLogin || LoginItem.IsEnabled(_dirs) }, _session, EngineRows(),
        () => GnomeExtension.Current(_session, _dirs),
        () => GnomeExtension.Install(_session, _dirs),
        async text => { if (Clipboard is { } c) await c.SetTextAsync(text); },
        ApplySettings,
        Version));

    public void ShowSettings()
    {
        if (_settingsWindow is { IsVisible: true }) { _settingsWindow.Activate(); return; }
        _settingsWindow = CreateSettingsWindow();
        Present(_settingsWindow);
    }

    public void ShowHistory()
    {
        if (_historyWindow is { IsVisible: true }) { _historyWindow.Activate(); return; }
        _historyWindow = new HistoryWindow(_loc, _history, _disposer, Reveal, op => OperationNames.Of(_catalog, op, _loc.Language));
        Present(_historyWindow);
    }

    public void ShowShelf()
    {
        _shelfWindow ??= new ShelfWindow(_loc, _shelf);
        _shelfWindow.Closed += (_, _) => _shelfWindow = null;
        Present(_shelfWindow);
    }

    internal WelcomeWindow CreateWelcomeWindow() =>
        new(_loc, WelcomeText.Keys(_session, GnomeExtension.Current(_session, _dirs)));

    public void ShowWelcome()
    {
        if (_welcomeWindow is { IsVisible: true }) { _welcomeWindow.Activate(); return; }
        _welcomeWindow = CreateWelcomeWindow();
        Present(_welcomeWindow);
    }

    private IReadOnlyList<EngineRow> EngineRows()
    {
        var engines = new[] { EngineId.Ffmpeg, EngineId.Qpdf, EngineId.Tesseract, EngineId.LibreOffice, EngineId.SevenZip, EngineId.Pandoc };
        var appDir = AppContext.BaseDirectory.TrimEnd('/', '\\');
        var bundleRoot = LoginItem.BundleOf(Path.Combine(appDir, "x")) ?? appDir;
        return engines.Select(e =>
        {
            var path = _engines.Locate(e);
            var bundled = path is not null && path.StartsWith(bundleRoot, StringComparison.Ordinal);
            return new EngineRow(e, _loc["engine.name." + e], path is not null, bundled, EngineHints.InstallCommand(e, _session.Os, EngineHints.Current));
        }).ToList();
    }

    private IReadOnlyList<string> ApplySettings(AppSettings shared, DesktopSettings desktop)
    {
        var errors = SettingsValidator.Validate(shared);
        if (errors.Count > 0) return errors;
        var loginChanged = desktop.StartAtLogin != _desktop.StartAtLogin;
        var menusChanged = desktop.FileManagerEntries != _desktop.FileManagerEntries;
        SaveShared(shared with { PinnedItems = _settings.PinnedItems, Paused = _settings.Paused });
        SaveDesktop(desktop);
        try
        {
            if (loginChanged) Log.Info(LoginItem.Apply(desktop.StartAtLogin, _dirs));
            if (menusChanged) IntegrateWithDesktop();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("apply settings", ex);
        }
        return [];
    }

    private void SaveShared(AppSettings s)
    {
        _settings = s;
        _settingsStore.Save(s);
    }

    private void SaveDesktop(DesktopSettings s)
    {
        _desktop = s;
        _desktopStore.Save(s);
    }

    // ------------------------------------------------------------------------------------------------------------------ tray

    private void CreateTray()
    {
        NativeMenuItem Item(string key, Action run)
        {
            var item = new NativeMenuItem(_loc[key]);
            item.Click += (_, _) => run();
            return item;
        }

        _pauseItem = Item(_settings.Paused ? "tray.resume" : "tray.pause", TogglePause);
        var menu = new NativeMenu();
        menu.Add(Item("tray.settings", ShowSettings));
        menu.Add(Item("tray.history", ShowHistory));
        menu.Add(Item("tray.shelf", ShowShelf));
        menu.Add(Item("tray.welcome", ShowWelcome));
        menu.Add(_pauseItem);
        menu.Add(new NativeMenuItemSeparator());
        menu.Add(Item("tray.quit", Quit));

        _tray = new TrayIcon
        {
            Icon = Kit.Icon,
            ToolTipText = _settings.Paused ? _loc["tray.tooltip.paused"] : _loc["tray.tooltip"],
            Menu = menu,
            IsVisible = true,
        };
        TrayIcon.SetIcons(_app, [_tray]);
    }

    private void TogglePause()
    {
        SaveShared(_settings with { Paused = !_settings.Paused });
        StartWatcher();
        if (_pauseItem is not null) _pauseItem.Header = _loc[_settings.Paused ? "tray.resume" : "tray.pause"];
        if (_tray is not null) _tray.ToolTipText = _settings.Paused ? _loc["tray.tooltip.paused"] : _loc["tray.tooltip"];
    }

    private void Quit()
    {
        _watcher.Dispose();
        foreach (var converter in _registry.Converters.OfType<IDisposable>()) converter.Dispose();
        if (_tray is not null) _tray.IsVisible = false;
        Log.Info("DropFlow desktop stopped");
        _lifetime.Shutdown();
    }

    // ------------------------------------------------------------------------------------------------------------------ snapshots

    /// <summary>Access for <see cref="Snapshotter"/>: the real windows, filled the way a user would see them.</summary>
    internal (WheelWindow Window, WheelSession Session, WheelPresenter Presenter) WheelParts => (_wheel, _wheelSession, _presenter);
    internal DesktopLocalizer Localizer => _loc;
    internal ToolRegistry Registry => _registry;
    internal void Shutdown() => _lifetime.Shutdown();
}
