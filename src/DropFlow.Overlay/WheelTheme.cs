using System.IO;
using System.Windows.Media;
using Microsoft.Win32;

namespace DropFlow.Overlay;

/// <summary>DropFlow's wheel colours (same indigo/cyan identity as the web). Frozen brushes, safe to share between threads.</summary>
public sealed class WheelTheme
{
    public required Brush Panel { get; init; }
    public required Brush PanelBorder { get; init; }
    public required Brush Wedge { get; init; }
    public required Brush WedgeHover { get; init; }
    public required Brush Accent { get; init; }
    public required Brush Hub { get; init; }
    public required Brush Text { get; init; }
    public required Brush TextMuted { get; init; }
    public required Brush TextOnAccent { get; init; }
    public required Brush Highlight { get; init; }
    public required bool IsDark { get; init; }

    private static Brush B(string hex, double opacity = 1)
    {
        var c = (Color)ColorConverter.ConvertFromString(hex);
        var brush = new SolidColorBrush(c) { Opacity = opacity };
        brush.Freeze();
        return brush;
    }

    public static WheelTheme Light { get; } = new()
    {
        IsDark = false,
        Panel = B("#FFFFFF", 0.96), PanelBorder = B("#DADFF0"),
        Wedge = B("#ECEFFA"), WedgeHover = B("#D9DFFB"), Accent = B("#4338CA"),
        Hub = B("#F7F8FC"), Text = B("#0A0F24"), TextMuted = B("#4A5372"), TextOnAccent = B("#FFFFFF"), Highlight = B("#0E7490"),
    };

    public static WheelTheme Dark { get; } = new()
    {
        IsDark = true,
        Panel = B("#0F1631", 0.96), PanelBorder = B("#263059"),
        Wedge = B("#182044"), WedgeHover = B("#252F63"), Accent = B("#8B95FB"),
        Hub = B("#070B1C"), Text = B("#EEF1FF"), TextMuted = B("#A3ACD8"), TextOnAccent = B("#070B1C"), Highlight = B("#22D3EE"),
    };

    /// <summary>Follows the Windows "app mode" setting; falls back to light when it cannot be read.</summary>
    public static WheelTheme FromSystem()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0 ? Dark : Light;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return Light;
        }
    }
}
