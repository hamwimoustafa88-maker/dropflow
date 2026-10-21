using Microsoft.ML.OnnxRuntime;

namespace DropFlow.AI.Imaging;

/// <summary>
/// Keeps the most recently used ONNX sessions alive (loading a 200 MB network takes seconds) and hands them out one run at a
/// time. Sessions are CPU-only: the plain Microsoft.ML.OnnxRuntime package has no GPU provider. A session that has not been used
/// for <see cref="IdleTimeout"/> is released so a desktop app does not hold hundreds of megabytes forever after one cut-out.
/// </summary>
public sealed class OnnxSessionCache : IDisposable
{
    public static readonly TimeSpan DefaultIdleTimeout = TimeSpan.FromMinutes(2);

    private readonly object _gate = new();
    private readonly int _capacity;
    private readonly List<(string Path, InferenceSession Session)> _entries = [];
    private readonly Timer _idleTimer;
    private bool _disposed;

    public OnnxSessionCache(int capacity = 2, TimeSpan? idleTimeout = null)
    {
        _capacity = Math.Max(1, capacity);
        IdleTimeout = idleTimeout ?? DefaultIdleTimeout;
        _idleTimer = new Timer(_ => ReleaseIfIdle(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public TimeSpan IdleTimeout { get; }

    /// <summary>Number of sessions currently held in memory.</summary>
    public int LoadedCount
    {
        get { lock (_gate) return _entries.Count; }
    }

    /// <summary>
    /// Loads (or reuses) the session for <paramref name="modelPath"/> and runs <paramref name="use"/> while holding it.
    /// Calls are serialised because one inference already uses all cores.
    /// </summary>
    public T Run<T>(string modelPath, Func<InferenceSession, T> use)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _idleTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            try
            {
                var session = Get(modelPath);
                try
                {
                    return use(session);
                }
                catch (OnnxRuntimeException ex)
                {
                    throw new ImageToolException($"The model '{Path.GetFileName(modelPath)}' could not run: {ex.Message}", ex);
                }
            }
            finally
            {
                if (_entries.Count > 0) _idleTimer.Change(IdleTimeout, Timeout.InfiniteTimeSpan);
            }
        }
    }

    private void ReleaseIfIdle()
    {
        // If a run is in progress the timer will be re-armed when it finishes.
        if (!Monitor.TryEnter(_gate)) return;
        try
        {
            if (_disposed) return;
            DisposeAll();
        }
        finally
        {
            Monitor.Exit(_gate);
        }
    }

    private InferenceSession Get(string modelPath)
    {
        var index = _entries.FindIndex(e => string.Equals(e.Path, modelPath, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            var hit = _entries[index];
            _entries.RemoveAt(index);
            _entries.Add(hit); // most recently used last
            return hit.Session;
        }

        InferenceSession session;
        try
        {
            using var options = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
            session = new InferenceSession(modelPath, options);
        }
        catch (OnnxRuntimeException ex)
        {
            throw new ImageToolException($"The model '{Path.GetFileName(modelPath)}' is damaged or not a valid ONNX file: {ex.Message}", ex);
        }

        _entries.Add((modelPath, session));
        while (_entries.Count > _capacity)
        {
            _entries[0].Session.Dispose();
            _entries.RemoveAt(0);
        }
        return session;
    }

    private void DisposeAll()
    {
        foreach (var (_, session) in _entries) session.Dispose();
        _entries.Clear();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _idleTimer.Dispose();
            DisposeAll();
        }
    }
}
