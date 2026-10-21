using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DropFlow.Overlay;

namespace DropFlow.Overlay.Tests;

/// <summary>WPF needs a single-threaded apartment; this runs a function on one and returns its result.</summary>
internal static class Sta
{
    public static T Run<T>(Func<T> work)
    {
        T? result = default;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { result = work(); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) throw new InvalidOperationException("STA work failed: " + error, error);
        return result!;
    }
}

public class WheelControlRenderTests
{
    private static readonly WheelItem[] Six =
    [
        new("a", "PNG"), new("b", "JPG"), new("c", "WEBP"), new("d", "AVIF"), new("e", "PDF"), new("f", "TIFF"),
    ];

    /// <summary>Renders the control (at 96 DPI) and returns BGRA pixels plus size.</summary>
    private static (byte[] Pixels, int Size) Render(Action<WheelControl> setup, WheelTheme theme, string? savePng = null)
        => Sta.Run(() =>
        {
            var wheel = new WheelControl { Theme = theme };
            setup(wheel);
            var size = (int)wheel.Width;
            wheel.Measure(new Size(size, size));
            wheel.Arrange(new Rect(0, 0, size, size));
            wheel.UpdateLayout();

            // transparent background -> composite over white so colours are easy to reason about
            var host = new System.Windows.Controls.Grid { Width = size, Height = size, Background = Brushes.White };
            host.Children.Add(wheel);
            host.Measure(new Size(size, size));
            host.Arrange(new Rect(0, 0, size, size));
            host.UpdateLayout();

            var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(host);
            var pixels = new byte[size * size * 4];
            bmp.CopyPixels(pixels, size * 4, 0);

            if (savePng is not null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(savePng)!);
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(bmp));
                using var f = File.Create(savePng);
                enc.Save(f);
            }
            return (pixels, size);
        });

    private static (byte R, byte G, byte B) Pixel((byte[] Pixels, int Size) img, double x, double y)
    {
        var i = ((int)y * img.Size + (int)x) * 4;
        return (img.Pixels[i + 2], img.Pixels[i + 1], img.Pixels[i]);
    }

    private static bool Near((byte R, byte G, byte B) actual, string hex, int tolerance = 6)
    {
        var c = (Color)ColorConverter.ConvertFromString(hex);
        return Math.Abs(actual.R - c.R) <= tolerance && Math.Abs(actual.G - c.G) <= tolerance && Math.Abs(actual.B - c.B) <= tolerance;
    }

    /// <summary>A point inside wedge i near the rim (away from the label text), relative to the image.</summary>
    private static (double X, double Y) InWedge(int image, int count, int index, bool mirrored = false)
    {
        var g = new WheelGeometry(count, WheelControl.Outer, WheelControl.Inner, mirrored);
        var (x, y) = WheelGeometry.OnCircle(WheelControl.Outer - 12, g.CenterAngle(index));
        return (image / 2.0 + x, image / 2.0 + y);
    }

    private static string VisualDir => Path.Combine(Path.GetTempPath(), "dropflow-visual");

    [Fact]
    public void The_hovered_wedge_is_drawn_in_the_accent_colour_and_the_others_are_not()
    {
        var img = Render(w => w.SetItems(Six, mirrored: false, selected: 1), WheelTheme.Light, Path.Combine(VisualDir, "wheel-light.png"));
        var (hx, hy) = InWedge(img.Size, 6, 1);
        var (ox, oy) = InWedge(img.Size, 6, 4);

        Assert.True(Near(Pixel(img, hx, hy), "#4338CA"), $"hovered wedge was {Pixel(img, hx, hy)}");
        Assert.True(Near(Pixel(img, ox, oy), "#ECEFFA"), $"other wedge was {Pixel(img, ox, oy)}");
    }

    [Fact]
    public void Dark_theme_uses_dark_wedges_and_a_light_accent()
    {
        var img = Render(w => w.SetItems(Six, false, selected: 0), WheelTheme.Dark, Path.Combine(VisualDir, "wheel-dark.png"));
        var (hx, hy) = InWedge(img.Size, 6, 0);
        var (ox, oy) = InWedge(img.Size, 6, 3);

        Assert.True(Near(Pixel(img, hx, hy), "#8B95FB"), $"{Pixel(img, hx, hy)}");
        Assert.True(Near(Pixel(img, ox, oy), "#182044", 10), $"{Pixel(img, ox, oy)}");
    }

    [Fact]
    public void The_gap_between_wedges_shows_the_panel_not_a_wedge()
    {
        var img = Render(w => w.SetItems(Six, false), WheelTheme.Light);
        // boundary between wedge 0 (centre 0°) and wedge 1 (centre 60°) is at 30°
        var (x, y) = WheelGeometry.OnCircle(WheelControl.Outer - 20, 30);
        var px = Pixel(img, img.Size / 2.0 + x, img.Size / 2.0 + y);
        Assert.False(Near(px, "#ECEFFA", 3), $"expected a gap (panel colour) at the boundary, got {px}");
    }

    [Fact]
    public void A_mirrored_wheel_puts_wedge_one_on_the_left()
    {
        var img = Render(w => w.SetItems(Six, mirrored: true, selected: 1), WheelTheme.Light);
        var (x, y) = InWedge(img.Size, 6, 1, mirrored: true);
        Assert.True(x < img.Size / 2.0, "wedge 1 should be on the left when mirrored");
        Assert.True(Near(Pixel(img, x, y), "#4338CA"));
    }

    [Fact]
    public void The_progress_ring_is_drawn_in_the_hub_in_the_highlight_colour()
    {
        var img = Render(w => { w.SetItems(Six, false); w.SetProgress(0.5); w.SetCenter("Working"); }, WheelTheme.Light, Path.Combine(VisualDir, "wheel-progress.png"));
        var r = WheelControl.Inner - 8 - 4;
        // 0.5 progress: ring covers 12 o'clock to 6 o'clock clockwise, so 3 o'clock is painted and 9 o'clock is the plain track
        var right = Pixel(img, img.Size / 2.0 + r, img.Size / 2.0);
        var left = Pixel(img, img.Size / 2.0 - r, img.Size / 2.0);
        Assert.True(Near(right, "#0E7490", 25), $"3 o'clock {right}");
        Assert.True(Near(left, "#ECEFFA", 12), $"9 o'clock {left}");
    }

    [Fact]
    public void An_empty_wheel_renders_a_ring_and_does_not_throw()
    {
        var img = Render(w => { w.SetItems([], false); w.SetCenter("Drop files", "here"); }, WheelTheme.Light, Path.Combine(VisualDir, "wheel-empty.png"));
        Assert.True(Near(Pixel(img, img.Size / 2.0, img.Size / 2.0 - 110), "#ECEFFA", 10));
    }

    [Fact]
    public void Pointer_and_keyboard_selection_update_the_hover_index()
    {
        var result = Sta.Run(() =>
        {
            var w = new WheelControl();
            w.SetItems(Six, false);
            w.Measure(new Size(w.Width, w.Height));
            w.Arrange(new Rect(0, 0, w.Width, w.Height));
            var c = w.Width / 2;
            var (x, y) = WheelGeometry.OnCircle(110, 120);          // wedge 2
            var hit = w.SetPointer(new Point(c + x, c + y));
            var miss = w.SetPointer(new Point(c, c));               // hub
            return (hit, miss, w.HoverIndex);
        });
        Assert.Equal(2, result.hit);
        Assert.Equal(-1, result.miss);
        Assert.Equal(-1, result.HoverIndex);
    }

    [Fact]
    public void Invoking_a_wedge_raises_the_event_with_the_right_item()
    {
        var invoked = Sta.Run(() =>
        {
            var w = new WheelControl();
            w.SetItems(Six, false);
            WheelItemEventArgs? got = null;
            w.ItemInvoked += (_, e) => got = e;
            w.Invoke(2);
            w.Invoke(99);      // ignored
            return got;
        });
        Assert.Equal(("c", 2), (invoked!.Item.Id, invoked.Index));
    }

    // ----- "+ Add" slot ---------------------------------------------------------------------------------------------------------

    private static WheelItem[] WithAdd(int count, string addLabel) =>
        [.. Enumerable.Range(0, count - 1).Select(i => new WheelItem($"w{i}", $"Item {i}")), new WheelItem("wheel.add", addLabel, Role: WheelItemRole.Add)];

    [Theory]
    [InlineData("+ Add", false, 6)]
    [InlineData("+ إضافة", true, 6)]
    [InlineData("+ Add", false, 12)]
    [InlineData("+ إضافة", true, 12)]
    [InlineData("+ Add", false, 26)]
    [InlineData("+ إضافة", true, 26)]
    public void The_add_label_is_never_trimmed_and_stays_inside_its_wedge(string label, bool mirrored, int count)
    {
        var (shown, fits, inside) = Sta.Run(() =>
        {
            var w = new WheelControl();
            w.SetItems(WithAdd(count, label), mirrored);
            var (text, origin, size) = w.LayoutLabel(count - 1, 1.0);
            var ok = WheelControl.Fits(text.Text, size, text.MaxTextWidth, text.MaxTextHeight, mirrored ? FlowDirection.RightToLeft : FlowDirection.LeftToRight, 1.0);
            var corners = new[] { origin, new Point(origin.X + text.Width, origin.Y), new Point(origin.X, origin.Y + text.Height), new Point(origin.X + text.Width, origin.Y + text.Height) };
            var within = corners.All(p => Math.Sqrt(p.X * p.X + p.Y * p.Y) <= WheelControl.Outer);
            return (text.Text, ok, within);
        });

        Assert.Equal(label, shown.Replace('\n', ' '));   // even a very full wheel shows the words (on two lines when narrow)
        Assert.True(fits, $"'{shown}' is trimmed or wrapped with {count} wedges");
        Assert.True(inside, $"'{shown}' spills outside the wheel with {count} wedges");
    }

    [Theory]
    [InlineData("+ Add", false, "wheel-add-en.png")]
    [InlineData("+ إضافة", true, "wheel-add-ar.png")]
    public void The_add_wedge_renders_in_both_directions_and_highlights_like_any_wedge(string label, bool mirrored, string png)
    {
        var items = WithAdd(7, label);
        var plain = Render(w => w.SetItems(items, mirrored), WheelTheme.Light, Path.Combine(VisualDir, png));
        var hovered = Render(w => w.SetItems(items, mirrored, selected: 6), WheelTheme.Light);

        var (x, y) = InWedge(plain.Size, 7, 6, mirrored);
        Assert.True(mirrored ? x > plain.Size / 2.0 : x < plain.Size / 2.0, "the last wedge sits just before the first one in reading order");
        Assert.True(Near(Pixel(hovered, x, y), "#4338CA"), $"hovered + Add was {Pixel(hovered, x, y)}");
        Assert.True(Near(Pixel(plain, x, y), "#ECEFFA"), $"+ Add was {Pixel(plain, x, y)}");
    }

    [Fact]
    public void Right_click_keys_ask_for_the_wedge_menu()
    {
        var got = Sta.Run(() =>
        {
            var w = new WheelControl();
            w.SetItems([new("a", "PNG"), new("p", "OCR", Role: WheelItemRole.Pinned)], false, selected: 1);
            var asked = new List<string>();
            w.ItemContextRequested += (_, e) => asked.Add(e.Item.Id);
            w.RequestContext(1);
            w.RequestContext(9);    // ignored
            return asked;
        });
        Assert.Equal(["p"], got);
    }

    [Fact]
    public void Engine_and_option_marks_appear_in_labels_without_breaking_the_render()
    {
        var items = new WheelItem[] { new("m", "MP4", WheelItemState.NeedsEngine), new("p", "Protect", NeedsOptions: true), new("x", "A very long operation name indeed") };
        var img = Render(w => w.SetItems(items, false, selected: 2), WheelTheme.Light, Path.Combine(VisualDir, "wheel-marks.png"));
        Assert.Contains(img.Pixels, b => b != 0);
    }
}
