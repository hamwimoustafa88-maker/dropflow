// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;

namespace DropFlow.Desktop.Views;

/// <summary>DropFlow's own colours (the same indigo/cyan identity as the Windows wheel and the website).</summary>
public sealed class Palette
{
    public required IBrush Panel { get; init; }
    public required IBrush PanelBorder { get; init; }
    public required IBrush Wedge { get; init; }
    public required IBrush WedgeHover { get; init; }
    public required IBrush Accent { get; init; }
    public required IBrush Hub { get; init; }
    public required IBrush Text { get; init; }
    public required IBrush TextMuted { get; init; }
    public required IBrush TextOnAccent { get; init; }
    public required IBrush Highlight { get; init; }
    public required IBrush Error { get; init; }
    public required bool IsDark { get; init; }

    private static IBrush B(string hex, double opacity = 1) => new SolidColorBrush(Color.Parse(hex), opacity).ToImmutable();

    public static Palette Light { get; } = new()
    {
        IsDark = false,
        Panel = B("#FFFFFF", 0.97), PanelBorder = B("#DADFF0"),
        Wedge = B("#ECEFFA"), WedgeHover = B("#D9DFFB"), Accent = B("#4338CA"),
        Hub = B("#F7F8FC"), Text = B("#0A0F24"), TextMuted = B("#4A5372"), TextOnAccent = B("#FFFFFF"), Highlight = B("#0E7490"),
        Error = B("#DC2626"),
    };

    public static Palette Dark { get; } = new()
    {
        IsDark = true,
        Panel = B("#0F1631", 0.97), PanelBorder = B("#263059"),
        Wedge = B("#182044"), WedgeHover = B("#252F63"), Accent = B("#8B95FB"),
        Hub = B("#070B1C"), Text = B("#EEF1FF"), TextMuted = B("#A3ACD8"), TextOnAccent = B("#070B1C"), Highlight = B("#22D3EE"),
        Error = B("#F87171"),
    };

    /// <summary>Follows the system light/dark setting.</summary>
    public static Palette Current => Application.Current?.ActualThemeVariant == ThemeVariant.Dark ? Dark : Light;

    /// <summary>Fonts that carry Arabic on each system, ahead of Avalonia's default.</summary>
    public static FontFamily Font { get; } = new(OperatingSystem.IsMacOS()
        ? "SF Pro Text, Geeza Pro, Helvetica Neue, Arial"
        : "Noto Sans, Noto Sans Arabic, Cantarell, Ubuntu, DejaVu Sans, Liberation Sans");
}
