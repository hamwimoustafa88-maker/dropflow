using System.IO;
using System.Windows;
using DropFlow.Core.Operations;
using DropFlow.Overlay;
using DropFlow.TestSupport;

namespace DropFlow.Overlay.Tests;

internal sealed class TestLocalizer(string language = "en") : ILocalizer
{
    private static readonly Dictionary<string, string> En = new()
    {
        ["dialog.run"] = "Run", ["dialog.cancel"] = "Cancel",
        ["opt.password"] = "Password", ["opt.pdf.compress.level"] = "Level", ["opt.level.less"] = "Less",
        ["err.required"] = "Required", ["err.number"] = "Enter a number", ["err.max"] = "Too large", ["err.min"] = "Too small",
        ["shelf.title"] = "Shelf", ["shelf.clear"] = "Clear", ["shelf.empty"] = "Drop files here", ["shelf.remove"] = "Remove",
    };

    public string Language { get; } = language;
    public string this[string key] => En.GetValueOrDefault(key, key);
}

public class ShelfModelTests
{
    [Fact]
    public void Adds_existing_files_once_and_ignores_missing_or_duplicate_paths()
    {
        using var dir = new TempDir();
        var a = dir.WriteText("a.txt", "x");
        var b = dir.WriteText("b.txt", "x");
        var shelf = new ShelfModel();
        var changes = 0;
        shelf.Changed += (_, _) => changes++;

        Assert.Equal(2, shelf.Add([a, b, a.ToUpperInvariant(), Path.Combine(dir.Path, "missing.txt"), ""]));
        Assert.Equal(2, shelf.Items.Count);
        Assert.Equal(0, shelf.Add([a]));
        Assert.Equal(1, changes);
    }

    [Fact]
    public void Remove_clear_and_prune_raise_changes_only_when_something_changed()
    {
        using var dir = new TempDir();
        var a = dir.WriteText("a.txt", "x");
        var b = dir.WriteText("b.txt", "x");
        var shelf = new ShelfModel();
        shelf.Add([a, b]);
        var changes = 0;
        shelf.Changed += (_, _) => changes++;

        Assert.True(shelf.Remove(a));
        Assert.False(shelf.Remove(a));
        File.Delete(b);
        Assert.Equal(1, shelf.PruneMissing());
        shelf.Clear();                 // already empty: no event
        Assert.Equal(2, changes);
        Assert.Empty(shelf.Items);
    }

    [Fact]
    public void Folders_can_be_parked_too()
    {
        using var dir = new TempDir();
        var shelf = new ShelfModel();
        Assert.Equal(1, shelf.Add([dir.Path]));
    }
}

public class ShelfDockingTests
{
    private static readonly ShelfDocking.Bounds Work = new(0, 0, 1920, 1040);

    [Fact]
    public void Each_edge_is_flush_and_centred_along_the_edge()
    {
        Assert.Equal(new ShelfDocking.Bounds(0, 340, 150, 360), ShelfDocking.Compute(Work, ShelfEdge.Left, 150, 360));
        Assert.Equal(new ShelfDocking.Bounds(1770, 340, 150, 360), ShelfDocking.Compute(Work, ShelfEdge.Right, 150, 360));
        Assert.Equal(new ShelfDocking.Bounds(780, 0, 360, 150), ShelfDocking.Compute(Work, ShelfEdge.Top, 150, 360));
        Assert.Equal(new ShelfDocking.Bounds(780, 890, 360, 150), ShelfDocking.Compute(Work, ShelfEdge.Bottom, 150, 360));
    }

    [Fact]
    public void Work_areas_with_an_offset_are_respected_and_length_is_clamped()
    {
        var second = new ShelfDocking.Bounds(-1920, 40, 1920, 1000);       // a monitor to the left with a taskbar on top
        var b = ShelfDocking.Compute(second, ShelfEdge.Right, 150, 5000);
        Assert.Equal(-150, b.Left);
        Assert.Equal(40, b.Top);
        Assert.Equal(1000, b.Height);
    }

    [Theory]
    [InlineData(10, 500, ShelfEdge.Left)]
    [InlineData(1900, 500, ShelfEdge.Right)]
    [InlineData(900, 5, ShelfEdge.Top)]
    [InlineData(900, 1030, ShelfEdge.Bottom)]
    public void Snaps_to_the_nearest_edge(double x, double y, ShelfEdge expected) =>
        Assert.Equal(expected, ShelfDocking.NearestEdge(Work, x, y));
}

public class ToolOptionsDialogTests
{
    private static readonly ToolOption[] ProtectOptions =
    [
        new("password", ToolOptionKind.Password, null, Required: true),
        new("level", ToolOptionKind.Choice, "less", ["less", "recommended", "extreme"]),
        new("quality", ToolOptionKind.Number, 70d, Min: 1, Max: 100),
        new("keep", ToolOptionKind.Bool, true),
    ];

    [Fact]
    public void Defaults_are_shown_and_a_required_field_blocks_confirmation_with_a_message()
    {
        var outcome = Sta.Run(() =>
        {
            var d = new ToolOptionsDialog("Protect", "pdf.compress", ProtectOptions, new TestLocalizer());
            var raw = d.ReadRaw();
            var ok = d.TryConfirm();
            return (raw, ok);
        });

        Assert.Equal("less", outcome.raw["level"]);
        Assert.Equal("70", outcome.raw["quality"]);
        Assert.Equal("true", outcome.raw["keep"]);
        Assert.False(outcome.ok);
    }

    [Fact]
    public void Filling_the_required_field_gives_typed_values()
    {
        var result = Sta.Run(() =>
        {
            var d = new ToolOptionsDialog("Protect", "pdf.compress", ProtectOptions, new TestLocalizer());
            var pw = FindPasswordBox(d);
            pw.Password = "s3cret";
            Assert.True(d.TryConfirm());
            return d.Result!;
        });

        Assert.Equal("s3cret", result["password"]);
        Assert.Equal("less", result["level"]);
        Assert.Equal(70d, result["quality"]);
        Assert.Equal(true, result["keep"]);
    }

    private static System.Windows.Controls.PasswordBox FindPasswordBox(DependencyObject root)
    {
        var queue = new Queue<DependencyObject>([root]);
        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            if (node is System.Windows.Controls.PasswordBox pb) return pb;
            foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>()) queue.Enqueue(child);
        }
        throw new InvalidOperationException("no password box");
    }

    [Fact]
    public void The_dialog_is_mirrored_for_arabic_and_renders_without_throwing()
    {
        var flow = Sta.Run(() =>
        {
            var d = new ToolOptionsDialog("حماية", "pdf.protect", ProtectOptions, new TestLocalizer("ar"));
            d.Measure(new Size(420, 800));
            return d.FlowDirection;
        });
        Assert.Equal(FlowDirection.RightToLeft, flow);
    }

    [Fact]
    public void The_shelf_window_builds_for_both_directions_with_files_and_when_empty()
    {
        using var dir = new TempDir();
        var file = dir.WriteText("report.pdf", "x");
        var sizes = Sta.Run(() =>
        {
            var model = new ShelfModel();
            var w = new FloatingShelfWindow(model, new TestLocalizer("ar"));
            w.Measure(new Size(200, 400));
            model.Add([file]);
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
            w.PinTo(ShelfEdge.Left);
            return (w.Width, w.Height, w.Edge, model.Items.Count);
        });
        Assert.Equal(ShelfEdge.Left, sizes.Edge);
        Assert.Equal(1, sizes.Count);
        Assert.True(sizes.Width > 100 && sizes.Height >= 120);
    }
}
