using DropFlow.Core.Settings;
using DropFlow.Desktop.Drag;
using DropFlow.Desktop.Platform;

namespace DropFlow.Desktop.Tests;

public class DragLineProtocolTests
{
    [Fact]
    public void Begin_and_end()
    {
        Assert.Equal(DragSignalKind.Started, DragLineProtocol.Parse("begin")!.Value.Kind);
        Assert.Equal(DragSignalKind.Ended, DragLineProtocol.Parse("end\r")!.Value.Kind);
    }

    [Theory]
    [InlineData("key shift 120 340", TriggerKey.Shift, 120, 340)]
    [InlineData("key ctrl 0 0", TriggerKey.Ctrl, 0, 0)]
    [InlineData("key alt 1919 -20", TriggerKey.Alt, 1919, -20)]       // a second monitor left of / above the primary
    [InlineData("  key alt 10.6 20.4 ", TriggerKey.Alt, 11, 20)]      // fractional stage coordinates are rounded
    public void Keys(string line, TriggerKey key, int x, int y)
    {
        var s = DragLineProtocol.Parse(line)!.Value;
        Assert.Equal(DragSignalKind.KeyTapped, s.Kind);
        Assert.Equal(key, s.Key);
        Assert.Equal((x, y), (s.X, s.Y));
    }

    [Theory]
    [InlineData("")]
    [InlineData("hello")]
    [InlineData("key super 1 2")]
    [InlineData("key shift")]
    [InlineData("key shift x y")]
    [InlineData("key shift 1e400 2")]
    [InlineData("begin now")]
    public void Garbage_is_ignored(string line) => Assert.Null(DragLineProtocol.Parse(line));

    [Fact]
    public void Lines_longer_than_the_limit_are_ignored() => Assert.Null(DragLineProtocol.Parse("key shift 1 " + new string('9', 300)));
}

public class HostSessionTests
{
    private static Func<string, string?> Env(params (string Key, string Value)[] vars) =>
        name => vars.FirstOrDefault(v => v.Key == name).Value;

    [Fact]
    public void Gnome_on_wayland()
    {
        var s = HostSession.Detect(OsKind.Linux, Env(("XDG_SESSION_TYPE", "wayland"), ("XDG_CURRENT_DESKTOP", "ubuntu:GNOME"), ("DISPLAY", ":0")));
        Assert.True(s.IsWayland);
        Assert.True(s.IsGnome);
        Assert.Equal(DragWatcherKind.GnomeShell, DragWatcherFactory.Choose(s));
    }

    [Fact]
    public void Wayland_is_also_recognised_by_its_display_variable()
    {
        var s = HostSession.Detect(OsKind.Linux, Env(("WAYLAND_DISPLAY", "wayland-0"), ("XDG_CURRENT_DESKTOP", "KDE")));
        Assert.True(s.IsWayland);
        Assert.False(s.IsGnome);
        Assert.Equal(DragWatcherKind.None, DragWatcherFactory.Choose(s));   // KDE Wayland: file-manager entries only
    }

    [Fact]
    public void X11_session_uses_the_x11_watcher_whatever_the_desktop()
    {
        var s = HostSession.Detect(OsKind.Linux, Env(("XDG_SESSION_TYPE", "x11"), ("DISPLAY", ":0"), ("XDG_CURRENT_DESKTOP", "X-Cinnamon")));
        Assert.True(s.IsX11);
        Assert.Equal(DragWatcherKind.X11, DragWatcherFactory.Choose(s));
    }

    [Fact]
    public void Display_alone_means_x11()
    {
        var s = HostSession.Detect(OsKind.Linux, Env(("DISPLAY", ":1")));
        Assert.Equal(DragWatcherKind.X11, DragWatcherFactory.Choose(s));
    }

    [Fact]
    public void No_display_at_all_has_no_watcher()
    {
        var s = HostSession.Detect(OsKind.Linux, Env());
        Assert.Equal(DragWatcherKind.None, DragWatcherFactory.Choose(s));
    }

    [Fact]
    public void Mac_uses_the_drag_pasteboard()
    {
        var s = HostSession.Detect(OsKind.MacOS, Env());
        Assert.Equal(DragWatcherKind.MacPasteboard, DragWatcherFactory.Choose(s));
        Assert.True(s.IsMac);
    }

    [Theory]
    [InlineData("GNOME", true)]
    [InlineData("pop:GNOME", true)]
    [InlineData("GNOME-Classic:GNOME", true)]
    [InlineData("Unity", false)]
    [InlineData("KDE", false)]
    [InlineData("gnome", true)]
    public void Gnome_detection(string desktop, bool gnome) =>
        Assert.Equal(gnome, HostSession.Detect(OsKind.Linux, Env(("XDG_CURRENT_DESKTOP", desktop))).IsGnome);
}
