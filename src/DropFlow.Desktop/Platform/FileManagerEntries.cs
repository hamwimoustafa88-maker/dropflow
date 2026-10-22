// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;

namespace DropFlow.Desktop.Platform;

/// <summary>One file DropFlow writes so a file manager shows "Open with DropFlow" for selected files.</summary>
public sealed record FileManagerFile(string FileManager, string Path, string Content, bool Executable);

/// <summary>
/// "Open with DropFlow" in the right-click menu of Nautilus (GNOME Files, under Scripts), Caja (Scripts), Nemo (an action) and Dolphin
/// (a service menu). Each runs <c>dropflow-desktop --wheel FILE…</c>, which opens a click-mode wheel for the selection — the way to
/// use DropFlow on desktops where drags cannot be watched (KDE Plasma on Wayland, for example). Only installed file managers get an
/// entry. Files are rewritten when the app moves (the AppImage, for example).
/// </summary>
public static class FileManagerEntries
{
    public static IReadOnlyList<FileManagerFile> Plan(string command, string title, AppDirectories dirs, Func<string, bool> installed)
    {
        var safeTitle = title.Replace('/', '-');
        var files = new List<FileManagerFile>();
        var script = ShellScript(command);

        if (installed("nautilus"))
            files.Add(new("nautilus", AppDirectories.Join(dirs.DataHome, "nautilus", "scripts", safeTitle), script, true));
        if (installed("caja"))
            files.Add(new("caja", AppDirectories.Join(dirs.ConfigHome, "caja", "scripts", safeTitle), script, true));
        if (installed("nemo"))
            files.Add(new("nemo", AppDirectories.Join(dirs.DataHome, "nemo", "actions", "dropflow.nemo_action"), $"""
                [Nemo Action]
                Name={title}
                Comment={title}
                Exec={LoginItem.QuoteExec(command)} --wheel %F
                Icon-Name=dropflow
                Selection=notnone
                Extensions=any;

                """.Replace("\r\n", "\n"), false));
        if (installed("dolphin"))
        {
            var menu = $"""
                [Desktop Entry]
                Type=Service
                MimeType=all/all;
                X-KDE-ServiceTypes=KonqPopupMenu/Plugin
                Actions=dropflowWheel

                [Desktop Action dropflowWheel]
                Name={title}
                Icon=dropflow
                Exec={LoginItem.QuoteExec(command)} --wheel %F

                """.Replace("\r\n", "\n");
            // Plasma 6 reads kio/servicemenus and requires the file to be executable; Plasma 5 read kservices5/ServiceMenus.
            files.Add(new("dolphin", AppDirectories.Join(dirs.DataHome, "kio", "servicemenus", "dropflow-wheel.desktop"), menu, true));
            files.Add(new("dolphin", AppDirectories.Join(dirs.DataHome, "kservices5", "ServiceMenus", "dropflow-wheel.desktop"), menu, false));
        }
        return files;
    }

    /// <summary>The Nautilus/Caja script: they pass the selected files as arguments and run the script in the viewed folder.</summary>
    public static string ShellScript(string command) =>
        "#!/bin/sh\n" +
        "# Written by DropFlow: opens the DropFlow wheel for the selected files. DropFlow rewrites it when the app moves.\n" +
        $"exec {ShellQuote(command)} --wheel \"$@\"\n";

    private static string ShellQuote(string s) =>
        s.All(c => char.IsAsciiLetterOrDigit(c) || c is '/' or '-' or '_' or '.') ? s : "'" + s.Replace("'", "'\\''") + "'";

    public static bool IsOnPath(string program)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin").Split(':', StringSplitOptions.RemoveEmptyEntries))
            if (File.Exists(Path.Combine(dir, program))) return true;
        return false;
    }

    /// <summary>Writes the entries (only when the content changed) and removes our older entry when the title's language changed.</summary>
    public static int Install(string command, string title, IEnumerable<string> otherTitles, AppDirectories dirs)
    {
        var written = 0;
        foreach (var f in Plan(command, title, dirs, IsOnPath))
        {
            try
            {
                if (!File.Exists(f.Path) || File.ReadAllText(f.Path) != f.Content)
                {
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(f.Path)!);
                    File.WriteAllText(f.Path, f.Content);
                    written++;
                }
                if (f.Executable && !OperatingSystem.IsWindows())
                    File.SetUnixFileMode(f.Path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

                // a script under the other language's title from an earlier run
                if (f.FileManager is "nautilus" or "caja")
                    foreach (var other in otherTitles.Where(t => t != title))
                    {
                        var old = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(f.Path)!, other.Replace('/', '-'));
                        if (File.Exists(old) && File.ReadAllText(old).Contains("Written by DropFlow", StringComparison.Ordinal)) File.Delete(old);
                    }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Debug.WriteLine($"file manager entry {f.Path}: {ex.Message}");
            }
        }
        return written;
    }

    public static void Remove(IEnumerable<string> titles, AppDirectories dirs)
    {
        foreach (var title in titles)
            foreach (var f in Plan("x", title, dirs, _ => true))
            {
                try
                {
                    if (File.Exists(f.Path) && File.ReadAllText(f.Path).Contains("--wheel", StringComparison.Ordinal)) File.Delete(f.Path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* leave it */ }
            }
    }
}
