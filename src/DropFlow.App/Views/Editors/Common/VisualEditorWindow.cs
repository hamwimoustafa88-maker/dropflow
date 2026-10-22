// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace DropFlow.App.Views.Editors;

/// <summary>
/// The shared frame of every visual editor: a large preview, a side panel of controls (on the left in Arabic, on the right in English),
/// and Cancel / Save at the bottom. The editor never writes a file itself: <see cref="Result"/> completes with the converter options
/// (the runner then queues the normal job, with history and undo) or with null when the window is closed.
/// </summary>
internal abstract class VisualEditorWindow : Window
{
    private readonly TaskCompletionSource<IReadOnlyDictionary<string, object?>?> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Grid _previewArea = new();

    protected VisualEditorWindow(UiKit ui, string title, IReadOnlyList<string> files, string saveKey = "editor.save")
    {
        Ui = ui;
        Files = files;
        ui.Style(this, 1120, title);
        SizeToContent = SizeToContent.Manual;
        Height = 760;
        MinWidth = 820;
        MinHeight = 520;
        ResizeMode = ResizeMode.CanResize;

        Preview = new PreviewHost(ui);
        Toolbar = new WrapPanel { Margin = new Thickness(12, 10, 12, 0), Visibility = Visibility.Collapsed };
        _previewArea.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _previewArea.RowDefinitions.Add(new RowDefinition());
        _previewArea.Children.Add(Toolbar);
        Grid.SetRow(Preview, 1);
        _previewArea.Children.Add(Preview);

        Side = new StackPanel { Margin = new Thickness(18, 14, 18, 14) };
        var side = new Border
        {
            Background = ui.Theme.Panel, BorderBrush = ui.Theme.PanelBorder, BorderThickness = new Thickness(1, 0, 0, 0),
            Child = new ScrollViewer { Content = Side, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled },
        };

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition());
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(360) });
        body.Children.Add(_previewArea);
        Grid.SetColumn(side, 1);
        body.Children.Add(side);

        Message = ui.Text("", 12, wrap: true);
        Message.Foreground = System.Windows.Media.Brushes.IndianRed;
        Message.Margin = new Thickness(0, 0, 12, 0);
        Message.VerticalAlignment = VerticalAlignment.Center;

        var source = files.Count switch
        {
            0 => "",
            1 => Path.GetFileName(files[0]),
            _ => ui.Loc.Format("editor.appliesTo", files.Count),
        };
        var name = ui.Text(source, 12, muted: true, wrap: false);
        name.VerticalAlignment = VerticalAlignment.Center;
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        name.Margin = new Thickness(0, 0, 12, 0);

        SaveButton = ui.Primary(ui.Loc[saveKey]);
        SaveButton.IsDefault = false;
        var cancel = ui.Secondary(ui.Loc["dialog.cancel"]);
        cancel.Margin = new Thickness(0, 0, 8, 0);
        cancel.Click += (_, _) => Close();
        SaveButton.Click += (_, _) => Save();

        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(cancel);
        buttons.Children.Add(SaveButton);
        DockPanel.SetDock(buttons, Dock.Right);
        var footer = new DockPanel { Margin = new Thickness(18, 10, 18, 12), LastChildFill = true };
        footer.Children.Add(buttons);
        footer.Children.Add(name);
        footer.Children.Add(Message);
        var footerBorder = new Border { BorderBrush = ui.Theme.PanelBorder, BorderThickness = new Thickness(0, 1, 0, 0), Child = footer, Background = ui.Theme.Hub };

        var root = new DockPanel();
        DockPanel.SetDock(footerBorder, Dock.Bottom);
        root.Children.Add(footerBorder);
        root.Children.Add(body);
        Content = root;

        PreviewKeyDown += OnKey;
        Closed += (_, _) =>
        {
            OnClosedCleanup();
            _result.TrySetResult(null);
        };
    }

    protected UiKit Ui { get; }
    protected IReadOnlyList<string> Files { get; }
    protected PreviewHost Preview { get; }

    /// <summary>The side panel (controls).</summary>
    protected StackPanel Side { get; }

    /// <summary>Tool buttons above the preview (hidden until something is added).</summary>
    protected WrapPanel Toolbar { get; }

    protected TextBlock Message { get; }
    protected Button SaveButton { get; }

    /// <summary>Completes with the options to run, or null when the editor was closed without saving.</summary>
    public Task<IReadOnlyDictionary<string, object?>?> Result => _result.Task;

    /// <summary>True once the first preview is on screen (snapshots and tests wait for it).</summary>
    public virtual bool IsReady => true;

    /// <summary>What Save would hand to the converter right now (null, with <see cref="Message"/> set, when something is invalid).</summary>
    internal IReadOnlyDictionary<string, object?>? CurrentOptions => BuildOptions();

    internal string MessageText => Message.Text;

    protected abstract IReadOnlyDictionary<string, object?>? BuildOptions();

    protected virtual void OnUndo() { }

    protected virtual void OnDeleteKey() { }

    protected virtual void OnClosedCleanup() { }

    /// <summary>Shows the toolbar row and adds a control to it.</summary>
    protected void AddTool(UIElement element)
    {
        Toolbar.Visibility = Visibility.Visible;
        Toolbar.Children.Add(element);
    }

    private void Save()
    {
        Message.Text = "";
        var options = BuildOptions();
        if (options is null) return;
        _result.TrySetResult(options);
        Close();
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        var typing = Keyboard.FocusedElement is TextBox;
        if (e.Key == Key.Escape) { Close(); e.Handled = true; }
        else if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control) { Save(); e.Handled = true; }
        else if (e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control && !typing) { OnUndo(); e.Handled = true; }
        else if (e.Key == Key.Delete && !typing) { OnDeleteKey(); e.Handled = true; }
    }
}
