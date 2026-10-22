// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Text;

namespace DropFlow.Desktop.Platform;

/// <summary>A program to start with its arguments (compared by value, so plans are easy to test).</summary>
public sealed record ProcessPlan(string FileName, IReadOnlyList<string> Arguments)
{
    public bool Equals(ProcessPlan? other) => other is not null && FileName == other.FileName && Arguments.SequenceEqual(other.Arguments);
    public override int GetHashCode() => HashCode.Combine(FileName, Arguments.Count);
    public override string ToString() => FileName + " " + string.Join(' ', Arguments);
}

/// <summary>How to show a file in the file manager and how to open a terminal, per OS. The first plan that starts wins.</summary>
public static class ShellPlans
{
    public static IEnumerable<ProcessPlan> Reveal(OsKind os, string path)
    {
        if (os == OsKind.MacOS)
        {
            yield return new ProcessPlan("open", ["-R", path]);
            yield break;
        }
        // The freedesktop FileManager1 interface selects the file (Nautilus, Dolphin, Nemo, Caja, Thunar implement it).
        yield return new ProcessPlan("dbus-send",
        [
            "--session", "--dest=org.freedesktop.FileManager1", "--type=method_call",
            "/org/freedesktop/FileManager1", "org.freedesktop.FileManager1.ShowItems",
            "array:string:" + FileUrl(path), "string:",
        ]);
        yield return new ProcessPlan("xdg-open", [ParentOf(path)]);
    }

    public static IEnumerable<ProcessPlan> Open(OsKind os, string path)
    {
        yield return new ProcessPlan(os == OsKind.MacOS ? "open" : "xdg-open", [path]);
    }

    /// <summary>Terminals to try, in order. The process also starts in <paramref name="folder"/>, for terminals without a switch.</summary>
    public static IEnumerable<ProcessPlan> Terminals(OsKind os, string folder)
    {
        if (os == OsKind.MacOS)
        {
            yield return new ProcessPlan("open", ["-a", "Terminal", folder]);
            yield break;
        }
        yield return new ProcessPlan("x-terminal-emulator", []);                       // Debian/Ubuntu alternative, opens in the working folder
        yield return new ProcessPlan("ptyxis", [$"--working-directory={folder}"]);        // GNOME 47+ default on Fedora/Ubuntu
        yield return new ProcessPlan("kgx", [$"--working-directory={folder}"]);           // GNOME Console
        yield return new ProcessPlan("gnome-terminal", [$"--working-directory={folder}"]);
        yield return new ProcessPlan("konsole", ["--workdir", folder]);
        yield return new ProcessPlan("xfce4-terminal", [$"--working-directory={folder}"]);
        yield return new ProcessPlan("mate-terminal", [$"--working-directory={folder}"]);
        yield return new ProcessPlan("tilix", [$"--working-directory={folder}"]);
        yield return new ProcessPlan("xterm", []);
    }

    /// <summary>RFC 8089 file URL with every non-unreserved byte percent-encoded.</summary>
    public static string FileUrl(string path)
    {
        var sb = new StringBuilder("file://");
        foreach (var b in Encoding.UTF8.GetBytes(path))
        {
            var c = (char)b;
            if (b < 0x80 && (char.IsAsciiLetterOrDigit(c) || c is '/' or '-' or '_' or '.' or '~')) sb.Append(c);
            else sb.Append('%').Append(b.ToString("X2"));
        }
        return sb.ToString();
    }

    private static string ParentOf(string path)
    {
        var slash = path.TrimEnd('/').LastIndexOf('/');
        return slash <= 0 ? "/" : path[..slash];
    }

    /// <summary>Starts the first plan that can be started. Returns false when none could (program not installed).</summary>
    public static bool TryStart(IEnumerable<ProcessPlan> plans, string? workingDirectory = null)
    {
        foreach (var plan in plans)
        {
            try
            {
                var psi = new ProcessStartInfo(plan.FileName) { UseShellExecute = false, WorkingDirectory = workingDirectory ?? "" };
                foreach (var a in plan.Arguments) psi.ArgumentList.Add(a);
                using var p = Process.Start(psi);
                if (p is null) continue;
                // dbus-send fails fast when no file manager implements FileManager1: wait a moment so the fallback can run.
                if (plan.FileName == "dbus-send" && p.WaitForExit(1500) && p.ExitCode != 0) continue;
                return true;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
            {
                // not installed: try the next one
            }
        }
        return false;
    }
}
