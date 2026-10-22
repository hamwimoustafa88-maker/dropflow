// SPDX-License-Identifier: GPL-3.0-or-later
namespace DropFlow.App.Views.Editors;

/// <summary>
/// Renders previews "latest request wins": every request cancels the one before it, waits a short quiet period (so dragging a slider
/// does not start a render per pixel), renders on a background task, and hands only the newest result to <c>apply</c> on the caller's
/// context (the UI thread). Results of stale renders are disposed, never shown. Not tied to WPF, so it is unit-tested.
/// </summary>
internal sealed class PreviewScheduler<T>(TimeSpan? quiet = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    public static readonly TimeSpan DefaultQuiet = TimeSpan.FromMilliseconds(100);

    private readonly TimeSpan _quiet = quiet ?? DefaultQuiet;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;
    private CancellationTokenSource? _current;
    private int _version;
    private int _running;

    /// <summary>True while a render is waiting or running.</summary>
    public bool IsBusy => Volatile.Read(ref _running) > 0;

    /// <summary>Raised (on the caller's context) when <see cref="IsBusy"/> may have changed.</summary>
    public event Action? BusyChanged;

    /// <summary>The number of results handed to <c>apply</c> so far (tests and snapshots wait on it).</summary>
    public int Applied { get; private set; }

    public async Task RequestAsync(Func<CancellationToken, T> render, Action<T> apply, Action<Exception>? failed = null)
    {
        _current?.Cancel();
        var cts = new CancellationTokenSource();
        _current = cts;
        var version = ++_version;
        Interlocked.Increment(ref _running);
        BusyChanged?.Invoke();
        try
        {
            await _delay(_quiet, cts.Token);
            var result = await Task.Run(() => render(cts.Token), cts.Token);
            if (version == _version && !cts.IsCancellationRequested)
            {
                Applied++;
                apply(result);
            }
            else
            {
                (result as IDisposable)?.Dispose();
            }
        }
        catch (OperationCanceledException)
        {
            // a newer request replaced this one
        }
        catch (Exception ex) when (failed is not null)
        {
            if (version == _version) failed(ex);    // a stale render's failure no longer matters
        }
        finally
        {
            Interlocked.Decrement(ref _running);
            BusyChanged?.Invoke();
            if (ReferenceEquals(_current, cts)) _current = null;
            cts.Dispose();
        }
    }

    /// <summary>Drops whatever is pending (window closing).</summary>
    public void Cancel()
    {
        _version++;
        _current?.Cancel();
    }
}
