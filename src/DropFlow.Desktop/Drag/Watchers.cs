// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text;
using Avalonia.Threading;
using DropFlow.Core.Settings;
using DropFlow.Desktop.App;
using DropFlow.Desktop.Native;

namespace DropFlow.Desktop.Drag;

/// <summary>Shared event plumbing: raises the interface's events on the UI thread from decider signals.</summary>
public abstract class DragWatcherBase : IDragWatcher
{
    public abstract string Name { get; }
    public event Action<int, int>? DragStarted;
    public event Action<TriggerKey, int, int, IReadOnlyList<string>>? KeyTapped;
    public event Action? DragEnded;

    public abstract bool Start();
    public abstract void Dispose();

    /// <summary>Raises the matching event. Call on the UI thread.</summary>
    protected void Raise(DragSignal signal, IReadOnlyList<string>? files = null)
    {
        switch (signal.Kind)
        {
            case DragSignalKind.Started: DragStarted?.Invoke(signal.X, signal.Y); break;
            case DragSignalKind.KeyTapped when signal.Key is { } key: KeyTapped?.Invoke(key, signal.X, signal.Y, files ?? []); break;
            case DragSignalKind.Ended: DragEnded?.Invoke(); break;
        }
    }

    protected void Post(DragSignal signal) => Dispatcher.UIThread.Post(() => Raise(signal));
}

/// <summary>
/// macOS without the Accessibility permission. While the button is up, a 5 Hz timer only remembers the drag pasteboard's change count.
/// Once the button is down it samples at 60 Hz: buttons, modifiers and pointer from <c>NSEvent</c>, and whether the drag pasteboard
/// changed since the button was up and now holds file URLs. Runs on the UI (main) thread, where AppKit objects belong.
/// Pointer coordinates are Cocoa points (bottom-left origin); the wheel window is placed with those directly.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class MacDragWatcher : DragWatcherBase
{
    private static readonly TimeSpan Idle = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan Active = TimeSpan.FromMilliseconds(1000.0 / 60);

    private readonly DragDecider _decider = new(threshold: 4);
    private DispatcherTimer? _timer;
    private MacDragPasteboard? _pasteboard;
    private long _idleChangeCount;

    public override string Name => "macOS drag pasteboard";

    public override bool Start()
    {
        try
        {
            _pasteboard = new MacDragPasteboard();
            if (!_pasteboard.IsAvailable) return false;
            _idleChangeCount = _pasteboard.ChangeCount();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            Log.Error("mac drag watcher", ex);
            return false;
        }
        _timer = new DispatcherTimer(Idle, DispatcherPriority.Input, (_, _) => Tick());
        _timer.Start();
        return true;
    }

    private void Tick()
    {
        var down = ModifierMap.MacLeftButtonDown(MacInput.PressedMouseButtons());
        if (!down && !_decider.IsDragging) _idleChangeCount = _pasteboard!.ChangeCount();
        var p = MacInput.MouseLocation();
        var source = down && _pasteboard!.ChangeCount() != _idleChangeCount && _pasteboard.HasFiles();
        var sample = new DragSample(down, ModifierMap.FromMacFlags(MacInput.ModifierFlags()), (int)Math.Round(p.X), (int)Math.Round(p.Y), source);

        foreach (var signal in _decider.Feed(sample))
            Raise(signal, signal.Kind == DragSignalKind.KeyTapped ? _pasteboard!.ReadFiles() : null);

        var wanted = down ? Active : Idle;
        if (_timer!.Interval != wanted) _timer.Interval = wanted;
    }

    public override void Dispose() => _timer?.Stop();
}

/// <summary>
/// Linux X11 without special permissions: a second X connection on its own thread. XFixes reports when a program takes the
/// <c>XdndSelection</c> (every toolkit does as a drag of data starts); <c>XQueryPointer</c> gives buttons, modifiers and the pointer
/// (60 Hz while the button is down, 25 Hz otherwise). Signals are posted to the UI thread.
/// </summary>
public sealed class X11DragWatcher : DragWatcherBase
{
    private readonly DragDecider _decider = new(threshold: 6);
    private Thread? _thread;
    private volatile bool _running;

    public override string Name => "X11 (XFixes + pointer)";

    public override bool Start()
    {
        if (_thread is not null) return true;
        _running = true;
        var ready = new ManualResetEventSlim();
        var ok = false;
        _thread = new Thread(() => Loop(ready, value => ok = value)) { IsBackground = true, Name = "DropFlow X11 drag watcher" };
        _thread.Start();
        ready.Wait(TimeSpan.FromSeconds(3));
        return ok;
    }

    private void Loop(ManualResetEventSlim ready, Action<bool> started)
    {
        IntPtr display;
        try { display = X11.XOpenDisplay(IntPtr.Zero); }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            Log.Error("libX11 not available", ex);
            started(false);
            ready.Set();
            return;
        }
        if (display == IntPtr.Zero)
        {
            started(false);
            ready.Set();
            return;
        }

        var root = X11.XDefaultRootWindow(display);
        var xdnd = X11.XInternAtom(display, "XdndSelection", 0);
        var fixesEvent = 0;
        var hasFixes = false;
        try
        {
            hasFixes = X11.XFixesQueryExtension(display, out fixesEvent, out _) != 0;
            if (hasFixes) X11.XFixesSelectSelectionInput(display, root, xdnd, X11.XFixesSetSelectionOwnerNotifyMask);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            Log.Info("XFixes missing: drags are recognised by the XdndSelection owner alone");
        }
        X11.XFlush(display);
        started(true);
        ready.Set();

        var buffer = new byte[192];   // sizeof(XEvent) on LP64
        var ownerChanged = false;
        try
        {
            while (_running)
            {
                while (X11.XPending(display) > 0)
                {
                    X11.XNextEvent(display, buffer);
                    if (hasFixes && BitConverter.ToInt32(buffer, 0) == fixesEvent + X11.XFixesSelectionNotify) ownerChanged = true;
                }

                if (X11.XQueryPointer(display, root, out _, out _, out var x, out var y, out _, out _, out var mask) == 0)
                {
                    Thread.Sleep(100);
                    continue;
                }

                var down = ModifierMap.X11ButtonOneDown(mask);
                // ownerChanged is cleared on every sample with the button up, so only a selection taken during this press counts
                var source = down && (ownerChanged || (!hasFixes && X11.XGetSelectionOwner(display, xdnd) != IntPtr.Zero));
                foreach (var signal in _decider.Feed(new DragSample(down, ModifierMap.FromX11Mask(mask), x, y, source))) Post(signal);
                if (!down) ownerChanged = false;
                Thread.Sleep(down ? 16 : 40);
            }
        }
        finally
        {
            X11.XCloseDisplay(display);
        }
    }

    public override void Dispose()
    {
        _running = false;
        _thread?.Join(500);
    }
}

/// <summary>
/// GNOME on Wayland: DropFlow's Shell extension connects to <c>$XDG_RUNTIME_DIR/dropflow/drag.sock</c> (folder 0700, socket 0600) and
/// writes the <see cref="DragLineProtocol"/>. Each line becomes an event on the UI thread.
/// </summary>
public sealed class ShellDragWatcher(string socketPath) : DragWatcherBase
{
    private readonly CancellationTokenSource _stop = new();
    private Socket? _listener;

    public override string Name => "GNOME Shell extension";

    public override bool Start()
    {
        try
        {
            InstanceChannel.CreatePrivateDirectory(Path.GetDirectoryName(socketPath)!);
            if (File.Exists(socketPath)) File.Delete(socketPath);
            _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            _listener.Bind(new UnixDomainSocketEndPoint(socketPath));
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(socketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            _listener.Listen(4);
            _ = Task.Run(() => AcceptAsync(_listener, _stop.Token));
            return true;
        }
        catch (Exception ex) when (ex is IOException or SocketException or UnauthorizedAccessException)
        {
            Log.Error("drag socket", ex);
            return false;
        }
    }

    private async Task AcceptAsync(Socket listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Socket client;
            try { client = await listener.AcceptAsync(ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException) { return; }
            _ = Task.Run(async () =>
            {
                using (client)
                {
                    try
                    {
                        using var reader = new StreamReader(new NetworkStream(client, ownsSocket: false), Encoding.UTF8);
                        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
                            if (DragLineProtocol.Parse(line) is { } signal) Post(signal);
                    }
                    catch (Exception ex) when (ex is IOException or OperationCanceledException or SocketException) { }
                }
            }, ct);
        }
    }

    public override void Dispose()
    {
        _stop.Cancel();
        _listener?.Dispose();
        try { if (File.Exists(socketPath)) File.Delete(socketPath); } catch (IOException) { }
    }
}
