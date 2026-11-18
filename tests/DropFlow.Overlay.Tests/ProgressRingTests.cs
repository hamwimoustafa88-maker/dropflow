using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DropFlow.Overlay;

namespace DropFlow.Overlay.Tests;

public class ProgressRingTests
{
    private static byte[] Render(Action<ProgressRingWindow.RingVisual> setup, string name) => Sta.Run(() =>
    {
        var ring = new ProgressRingWindow.RingVisual(WheelTheme.Light);
        setup(ring);
        const int size = (int)ProgressRingWindow.SizeDips;
        var host = new System.Windows.Controls.Grid { Width = size, Height = size, Background = Brushes.White, Children = { ring } };
        host.Measure(new Size(size, size));
        host.Arrange(new Rect(0, 0, size, size));
        host.UpdateLayout();
        var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(host);
        var dir = Path.Combine(Path.GetTempPath(), "dropflow-visual");
        Directory.CreateDirectory(dir);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using (var f = File.Create(Path.Combine(dir, name))) enc.Save(f);
        var px = new byte[size * size * 4];
        bmp.CopyPixels(px, size * 4, 0);
        return px;
    });

    [Fact]
    public void Running_done_and_failed_look_different_and_progress_fills_the_ring()
    {
        var spinning = Render(r => r.Set(RingState.Running, null), "ring-spin.png");
        var quarter = Render(r => r.Set(RingState.Running, 0.25), "ring-25.png");
        var most = Render(r => r.Set(RingState.Running, 0.9), "ring-90.png");
        var done = Render(r => r.Set(RingState.Done, 1), "ring-done.png");
        var failed = Render(r => r.Set(RingState.Failed, 1), "ring-failed.png");

        Assert.NotEqual(quarter, most);
        Assert.NotEqual(done, failed);
        Assert.NotEqual(spinning, quarter);
        // more of the ring is coloured at 90 % than at 25 %
        static int Coloured(byte[] px) { var n = 0; for (var i = 0; i < px.Length; i += 4) if (Math.Abs(px[i] - px[i + 2]) > 40) n++; return n; }
        Assert.True(Coloured(most) > Coloured(quarter) * 2, $"{Coloured(most)} vs {Coloured(quarter)}");
    }

    [Fact]
    public void Progress_is_kept_between_zero_and_one()
    {
        var p = Sta.Run(() =>
        {
            var r = new ProgressRingWindow.RingVisual(WheelTheme.Dark);
            r.Set(RingState.Running, 3);
            var high = r.Progress;
            r.Set(RingState.Running, -1);
            return (high, low: r.Progress);
        });
        Assert.Equal(1, p.high);
        Assert.Equal(0, p.low);
    }
}
