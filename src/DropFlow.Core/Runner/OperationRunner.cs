using DropFlow.Core.Catalog;
using DropFlow.Core.Operations;
using DropFlow.Core.Queue;
using DropFlow.Core.Registry;
using DropFlow.Core.Session;

namespace DropFlow.Core.Runner;

/// <summary>Everything the runner may need to ask the human. Implemented by dialogs in the app; faked in tests.</summary>
public interface IUserPrompts
{
    /// <summary>Show the options form. Returns the chosen values, or null when the user cancelled.</summary>
    Task<IReadOnlyDictionary<string, object?>?> AskOptionsAsync(string operation, string title, IReadOnlyList<ToolOption> options);

    /// <summary>"This needs FFmpeg (~100 MB). Install it now?"</summary>
    Task<bool> ConfirmEngineInstallAsync(EngineId engine, int approxSizeMb);

    Task ShowErrorAsync(string message);
}

/// <summary>Installs an engine (winget). Returns true when it is now available.</summary>
public interface IEngineInstallService
{
    Task<bool> InstallAsync(EngineId engine, IProgress<string> log, CancellationToken cancellationToken);
}

/// <summary>
/// Turns a wheel selection into a queued job: finds the converter, makes sure its engine is installed (asking first), asks for
/// options when the tool needs them, then enqueues. Returns null when the user cancelled or something could not start.
/// </summary>
public sealed class OperationRunner(
    ToolRegistry registry,
    JobQueue queue,
    IEngineProvider engines,
    IEngineInstallService installer,
    IUserPrompts prompts,
    Func<EngineId, int> approxSizeMb,
    Func<string, string> toolTitle)
{
    public async Task<BatchHandle?> RunAsync(WheelSelection selection, CancellationToken cancellationToken = default)
    {
        var op = selection.Wedge.Operation;
        var ext = ToolCatalog.ExtensionOf(selection.Files[0]);
        var converter = registry.Find(op, ext);
        if (converter is null)
        {
            await prompts.ShowErrorAsync($"DropFlow cannot run '{selection.Wedge.Label}' on .{ext} files.").ConfigureAwait(false);
            return null;
        }

        var engine = converter.EngineFor(op);
        if (engine != EngineId.None && engines.Locate(engine) is null)
        {
            if (!await prompts.ConfirmEngineInstallAsync(engine, approxSizeMb(engine)).ConfigureAwait(false)) return null;

            var installed = await installer.InstallAsync(engine, new Progress<string>(), cancellationToken).ConfigureAwait(false);
            if (!installed || engines.Locate(engine) is null)
            {
                await prompts.ShowErrorAsync($"{engine} could not be installed. Install it manually and try again.").ConfigureAwait(false);
                return null;
            }
        }

        IReadOnlyDictionary<string, object?>? values = null;
        var options = converter.OptionsFor(op);
        if (options.Count > 0)
        {
            values = await prompts.AskOptionsAsync(op, toolTitle(op), options).ConfigureAwait(false);
            if (values is null) return null;      // cancelled
        }

        return queue.Enqueue(new ConversionJob(selection.Files, op, values));
    }
}
