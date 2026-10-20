using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace DropFlow.Core.App;

/// <summary>
/// Only one DropFlow runs per user. The first process owns a named mutex and listens on a named pipe; later launches (the context
/// menu, "Send to", a second double-click) send their command line to it and exit.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private readonly Mutex? _mutex;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _stop = new();
    private Task? _server;

    private SingleInstance(Mutex? mutex, string name, bool isPrimary)
    {
        _mutex = mutex;
        _pipeName = name + ".pipe";
        IsPrimary = isPrimary;
    }

    public bool IsPrimary { get; }

    /// <summary>Raised (on a thread-pool thread) when another launch forwarded its arguments to this instance.</summary>
    public event Action<string[]>? ArgumentsReceived;

    /// <summary>Claims the instance name. <paramref name="name"/> must not contain backslashes.</summary>
    public static SingleInstance Acquire(string name)
    {
        var mutex = new Mutex(initiallyOwned: true, @"Local\" + name, out var created);
        if (created) return new SingleInstance(mutex, name, true);
        mutex.Dispose();
        return new SingleInstance(null, name, false);
    }

    /// <summary>Starts listening for forwarded launches (primary instance only).</summary>
    public void StartListening()
    {
        if (!IsPrimary) throw new InvalidOperationException("Only the primary instance listens.");
        _server = Task.Run(() => ServeAsync(_stop.Token));
    }

    /// <summary>Sends this process' arguments to the primary instance. Returns false when it could not be reached in time.</summary>
    public bool Forward(string[] args, TimeSpan timeout)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out);
            client.Connect((int)timeout.TotalMilliseconds);
            var payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(args));
            client.Write(BitConverter.GetBytes(payload.Length));
            client.Write(payload);
            client.Flush();
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private async Task ServeAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(_pipeName, PipeDirection.In, 4, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);

                var header = new byte[4];
                await server.ReadExactlyAsync(header, ct).ConfigureAwait(false);
                var length = BitConverter.ToInt32(header);
                if (length is < 0 or > 1_000_000) continue;          // ignore garbage, keep serving

                var body = new byte[length];
                await server.ReadExactlyAsync(body, ct).ConfigureAwait(false);
                var args = JsonSerializer.Deserialize<string[]>(body) ?? [];
                ArgumentsReceived?.Invoke(args);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or JsonException or EndOfStreamException)
            {
                // a client that disconnected early or sent junk must never stop the server
            }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        try { _server?.Wait(TimeSpan.FromSeconds(1)); } catch (AggregateException) { /* cancelled */ }
        if (IsPrimary)
        {
            try { _mutex?.ReleaseMutex(); } catch (ApplicationException) { /* released on another thread: the OS frees it at exit */ }
            _mutex?.Dispose();
        }
        _stop.Dispose();
    }
}
