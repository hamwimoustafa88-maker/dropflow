// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using DropFlow.Core.Operations;
using DropFlow.Core.Runner;

namespace DropFlow.App.Views.Editors;

/// <summary>
/// The visual editors for the image tools. Choosing one of these tools on the wheel opens its editor instead of the generic options
/// form; the editor fills the converter's options and the normal job runs (the generic form stays for the CLI, recipes and any tool
/// without an editor).
/// </summary>
internal sealed class ImageEditors(UiKit ui, IRedactDetectors? detectors = null) : IOptionsEditor
{
    public static readonly IReadOnlyList<string> Operations = ["img.edit", "img.annotate", "img.crop", "img.redact", "img.background", "img.qr", "img.metadata"];

    private static readonly HashSet<string> Pictures = ["jpg", "jpeg", "png", "webp", "heic", "heif", "tiff", "tif", "avif", "bmp", "gif", "svg", "ico"];

    public bool CanEdit(string operation, IReadOnlyList<string> files)
    {
        if (!Operations.Contains(operation) || files.Count == 0 || !files.All(File.Exists)) return false;
        var ext = Path.GetExtension(files[0]).TrimStart('.').ToLowerInvariant();
        return Pictures.Contains(ext) || (operation == "img.qr" && ext == "txt");
    }

    public async Task<OptionsEdit?> EditAsync(string operation, string title, IReadOnlyList<string> files, IReadOnlyList<ToolOption> options)
    {
        var window = Create(ui, operation, files, detectors);
        window.Show();
        window.Activate();
        var values = await window.Result;
        return values is null ? null : new OptionsEdit(values);
    }
    /// <summary>The editor window for an operation (also used by snapshot mode).</summary>
    public static VisualEditorWindow Create(UiKit ui, string operation, IReadOnlyList<string> files, IRedactDetectors? detectors = null) => operation switch
    {
        "img.edit" => new PhotoEditWindow(ui, files),
        "img.annotate" => new AnnotateWindow(ui, files),
        "img.crop" => new CropWindow(ui, files),
        "img.redact" => new RedactWindow(ui, files, detectors),
        "img.background" => new BackgroundWindow(ui, files),
        "img.qr" => new QrWindow(ui, files),
        "img.metadata" => new MetadataWindow(ui, files),
        _ => throw new ArgumentException($"No editor for '{operation}'.", nameof(operation)),
    };
}
