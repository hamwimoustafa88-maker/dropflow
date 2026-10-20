using System.Diagnostics;
using System.Text;

namespace DropFlow.Engines.Common;

public sealed record ProcessOptions
{
    /// <summary>Kill the process (and its children) after this long. Null = no limit.</summary>
    public TimeSpan? Timeout { get; init; }
    public string? WorkingDirectory { get; init; }

    /// <summary>Called for every stdout line as it arrives (e.g. ffmpeg's "-progress pipe:1").</summary>
    public Action<string>? OnOutputLine { get; init; }

    /// <summary>Called for every stderr line as it arrives.</summary>
    public Action<string>? OnErrorLine { get; init; }

    public IReadOnlyDictionary<string, string>? Environment { get; init; }

    /// <summary>Max characters kept for <see cref="ProcessResult.StdOut"/>/<see cref="ProcessResult.StdErr"/> (the tail is kept).</summary>
    public int MaxCapturedChars { get; init; } = 200_000;
}

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr, TimeSpan Duration, bool TimedOut, bool Cancelled)
{
    public bool Success => ExitCode == 0 && !TimedOut && !Cancelled;

    /// <summary>Last non-empty stderr line (or stdout when stderr is empty): a short human-readable failure reason.</summary>
    public string LastMessage =>
        (StdErr.Length > 0 ? StdErr : StdOut)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault() ?? $"exit code {ExitCode}";
}

/// <summary>
/// Runs an external engine the safe way: arguments are passed as a list (never a shell string), output is streamed and
/// size-capped, and the whole process tree is killed on timeout or cancellation. Nothing here blocks the calling thread.
/// </summary>
public static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(
        string executable,
        IEnumerable<string> arguments,
        ProcessOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new ProcessOptions();
        var psi = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = options.WorkingDirectory ?? "",
        };
        foreach (var a in arguments) psi.ArgumentList.Add(a);
        if (options.Environment is not null)
            foreach (var (k, v) in options.Environment) psi.Environment[k] = v;

        var stdout = new TailBuffer(options.MaxCapturedChars);
        var stderr = new TailBuffer(options.MaxCapturedChars);
        var sw = Stopwatch.StartNew();

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            stdout.AppendLine(e.Data);
            options.OnOutputLine?.Invoke(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            stderr.AppendLine(e.Data);
            options.OnErrorLine?.Invoke(e.Data);
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        process.StandardInput.Close(); // engines must never wait for interactive input

        using var timeoutCts = options.Timeout is { } t ? new CancellationTokenSource(t) : new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        var timedOut = false;
        var cancelled = false;
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            timedOut = timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested;
            cancelled = cancellationToken.IsCancellationRequested;
            Kill(process);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }

        // Flush the asynchronous output readers.
        if (!timedOut && !cancelled) await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);

        return new ProcessResult(process.HasExited ? process.ExitCode : -1, stdout.ToString(), stderr.ToString(), sw.Elapsed, timedOut, cancelled);
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { /* already exited */ }
    }

    private sealed class TailBuffer(int max)
    {
        private readonly StringBuilder _sb = new();
        private readonly object _gate = new();

        public void AppendLine(string line)
        {
            lock (_gate)
            {
                _sb.Append(line).Append('\n');
                if (_sb.Length > max) _sb.Remove(0, _sb.Length - max);
            }
        }

        public override string ToString()
        {
            lock (_gate) return _sb.ToString().TrimEnd();
        }
    }
}
