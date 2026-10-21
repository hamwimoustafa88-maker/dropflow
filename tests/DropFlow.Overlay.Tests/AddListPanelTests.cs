using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DropFlow.Overlay;

namespace DropFlow.Overlay.Tests;

public class AddListPanelTests
{
    private static readonly WheelItemGroup[] Groups =
    [
        new("Convert to", []),                                                       // empty groups get no header
        new("Tools", [new("img.ocr", "Extract text (OCR)"), new("img.resize", "Resize images", NeedsOptions: true)]),
        new("Actions", [new("act.copy-path", "Copy Path")]),
    ];

    private static AddListPanel Laid(AddListPanel panel)
    {
        panel.Measure(new Size(panel.Width, panel.Height));
        panel.Arrange(new Rect(0, 0, panel.Width, panel.Height));
        panel.UpdateLayout();
        return panel;
    }

    [Fact]
    public void Rows_are_grouped_under_headers_and_the_first_row_starts_highlighted()
    {
        var (headers, entries, selected, title) = Sta.Run(() =>
        {
            var p = Laid(new AddListPanel());
            p.SetContent("Add to the Convert wheel", "hint", "Back", Groups, rightToLeft: false);
            return (p.Headers.ToList(), p.Entries.Select(e => e.Id).ToList(), p.SelectedEntry, p.Title);
        });

        Assert.Equal(["Tools", "Actions"], headers);
        Assert.Equal(["img.ocr", "img.resize", "act.copy-path"], entries);
        Assert.Equal(0, selected);
        Assert.Equal("Add to the Convert wheel", title);
    }

    [Fact]
    public void Enter_picks_the_highlighted_row_and_escape_goes_back()
    {
        var (picked, back) = Sta.Run(() =>
        {
            var p = new AddListPanel();
            var host = new Window { Content = p, SizeToContent = SizeToContent.WidthAndHeight, ShowActivated = false, WindowStyle = WindowStyle.None, Left = -10000, Top = -10000, ShowInTaskbar = false };
            host.Show();
            p.SetContent("t", "h", "Back", Groups, false);
            var ids = new List<string>();
            var backs = 0;
            p.Picked += (_, id) => ids.Add(id);
            p.Back += (_, _) => backs++;

            p.Select(2);
            Press(p, Key.Enter);
            Press(p, Key.Escape);
            p.Pick(99);                       // ignored
            host.Close();
            return (ids, backs);
        });

        Assert.Equal(["act.copy-path"], picked);
        Assert.Equal(1, back);
    }

    [Fact]
    public void Group_headers_cannot_be_selected_or_focused()
    {
        var focusable = Sta.Run(() =>
        {
            var p = Laid(new AddListPanel());
            p.SetContent("t", "h", "Back", Groups, false);
            var list = Find<ListBox>(p);
            return list.Items.OfType<ListBoxItem>().Where(i => i.Tag is null).Select(i => i.Focusable || i.IsHitTestVisible).ToList();
        });
        Assert.Equal([false, false], focusable);
    }

    [Theory]
    [InlineData(false, "en")]
    [InlineData(true, "ar")]
    public void The_panel_renders_in_both_directions_inside_the_wheel_window(bool rtl, string language)
    {
        var (direction, size, png) = Sta.Run(() =>
        {
            var p = new AddListPanel { Theme = WheelTheme.Light };
            var groups = rtl
                ? new WheelItemGroup[] { new("تحويل إلى", [new("convert:pdf", "PDF")]), new("أدوات", [new("img.ocr", "استخراج النص (OCR)")]) }
                : Groups;
            p.SetContent(rtl ? "إضافة إلى عجلة التحويل" : "Add to the Convert wheel", rtl ? "اختر عنصرًا" : "Pick one", rtl ? "رجوع" : "Back", groups, rtl);
            Laid(p);

            var bmp = new RenderTargetBitmap((int)p.Width, (int)p.Height, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(p);
            var path = Path.Combine(Path.GetTempPath(), "dropflow-visual", $"wheel-add-list-{language}.png");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using (var f = File.Create(path)) enc.Save(f);
            return (p.FlowDirection, p.Width, path);
        });

        Assert.Equal(rtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight, direction);
        Assert.True(size <= WheelWindow.SizeDips, "the list fits in the wheel window");
        Assert.True(new FileInfo(png).Length > 2_000);
    }

    private static void Press(UIElement target, Key key)
    {
        var source = PresentationSource.FromVisual(target) ?? throw new InvalidOperationException("Host the panel in a shown window first.");
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key)
        {
            RoutedEvent = UIElement.PreviewKeyDownEvent,
        };
        target.RaiseEvent(args);
    }

    private static T Find<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            if (TryFind<T>(child) is { } nested) return nested;
        }
        throw new InvalidOperationException(typeof(T).Name + " not found");
    }

    private static T? TryFind<T>(DependencyObject root) where T : DependencyObject
    {
        try { return Find<T>(root); } catch (InvalidOperationException) { return null; }
    }
}
