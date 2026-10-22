using DropFlow.Engines.Common;

namespace DropFlow.Engines.Common.Tests;

public class ProcessRunnerTests
{
    private const string Cmd = "cmd.exe";

    [Fact]
    public async Task Captures_stdout_and_a_zero_exit_code()
    {
        var r = await ProcessRunner.RunAsync(Cmd, ["/c", "echo hello"]);
        Assert.True(r.Success);
        Assert.Equal("hello", r.StdOut);
    }

    [Fact]
    public async Task Reports_a_failing_exit_code_and_the_last_stderr_line()
    {
        var r = await ProcessRunner.RunAsync(Cmd, ["/c", "echo first 1>&2 & echo broken 1>&2 & exit 3"]);
        Assert.False(r.Success);
        Assert.Equal(3, r.ExitCode);
        Assert.Equal("broken", r.LastMessage);
    }

    [Fact]
    public async Task Streams_output_lines_while_the_process_runs()
    {
        var lines = new List<string>();
        var r = await ProcessRunner.RunAsync(Cmd, ["/c", "echo a & echo b & echo c"], new ProcessOptions { OnOutputLine = l => { lock (lines) lines.Add(l); } });
        Assert.True(r.Success);
        Assert.Equal(["a", "b", "c"], lines.Select(l => l.Trim()));
    }

    [Fact]
    public async Task Arguments_with_spaces_and_quotes_reach_the_program_intact()
    {
        // The argument list is never turned into a shell string, so spaces/quotes/ampersands are plain data.
        var script = Path.Combine(Path.GetTempPath(), $"dropflow-echo-{Guid.NewGuid():N}.ps1");
        await File.WriteAllTextAsync(script, "param([string]$s) [Console]::Out.Write($s)");
        try
        {
            var r = await ProcessRunner.RunAsync("powershell.exe", ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, "a b & \"c\""]);
            Assert.Equal("a b & \"c\"", r.StdOut);
        }
        finally
        {
            File.Delete(script);
        }
    }

    [Fact]
    public async Task A_timeout_kills_the_process()
    {
        var r = await ProcessRunner.RunAsync(Cmd, ["/c", "ping -n 20 127.0.0.1 >nul"], new ProcessOptions { Timeout = TimeSpan.FromMilliseconds(400) });
        Assert.True(r.TimedOut);
        Assert.False(r.Success);
        Assert.True(r.Duration < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Cancellation_kills_the_process_tree()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
        var r = await ProcessRunner.RunAsync(Cmd, ["/c", "ping -n 20 127.0.0.1 >nul"], cancellationToken: cts.Token);
        Assert.True(r.Cancelled);
        Assert.False(r.TimedOut);
    }

    [Fact]
    public async Task A_process_that_waits_for_input_does_not_hang()
    {
        // stdin is closed immediately, so "set /p" gets EOF instead of blocking forever.
        var r = await ProcessRunner.RunAsync(Cmd, ["/c", "set /p x= & echo done"], new ProcessOptions { Timeout = TimeSpan.FromSeconds(10) });
        Assert.False(r.TimedOut);
    }

    [Fact]
    public async Task Captured_output_is_capped_keeping_the_tail()
    {
        var r = await ProcessRunner.RunAsync("powershell.exe", ["-NoProfile", "-Command", "1..2000 | ForEach-Object { 'line' + $_ }"], new ProcessOptions { MaxCapturedChars = 500 });
        Assert.True(r.StdOut.Length <= 501);
        Assert.EndsWith("line2000", r.StdOut);
    }

    [Fact]
    public async Task Binary_stdout_is_copied_byte_for_byte_into_the_given_stream()
    {
        // Bytes that a text reader would mangle: zero, CR/LF, and values above 0x7F.
        using var sink = new MemoryStream();
        var r = await ProcessRunner.RunAsync("powershell.exe",
            ["-NoProfile", "-Command", "$o=[Console]::OpenStandardOutput(); $b=[byte[]](0,13,10,255,128,65,0,200); $o.Write($b,0,$b.Length); $o.Flush(); [Console]::Error.WriteLine('note')"],
            new ProcessOptions { StandardOutput = sink, Timeout = TimeSpan.FromSeconds(30) });

        Assert.True(r.Success, r.StdErr);
        Assert.Equal(new byte[] { 0, 13, 10, 255, 128, 65, 0, 200 }, sink.ToArray());
        Assert.Equal("", r.StdOut);
        Assert.Equal("note", r.StdErr);
    }

    [Fact]
    public async Task Cancelling_a_binary_run_kills_the_process()
    {
        using var sink = new MemoryStream();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
        var r = await ProcessRunner.RunAsync(Cmd, ["/c", "ping -n 20 127.0.0.1"], new ProcessOptions { StandardOutput = sink }, cts.Token);
        Assert.True(r.Cancelled);
    }

    [Fact]
    public async Task A_missing_executable_throws_instead_of_hanging()
    {
        await Assert.ThrowsAnyAsync<Exception>(() => ProcessRunner.RunAsync(@"C:\definitely\missing.exe", []));
    }
}
