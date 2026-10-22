// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using DropFlow.Desktop.App;
using DropFlow.Desktop.Native;

namespace DropFlow.Desktop.Views;

/// <summary>
/// The progress / result card in the bottom corner of the screen (bottom-left in Arabic). Never takes focus; one card is reused for
/// every message so a batch updates in place.
/// </summary>
public sealed class ToastWindow : Window
{
    private readonly TextBlock _title = new() { FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, MaxWidth = 320 };
    private readonly TextBlock _detail = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxWidth = 320 };
    private readonly ProgressBar _progress = new() { Minimum = 0, Maximum = 1, Height = 4, Margin = new Thickness(0, 8, 0, 0) };
    private readonly StackPanel _buttons = new() { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 10, 0, 0) };
    private readonly DesktopLocalizer _loc;
    private DispatcherTimer? _hideTimer;
    private bool _prepared;

    public ToastWindow(DesktopLocalizer loc)
    {
        _loc = loc;
        this.Styled(loc, "DropFlow");
        WindowDecorations = WindowDecorations.None;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        CanResize = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        var p = Palette.Current;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Content = new Border
        {
            Background = p.Panel,
            BorderBrush = p.PanelBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(16, 12),
            Width = 352,
            Child = new StackPanel { Children = { _title, _detail, _progress, _buttons } },
        };
        _detail.Foreground = p.TextMuted;
    }

    /// <param name="progress">0..1 shows a bar, negative an indeterminate bar, null none.</param>
    public void Present(string title, string? detail, double? progress, IReadOnlyList<(string Label, Action Run)> actions, TimeSpan? autoHide)
    {
        _title.Text = title;
        _detail.Text = detail ?? "";
        _detail.IsVisible = !string.IsNullOrEmpty(detail);
        _progress.IsVisible = progress is not null;
        _progress.IsIndeterminate = progress is < 0;
        if (progress is >= 0) _progress.Value = progress.Value;

        _buttons.Children.Clear();
        foreach (var (label, run) in actions)
        {
            var b = Kit.Secondary(label);
            b.MinWidth = 0;
            b.Click += (_, _) => run();
            _buttons.Children.Add(b);
        }
        _buttons.IsVisible = actions.Count > 0;

        _hideTimer?.Stop();
        if (autoHide is { } after)
        {
            _hideTimer = new DispatcherTimer(after, DispatcherPriority.Background, (_, _) => { _hideTimer?.Stop(); Hide(); });
            _hideTimer.Start();
        }

        if (!_prepared)
        {
            _prepared = true;
            if (OperatingSystem.IsMacOS())
            {
                var handle = TryGetPlatformHandle();
                MacWindow.MakeOverlay(MacWindow.FromHandle(handle?.Handle ?? IntPtr.Zero, handle?.HandleDescriptor));
            }
        }
        if (!IsVisible) Show();
        Place();
        if (OperatingSystem.IsMacOS())
        {
            var handle = TryGetPlatformHandle();
            ObjC.Send(MacWindow.FromHandle(handle?.Handle ?? IntPtr.Zero, handle?.HandleDescriptor), ObjC.Sel("orderFrontRegardless"));
        }
    }

    private void Place()
    {
        var screen = Screens.Primary;
        if (screen is null) return;
        var area = screen.WorkingArea;
        var scale = screen.Scaling;
        var width = (int)Math.Ceiling(Math.Max(Bounds.Width, 352) * scale);
        var height = (int)Math.Ceiling(Math.Max(Bounds.Height, 60) * scale);
        var margin = (int)(16 * scale);
        var x = _loc.IsRightToLeft ? area.X + margin : area.Right - width - margin;
        Position = new PixelPoint(x, area.Bottom - height - margin);
    }
}
