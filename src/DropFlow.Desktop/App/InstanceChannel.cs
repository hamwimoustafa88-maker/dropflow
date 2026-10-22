// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace DropFlow.Desktop.App;

/// <summary>
/// One DropFlow per user on macOS and Linux. The first launch holds an exclusive lock file next to a Unix domain socket and listens on
/// it; later launches (file-manager entries, a second double-click) send their arguments and exit. The socket lives in a folder only
/// the user can open ($XDG_RUNTIME_DIR/dropflow or ~/Library/Application Support/DropFlow) and is itself made 0600. Framing: a 4-byte
/// little-endian length, then a UTF-8 JSON array of strings (the same framing as the Windows pipe).
/// </summary>
public sealed class InstanceChannel : IDisposable
{
    private const int MaxPayload = 1_000_000;
    private readonly string _socketPath;
    private readonly FileStream _lock;
    private readonly CancellationTokenSource _stop = new();
    private Socket? _listener;

    private InstanceChannel(string socketPath, FileStream lockFile)
    {
        _socketPath = socketPath;
        _lock = lockFile;
    }

    /// <summary>Raised on a thread-pool thread with the arguments another launch forwarded.</summary>
    public event Action<string[]>? ArgumentsReceived;

    /// <summary>Claims the instance. Returns null when another DropFlow already holds it.</summary>
    public static InstanceChannel? TryBecomePrimary(string socketPath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(socketPath))!;
        CreatePrivateDirectory(dir);
        try
        {
            // FileShare.None is an exclusive lock on every OS (.NET uses flock on Unix); the OS drops it when the process dies.
            var lockFile = new FileStream(socketPath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return new InstanceChannel(socketPath, lockFile);
        }
        catch (IOException)
        {
            return null;
        }
    }

    public void StartListening()
    {
        try { if (File.Exists(_socketPath)) File.Delete(_socketPath); }      // left over from a crash; we hold the lock
        catch (IOException) { }

        _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        _listener.Bind(new UnixDomainSocketEndPoint(_socketPath));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(_socketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        _listener.Listen(8);
        _ = Task.Run(() => AcceptLoopAsync(_listener, _stop.Token));
    }

    /// <summary>Sends arguments to the running instance. False when none answers in time.</summary>
    public static bool Forward(string socketPath, string[] args, TimeSpan timeout)
    {
        if (!File.Exists(socketPath)) return false;
        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            using var cts = new CancellationTokenSource(timeout);
            socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), cts.Token).AsTask().GetAwaiter().GetResult();
            var payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(args));
            socket.Send(BitConverter.GetBytes(payload.Length));
            socket.Send(payload);
            socket.Shutdown(SocketShutdown.Send);
            return true;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or IOException)
        {
            return false;
        }
    }

    private async Task AcceptLoopAsync(Socket listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Socket client;
            try { client = await listener.AcceptAsync(ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException) { return; }
            _ = Task.Run(() => ServeAsync(client, ct), ct);
        }
    }

    private async Task ServeAsync(Socket client, CancellationToken ct)
    {
        using (client)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                await using var stream = new NetworkStream(client, ownsSocket: false);
                var header = new byte[4];
                await stream.ReadExactlyAsync(header, timeout.Token).ConfigureAwait(false);
                var length = BitConverter.ToInt32(header);
                if (length is < 0 or > MaxPayload) return;
                var body = new byte[length];
                await stream.ReadExactlyAsync(body, timeout.Token).ConfigureAwait(false);
                var args = JsonSerializer.Deserialize<string[]>(body);
                if (args is not null) ArgumentsReceived?.Invoke(args);
            }
            catch (Exception ex) when (ex is IOException or JsonException or EndOfStreamException or OperationCanceledException or SocketException)
            {
                // a client that hung up early or sent junk never stops the server
            }
        }
    }

    /// <summary>Creates the socket folder readable only by the user (0700), as XDG requires for runtime files.</summary>
    internal static void CreatePrivateDirectory(string dir)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(dir);
            return;
        }
        Directory.CreateDirectory(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener?.Dispose();
        try { if (_listener is not null && File.Exists(_socketPath)) File.Delete(_socketPath); } catch (IOException) { }
        _lock.Dispose();
        _stop.Dispose();
    }
}
