using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media.Imaging;
using DropFlow.Core.Sources;

namespace DropFlow.App.Services;

/// <summary>
/// Turns whatever is on the clipboard into files the wheel can work on: copied files are used as they are, an image becomes a PNG,
/// and text becomes a .txt. Must run on the UI thread (clipboard access).
/// </summary>
internal static class ClipboardSource
{
    /// <summary>
    /// The address on the clipboard when it holds exactly one http(s) link as text and nothing more specific (no copied files, no picture),
    /// else null. Several links, or a link inside other text, are left to <see cref="Export"/> (which saves the text).
    /// </summary>
    public static Uri? ReadSingleUrl()
    {
        if (Clipboard.ContainsFileDropList() || Clipboard.ContainsImage() || !Clipboard.ContainsText()) return null;
        return LinkText.Classify(Clipboard.GetText(), out var url) == ClipboardTextKind.SingleUrl ? url : null;
    }

    public static IReadOnlyList<string> Export(string tempDirectory, DateTime? now = null)
    {
        if (Clipboard.ContainsFileDropList())
            return Clipboard.GetFileDropList().Cast<string>().Where(p => File.Exists(p) || Directory.Exists(p)).ToList();

        Directory.CreateDirectory(tempDirectory);
        var stamp = (now ?? DateTime.Now).ToString("yyyyMMdd-HHmmss");

        if (Clipboard.ContainsImage() && Clipboard.GetImage() is { } image)
        {
            var path = Path.Combine(tempDirectory, $"clipboard-{stamp}.png");
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using var stream = File.Create(path);
            encoder.Save(stream);
            return [path];
        }

        if (Clipboard.ContainsText() && Clipboard.GetText() is { Length: > 0 } text)
        {
            var path = Path.Combine(tempDirectory, $"clipboard-{stamp}.txt");
            File.WriteAllText(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            return [path];
        }
        return [];
    }
}
