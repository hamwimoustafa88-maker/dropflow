// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using DropFlow.Overlay;

namespace DropFlow.App.Views.Editors;

/// <summary>Small building blocks shared by the visual editors (sliders, segmented choices, colour palette, collapsible groups).</summary>
internal static class EditorControls
{
    /// <summary>DropFlow's annotation palette: the brand indigo/cyan plus the usual marker colours, black, grey and white.</summary>
    public static readonly string[] Palette =
        ["#ef4444", "#f97316", "#facc15", "#22c55e", "#0e7490", "#0ea5e9", "#4338ca", "#a855f7", "#ec4899", "#000000", "#6b7280", "#ffffff"];

    /// <summary>A labelled slider with its value. Double-clicking the label puts the neutral value back.</summary>
    public static (FrameworkElement Row, Slider Slider) SliderRow(UiKit ui, string label, double min, double max, double value, double neutral,
        Action<double> changed, string format = "0")
    {
        var slider = new Slider
        {
            Minimum = min, Maximum = max, Value = value, IsMoveToPointEnabled = true, SmallChange = 1, LargeChange = 10,
            VerticalAlignment = VerticalAlignment.Center,
        };
        System.Windows.Automation.AutomationProperties.SetName(slider, label);
        var number = new TextBlock
        {
            Text = value.ToString(format, CultureInfo.InvariantCulture), Width = 44, TextAlignment = TextAlignment.Right,
            Foreground = ui.Theme.TextMuted, FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
        };
        var text = new TextBlock { Text = label, Foreground = ui.Theme.Text, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, ToolTip = ui.Loc["editor.resetTip"] };
        slider.ValueChanged += (_, e) =>
        {
            number.Text = e.NewValue.ToString(format, CultureInfo.InvariantCulture);
            changed(e.NewValue);
        };
        text.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) slider.Value = neutral; };

        var head = new DockPanel();
        DockPanel.SetDock(number, Dock.Right);
        head.Children.Add(number);
        head.Children.Add(text);
        var row = new StackPanel { Margin = new Thickness(0, 2, 0, 6), Children = { head, slider } };
        return (row, slider);
    }

    /// <summary>A row of toggle buttons where exactly one is on.</summary>
    public static (FrameworkElement Panel, Action<T> Select) Segmented<T>(UiKit ui, IEnumerable<(string Label, T Value)> items, T selected, Action<T> changed)
    {
        var panel = new WrapPanel { Margin = new Thickness(0, 2, 0, 8) };
        var buttons = new List<(ToggleButton Button, T Value)>();
        var syncing = false;
        foreach (var (label, value) in items)
        {
            var b = new ToggleButton
            {
                Content = label, Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 6, 6), MinHeight = 30,
                Background = ui.Theme.Wedge, Foreground = ui.Theme.Text, BorderBrush = ui.Theme.PanelBorder,
                IsChecked = EqualityComparer<T>.Default.Equals(value, selected),
            };
            buttons.Add((b, value));
            b.Checked += (_, _) =>
            {
                if (syncing) return;
                syncing = true;
                foreach (var (other, _) in buttons) if (other != b) other.IsChecked = false;
                syncing = false;
                changed(value);
            };
            b.Unchecked += (_, _) => { if (!syncing) { syncing = true; b.IsChecked = true; syncing = false; } };
            panel.Children.Add(b);
        }

        void Select(T value)
        {
            syncing = true;
            foreach (var (b, v) in buttons) b.IsChecked = EqualityComparer<T>.Default.Equals(v, value);
            syncing = false;
        }
        return (panel, Select);
    }

    /// <summary>Colour swatches plus a "#rrggbb" box. <paramref name="changed"/> receives a normalised "#rrggbb" (or "transparent").</summary>
    public static (FrameworkElement Panel, Action<string> Set) ColorPicker(UiKit ui, string initial, Action<string> changed, bool allowTransparent = false)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 2, 0, 8) };
        var swatches = new WrapPanel();
        var box = new TextBox { Text = initial, Width = 96, MinHeight = 28, Padding = new Thickness(4, 3, 4, 3), FlowDirection = FlowDirection.LeftToRight, HorizontalAlignment = HorizontalAlignment.Left };
        var current = initial;
        var rings = new List<(Border Ring, string Color)>();

        void Mark()
        {
            foreach (var (ring, color) in rings)
                ring.BorderBrush = string.Equals(color, current, StringComparison.OrdinalIgnoreCase) ? ui.Theme.Accent : Brushes.Transparent;
        }

        void Set(string color, bool fromBox = false)
        {
            current = color;
            if (!fromBox) box.Text = color;
            Mark();
        }

        IEnumerable<string> colors = allowTransparent ? [.. Palette, "transparent"] : Palette;
        foreach (var color in colors)
        {
            var fill = color == "transparent" ? (Brush)new LinearGradientBrush(Colors.White, Color.FromRgb(0xDD, 0xE0, 0xE8), 45) : Brush(color);
            var dot = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Background = fill, BorderBrush = new SolidColorBrush(Color.FromArgb(60, 0, 0, 0)), BorderThickness = new Thickness(1) };
            var ring = new Border { Padding = new Thickness(2), CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(2), Child = dot, Margin = new Thickness(0, 0, 4, 4), Cursor = Cursors.Hand, ToolTip = color };
            rings.Add((ring, color));
            ring.MouseLeftButtonUp += (_, _) => { Set(color); changed(color); };
            swatches.Children.Add(ring);
        }
        box.TextChanged += (_, _) =>
        {
            var normalised = Normalise(box.Text, allowTransparent);
            if (normalised is null || string.Equals(normalised, current, StringComparison.OrdinalIgnoreCase)) return;
            Set(normalised, fromBox: true);
            changed(normalised);
        };
        panel.Children.Add(swatches);
        panel.Children.Add(box);
        Mark();
        return (panel, c => Set(c));
    }

    /// <summary>"#abc", "abc", "#aabbcc" → "#aabbcc"; anything else → null.</summary>
    public static string? Normalise(string? text, bool allowTransparent = false)
    {
        var t = (text ?? "").Trim();
        if (allowTransparent && t.Equals("transparent", StringComparison.OrdinalIgnoreCase)) return "transparent";
        if (t.StartsWith('#')) t = t[1..];
        if (t.Length == 3 && t.All(Uri.IsHexDigit)) t = string.Concat(t.Select(c => $"{c}{c}"));
        return t.Length == 6 && t.All(Uri.IsHexDigit) ? "#" + t.ToLowerInvariant() : null;
    }

    public static SolidColorBrush Brush(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    public static Color ColorOf(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    /// <summary>A collapsible group with a bold title.</summary>
    public static Expander Group(UiKit ui, string title, UIElement content, bool expanded = true) => new()
    {
        Header = new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, Foreground = ui.Theme.Text, FontSize = 13 },
        Content = new Border { Padding = new Thickness(4, 6, 0, 4), Child = content },
        IsExpanded = expanded,
        Margin = new Thickness(0, 0, 0, 8),
        Foreground = ui.Theme.Text,
    };

    public static TextBlock Label(UiKit ui, string text)
    {
        var t = ui.Text(text, 12, muted: true);
        t.Margin = new Thickness(0, 6, 0, 3);
        return t;
    }

    public static CheckBox Check(UiKit ui, string text, bool value, Action<bool> changed)
    {
        var box = new CheckBox { Content = text, IsChecked = value, Foreground = ui.Theme.Text, Margin = new Thickness(0, 4, 0, 6) };
        box.Click += (_, _) => changed(box.IsChecked == true);
        return box;
    }

    /// <summary>A text box that reads numbers typed with Arabic-Indic digits too.</summary>
    public static bool TryNumber(string text, out double value) => OptionForm.TryParseNumber(text, out value);
}
