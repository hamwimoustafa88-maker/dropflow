using System.IO;
using System.Windows.Media;
using Microsoft.Win32;

namespace DropFlow.Overlay;

/// <summary>
/// DropFlow's colours for the wheel and every desktop window: warm paper, violet and mint (the same identity as the website, 2026-11-18).
/// Frozen brushes, safe to share between threads. <see cref="With"/> applies the user's accent colour and wheel opacity.
/// </summary>
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
        Panel = B("#FFFFFF", 0.96), PanelBorder = B("#E4E1D8"),
        Wedge = B("#F1EFE9"), WedgeHover = B("#E6E3F7"), Accent = B("#5B4FD0"),
        Hub = B("#F8F7F3"), Text = B("#15141C"), TextMuted = B("#57555F"), TextOnAccent = B("#FFFFFF"), Highlight = B("#08765A"),
    };

    public static WheelTheme Dark { get; } = new()
    {
        IsDark = true,
        Panel = B("#1A1924", 0.96), PanelBorder = B("#2F2D3D"),
        Wedge = B("#232230"), WedgeHover = B("#2F2C4A"), Accent = B("#A59CF6"),
        Hub = B("#0F0E16"), Text = B("#F2F1F7"), TextMuted = B("#AAA8BA"), TextOnAccent = B("#15141C"), Highlight = B("#3FCF9F"),
    };

    /// <summary>Accent swatches offered in Settings (the first is DropFlow's own violet).</summary>
    public static readonly string[] AccentSwatches = ["#5B4FD0", "#0A7A5C", "#0E7490", "#B4235A", "#C2410C", "#1D4ED8", "#6D28D9", "#374151"];

    /// <summary>
    /// This theme with the user's accent colour (null keeps DropFlow's) and wheel opacity (0.6-1, applied to the wheel's own surfaces).
    /// Text on the accent is black or white, whichever reads better (at least 4.5:1 for any swatch).
    /// </summary>
    public WheelTheme With(string? accentHex, double opacity = 1)
    {
        var accent = Accent;
        var onAccent = TextOnAccent;
        if (accentHex is not null && TryParse(accentHex, out var color))
        {
            // In dark mode a dark accent would vanish: lighten it until it stands out from the wedges.
            if (IsDark) color = Mix(color, Colors.White, 0.45);
            accent = Solid(color);
            onAccent = Solid(ContrastRatio(color, Colors.White) >= ContrastRatio(color, Color.FromRgb(0x15, 0x14, 0x1C)) ? Colors.White : Color.FromRgb(0x15, 0x14, 0x1C));
        }
        var o = Math.Clamp(opacity, 0.6, 1);
        return new WheelTheme
        {
            IsDark = IsDark,
            Panel = Faded(Panel, o), PanelBorder = PanelBorder, Wedge = Faded(Wedge, o), WedgeHover = Faded(WedgeHover, o),
            Accent = accent, Hub = Faded(Hub, o), Text = Text, TextMuted = TextMuted, TextOnAccent = onAccent, Highlight = Highlight,
        };
    }

    public static bool TryParse(string hex, out Color color)
    {
        color = default;
        try
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(hex, "^#[0-9A-Fa-f]{6}$")) return false;
            color = (Color)ColorConverter.ConvertFromString(hex);
            return true;
        }
        catch (FormatException) { return false; }
    }

    /// <summary>WCAG contrast ratio of two opaque colours.</summary>
    public static double ContrastRatio(Color a, Color b)
    {
        static double L(Color c)
        {
            static double Ch(byte v) { var x = v / 255.0; return x <= 0.03928 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4); }
            return 0.2126 * Ch(c.R) + 0.7152 * Ch(c.G) + 0.0722 * Ch(c.B);
        }
        var (hi, lo) = (Math.Max(L(a), L(b)), Math.Min(L(a), L(b)));
        return (hi + 0.05) / (lo + 0.05);
    }

    private static Color Mix(Color a, Color b, double t) =>
        Color.FromRgb((byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    private static Brush Solid(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    private static Brush Faded(Brush brush, double opacity)
    {
        if (opacity >= 1) return brush;
        var b = brush.Clone();
        b.Opacity = brush.Opacity * opacity;
        b.Freeze();
        return b;
    }

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
