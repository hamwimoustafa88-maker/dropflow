// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DropFlow.App.Services;
using DropFlow.Engines.Images;
using ImageMagick;
using ImageMetadata = DropFlow.Engines.Images.ImageMetadata;
using TextAlignment = System.Windows.TextAlignment;

namespace DropFlow.App.Views.Editors;

/// <summary>One row the metadata editor shows: the field as read, plus what the user did to it.</summary>
internal sealed record MetadataRow(MetadataField Field, string Value, bool Edited, bool Removed, bool Added);

/// <summary>
/// The non-visual side of the metadata editor: the fields read from the file, the user's edits, removals and additions, a search
/// filter, and the list of <see cref="MetadataChange"/> the converter applies. Pure, so it is unit-tested.
/// </summary>
internal sealed class MetadataEditModel(IReadOnlyList<MetadataField> fields)
{
    private readonly Dictionary<(MetadataGroup, string), string?> _changes = [];
    private readonly List<MetadataField> _added = [];
    private readonly HashSet<MetadataGroup> _removedGroups = [];

    public static readonly MetadataGroup[] Groups = [MetadataGroup.File, MetadataGroup.Exif, MetadataGroup.Gps, MetadataGroup.Iptc, MetadataGroup.Xmp];

    public bool HasChanges => _changes.Count > 0 || _removedGroups.Count > 0;

    public bool IsGroupRemoved(MetadataGroup group) => _removedGroups.Contains(group);

    public void Edit(MetadataGroup group, string key, string value) => _changes[(group, key)] = value;

    public void Remove(MetadataGroup group, string key)
    {
        if (_added.RemoveAll(f => f.Group == group && f.Key == key) > 0) { _changes.Remove((group, key)); return; }
        _changes[(group, key)] = null;
    }

    /// <summary>Undoes the edit or removal of one field.</summary>
    public void Restore(MetadataGroup group, string key) => _changes.Remove((group, key));

    public void RemoveGroup(MetadataGroup group)
    {
        if (group == MetadataGroup.File) return;
        _removedGroups.Add(group);
        if (group == MetadataGroup.Exif) _removedGroups.Add(MetadataGroup.Gps);   // the EXIF block carries the GPS tags
        foreach (var key in _changes.Keys.Where(k => _removedGroups.Contains(k.Item1)).ToList()) _changes.Remove(key);
        _added.RemoveAll(f => _removedGroups.Contains(f.Group));
    }

    public void RemoveAll()
    {
        foreach (var g in Groups.Skip(1)) RemoveGroup(g);
    }

    public void RestoreGroup(MetadataGroup group) => _removedGroups.Remove(group);

    /// <summary>Adds a field (or edits it when it already exists). Returns false for an empty value.</summary>
    public bool Add(MetadataGroup group, string key, string value)
    {
        if (string.IsNullOrWhiteSpace(value) || group == MetadataGroup.File) return false;
        _removedGroups.Remove(group);
        if (!fields.Any(f => f.Group == group && f.Key == key) && !_added.Any(f => f.Group == group && f.Key == key))
            _added.Add(new MetadataField(group, key, key, value, Editable: true, Removable: true));
        _changes[(group, key)] = value;
        return true;
    }

    /// <summary>The rows of one group that match <paramref name="filter"/> (name, key or value, case-insensitive).</summary>
    public IReadOnlyList<MetadataRow> Rows(MetadataGroup group, string? filter = null) =>
        [.. fields.Where(f => f.Group == group).Concat(_added.Where(f => f.Group == group))
            .Select(f =>
            {
                var removedGroup = _removedGroups.Contains(group);
                var changed = _changes.TryGetValue((f.Group, f.Key), out var v);
                var added = _added.Contains(f);
                return new MetadataRow(f, changed && v is not null ? v : f.Value, changed && v is not null && !added, removedGroup || (changed && v is null), added);
            })
            .Where(r => Matches(r, filter))];

    private static bool Matches(MetadataRow row, string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter)) return true;
        var f = filter.Trim();
        return row.Field.Name.Contains(f, StringComparison.OrdinalIgnoreCase) || row.Field.Key.Contains(f, StringComparison.OrdinalIgnoreCase)
            || row.Value.Contains(f, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>What the converter applies: whole-group removals first, then single fields.</summary>
    public IReadOnlyList<MetadataChange> Changes()
    {
        var list = new List<MetadataChange>();
        foreach (var g in Groups.Where(_removedGroups.Contains)) list.Add(new MetadataChange(g, "*", null));
        foreach (var ((group, key), value) in _changes)
        {
            if (_removedGroups.Contains(group)) continue;
            var original = fields.FirstOrDefault(f => f.Group == group && f.Key == key);
            if (original is not null && value is not null && value == original.Value) continue;   // typed back to what it was
            list.Add(new MetadataChange(group, key, value));
        }
        return list;
    }
}

/// <summary>
/// Metadata (img.metadata): every field grouped as File, EXIF, GPS, IPTC and XMP, with search, text editing, removal of one field or
/// a whole group, "remove everything" and adding common fields. JPEG edits are written without re-encoding the picture.
/// </summary>
internal sealed class MetadataWindow : VisualEditorWindow
{
    private readonly StackPanel _list = new();
    private readonly TextBox _search = new() { MinHeight = 30, Padding = new Thickness(6, 4, 6, 4) };
    private readonly ComboBox _addGroup = new() { MinHeight = 28 };
    private readonly ComboBox _addKey = new() { MinHeight = 28, IsEditable = true };
    private readonly TextBox _addValue = new() { MinHeight = 28, Padding = new Thickness(6, 3, 6, 3) };
    private MetadataEditModel? _model;

    public MetadataWindow(UiKit ui, IReadOnlyList<string> files) : base(ui, ui.Loc["editor.meta.title"], files)
    {
        Side.Children.Add(EditorControls.Label(ui, ui.Loc["editor.meta.search"]));
        _search.TextChanged += (_, _) => Rebuild();
        Side.Children.Add(_search);

        var removeAll = ui.Secondary(ui.Loc["editor.meta.removeAll"]);
        removeAll.HorizontalAlignment = HorizontalAlignment.Left;
        removeAll.Margin = new Thickness(0, 8, 0, 8);
        removeAll.Click += (_, _) => { _model?.RemoveAll(); Rebuild(); };
        Side.Children.Add(removeAll);

        _list.Margin = new Thickness(0, 4, 0, 8);
        Side.Children.Add(_list);

        // add a field
        var add = new StackPanel();
        foreach (var g in new[] { MetadataGroup.Xmp, MetadataGroup.Iptc, MetadataGroup.Exif })
            _addGroup.Items.Add(new ComboBoxItem { Content = ui.Loc["editor.meta.group." + g.ToString().ToLowerInvariant()], Tag = g });
        _addGroup.SelectionChanged += (_, _) => FillKeys();
        _addGroup.SelectedIndex = 0;
        add.Children.Add(_addGroup);
        _addKey.Margin = new Thickness(0, 6, 0, 0);
        _addKey.FlowDirection = FlowDirection.LeftToRight;
        add.Children.Add(_addKey);
        _addValue.Margin = new Thickness(0, 6, 0, 0);
        add.Children.Add(_addValue);
        var addButton = ui.Secondary(ui.Loc["editor.meta.add"]);
        addButton.HorizontalAlignment = HorizontalAlignment.Left;
        addButton.Margin = new Thickness(0, 6, 0, 0);
        addButton.Click += (_, _) => AddField();
        add.Children.Add(addButton);
        add.Children.Add(ui.Text(ui.Loc["editor.meta.arabicTip"], 11, muted: true));
        Side.Children.Add(EditorControls.Group(ui, ui.Loc["editor.meta.addTitle"], add, expanded: false));

        Loaded += async (_, _) => await LoadAsync();
    }

    public override bool IsReady => _model is not null && _thumbnailDone;

    private bool _thumbnailDone;

    internal MetadataEditModel? Model => _model;

    private void FillKeys()
    {
        _addKey.Items.Clear();
        if (_addGroup.SelectedItem is ComboBoxItem { Tag: MetadataGroup g } && ImageMetadata.AddableKeys.TryGetValue(g, out var keys))
            foreach (var k in keys) _addKey.Items.Add(k);
        if (_addKey.Items.Count > 0) _addKey.SelectedIndex = 0;
    }

    private async Task LoadAsync()
    {
        var path = Files[0];
        try
        {
            var fields = await Task.Run(() => ImageMetadata.Read(path));
            _model = new MetadataEditModel(fields);
            Rebuild();
        }
        catch (Exception ex) when (ex is InvalidDataException or MagickException or IOException or UnauthorizedAccessException)
        {
            FileLog.Error("metadata editor", ex);
            Message.Text = Ui.Loc["editor.meta.readFailed"];
        }

        try
        {
            using var source = await Task.Run(() => PreviewImage.Load(path, 900));
            Preview.SetImage(PreviewImage.ToBitmap(source.Image));
        }
        catch (Exception ex) when (ex is MagickException or IOException or UnauthorizedAccessException)
        {
            FileLog.Error("metadata editor preview", ex);
        }
        _thumbnailDone = true;
    }

    /// <summary>
    /// Values keep their own direction: an Arabic caption reads right to left, a size, date or camera model left to right, and both
    /// sit against the side the labels start from.
    /// </summary>
    private void Direct(FrameworkElement element, string value, Action<TextAlignment> align)
    {
        element.FlowDirection = AnnotationDocument.IsRightToLeft(value) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        var start = Ui.Loc.IsRightToLeft ? TextAlignment.Right : TextAlignment.Left;
        // with right-to-left flow, "Left" is mirrored to the right edge
        align(element.FlowDirection == FlowDirection.RightToLeft ? (start == TextAlignment.Right ? TextAlignment.Left : TextAlignment.Right) : start);
    }

    private void AddField()
    {
        if (_model is null || _addGroup.SelectedItem is not ComboBoxItem { Tag: MetadataGroup g }) return;
        var key = (_addKey.Text ?? "").Trim();
        if (key.Length == 0 || !_model.Add(g, key, _addValue.Text)) return;
        _addValue.Text = "";
        Rebuild();
    }

    private string FieldName(MetadataField f)
    {
        var key = $"editor.meta.field.{f.Group.ToString().ToLowerInvariant()}.{f.Key}";
        var text = Ui.Loc[key];
        return text == key ? f.Name : text;
    }

    private void Rebuild()
    {
        _list.Children.Clear();
        if (_model is null) return;
        var filter = _search.Text;
        var any = false;
        foreach (var group in MetadataEditModel.Groups)
        {
            var rows = _model.Rows(group, filter);
            if (rows.Count == 0 && !_model.IsGroupRemoved(group)) continue;
            any = true;

            var header = new DockPanel { Margin = new Thickness(0, 10, 0, 4) };
            var title = Ui.Text(Ui.Loc["editor.meta.group." + group.ToString().ToLowerInvariant()], 13, weight: FontWeights.SemiBold, wrap: false);
            if (group != MetadataGroup.File)
            {
                var removed = _model.IsGroupRemoved(group);
                var button = new Button
                {
                    Content = Ui.Loc[removed ? "editor.meta.restore" : "editor.meta.removeGroup"], Padding = new Thickness(8, 1, 8, 1), FontSize = 11,
                    Background = Brushes.Transparent, Foreground = Ui.Theme.Accent, BorderThickness = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand,
                };
                button.Click += (_, _) => { if (removed) _model.RestoreGroup(group); else _model.RemoveGroup(group); Rebuild(); };
                DockPanel.SetDock(button, Dock.Right);
                header.Children.Add(button);
            }
            header.Children.Add(title);
            _list.Children.Add(header);
            foreach (var row in rows) _list.Children.Add(Row(row));
        }
        if (!any) _list.Children.Add(Ui.Text(Ui.Loc["editor.meta.nothing"], 12, muted: true));
    }

    private UIElement Row(MetadataRow row)
    {
        var f = row.Field;
        var panel = new StackPanel { Margin = new Thickness(0, 2, 0, 6) };
        var head = new DockPanel();
        var name = Ui.Text(FieldName(f), 11, muted: true, wrap: false);
        name.ToolTip = f.Key;
        if (row.Removed) name.TextDecorations = TextDecorations.Strikethrough;
        if (f.Removable && !_model!.IsGroupRemoved(f.Group))
        {
            var remove = new Button
            {
                Content = row.Removed ? Ui.Loc["editor.meta.restore"] : "✕", Padding = new Thickness(6, 0, 6, 0), FontSize = 11, MinWidth = 22,
                Background = Brushes.Transparent, Foreground = Ui.Theme.TextMuted, BorderThickness = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = Ui.Loc["editor.meta.removeField"],
            };
            remove.Click += (_, _) => { if (row.Removed) _model.Restore(f.Group, f.Key); else _model.Remove(f.Group, f.Key); Rebuild(); };
            DockPanel.SetDock(remove, Dock.Right);
            head.Children.Add(remove);
        }
        head.Children.Add(name);
        panel.Children.Add(head);

        if (f.Editable && !row.Removed)
        {
            var box = new TextBox { Text = row.Value, MinHeight = 26, Padding = new Thickness(4, 2, 4, 2), TextWrapping = TextWrapping.Wrap };
            if (row.Edited || row.Added) box.BorderBrush = Ui.Theme.Accent;
            Direct(box, row.Value, a => box.TextAlignment = a);
            box.TextChanged += (_, _) => Direct(box, box.Text, a => box.TextAlignment = a);
            box.LostFocus += (_, _) => { if (box.Text != row.Value) { _model!.Edit(f.Group, f.Key, box.Text); Rebuild(); } };
            panel.Children.Add(box);
        }
        else
        {
            var value = Ui.Text(row.Value, 12, wrap: true);
            value.Opacity = row.Removed ? 0.45 : 1;
            Direct(value, row.Value, a => value.TextAlignment = a);
            if (row.Removed) value.TextDecorations = TextDecorations.Strikethrough;
            panel.Children.Add(value);
        }
        return panel;
    }

    protected override IReadOnlyDictionary<string, object?>? BuildOptions()
    {
        if (_model is null) return null;
        // a field still being typed in counts
        if (System.Windows.Input.Keyboard.FocusedElement is TextBox focused && _list.IsAncestorOf(focused))
            System.Windows.Input.Keyboard.ClearFocus();
        var changes = _model.Changes();
        if (changes.Count == 0)
        {
            Message.Text = Ui.Loc["editor.nothingChanged"];
            return null;
        }
        return new Dictionary<string, object?> { ["mode"] = "edit", ["changes"] = ImageMetadata.ToJson(changes) };
    }
}
