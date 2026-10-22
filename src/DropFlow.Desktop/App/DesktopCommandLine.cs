// SPDX-License-Identifier: GPL-3.0-or-later
using DropFlow.Core.Wheel;

namespace DropFlow.Desktop.App;

public enum DesktopCommandKind { Start, Wheel, Settings, History, Shelf, Welcome, Quit, Snapshot }

public sealed record DesktopCommand(
    DesktopCommandKind Kind,
    IReadOnlyList<string> Paths,
    WheelKind? Wheel = null,
    bool Minimized = false,
    string? SnapshotFolder = null,
    string? Language = null);

/// <summary>
/// <c>dropflow-desktop</c>'s command line. File-manager entries run <c>dropflow-desktop --wheel FILE…</c>; bare paths do the same.
/// Paths are made absolute here, in the launching process, because the running instance that receives them has another working folder.
/// </summary>
public static class DesktopCommandLine
{
    public static DesktopCommand Parse(IEnumerable<string> args, string workingDirectory)
    {
        var kind = DesktopCommandKind.Start;
        WheelKind? wheel = null;
        var minimized = false;
        string? snapshot = null;
        string? language = null;
        var paths = new List<string>();

        using var e = args.GetEnumerator();
        while (e.MoveNext())
        {
            var arg = e.Current;
            switch (arg)
            {
                case "--wheel" or "--open": kind = DesktopCommandKind.Wheel; break;
                case "--convert": wheel = WheelKind.Convert; break;
                case "--tools": wheel = WheelKind.Tools; break;
                case "--actions": wheel = WheelKind.Actions; break;
                case "--settings": kind = DesktopCommandKind.Settings; break;
                case "--history": kind = DesktopCommandKind.History; break;
                case "--shelf": kind = DesktopCommandKind.Shelf; break;
                case "--welcome": kind = DesktopCommandKind.Welcome; break;
                case "--quit": kind = DesktopCommandKind.Quit; break;
                case "--minimized": minimized = true; break;
                case "--snapshot":
                    kind = DesktopCommandKind.Snapshot;
                    if (e.MoveNext()) snapshot = Absolute(e.Current, workingDirectory);
                    break;
                case "--lang":
                    if (e.MoveNext()) language = e.Current is "ar" or "en" ? e.Current : null;
                    break;
                default:
                    // "-psn_0_123" is added by old macOS versions when launched from Finder; other unknown switches are ignored.
                    if (arg.StartsWith('-')) break;
                    paths.Add(Absolute(arg, workingDirectory));
                    if (kind == DesktopCommandKind.Start) kind = DesktopCommandKind.Wheel;
                    break;
            }
        }
        return new DesktopCommand(kind, paths, wheel, minimized, snapshot, language);
    }

    /// <summary>The arguments that make the running instance do the same thing (paths are already absolute).</summary>
    public static string[] Build(DesktopCommand c)
    {
        var args = new List<string>();
        switch (c.Kind)
        {
            case DesktopCommandKind.Wheel: args.Add("--wheel"); break;
            case DesktopCommandKind.Settings: args.Add("--settings"); break;
            case DesktopCommandKind.History: args.Add("--history"); break;
            case DesktopCommandKind.Shelf: args.Add("--shelf"); break;
            case DesktopCommandKind.Welcome: args.Add("--welcome"); break;
            case DesktopCommandKind.Quit: args.Add("--quit"); break;
        }
        if (c.Wheel is { } w) args.Add("--" + w.ToString().ToLowerInvariant());
        args.AddRange(c.Paths);
        return [.. args];
    }

    private static string Absolute(string path, string workingDirectory) =>
        Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(workingDirectory, path));
}
