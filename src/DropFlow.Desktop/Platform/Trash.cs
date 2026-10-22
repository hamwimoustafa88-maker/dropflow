// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text;
using DropFlow.Core.Operations;

namespace DropFlow.Desktop.Platform;

/// <summary>
/// Undo on Linux: moves DropFlow's own outputs (never originals) to the freedesktop.org trash (<c>$XDG_DATA_HOME/Trash</c>), with the
/// <c>.trashinfo</c> file that lets the file manager restore them. When the output is on another disk (a move would be a copy), it is
/// deleted instead: it is a file DropFlow made a moment ago.
/// </summary>
public sealed class FreedesktopTrash(string trashFolder, Func<DateTime>? now = null) : IFileDisposer
{
    private readonly Func<DateTime> _now = now ?? (() => DateTime.Now);

    public void Dispose(string path)
    {
        var full = Path.GetFullPath(path);
        var files = Path.Combine(trashFolder, "files");
        var info = Path.Combine(trashFolder, "info");
        Directory.CreateDirectory(files);
        Directory.CreateDirectory(info);

        var name = Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar, '/'));
        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        var candidate = name;
        for (var n = 2; File.Exists(Path.Combine(files, candidate)) || Directory.Exists(Path.Combine(files, candidate))
                        || File.Exists(Path.Combine(info, candidate + ".trashinfo")); n++)
            candidate = $"{stem} {n}{ext}";

        // The info file is written first, as the spec asks, so a crash in between never leaves an orphan without its origin.
        var infoFile = Path.Combine(info, candidate + ".trashinfo");
        File.WriteAllText(infoFile,
            "[Trash Info]\n" +
            $"Path={EncodePath(full)}\n" +
            $"DeletionDate={_now().ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture)}\n");
        try
        {
            if (Directory.Exists(full)) Directory.Move(full, Path.Combine(files, candidate));
            else File.Move(full, Path.Combine(files, candidate));
        }
        catch (IOException)
        {
            File.Delete(infoFile);
            if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
            else File.Delete(full);
        }
    }

    /// <summary>Percent-encodes a path for the <c>Path=</c> key (RFC 2396 escaping, slashes kept).</summary>
    public static string EncodePath(string path)
    {
        var sb = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(path))
        {
            var c = (char)b;
            if (b < 0x80 && (char.IsAsciiLetterOrDigit(c) || c is '/' or '-' or '_' or '.' or '~' or '(' or ')' or '!' or '*' or '\'')) sb.Append(c);
            else sb.Append('%').Append(b.ToString("X2"));
        }
        return sb.ToString();
    }
}

/// <summary>Undo on macOS: moves the output into <c>~/.Trash</c> with a unique name ("x 2.png").</summary>
public sealed class FolderTrash(string trashFolder) : IFileDisposer
{
    public void Dispose(string path)
    {
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(trashFolder);
        var name = Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar, '/'));
        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        var target = Path.Combine(trashFolder, name);
        for (var n = 2; File.Exists(target) || Directory.Exists(target); n++) target = Path.Combine(trashFolder, $"{stem} {n}{ext}");
        try
        {
            if (Directory.Exists(full)) Directory.Move(full, target);
            else File.Move(full, target);
        }
        catch (IOException)
        {
            if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
            else File.Delete(full);
        }
    }
}
