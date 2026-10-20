using DropFlow.Hooks;

namespace DropFlow.Hooks.Tests;

public class DragDetectorTests
{
    private const int VkShift = 0xA0;
    private const int VkCtrl = 0xA2;
    private const int VkAlt = 0xA4;
    private const int VkC = 0x43;

    private sealed class Recorder
    {
        public readonly List<string> Events = [];
        public Recorder(DragDetector d)
        {
            d.DragStarted += p => Events.Add($"start@{p.X},{p.Y}");
            d.DragEnded += p => Events.Add($"end@{p.X},{p.Y}");
            d.DragCancelled += () => Events.Add("cancel");
            d.KeyTapped += (k, p) => Events.Add($"tap:{k}@{p.X},{p.Y}");
        }
    }

    private static (DragDetector Detector, Recorder Rec) Create(int threshold = 4)
    {
        var d = new DragDetector(threshold, threshold);
        return (d, new Recorder(d));
    }

    private static void StartDrag(DragDetector d)
    {
        d.OnMouseDown(100, 100);
        d.OnMouseMove(120, 120);
    }

    [Fact]
    public void Movement_below_threshold_is_not_a_drag()
    {
        var (d, rec) = Create(4);
        d.OnMouseDown(100, 100);
        d.OnMouseMove(103, 102);
        d.OnMouseUp(103, 102);
        Assert.False(d.IsDragging);
        Assert.Empty(rec.Events);
    }

    [Fact]
    public void Movement_beyond_threshold_while_left_down_starts_a_drag_once()
    {
        var (d, rec) = Create(4);
        d.OnMouseDown(100, 100);
        d.OnMouseMove(110, 100);
        d.OnMouseMove(130, 100);
        Assert.True(d.IsDragging);
        Assert.Equal(["start@110,100"], rec.Events);
    }

    [Fact]
    public void Movement_without_left_button_is_ignored()
    {
        var (d, rec) = Create();
        d.OnMouseMove(500, 500);
        Assert.False(d.IsDragging);
        Assert.Empty(rec.Events);
    }

    [Fact]
    public void Releasing_the_button_ends_the_drag()
    {
        var (d, rec) = Create();
        StartDrag(d);
        d.OnMouseUp(140, 130);
        Assert.False(d.IsDragging);
        Assert.Equal(["start@120,120", "end@140,130"], rec.Events);
    }

    [Theory]
    [InlineData(VkShift, ModifierKey.Shift)]
    [InlineData(0x10, ModifierKey.Shift)]
    [InlineData(VkCtrl, ModifierKey.Ctrl)]
    [InlineData(VkAlt, ModifierKey.Alt)]
    public void Tapping_a_modifier_during_a_drag_raises_KeyTapped_on_release(int vk, ModifierKey expected)
    {
        var (d, rec) = Create();
        StartDrag(d);
        d.OnMouseMove(150, 160);
        d.OnKeyDown(vk);
        Assert.DoesNotContain(rec.Events, e => e.StartsWith("tap"));
        d.OnKeyUp(vk);
        Assert.Contains($"tap:{expected}@150,160", rec.Events);
    }

    [Fact]
    public void Modifier_tap_without_a_drag_is_ignored()
    {
        var (d, rec) = Create();
        d.OnKeyDown(VkShift);
        d.OnKeyUp(VkShift);
        Assert.Empty(rec.Events);
    }

    [Fact]
    public void Auto_repeat_keydown_produces_a_single_tap()
    {
        var (d, rec) = Create();
        StartDrag(d);
        d.OnKeyDown(VkShift);
        d.OnKeyDown(VkShift);
        d.OnKeyDown(VkShift);
        d.OnKeyUp(VkShift);
        Assert.Single(rec.Events, e => e.StartsWith("tap"));
    }

    [Fact]
    public void Another_key_pressed_in_between_makes_it_a_chord_not_a_tap()
    {
        var (d, rec) = Create();
        StartDrag(d);
        d.OnKeyDown(VkCtrl);
        d.OnKeyDown(VkC);
        d.OnKeyUp(VkC);
        d.OnKeyUp(VkCtrl);
        Assert.DoesNotContain(rec.Events, e => e.StartsWith("tap"));
    }

    [Fact]
    public void Two_modifiers_together_are_not_a_tap()
    {
        var (d, rec) = Create();
        StartDrag(d);
        d.OnKeyDown(VkShift);
        d.OnKeyDown(VkCtrl);
        d.OnKeyUp(VkCtrl);
        d.OnKeyUp(VkShift);
        Assert.DoesNotContain(rec.Events, e => e.StartsWith("tap"));
    }

    [Fact]
    public void Modifier_already_held_when_the_drag_starts_is_not_a_tap()
    {
        var (d, rec) = Create();
        d.OnKeyDown(VkShift);
        StartDrag(d);
        d.OnKeyUp(VkShift);
        Assert.DoesNotContain(rec.Events, e => e.StartsWith("tap"));
    }

    [Fact]
    public void Several_taps_in_one_drag_are_each_reported()
    {
        var (d, rec) = Create();
        StartDrag(d);
        d.OnKeyDown(VkShift); d.OnKeyUp(VkShift);
        d.OnKeyDown(VkCtrl); d.OnKeyUp(VkCtrl);
        Assert.Equal(["tap:Shift@120,120", "tap:Ctrl@120,120"], rec.Events.Where(e => e.StartsWith("tap")));
    }

    [Fact]
    public void Escape_during_a_drag_cancels_it()
    {
        var (d, rec) = Create();
        StartDrag(d);
        d.OnKeyDown(DragDetector.VkEscape);
        Assert.False(d.IsDragging);
        Assert.Equal("cancel", rec.Events[^1]);
        d.OnMouseUp(130, 130);
        Assert.DoesNotContain(rec.Events, e => e.StartsWith("end"));
    }

    [Fact]
    public void A_new_drag_after_the_previous_one_works()
    {
        var (d, rec) = Create();
        StartDrag(d);
        d.OnMouseUp(120, 120);
        StartDrag(d);
        Assert.Equal(2, rec.Events.Count(e => e.StartsWith("start")));
    }
}
