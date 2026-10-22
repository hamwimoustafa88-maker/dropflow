using Avalonia;
using Avalonia.Input;
using DropFlow.Desktop.App;
using DropFlow.Desktop.Platform;
using DropFlow.Desktop.Wheel;

namespace DropFlow.Desktop.Tests;

public class DropEffectPolicyTests
{
    [Theory]
    [InlineData(DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link, DragDropEffects.Copy)]
    [InlineData(DragDropEffects.Copy, DragDropEffects.Copy)]
    [InlineData(DragDropEffects.Move, DragDropEffects.Move)]                  // Finder while ⌘ is held: accept, never delete the source
    [InlineData(DragDropEffects.Link, DragDropEffects.Link)]
    [InlineData(DragDropEffects.None, DragDropEffects.None)]
    public void Prefers_copy(DragDropEffects allowed, DragDropEffects expected) => Assert.Equal(expected, DropEffectPolicy.Choose(allowed));
}

public class WheelPlacementTests
{
    private static readonly PixelRect Screen = new(0, 0, 1920, 1080);

    [Fact]
    public void Centred_on_the_pointer() =>
        Assert.Equal((960 - 200, 540 - 200), WheelPlacement.TopLeft(960, 540, 400, Screen));

    [Fact]
    public void Kept_on_screen_near_the_edges()
    {
        Assert.Equal((0, 0), WheelPlacement.TopLeft(10, 10, 400, Screen));
        Assert.Equal((1920 - 400, 1080 - 400), WheelPlacement.TopLeft(1915, 1075, 400, Screen));
    }

    [Fact]
    public void A_second_monitor_with_negative_coordinates()
    {
        var left = new PixelRect(-1280, 0, 1280, 1024);
        Assert.Equal((-1280, 300), WheelPlacement.TopLeft(-1270, 500, 400, left));
    }

    [Fact]
    public void Mac_frame_origin_is_bottom_left_in_points() =>
        // Cocoa: the pointer (500, 300) is measured from the bottom-left; the window's origin is its own bottom-left corner.
        Assert.Equal((300d, 100d), WheelPlacement.CocoaOrigin(500, 300, 400));
}

public class WelcomeTextTests
{
    [Fact]
    public void Mac_explains_the_keys_the_menu_bar_and_gatekeeper()
    {
        var keys = WelcomeText.Keys(HostSession.Detect(OsKind.MacOS, _ => null), GnomeExtensionState.NotNeeded);
        Assert.Equal(["welcome.drag.mac", "welcome.mac.menubar", "welcome.mac.gatekeeper", "welcome.privacy"], keys);
    }

    [Fact]
    public void Gnome_wayland_mentions_signing_out_when_the_extension_waits()
    {
        var session = new HostSession(OsKind.Linux, IsWayland: true, IsX11: false, Desktop: "GNOME");
        var keys = WelcomeText.Keys(session, GnomeExtensionState.NeedsSignOut);
        Assert.Contains("welcome.gnome.signout", keys);
        Assert.Contains("welcome.drag.linux", keys);
        Assert.Contains("welcome.clickAlso", keys);
    }

    [Fact]
    public void Other_wayland_desktops_get_the_file_manager_route_only()
    {
        var session = new HostSession(OsKind.Linux, IsWayland: true, IsX11: false, Desktop: "KDE");
        var keys = WelcomeText.Keys(session, GnomeExtensionState.NotNeeded);
        Assert.Contains("welcome.click", keys);
        Assert.DoesNotContain("welcome.drag.linux", keys);
    }

    [Fact]
    public void X11_gets_the_drag_explanation()
    {
        var session = new HostSession(OsKind.Linux, IsWayland: false, IsX11: true, Desktop: "XFCE");
        Assert.Equal("welcome.drag.linux", WelcomeText.Keys(session, GnomeExtensionState.NotNeeded)[0]);
    }
}
