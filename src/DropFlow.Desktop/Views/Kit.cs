// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using DropFlow.Desktop.App;

namespace DropFlow.Desktop.Views;

/// <summary>Small helpers so every DropFlow window looks the same (palette, font, direction, icon).</summary>
public static class Kit
{
    private static WindowIcon? _icon;

    public static WindowIcon Icon => _icon ??= new WindowIcon(typeof(Kit).Assembly.GetManifestResourceStream("DropFlow.Desktop.icon-192.png")!);

    public static Bitmap IconBitmap(int size) =>
        Bitmap.DecodeToWidth(typeof(Kit).Assembly.GetManifestResourceStream("DropFlow.Desktop.icon-512.png")!, size);

    /// <summary>Applies the palette, font, direction and icon to a DropFlow window.</summary>
    public static T Styled<T>(this T window, DesktopLocalizer loc, string title) where T : Window
    {
        var p = Palette.Current;
        window.Title = title;
        window.Icon = Icon;
        window.Background = p.Hub;
        window.Foreground = p.Text;
        window.FontFamily = Palette.Font;
        window.FontSize = 14;
        window.FlowDirection = loc.IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        return window;
    }

    public static TextBlock Heading(string text) => new()
    {
        Text = text,
        FontSize = 15,
        FontWeight = FontWeight.SemiBold,
        Margin = new Thickness(0, 14, 0, 6),
        Foreground = Palette.Current.Text,
    };

    public static TextBlock Muted(string text) => new()
    {
        Text = text,
        FontSize = 12,
        TextWrapping = TextWrapping.Wrap,
        Foreground = Palette.Current.TextMuted,
        Margin = new Thickness(0, 2, 0, 6),
    };

    public static TextBlock Paragraph(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 0, 0, 10),
        Foreground = Palette.Current.Text,
        LineHeight = 21,
    };

    public static Button Primary(string text)
    {
        var p = Palette.Current;
        return new Button
        {
            Content = text,
            MinWidth = 96,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Background = p.Accent,
            Foreground = p.TextOnAccent,
            Padding = new Thickness(14, 7),
        };
    }

    public static Button Secondary(string text) => new()
    {
        Content = text,
        MinWidth = 96,
        HorizontalContentAlignment = HorizontalAlignment.Center,
        Padding = new Thickness(14, 7),
    };

    /// <summary>A row of buttons aligned to the end of the line (right in English, left in Arabic).</summary>
    public static StackPanel ButtonRow(params Control[] buttons)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Margin = new Thickness(0, 12, 0, 0) };
        foreach (var b in buttons) row.Children.Add(b);
        return row;
    }
}
