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
/// A dedicated window that fills a tool's options (a trim timeline instead of typed start/end text, say). It only collects values:
/// the job still runs through the normal queue with the tool's own converter, so the CLI and recipes keep using the same options.
/// </summary>
public interface IOptionsEditor
{
    /// <summary>True when a dedicated editor exists for <paramref name="operation"/> on these files.</summary>
    bool CanEdit(string operation, IReadOnlyList<string> files);

    /// <summary>
    /// Opens the editor and returns the chosen option values, or null when the user closed it without running.
    /// <paramref name="options"/> is what the converter declares, so the editor can check which keys it supports.
    /// </summary>
    Task<OptionsEdit?> EditAsync(string operation, string title, IReadOnlyList<string> files, IReadOnlyList<ToolOption> options);
}

/// <summary>
/// What an <see cref="IOptionsEditor"/> decided. <paramref name="Files"/> is set when the editor changed the order of the files (join, merge);
/// it may only reorder or leave out files of the selection, never add new ones.
/// </summary>
public sealed record OptionsEdit(IReadOnlyDictionary<string, object?> Values, IReadOnlyList<string>? Files = null);

/// <summary>Several editors (media timelines, image canvases...) behind one <see cref="IOptionsEditor"/>: the first that can edit wins.</summary>
public sealed class CompositeOptionsEditor(params IOptionsEditor[] editors) : IOptionsEditor
{
    public bool CanEdit(string operation, IReadOnlyList<string> files) => editors.Any(e => e.CanEdit(operation, files));

    public Task<OptionsEdit?> EditAsync(string operation, string title, IReadOnlyList<string> files, IReadOnlyList<ToolOption> options) =>
        editors.First(e => e.CanEdit(operation, files)).EditAsync(operation, title, files, options);
}

/// <summary>
/// Turns a wheel selection into a queued job: finds the converter, makes sure its engine is installed (asking first), asks for
/// options when the tool needs them (in a dedicated <see cref="IOptionsEditor"/> when one handles the tool, else the generic form),
/// then enqueues. Returns null when the user cancelled or something could not start.
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
        IReadOnlyDictionary<string, object?>? values = null;
        var files = selection.Files;
        var options = converter.OptionsFor(op);

        // A dedicated editor previews the file with the engine (thumbnails, waveform), so the engine must exist before it opens.
        if (editors is not null && editors.CanEdit(op, selection.Files))
        {
            if (!await EnsureEngineAsync(converter.EngineFor(op), cancellationToken).ConfigureAwait(false)) return null;
            var edit = await editors.EditAsync(op, toolTitle(op), selection.Files, options).ConfigureAwait(false);
            if (edit is null) return null;      // closed without running
            values = edit.Values;
            if (edit.Files is { Count: > 0 } reordered)
            {
                var allowed = new HashSet<string>(selection.Files, StringComparer.OrdinalIgnoreCase);
                if (!reordered.All(allowed.Contains))
                {
                    await prompts.ShowErrorAsync("The editor returned a file that was not selected.").ConfigureAwait(false);
                    return null;
                }
                files = reordered;
            }
        }
        else if (options.Count > 0)
        {
            values = await prompts.AskOptionsAsync(op, toolTitle(op), options).ConfigureAwait(false);
            if (values is null) return null;      // cancelled
        }

        if (!await EnsureEngineAsync(converter.EngineFor(op, values ?? ConversionJob.NoOptions), cancellationToken).ConfigureAwait(false)) return null;

        return queue.Enqueue(new ConversionJob(files, op, values));
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
