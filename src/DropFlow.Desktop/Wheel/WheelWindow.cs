// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using DropFlow.Desktop.App;
using DropFlow.Desktop.Native;
using DropFlow.Desktop.Views;
using DropFlow.Overlay;

namespace DropFlow.Desktop.Wheel;

/// <summary>
/// The transparent, topmost, borderless window that carries the wheel. It never takes focus in drag mode (the drag source keeps it)
/// and is a drop target: the drag entering it hands DropFlow the files. On macOS the NSWindow is lifted to the pop-up-menu level, joins
/// every Space and full-screen app, and is ordered front without activating DropFlow. On X11 it is override-redirect so the window
/// manager maps it immediately even while a drag holds the pointer.
/// </summary>
public sealed class WheelWindow : Window
{
    public const double SizeDip = (WheelView.Outer + 16) * 2 + 24;

    private readonly Grid _root;
    private IReadOnlyList<string> _files = [];
    private IntPtr _nsWindow;
    private bool _prepared;

    public WheelWindow()
    {
        WindowDecorations = WindowDecorations.None;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        CanResize = false;
        Width = Height = SizeDip;
        Title = "DropFlow wheel";
        FontFamily = Palette.Font;

        Wheel = new WheelView { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        AddList = new AddListPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsVisible = false };

        // An almost invisible but hit-testable background: a fully transparent window receives no drops on some systems.
        _root = new Grid { Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)) };
        _root.Children.Add(Wheel);
        _root.Children.Add(AddList);
        Content = _root;
        DragDrop.SetAllowDrop(_root, true);
        DragDrop.AddDragEnterHandler(_root, OnDragEnter);
        DragDrop.AddDragOverHandler(_root, OnDragOver);
        DragDrop.AddDragLeaveHandler(_root, (_, _) => Wheel.ClearHover());
        DragDrop.AddDropHandler(_root, OnDrop);

        Wheel.Cancelled += (_, _) => DropCancelled?.Invoke(this, EventArgs.Empty);
        Wheel.ItemInvoked += (_, e) => ItemClicked?.Invoke(this, e.Index);
        Wheel.ItemContextRequested += (_, e) => ItemContextRequested?.Invoke(this, e.Index);
        AddList.Picked += (_, id) => AddItemPicked?.Invoke(this, id);
        AddList.Back += (_, _) => AddListBack?.Invoke(this, EventArgs.Empty);
        Deactivated += (_, _) =>
        {
            if (IsInteractive && IsWheelVisible) DropCancelled?.Invoke(this, EventArgs.Empty);
        };
        // A click on the transparent corners (outside the wheel and the "+ Add" card) closes a click-mode wheel; this also works where
        // the window never gets focus (override-redirect on X11), so it does not depend on Deactivated.
        _root.PointerPressed += (_, e) =>
        {
            if (!IsInteractive || !IsWheelVisible || e.Source != _root) return;
            var p = e.GetPosition(Wheel);
            var r = Math.Sqrt(Math.Pow(p.X - Wheel.Bounds.Width / 2, 2) + Math.Pow(p.Y - Wheel.Bounds.Height / 2, 2));
            if (AddList.IsVisible || r > WheelView.Outer + 12) DropCancelled?.Invoke(this, EventArgs.Empty);
        };
    }

    public WheelView Wheel { get; }
    public AddListPanel AddList { get; }
    public bool IsWheelVisible { get; private set; }
    public bool IsInteractive { get; private set; }
    public bool IsAddListVisible => AddList.IsVisible;
    public IReadOnlyList<string> Files => _files;

    public event EventHandler<IReadOnlyList<string>>? FilesEntered;
    public event EventHandler<(IReadOnlyList<string> Files, int Index)>? ItemDropped;
    public event EventHandler<int>? ItemClicked;
    public event EventHandler? DropCancelled;
    public event EventHandler<int>? ItemContextRequested;
    public event EventHandler<string>? AddItemPicked;
    public event EventHandler? AddListBack;

    /// <summary>Platform tweaks that must happen once, before the first show.</summary>
    public void Prepare()
    {
        if (_prepared) return;
        _prepared = true;
        var handle = TryGetPlatformHandle();
        if (OperatingSystem.IsMacOS())
        {
            _nsWindow = MacWindow.FromHandle(handle?.Handle ?? IntPtr.Zero, handle?.HandleDescriptor);
            MacWindow.MakeOverlay(_nsWindow);
        }
        else if (OperatingSystem.IsLinux() && UseOverrideRedirect && handle?.HandleDescriptor == "XID")
        {
            X11.SetOverrideRedirect(handle.Handle);
        }
    }

    /// <summary>
    /// X11 only: map the window outside the window manager's control. Wanted where drags are watched (the window must appear under a
    /// drag at once); not wanted where only click mode exists, because an override-redirect window gets no keyboard focus from the WM.
    /// </summary>
    public bool UseOverrideRedirect { get; set; } = true;

    /// <summary>
    /// Shows the wheel centred on the pointer. (x, y) are in the drag watcher's coordinates: Cocoa points on macOS, X11 root pixels on
    /// Linux. <paramref name="interactive"/> (click mode: file-manager entries, the command line) lets the window take focus so the
    /// keyboard works, and closes it when focus goes elsewhere.
    /// </summary>
    public void ShowAt(int x, int y, bool interactive)
    {
        Prepare();
        _files = [];
        AddList.IsVisible = false;
        Wheel.IsVisible = true;
        Wheel.ClearHover();
        Wheel.Palette = Palette.Current;
        IsInteractive = interactive;
        ShowActivated = interactive;

        if (OperatingSystem.IsMacOS())
        {
            Show();
            var (ox, oy) = WheelPlacement.CocoaOrigin(x, y, SizeDip);
            MacWindow.ShowAt(_nsWindow, ox, oy);
        }
        else
        {
            var point = new PixelPoint(x, y);
            var screen = Screens.ScreenFromPoint(point) ?? Screens.Primary;
            var scaling = screen?.Scaling ?? 1;
            var size = (int)Math.Ceiling(SizeDip * scaling);
            var (left, top) = screen is null ? (x - size / 2, y - size / 2) : WheelPlacement.TopLeft(x, y, size, screen.WorkingArea);
            Position = new PixelPoint(left, top);
            Show();
        }

        IsWheelVisible = true;
        if (interactive)
        {
            Activate();
            Wheel.Focus();
        }
    }

    public void SetFiles(IReadOnlyList<string> files) => _files = files;

    public void HideWheel()
    {
        if (!IsWheelVisible) return;
        Hide();
        IsWheelVisible = false;
        IsInteractive = false;
        AddList.IsVisible = false;
        Wheel.IsVisible = true;
        Wheel.SetProgress(null);
        Wheel.ClearHover();
    }

    public void ShowAddList(string title, string hint, string back, IReadOnlyList<WheelItemGroup> groups, bool rightToLeft)
    {
        AddList.SetContent(title, hint, back, groups, rightToLeft, Wheel.Palette);
        Wheel.IsVisible = false;
        AddList.IsVisible = true;
        IsInteractive = true;
        Activate();
        AddList.FocusList();
    }

    public void HideAddList()
    {
        if (!AddList.IsVisible) return;
        AddList.IsVisible = false;
        Wheel.IsVisible = true;
        Wheel.Focus();
    }

    /// <summary>A one-item menu at the pointer ("Remove from wheel" on a pinned wedge).</summary>
    public void ShowItemMenu(string label, bool rightToLeft, Action run)
    {
        var item = new MenuItem { Header = label };
        item.Click += (_, _) => run();
        var menu = new ContextMenu { FlowDirection = rightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight, Items = { item } };
        menu.Open(Wheel);
    }

    // ------------------------------------------------------------------------------------------------------------------- drops

    private static IReadOnlyList<string> PathsOf(DragEventArgs e) =>
        (e.DataTransfer.TryGetFiles() ?? [])
        .Select(f => f.TryGetLocalPath())
        .Where(p => !string.IsNullOrEmpty(p) && (File.Exists(p) || Directory.Exists(p)))
        .Select(p => p!)
        .ToList();

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        if (IsAddListVisible || !e.DataTransfer.Contains(DataFormat.File))
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }
        var files = PathsOf(e);
        if (!files.SequenceEqual(_files))
        {
            _files = files;
            FilesEntered?.Invoke(this, files);
        }
        e.DragEffects = DropEffectPolicy.Choose(e.DragEffects);
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        var index = IsAddListVisible ? -1 : Wheel.SetPointer(e.GetPosition(Wheel));
        e.DragEffects = index >= 0 && e.DataTransfer.Contains(DataFormat.File) ? DropEffectPolicy.Choose(e.DragEffects) : DragDropEffects.None;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (IsAddListVisible) return;
        var index = Wheel.WedgeAt(e.GetPosition(Wheel));
        var files = _files.Count > 0 ? _files : PathsOf(e);
        // Copy (or the Move Finder forces while ⌘ is held): DropFlow only reads the files; the source is never moved or deleted.
        e.DragEffects = DropEffectPolicy.Choose(e.DragEffects);
        if (index < 0 || files.Count == 0)
        {
            DropCancelled?.Invoke(this, EventArgs.Empty);
            return;
        }
        ItemDropped?.Invoke(this, (files, index));
    }
}
