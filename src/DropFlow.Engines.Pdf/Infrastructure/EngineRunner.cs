using DropFlow.Engines.Common;

namespace DropFlow.Engines.Pdf.Infrastructure;

/// <summary>Runs an external engine. A seam over <see cref="ProcessRunner"/> so orchestration can be tested without the engine installed.</summary>
internal interface IEngineRunner
{
    Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, ProcessOptions? options, CancellationToken cancellationToken);
}

internal sealed class ProcessEngineRunner : IEngineRunner
{
    public Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, ProcessOptions? options, CancellationToken cancellationToken) =>
        ProcessRunner.RunAsync(executable, arguments, options, cancellationToken);
}
