// SPDX-License-Identifier: GPL-3.0-or-later
using DropFlow.Core.Catalog;
using DropFlow.Core.Operations;
using DropFlow.Core.Queue;
using DropFlow.Core.Registry;
using DropFlow.Core.Session;

namespace DropFlow.Desktop.App;

/// <summary>What the runner may ask or tell the user. Implemented by Avalonia dialogs and toasts; faked in tests.</summary>
public interface IDesktopPrompts
{
    /// <summary>Shows the generated options form. Returns the values, or null when the user cancelled.</summary>
    Task<IReadOnlyDictionary<string, object?>?> AskOptionsAsync(string operation, string title, IReadOnlyList<ToolOption> options);

    Task ShowMessageAsync(string message);
}

/// <summary>
/// The macOS/Linux counterpart of Core's <c>OperationRunner</c>: finds the converter, asks for options, checks the helper program,
/// then queues the job — with the output folder from the settings (beside the source, Downloads or Desktop). There is no winget here
/// and DropFlow never installs software by itself, so a missing helper is explained with the command to install it.
/// </summary>
public sealed class DesktopRunner(
    ToolRegistry registry,
    JobQueue queue,
    IEngineProvider engines,
    IDesktopPrompts prompts,
    Func<string, string> toolTitle,
    Func<string?> outputDirectory,
    Func<EngineId, string> missingEngineMessage,
    Func<string, string> notSupportedMessage,
    Func<string, string, string> cannotRunMessage)
{
    public async Task<BatchHandle?> RunAsync(WheelSelection selection)
    {
        if (selection.Files.Count == 0) return null;
        var op = selection.Wedge.Operation;
        var ext = ToolCatalog.ExtensionOf(selection.Files[0]);
        var converter = registry.Find(op, ext);
        if (converter is null)
        {
            await prompts.ShowMessageAsync(cannotRunMessage(op, ext)).ConfigureAwait(false);
            return null;
        }

        // Tools with their own window (the Encoder) have no Avalonia window yet.
        if (converter is IInteractiveTool)
        {
            await prompts.ShowMessageAsync(notSupportedMessage(toolTitle(op))).ConfigureAwait(false);
            return null;
        }

        IReadOnlyDictionary<string, object?>? values = null;
        var options = converter.OptionsFor(op);
        if (options.Count > 0)
        {
            values = await prompts.AskOptionsAsync(op, toolTitle(op), options).ConfigureAwait(false);
            if (values is null) return null;
        }

        var engine = converter.EngineFor(op, values ?? ConversionJob.NoOptions);
        if (engine != EngineId.None && engines.Locate(engine) is null)
        {
            await prompts.ShowMessageAsync(missingEngineMessage(engine)).ConfigureAwait(false);
            return null;
        }

        return queue.Enqueue(new ConversionJob(selection.Files, op, values, outputDirectory()));
    }
}
