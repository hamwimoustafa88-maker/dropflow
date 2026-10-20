using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DropFlow.Core.Localization;
using DropFlow.Overlay;

namespace DropFlow.App.Views;

/// <summary>Small helpers so every window looks like DropFlow (same tokens as the wheel and the website).</summary>
internal sealed class UiKit(WheelTheme theme, ILocalizer loc)
{
    public WheelTheme Theme { get; } = theme;
    public ILocalizer Loc { get; } = loc;

    public FontFamily Font { get; } = new("Segoe UI Variable Text, Segoe UI, Tahoma");

    public FlowDirection Flow => Loc.IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

    public void Style(Window w, double width, string title)
    {
        w.Title = title;
        w.Width = width;
        w.SizeToContent = SizeToContent.Height;
        w.Background = Theme.Hub;
        w.Foreground = Theme.Text;
        w.FontFamily = Font;
        w.FontSize = 14;
        w.FlowDirection = Flow;
        w.ResizeMode = ResizeMode.NoResize;
        w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
    }

    public TextBlock Text(string text, double size = 14, bool muted = false, FontWeight? weight = null, bool wrap = true) => new()
    {
        Text = text,
        FontSize = size,
        Foreground = muted ? Theme.TextMuted : Theme.Text,
        FontWeight = weight ?? FontWeights.Normal,
        TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
        Margin = new Thickness(0, 0, 0, 4),
    };

    public Button Primary(string text) => new()
    {
        Content = text, MinWidth = 96, MinHeight = 34, Padding = new Thickness(14, 4, 14, 4),
        Background = Theme.Accent, Foreground = Theme.TextOnAccent, BorderThickness = new Thickness(0),
    };

    public Button Secondary(string text) => new()
    {
        Content = text, MinWidth = 80, MinHeight = 34, Padding = new Thickness(14, 4, 14, 4),
        Background = Theme.Wedge, Foreground = Theme.Text, BorderBrush = Theme.PanelBorder, BorderThickness = new Thickness(1),
    };

    public Border Card(UIElement child, double padding = 14) => new()
    {
        Background = Theme.Panel, BorderBrush = Theme.PanelBorder, BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(12), Padding = new Thickness(padding), Child = child, Margin = new Thickness(0, 0, 0, 12),
    };

    public TextBlock SectionTitle(string text) => new()
    {
        Text = text, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Theme.TextMuted, Margin = new Thickness(2, 6, 0, 6),
    };
}
