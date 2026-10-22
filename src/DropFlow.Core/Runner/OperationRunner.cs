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
    Func<string, string> toolTitle,
    IOptionsEditor? editors = null)
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

        // A window tool has no options to ask for: make sure its engine exists, then open it.
        if (converter is IInteractiveTool window)
        {
            if (!await EnsureEngineAsync(converter.EngineFor(op), cancellationToken).ConfigureAwait(false)) return null;
            await window.OpenAsync(op, selection.Files).ConfigureAwait(false);
            return null;
        }

        // Options first, so the choice can decide whether an engine is needed at all (PDF "extreme" compression needs none).
        // A visual editor, when there is one for this tool, replaces the generic form.
        IReadOnlyDictionary<string, object?>? values = null;
        var options = converter.OptionsFor(op);
        if (editors is not null && editors.CanEdit(op, selection.Files))
        {
            values = await editors.EditAsync(op, selection.Files, options).ConfigureAwait(false);
            if (values is null) return null;      // closed
        }
        else if (options.Count > 0)
        {
            values = await prompts.AskOptionsAsync(op, toolTitle(op), options).ConfigureAwait(false);
            if (values is null) return null;      // cancelled
        }

        if (!await EnsureEngineAsync(converter.EngineFor(op, values ?? ConversionJob.NoOptions), cancellationToken).ConfigureAwait(false)) return null;

        return queue.Enqueue(new ConversionJob(selection.Files, op, values));
    }

    /// <summary>True when the engine is not needed or is installed (after asking the user and installing it).</summary>
    private async Task<bool> EnsureEngineAsync(EngineId engine, CancellationToken cancellationToken)
    {
        if (engine == EngineId.None || engines.Locate(engine) is not null) return true;
        if (!await prompts.ConfirmEngineInstallAsync(engine, approxSizeMb(engine)).ConfigureAwait(false)) return false;

        var installed = await installer.InstallAsync(engine, new Progress<string>(), cancellationToken).ConfigureAwait(false);
        if (!installed || engines.Locate(engine) is null)
        {
            await prompts.ShowErrorAsync($"{engine} could not be installed. Install it manually and try again.").ConfigureAwait(false);
            return false;
        }
        return true;
    }
}
