using System.Globalization;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace DropFlow.Overlay;

/// <summary>What the ring shows: a percentage while the job runs (null = not known yet: a spinning arc), then a tick or an exclamation mark.</summary>
public enum RingState { Running, Done, Failed }

/// <summary>
/// After a drop the wheel folds into this small ring at the same spot: it shows the job's progress, then ✓ or ! for a moment, and goes.
/// Topmost, never takes focus and lets clicks through, so it never gets in the way; the toast still carries Cancel and the result.
/// </summary>
public sealed class ProgressRingWindow : Window
{
    public const double SizeDips = 92;
    public static readonly TimeSpan ResultTime = TimeSpan.FromMilliseconds(700);

    private readonly RingVisual _ring;
    private nint _hwnd;
    private DispatcherTimer? _spin;

    public ProgressRingWindow(WheelTheme theme)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        Focusable = false;
        IsHitTestVisible = false;
        Width = Height = SizeDips;
        Left = Top = -32000;
        Title = "DropFlow progress";
        _ring = new RingVisual(theme);
        Content = _ring;
    }

    public RingState State => _ring.State;

    public double? Progress => _ring.Progress;

    /// <summary>Shows the ring centred at a physical screen pixel.</summary>
    public void ShowAt(PixelPoint center)
    {
        _hwnd = new WindowInteropHelper(this).EnsureHandle();
        WindowNative.MakeNonActivating(_hwnd);
        WindowNative.SetClickThrough(_hwnd, true);
        Show();
        WindowNative.ShowTopmostCentered(_hwnd, center, SizeDips);
        if (WheelControl.Animate())
        {
            var scale = new ScaleTransform(1.9, 1.9, SizeDips / 2, SizeDips / 2);
            _ring.RenderTransform = scale;
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.9, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.9, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
            _spin = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
            _spin.Tick += (_, _) => { if (_ring.Progress is null) _ring.Turn(); };
            _spin.Start();
        }
    }

    public void SetProgress(double? progress) => _ring.Set(RingState.Running, progress);

    /// <summary>Shows ✓ (or !) and closes after <see cref="ResultTime"/>.</summary>
    public void Finish(bool success)
    {
        _ring.Set(success ? RingState.Done : RingState.Failed, 1);
        _spin?.Stop();
        var close = new DispatcherTimer { Interval = ResultTime };
        close.Tick += (_, _) => { close.Stop(); Close(); };
        close.Start();
    }

    protected override void OnClosed(EventArgs e)
    {
        _spin?.Stop();
        base.OnClosed(e);
    }

    /// <summary>The drawing, separate so it can be rendered in tests without a window.</summary>
    public sealed class RingVisual(WheelTheme theme) : FrameworkElement
    {
        private static readonly Typeface Face = new(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
        private double _turn;

        public RingState State { get; private set; } = RingState.Running;
        public double? Progress { get; private set; }

        public void Set(RingState state, double? progress)
        {
            State = state;
            Progress = progress is { } p ? Math.Clamp(p, 0, 1) : null;
            InvalidateVisual();
        }

        public void Turn()
        {
            _turn = (_turn + 12) % 360;
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            var size = Math.Min(ActualWidth, ActualHeight);
            if (size <= 0) return;
            var c = new Point(ActualWidth / 2, ActualHeight / 2);
            var r = size / 2 - 6;
            var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            dc.DrawEllipse(theme.Panel, new Pen(theme.PanelBorder, 1), c, r + 4, r + 4);
            dc.DrawEllipse(null, new Pen(theme.Wedge, 6), c, r - 4, r - 4);

            var color = State == RingState.Failed ? Brushes.IndianRed : State == RingState.Done ? theme.Highlight : theme.Accent;
            var (from, sweep) = Progress is { } p ? (0.0, Math.Max(0.001, p) * 359.9) : (_turn, 90.0);
            var arc = new StreamGeometry();
            using (var ctx = arc.Open())
            {
                Point P(double a) { var (x, y) = WheelGeometry.OnCircle(r - 4, a); return new Point(c.X + x, c.Y + y); }
                ctx.BeginFigure(P(from), false, false);
                ctx.ArcTo(P(from + sweep), new Size(r - 4, r - 4), 0, sweep > 180, SweepDirection.Clockwise, true, false);
            }
            arc.Freeze();
            dc.DrawGeometry(null, new Pen(color, 6) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, arc);

            var label = State switch
            {
                RingState.Done => "✓",
                RingState.Failed => "!",
                _ => Progress is { } q ? $"{Math.Round(q * 100).ToString(CultureInfo.InvariantCulture)}%" : "",
            };
            if (label.Length == 0) return;
            var text = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, State == RingState.Running ? 17 : 30,
                State == RingState.Running ? theme.Text : color, dpi);
            dc.DrawText(text, new Point(c.X - text.Width / 2, c.Y - text.Height / 2));
        }
    }
}
