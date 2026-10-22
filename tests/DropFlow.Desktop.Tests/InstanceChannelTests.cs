using System.Net.Sockets;
using DropFlow.Desktop.App;

namespace DropFlow.Desktop.Tests;

/// <summary>Unix domain sockets also exist on Windows 10 1803+, so the single-instance channel is tested on every CI system.</summary>
public sealed class InstanceChannelTests : IDisposable
{
    // AF_UNIX paths are limited to ~104 bytes: keep the test folder short.
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dfi" + Guid.NewGuid().ToString("N")[..6]);

    public InstanceChannelTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string Socket => Path.Combine(_dir, "a.sock");

    [Fact]
    public async Task The_first_launch_is_primary_and_receives_what_a_second_launch_forwards()
    {
        using var primary = InstanceChannel.TryBecomePrimary(Socket);
        Assert.NotNull(primary);
        var received = new TaskCompletionSource<string[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        primary!.ArgumentsReceived += args => received.TrySetResult(args);
        primary.StartListening();

        Assert.Null(InstanceChannel.TryBecomePrimary(Socket));             // a second launch is not primary
        Assert.True(InstanceChannel.Forward(Socket, ["--wheel", "/tmp/ملف عربي.png"], TimeSpan.FromSeconds(3)));

        var args = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(["--wheel", "/tmp/ملف عربي.png"], args);
    }

    [Fact]
    public void Forwarding_to_nobody_fails_quickly() =>
        Assert.False(InstanceChannel.Forward(Socket, ["--settings"], TimeSpan.FromMilliseconds(500)));

    [Fact]
    public void A_stale_socket_file_from_a_crash_does_not_block_the_next_start()
    {
        File.WriteAllText(Socket, "left over");
        using var primary = InstanceChannel.TryBecomePrimary(Socket);
        Assert.NotNull(primary);
        primary!.StartListening();
        Assert.True(InstanceChannel.Forward(Socket, ["--shelf"], TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public async Task Junk_from_a_client_does_not_stop_the_server()
    {
        using var primary = InstanceChannel.TryBecomePrimary(Socket)!;
        var received = new TaskCompletionSource<string[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        primary.ArgumentsReceived += args => received.TrySetResult(args);
        primary.StartListening();

        using (var junk = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified))
        {
            junk.Connect(new UnixDomainSocketEndPoint(Socket));
            junk.Send([0xFF, 0xFF, 0xFF, 0x7F, 1, 2, 3]);                 // absurd length header
        }

        Assert.True(InstanceChannel.Forward(Socket, ["--history"], TimeSpan.FromSeconds(3)));
        Assert.Equal(["--history"], await received.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void After_the_primary_exits_a_new_launch_can_take_over()
    {
        var first = InstanceChannel.TryBecomePrimary(Socket)!;
        first.StartListening();
        first.Dispose();

        using var second = InstanceChannel.TryBecomePrimary(Socket);
        Assert.NotNull(second);
    }
}
