using DropFlow.Core.Wheel;
using DropFlow.Desktop.App;
using DropFlow.Desktop.Platform;

namespace DropFlow.Desktop.Tests;

public class AppDirectoriesTests
{
    private static Func<string, string?> Env(params (string Key, string Value)[] vars) =>
        name => vars.FirstOrDefault(v => v.Key == name).Value;

    [Fact]
    public void Mac_keeps_everything_under_application_support()
    {
        var d = AppDirectories.For(OsKind.MacOS, "/Users/sara", Env());
        Assert.Equal("/Users/sara/Library/Application Support/DropFlow", d.Data);
        Assert.Equal("/Users/sara/Library/Application Support/DropFlow/dropflow.sock", d.InstanceSocket);
        Assert.Equal("/Users/sara/Library/LaunchAgents", d.LaunchAgents);
        Assert.Equal("/Users/sara/Downloads", d.Downloads);
        Assert.Equal("/Users/sara/Desktop", d.Desktop);
    }

    [Fact]
    public void Linux_follows_the_xdg_base_directories()
    {
        var d = AppDirectories.For(OsKind.Linux, "/home/omar",
            Env(("XDG_DATA_HOME", "/data"), ("XDG_CONFIG_HOME", "/cfg"), ("XDG_RUNTIME_DIR", "/run/user/1000")));
        Assert.Equal("/data/dropflow", d.Data);
        Assert.Equal("/run/user/1000/dropflow", d.Runtime);
        Assert.Equal("/run/user/1000/dropflow/app.sock", d.InstanceSocket);
        Assert.Equal("/run/user/1000/dropflow/drag.sock", d.DragSocket);
        Assert.Equal("/cfg/autostart", d.Autostart);
        Assert.Equal("/data/gnome-shell/extensions", d.GnomeExtensions);
    }

    [Fact]
    public void Linux_defaults_without_variables()
    {
        var d = AppDirectories.For(OsKind.Linux, "/home/omar", Env());
        Assert.Equal("/home/omar/.local/share/dropflow", d.Data);
        Assert.Equal("/home/omar/.config/autostart", d.Autostart);
        // no runtime dir (rare: a session without logind): fall back to the private data folder
        Assert.Equal("/home/omar/.local/share/dropflow/run", d.Runtime);
        Assert.Equal("/home/omar/Downloads", d.Downloads);
    }

    [Fact]
    public void Relative_xdg_values_are_ignored_as_the_spec_says()
    {
        var d = AppDirectories.For(OsKind.Linux, "/home/omar", Env(("XDG_DATA_HOME", "relative/path")));
        Assert.Equal("/home/omar/.local/share/dropflow", d.Data);
    }

    [Fact]
    public void Linux_reads_localised_download_and_desktop_folders()
    {
        const string userDirs = """
            # This file is written by xdg-user-dirs-update
            XDG_DESKTOP_DIR="$HOME/سطح المكتب"
            XDG_DOWNLOAD_DIR="$HOME/التنزيلات"
            XDG_MUSIC_DIR="/mnt/music"
            """;
        var d = AppDirectories.For(OsKind.Linux, "/home/omar", Env(), userDirs);
        Assert.Equal("/home/omar/التنزيلات", d.Downloads);
        Assert.Equal("/home/omar/سطح المكتب", d.Desktop);
    }

    [Fact]
    public void User_dirs_parser_handles_absolute_paths_escapes_and_junk()
    {
        var dirs = XdgUserDirs.Parse("""
            XDG_DOWNLOAD_DIR="/srv/down loads"
            XDG_DESKTOP_DIR="$HOME/My \"Desk\""
            garbage line
            XDG_BROKEN=unquoted
            """, "/home/a");
        Assert.Equal("/srv/down loads", dirs["XDG_DOWNLOAD_DIR"]);
        Assert.Equal("/home/a/My \"Desk\"", dirs["XDG_DESKTOP_DIR"]);
        Assert.False(dirs.ContainsKey("XDG_BROKEN"));
    }
}

public class OutputLocationTests
{
    private static readonly AppDirectories Dirs = AppDirectories.For(OsKind.Linux, "/home/u", _ => null);

    [Fact]
    public void Beside_the_source_means_no_override() => Assert.Null(OutputPolicy.DirectoryFor(OutputLocation.BesideSource, Dirs));

    [Fact]
    public void Downloads_and_desktop()
    {
        Assert.Equal("/home/u/Downloads", OutputPolicy.DirectoryFor(OutputLocation.Downloads, Dirs));
        Assert.Equal("/home/u/Desktop", OutputPolicy.DirectoryFor(OutputLocation.Desktop, Dirs));
    }
}

public class DesktopCommandLineTests
{
    private const string Cwd = "/home/u/Pictures";

    [Fact]
    public void No_arguments_starts_normally()
    {
        var c = DesktopCommandLine.Parse([], Cwd);
        Assert.Equal(DesktopCommandKind.Start, c.Kind);
        Assert.Empty(c.Paths);
    }

    [Fact]
    public void Wheel_resolves_relative_paths_in_the_launching_process()
    {
        // Nautilus runs scripts in the folder being viewed and passes bare names; the running instance has another folder.
        var c = DesktopCommandLine.Parse(["--wheel", "a.png", "/tmp/b.pdf"], Cwd);
        Assert.Equal(DesktopCommandKind.Wheel, c.Kind);
        Assert.Equal([Path.GetFullPath(Path.Combine(Cwd, "a.png")), Path.GetFullPath("/tmp/b.pdf")], c.Paths);
    }

    [Fact]
    public void Bare_paths_open_the_wheel_too() =>
        Assert.Equal(DesktopCommandKind.Wheel, DesktopCommandLine.Parse(["/tmp/x.mp4"], Cwd).Kind);

    [Theory]
    [InlineData("--tools", WheelKind.Tools)]
    [InlineData("--actions", WheelKind.Actions)]
    [InlineData("--convert", WheelKind.Convert)]
    public void Wheel_choice(string flag, WheelKind kind)
    {
        var c = DesktopCommandLine.Parse(["--wheel", flag, "/tmp/x.mp4"], Cwd);
        Assert.Equal(kind, c.Wheel);
    }

    [Theory]
    [InlineData("--settings", DesktopCommandKind.Settings)]
    [InlineData("--history", DesktopCommandKind.History)]
    [InlineData("--shelf", DesktopCommandKind.Shelf)]
    [InlineData("--quit", DesktopCommandKind.Quit)]
    [InlineData("--welcome", DesktopCommandKind.Welcome)]
    public void Window_commands(string flag, DesktopCommandKind kind) => Assert.Equal(kind, DesktopCommandLine.Parse([flag], Cwd).Kind);

    [Fact]
    public void Snapshot_takes_a_folder_and_a_language()
    {
        var c = DesktopCommandLine.Parse(["--snapshot", "out", "--lang", "ar"], Cwd);
        Assert.Equal(DesktopCommandKind.Snapshot, c.Kind);
        Assert.Equal(Path.GetFullPath(Path.Combine(Cwd, "out")), c.SnapshotFolder);
        Assert.Equal("ar", c.Language);
    }

    [Fact]
    public void Unknown_switches_are_ignored_and_minimized_is_kept()
    {
        var c = DesktopCommandLine.Parse(["--frobnicate", "--minimized"], Cwd);
        Assert.Equal(DesktopCommandKind.Start, c.Kind);
        Assert.True(c.Minimized);
    }

    [Fact]
    public void macOS_process_serial_number_argument_is_ignored()
    {
        // Older macOS versions pass "-psn_0_12345" when the app is opened from Finder.
        var c = DesktopCommandLine.Parse(["-psn_0_1234567"], Cwd);
        Assert.Equal(DesktopCommandKind.Start, c.Kind);
        Assert.Empty(c.Paths);
    }

    [Fact]
    public void Forwarded_arguments_round_trip()
    {
        var original = DesktopCommandLine.Parse(["--wheel", "--tools", "a b.png"], Cwd);
        var again = DesktopCommandLine.Parse(DesktopCommandLine.Build(original), "/somewhere/else");
        Assert.Equal(original.Kind, again.Kind);
        Assert.Equal(original.Paths, again.Paths);
        Assert.Equal(original.Wheel, again.Wheel);
    }
}
