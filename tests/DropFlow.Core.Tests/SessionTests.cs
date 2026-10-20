using DropFlow.Core.Catalog;
using DropFlow.Core.Operations;
using DropFlow.Core.Registry;
using DropFlow.Core.Session;
using DropFlow.Core.Settings;
using DropFlow.Core.Wheel;

namespace DropFlow.Core.Tests;

internal sealed class FakeView : IWheelView
{
    public List<string> Log { get; } = [];
    public WheelKind? LastKind { get; private set; }
    public IReadOnlyList<Wedge> LastItems { get; private set; } = [];
    public int LastFileCount { get; private set; }
    public bool Visible { get; private set; }

    public void Show(WheelKind kind, int x, int y) { Visible = true; LastKind = kind; Log.Add($"show {kind} @{x},{y}"); }
    public void SetItems(WheelKind kind, IReadOnlyList<Wedge> wedges, int fileCount) { LastKind = kind; LastItems = wedges; LastFileCount = fileCount; Log.Add($"items {kind} {wedges.Count} files={fileCount}"); }
    public void Hide() { Visible = false; Log.Add("hide"); }
}

/// <summary>Collects scheduled actions; the test decides when time "passes".</summary>
internal sealed class ManualScheduler : IScheduler
{
    private sealed class Handle(ManualScheduler owner, Action action) : IDisposable
    {
        public bool Cancelled;
        public Action Action { get; } = action;
        public void Dispose() { Cancelled = true; owner._handles.Remove(this); }
    }

    private readonly List<Handle> _handles = [];
    public int Pending => _handles.Count;

    public IDisposable After(TimeSpan delay, Action action)
    {
        var h = new Handle(this, action);
        _handles.Add(h);
        return h;
    }

    public void RunAll()
    {
        foreach (var h in _handles.ToList())
        {
            _handles.Remove(h);
            if (!h.Cancelled) h.Action();
        }
    }
}

public class WheelSessionTests
{
    private static readonly ToolCatalog Catalog = ToolCatalog.LoadEmbedded();
    private static readonly string[] Actions = ["act.copy", "act.copy-path"];

    private sealed class Rig
    {
        public FakeView View { get; } = new();
        public ManualScheduler Scheduler { get; } = new();
        public List<WheelSelection> Selected { get; } = [];
        public AppSettings Settings { get; set; } = new();
        public WheelSession Session { get; }

        public Rig()
        {
            var registry = new ToolRegistry().Register(new FakeConverter(["convert:jpg", "convert:webp", "img.compress"],
                supports: (op, ext) => Catalog.CategoryForExtension(ext) == "image" && Operation.TargetOf(op) != ext || op == "img.compress"));
            var builder = new WheelBuilder(Catalog, registry, new FakeEngines());
            Session = new WheelSession(builder, View, Scheduler, () => Settings, () => "en", () => Actions,
                s => { Selected.Add(s); return Task.CompletedTask; });
        }
    }

    [Fact]
    public void Tapping_a_trigger_key_shows_the_matching_wheel_empty_until_files_arrive()
    {
        var r = new Rig();
        r.Session.OnKeyTapped(TriggerKey.Shift, 100, 200);

        Assert.True(r.Session.IsVisible);
        Assert.Equal(["show Convert @100,200", "items Convert 0 files=0"], r.View.Log);
    }

    [Theory]
    [InlineData(TriggerKey.Shift, WheelKind.Convert)]
    [InlineData(TriggerKey.Ctrl, WheelKind.Tools)]
    [InlineData(TriggerKey.Alt, WheelKind.Actions)]
    public void Keys_map_to_wheels_by_the_settings(TriggerKey key, WheelKind expected)
    {
        var r = new Rig();
        r.Session.OnKeyTapped(key, 0, 0);
        Assert.Equal(expected, r.Session.Kind);
    }

    [Fact]
    public void Custom_key_assignments_are_honoured()
    {
        var r = new Rig { Settings = new AppSettings { Keys = new WheelKeys { Convert = TriggerKey.Alt, Tools = TriggerKey.Shift, Actions = TriggerKey.Ctrl } } };
        r.Session.OnKeyTapped(TriggerKey.Alt, 0, 0);
        Assert.Equal(WheelKind.Convert, r.Session.Kind);
    }

    [Fact]
    public void Files_entering_the_wheel_fill_it_with_wedges()
    {
        var r = new Rig();
        r.Session.OnKeyTapped(TriggerKey.Shift, 0, 0);
        r.Session.OnFilesEntered([@"C:\pics\a.png"]);

        Assert.Equal(["JPG", "WEBP"], r.View.LastItems.Select(w => w.Label));
        Assert.Equal(1, r.View.LastFileCount);
    }

    [Fact]
    public void Tapping_another_key_while_open_switches_the_wheel_without_reopening()
    {
        var r = new Rig();
        r.Session.OnKeyTapped(TriggerKey.Shift, 5, 5);
        r.Session.OnFilesEntered([@"C:\a.png"]);
        r.Session.OnKeyTapped(TriggerKey.Ctrl, 9, 9);

        Assert.Equal(1, r.View.Log.Count(l => l.StartsWith("show")));
        Assert.Equal(WheelKind.Tools, r.View.LastKind);
        Assert.Equal(["Compress images"], r.View.LastItems.Select(w => w.Label));
    }

    [Fact]
    public async Task Dropping_on_a_wedge_selects_it_and_closes_the_wheel()
    {
        var r = new Rig();
        r.Session.OnKeyTapped(TriggerKey.Shift, 0, 0);
        r.Session.OnFilesEntered([@"C:\a.png", @"C:\b.png"]);

        await r.Session.OnItemDroppedAsync([@"C:\a.png", @"C:\b.png"], 1);

        var sel = Assert.Single(r.Selected);
        Assert.Equal((WheelKind.Convert, "convert:webp"), (sel.Kind, sel.Wedge.Operation));
        Assert.Equal(2, sel.Files.Count);
        Assert.False(r.Session.IsVisible);
        Assert.False(r.View.Visible);
    }

    [Fact]
    public async Task Dropping_off_the_wedges_just_closes_it()
    {
        var r = new Rig();
        r.Session.OnKeyTapped(TriggerKey.Shift, 0, 0);
        r.Session.OnFilesEntered([@"C:\a.png"]);

        await r.Session.OnItemDroppedAsync([@"C:\a.png"], -1);
        await r.Session.OnItemDroppedAsync([@"C:\a.png"], 99);

        Assert.Empty(r.Selected);
        Assert.False(r.View.Visible);
    }

    [Fact]
    public void Releasing_the_mouse_hides_the_wheel_only_after_the_grace_period()
    {
        var r = new Rig();
        r.Session.OnKeyTapped(TriggerKey.Shift, 0, 0);
        r.Session.OnDragEnded();

        Assert.True(r.View.Visible);
        r.Scheduler.RunAll();
        Assert.False(r.View.Visible);
    }

    [Fact]
    public async Task A_drop_that_arrives_during_the_grace_period_cancels_the_hide_and_still_selects()
    {
        var r = new Rig();
        r.Session.OnKeyTapped(TriggerKey.Shift, 0, 0);
        r.Session.OnFilesEntered([@"C:\a.png"]);
        r.Session.OnDragEnded();                      // mouse-up is seen by the hook first…
        await r.Session.OnItemDroppedAsync([@"C:\a.png"], 0);   // …and the OLE drop arrives right after

        Assert.Single(r.Selected);
        Assert.Equal(0, r.Scheduler.Pending);
    }

    [Fact]
    public void Escape_dismisses_immediately_and_is_safe_when_nothing_is_open()
    {
        var r = new Rig();
        r.Session.OnDragCancelled();                  // nothing open: no hide call
        Assert.DoesNotContain("hide", r.View.Log);

        r.Session.OnKeyTapped(TriggerKey.Shift, 0, 0);
        r.Session.OnDragCancelled();
        Assert.False(r.View.Visible);
    }

    [Fact]
    public void Paused_means_no_wheel_at_all()
    {
        var r = new Rig { Settings = new AppSettings { Paused = true } };
        r.Session.OnKeyTapped(TriggerKey.Shift, 0, 0);
        Assert.False(r.Session.IsVisible);
        Assert.Empty(r.View.Log);
    }

    [Fact]
    public void Files_arriving_when_nothing_is_open_are_ignored()
    {
        var r = new Rig();
        r.Session.OnFilesEntered([@"C:\a.png"]);
        Assert.Empty(r.View.Log);
    }

    [Fact]
    public void ShowFor_opens_a_filled_wheel_for_known_files()
    {
        var r = new Rig();
        r.Session.ShowFor([@"C:\a.png"], WheelKind.Convert, 50, 60);
        Assert.Equal(["show Convert @50,60", "items Convert 2 files=1"], r.View.Log);
    }
}
