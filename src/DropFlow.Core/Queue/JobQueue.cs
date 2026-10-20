using System.Diagnostics;
using DropFlow.Core.Catalog;
using DropFlow.Core.Operations;
using DropFlow.Core.Registry;

namespace DropFlow.Core.Queue;

public enum JobState { Queued, Running, Completed, Failed, Cancelled }

/// <summary>One unit of work: a single file, or the whole selection for multi-file tools such as merge.</summary>
public sealed class JobHandle
{
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource<ConversionResult> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal JobHandle(ConversionJob job) => Job = job;

    public Guid Id { get; } = Guid.NewGuid();
    public ConversionJob Job { get; }
    public JobState State { get; internal set; } = JobState.Queued;
    public double Progress { get; internal set; }
    public string? Message { get; internal set; }
    public ConversionResult? Result { get; internal set; }
    public Task<ConversionResult> Completion => _done.Task;

    internal CancellationToken Token => _cts.Token;
    internal void Complete(ConversionResult result) => _done.TrySetResult(result);
    public void Cancel() => _cts.Cancel();
}

/// <summary>Everything the user dropped in one gesture: one job per file, or a single job for multi-file tools.</summary>
public sealed class BatchHandle
{
    internal BatchHandle(ConversionJob job, IReadOnlyList<JobHandle> jobs)
    {
        Job = job;
        Jobs = jobs;
        Completion = Task.WhenAll(jobs.Select(j => j.Completion)).ContinueWith(t => Aggregate(t.Result), TaskScheduler.Default);
    }

    public Guid Id { get; } = Guid.NewGuid();
    public ConversionJob Job { get; }
    public IReadOnlyList<JobHandle> Jobs { get; }
    public Task<ConversionResult> Completion { get; }

    public double Progress => Jobs.Count == 0 ? 1 : Jobs.Average(j => j.State is JobState.Completed or JobState.Failed or JobState.Cancelled ? 1 : j.Progress);
    public bool IsFinished => Jobs.All(j => j.State is JobState.Completed or JobState.Failed or JobState.Cancelled);

    public void Cancel()
    {
        foreach (var j in Jobs) j.Cancel();
    }

    private static ConversionResult Aggregate(ConversionResult[] results)
    {
        var outputs = results.SelectMany(r => r.Outputs).ToList();
        var errors = results.Where(r => !r.Success).Select(r => r.Error).Where(e => !string.IsNullOrEmpty(e)).ToList();
        var duration = results.Length == 0 ? TimeSpan.Zero : TimeSpan.FromTicks(results.Sum(r => r.Duration.Ticks));
        var notes = results.Select(r => r.Note).Where(n => !string.IsNullOrEmpty(n)).Distinct().ToList();
        var note = notes.Count == 0 ? null : string.Join("; ", notes);
        return errors.Count == 0 && results.All(r => r.Success)
            ? ConversionResult.Ok(outputs, duration, note)
            : new ConversionResult(false, outputs, string.Join("; ", errors), duration, note);
    }
}

/// <summary>
/// Runs jobs in the background with bounded concurrency. The UI subscribes to <see cref="Changed"/> and never blocks.
/// </summary>
public sealed class JobQueue(ToolRegistry registry, ToolCatalog catalog, int maxConcurrency = 2)
{
    private readonly SemaphoreSlim _slots = new(Math.Max(1, maxConcurrency));
    private readonly List<BatchHandle> _batches = [];
    private readonly object _gate = new();

    /// <summary>Raised whenever any job changes state or reports progress. May fire on any thread.</summary>
    public event Action<BatchHandle>? Changed;

    /// <summary>Raised once when every job in a batch has finished.</summary>
    public event Action<BatchHandle, ConversionResult>? BatchCompleted;

    public IReadOnlyList<BatchHandle> Batches
    {
        get { lock (_gate) return _batches.ToList(); }
    }

    public BatchHandle Enqueue(ConversionJob job)
    {
        var multi = !Operation.IsConvert(job.Operation) && (catalog.Tool(job.Operation)?.Multi ?? false);
        var jobs = (multi || job.Inputs.Count <= 1
                ? [job]
                : job.Inputs.Select(i => job with { Inputs = [i] }).ToList())
            .Select(j => new JobHandle(j))
            .ToList();

        var batch = new BatchHandle(job, jobs);
        lock (_gate) _batches.Add(batch);

        foreach (var handle in jobs) _ = Task.Run(() => RunAsync(batch, handle));
        _ = batch.Completion.ContinueWith(t => BatchCompleted?.Invoke(batch, t.Result), TaskScheduler.Default);
        return batch;
    }

    private async Task RunAsync(BatchHandle batch, JobHandle handle)
    {
        var started = Stopwatch.StartNew();
        try
        {
            await _slots.WaitAsync(handle.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Finish(batch, handle, JobState.Cancelled, ConversionResult.Fail("Cancelled", started.Elapsed));
            return;
        }

        try
        {
            handle.State = JobState.Running;
            Changed?.Invoke(batch);

            var ext = ToolCatalog.ExtensionOf(handle.Job.Inputs[0]);
            var converter = registry.Find(handle.Job.Operation, ext);
            if (converter is null)
            {
                Finish(batch, handle, JobState.Failed, ConversionResult.Fail($"No converter for '{handle.Job.Operation}' on .{ext}", started.Elapsed));
                return;
            }

            var progress = new InlineProgress(p =>
            {
                handle.Progress = Math.Clamp(p.Fraction, 0, 1);
                handle.Message = p.Message;
                Changed?.Invoke(batch);
            });

            var result = await converter.RunAsync(handle.Job, progress, handle.Token).ConfigureAwait(false);
            result = result with { Duration = started.Elapsed };
            Finish(batch, handle, result.Success ? JobState.Completed : JobState.Failed, result);
        }
        catch (OperationCanceledException)
        {
            Finish(batch, handle, JobState.Cancelled, ConversionResult.Fail("Cancelled", started.Elapsed));
        }
        catch (Exception ex)
        {
            Finish(batch, handle, JobState.Failed, ConversionResult.Fail(ex.Message, started.Elapsed));
        }
        finally
        {
            _slots.Release();
        }
    }

    private void Finish(BatchHandle batch, JobHandle handle, JobState state, ConversionResult result)
    {
        handle.State = state;
        handle.Progress = 1;
        handle.Result = result;
        handle.Complete(result);
        Changed?.Invoke(batch);
    }

    /// <summary>Progress sink that calls the callback inline (no synchronisation-context hop), so ordering is deterministic.</summary>
    private sealed class InlineProgress(Action<JobProgress> report) : IProgress<JobProgress>
    {
        public void Report(JobProgress value) => report(value);
    }
}
