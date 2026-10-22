// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Windows.Threading;
using DropFlow.App.Services;
using DropFlow.Core.Operations;
using DropFlow.Core.Runner;
using DropFlow.Engines.Media.Encoding;
using DropFlow.Engines.Media.Preview;

namespace DropFlow.App.Views.Editors.Media;

/// <summary>
/// The media tools that open a dedicated editor instead of the generic options form when chosen on the wheel. The editor only fills the
/// tool's options; the job runs through the normal queue with the media engine's converter (the CLI and recipes keep the typed options).
/// Single-file tools get an editor for exactly one file (several files still use the form, since one set of times rarely fits them all);
/// join and merge get the ordering editor for two files or more.
/// </summary>
internal sealed class MediaEditors(Dispatcher ui, UiKit kit, IEngineProvider engines, Func<string, Task> showError) : IOptionsEditor
{
    private static readonly Dictionary<string, Func<MediaEditorContext, MediaSession, MediaEditorWindow>> Single = new()
    {
        ["vid.trim"] = (c, m) => new MediaTrimEditor(c, m),
        ["aud.trim"] = (c, m) => new MediaTrimEditor(c, m),
        ["vid.split"] = (c, m) => new MediaSplitEditor(c, m),
        ["vid.speed"] = (c, m) => new MediaSpeedEditor(c, m),
        ["vid.crop"] = (c, m) => new MediaCropEditor(c, m),
        ["vid.snapshot"] = (c, m) => new MediaFramesEditor(c, m),
        ["vid.redact"] = (c, m) => new MediaRedactEditor(c, m),
        ["aud.bleep"] = (c, m) => new MediaBleepEditor(c, m),
        ["aud.normalize"] = (c, m) => new MediaNormalizeEditor(c, m),
        ["aud.channels"] = (c, m) => new MediaChannelsEditor(c, m),
    };

    private static readonly HashSet<string> Multi = ["vid.join", "aud.merge"];

    /// <summary>Every operation that has a media editor.</summary>
    public static IReadOnlyCollection<string> Operations => [.. Single.Keys, .. Multi];

    /// <summary>The single-file editor window for <paramref name="operation"/> (also used by tests).</summary>
    internal static MediaEditorWindow Create(string operation, MediaEditorContext context, MediaSession session) => Single[operation](context, session);

    public bool CanEdit(string operation, IReadOnlyList<string> files)
    {
        if (files.Count == 0 || files.Any(Directory.Exists)) return false;
        return Single.ContainsKey(operation) ? files.Count == 1 : Multi.Contains(operation) && files.Count >= 2;
    }

    public Task<OptionsEdit?> EditAsync(string operation, string title, IReadOnlyList<string> files, IReadOnlyList<ToolOption> options) =>
        ui.InvokeAsync(() => OpenAsync(operation, title, files, options)).Task.Unwrap();

    private async Task<OptionsEdit?> OpenAsync(string operation, string title, IReadOnlyList<string> files, IReadOnlyList<ToolOption> options)
    {
        var scratch = Path.Combine(Path.GetTempPath(), "dropflow-preview-" + Guid.NewGuid().ToString("N")[..10]);
        var context = new MediaEditorContext(kit, engines, operation, title, files, options, scratch);
        try
        {
            if (Multi.Contains(operation))
            {
                var order = new MediaJoinEditor(context);
                order.Show();
                return await order.Completion;
            }

            MediaSession session;
            try
            {
                session = await MediaSession.OpenAsync(engines, files[0], scratch);
            }
            catch (Exception ex) when (ex is MediaProbeException or IOException or UnauthorizedAccessException)
            {
                FileLog.Info($"media editor could not read {Path.GetFileName(files[0])}: {ex.Message}");
                await showError(kit.Loc.Format("media.cannotRead", Path.GetFileName(files[0])));
                return null;
            }

            var window = Create(operation, context, session);
            window.Show();
            return await window.Completion;
        }
        finally
        {
            try { if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* a preview may still be open; temp is cleaned by Windows */ }
        }
    }
}
