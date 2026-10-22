// SPDX-License-Identifier: GPL-3.0-or-later
namespace DropFlow.Core.Operations;

/// <summary>
/// An <see cref="IProgress{T}"/> that runs its callback on the reporting thread, before <c>Report</c> returns.
/// <see cref="Progress{T}"/> posts every report to the thread pool, so a converter that forwards an inner step's progress to the job's progress
/// can see its last inner report arrive AFTER its final "done" report and move the bar backwards. Converters use this for inner steps.
/// </summary>
public sealed class InlineProgress<T>(Action<T> handler) : IProgress<T>
{
    public void Report(T value) => handler(value);
}
