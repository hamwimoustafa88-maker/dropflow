using DropFlow.Core.Catalog;
using DropFlow.Core.Operations;
using DropFlow.Core.Queue;
using DropFlow.Core.Registry;

namespace DropFlow.Core.Tests;

public class JobQueueTests
{
    private static readonly ToolCatalog Catalog = ToolCatalog.LoadEmbedded();

    private static JobQueue Queue(ToolRegistry registry, int concurrency = 2) => new(registry, Catalog, concurrency);

    private static TimeSpan Timeout => TimeSpan.FromSeconds(5);

    [Fact]
    public async Task A_single_file_runs_and_reports_its_outputs()
    {
        var registry = new ToolRegistry().Register(new FakeConverter(["convert:png"]));
        var batch = Queue(registry).Enqueue(new ConversionJob([@"C:\a.heic"], "convert:png"));

        var result = await batch.Completion.WaitAsync(Timeout);

        Assert.True(result.Success);
        Assert.Equal([@"C:\a.heic.out"], result.Outputs);
        Assert.Equal(JobState.Completed, batch.Jobs[0].State);
        Assert.Equal(1, batch.Progress);
    }

    [Fact]
    public async Task Several_files_become_one_job_each_for_a_normal_operation()
    {
        var registry = new ToolRegistry().Register(new FakeConverter(["convert:png"]));
        var batch = Queue(registry).Enqueue(new ConversionJob([@"C:\a.heic", @"C:\b.heic", @"C:\c.heic"], "convert:png"));

        var result = await batch.Completion.WaitAsync(Timeout);

        Assert.Equal(3, batch.Jobs.Count);
        Assert.Equal(3, result.Outputs.Count);
    }

    [Fact]
    public async Task A_multi_file_tool_gets_all_inputs_in_a_single_job()
    {
        ConversionJob? seen = null;
        var registry = new ToolRegistry().Register(new FakeConverter(["pdf.merge"], run: (job, _, _) =>
        {
            seen = job;
            return Task.FromResult(ConversionResult.Ok([@"C:\merged.pdf"]));
        }));

        var batch = Queue(registry).Enqueue(new ConversionJob([@"C:\a.pdf", @"C:\b.pdf"], "pdf.merge"));
        await batch.Completion.WaitAsync(Timeout);

        Assert.Single(batch.Jobs);
        Assert.Equal(2, seen!.Inputs.Count);
    }

    [Fact]
    public async Task Concurrency_is_bounded()
    {
        var running = 0;
        var peak = 0;
        var gate = new TaskCompletionSource();
        var registry = new ToolRegistry().Register(new FakeConverter(["convert:png"], run: async (job, _, ct) =>
        {
            var now = Interlocked.Increment(ref running);
            InterlockedMax(ref peak, now);
            await gate.Task.WaitAsync(ct);
            Interlocked.Decrement(ref running);
            return ConversionResult.Ok([job.Inputs[0] + ".out"]);
        }));

        var batch = Queue(registry, concurrency: 2).Enqueue(new ConversionJob(["1.heic", "2.heic", "3.heic", "4.heic"], "convert:png"));
        await Task.Delay(300);
        Assert.Equal(2, running);

        gate.SetResult();
        await batch.Completion.WaitAsync(Timeout);
        Assert.Equal(2, peak);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while (value > (current = Volatile.Read(ref target)))
            if (Interlocked.CompareExchange(ref target, value, current) == current) return;
    }

    [Fact]
    public async Task Progress_is_reported_through_the_queue_event()
    {
        var seen = new List<double>();
        var registry = new ToolRegistry().Register(new FakeConverter(["convert:png"], run: (job, progress, _) =>
        {
            progress.Report(new JobProgress(0.25));
            progress.Report(new JobProgress(0.75, "almost"));
            return Task.FromResult(ConversionResult.Ok([]));
        }));
        var queue = Queue(registry);
        queue.Changed += b => seen.Add(b.Jobs[0].Progress);

        await queue.Enqueue(new ConversionJob(["a.heic"], "convert:png")).Completion.WaitAsync(Timeout);

        Assert.Contains(0.25, seen);
        Assert.Contains(0.75, seen);
    }

    [Fact]
    public async Task Cancelling_a_running_job_ends_it_as_cancelled()
    {
        var started = new TaskCompletionSource();
        var registry = new ToolRegistry().Register(new FakeConverter(["convert:png"], run: async (_, _, ct) =>
        {
            started.SetResult();
            await Task.Delay(Timeout, ct);
            return ConversionResult.Ok([]);
        }));

        var batch = Queue(registry).Enqueue(new ConversionJob(["a.heic"], "convert:png"));
        await started.Task.WaitAsync(Timeout);
        batch.Cancel();
        var result = await batch.Completion.WaitAsync(Timeout);

        Assert.False(result.Success);
        Assert.Equal(JobState.Cancelled, batch.Jobs[0].State);
    }

    [Fact]
    public async Task Cancelling_a_queued_job_never_runs_it()
    {
        var gate = new TaskCompletionSource();
        var ran = new List<string>();
        var registry = new ToolRegistry().Register(new FakeConverter(["convert:png"], run: async (job, _, ct) =>
        {
            lock (ran) ran.Add(job.Inputs[0]);
            await gate.Task.WaitAsync(ct);
            return ConversionResult.Ok([]);
        }));

        var queue = Queue(registry, concurrency: 1);
        var first = queue.Enqueue(new ConversionJob(["first.heic"], "convert:png"));
        await Task.Delay(200);
        var second = queue.Enqueue(new ConversionJob(["second.heic"], "convert:png"));
        second.Cancel();
        await second.Completion.WaitAsync(Timeout);
        gate.SetResult();
        await first.Completion.WaitAsync(Timeout);

        Assert.Equal(["first.heic"], ran);
        Assert.Equal(JobState.Cancelled, second.Jobs[0].State);
    }

    [Fact]
    public async Task A_missing_converter_fails_the_job_without_throwing()
    {
        var batch = Queue(new ToolRegistry()).Enqueue(new ConversionJob(["a.heic"], "convert:png"));
        var result = await batch.Completion.WaitAsync(Timeout);

        Assert.False(result.Success);
        Assert.Contains("No converter", result.Error);
    }

    [Fact]
    public async Task A_converter_that_throws_fails_only_its_own_job()
    {
        var registry = new ToolRegistry().Register(new FakeConverter(["convert:png"], run: (job, _, _) =>
            job.Inputs[0] == "bad.heic" ? throw new InvalidOperationException("boom") : Task.FromResult(ConversionResult.Ok([job.Inputs[0] + ".out"]))));

        var batch = Queue(registry).Enqueue(new ConversionJob(["good.heic", "bad.heic"], "convert:png"));
        var result = await batch.Completion.WaitAsync(Timeout);

        Assert.False(result.Success);
        Assert.Equal(["good.heic.out"], result.Outputs);
        Assert.Contains("boom", result.Error);
        Assert.Equal(JobState.Completed, batch.Jobs[0].State);
        Assert.Equal(JobState.Failed, batch.Jobs[1].State);
    }

    [Fact]
    public async Task Notes_from_successful_jobs_are_kept_and_deduplicated()
    {
        var registry = new ToolRegistry().Register(new FakeConverter(["img.compress"], run: (job, _, _) =>
            Task.FromResult(ConversionResult.Ok([], note: "already optimal"))));

        var batch = Queue(registry).Enqueue(new ConversionJob(["a.png", "b.png"], "img.compress"));
        var result = await batch.Completion.WaitAsync(Timeout);

        Assert.True(result.Success);
        Assert.Equal("already optimal", result.Note);
    }

    [Fact]
    public async Task BatchCompleted_fires_once_with_the_aggregate()
    {
        var registry = new ToolRegistry().Register(new FakeConverter(["convert:png"]));
        var queue = Queue(registry);
        var fired = new TaskCompletionSource<ConversionResult>();
        var count = 0;
        queue.BatchCompleted += (_, r) => { Interlocked.Increment(ref count); fired.TrySetResult(r); };

        queue.Enqueue(new ConversionJob(["a.heic", "b.heic"], "convert:png"));
        var result = await fired.Task.WaitAsync(Timeout);
        await Task.Delay(100);

        Assert.Equal(2, result.Outputs.Count);
        Assert.Equal(1, count);
    }
}
