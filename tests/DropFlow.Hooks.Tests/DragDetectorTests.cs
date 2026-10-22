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

    // ---------------------------------------------------------------- (a) only real file drags open the wheel

    [Fact]
    public void A_tap_during_a_drag_that_is_not_a_file_drag_is_rejected_not_reported()
    {
        var (d, rec) = Create();
        var rejected = new List<ModifierKey>();
        d.TapRejected += rejected.Add;
        d.ConfirmFileDrag = () => false;          // e.g. Shift+drag selecting text, or moving a window

        StartDrag(d);
        d.OnKeyDown(VkShift);
        d.OnKeyUp(VkShift);

        Assert.DoesNotContain(rec.Events, e => e.StartsWith("tap"));
        Assert.Equal([ModifierKey.Shift], rejected);
    }

    [Fact]
    public void The_file_drag_check_is_asked_at_the_tap_and_a_confirmed_drag_taps_normally()
    {
        var (d, rec) = Create();
        var asked = 0;
        d.ConfirmFileDrag = () => { asked++; return true; };

        StartDrag(d);
        Assert.Equal(0, asked);                   // nothing is asked while merely dragging
        d.OnKeyDown(VkCtrl);
        d.OnKeyUp(VkCtrl);

        Assert.Equal(1, asked);
        Assert.Contains("tap:Ctrl@120,120", rec.Events);
    }

    [Fact]
    public void Without_a_check_every_tap_counts_the_escape_hatch()
    {
        var (d, rec) = Create();
        d.ConfirmFileDrag = null;
        StartDrag(d);
        d.OnKeyDown(VkAlt);
        d.OnKeyUp(VkAlt);
        Assert.Contains("tap:Alt@120,120", rec.Events);
    }

    [Fact]
    public void A_check_that_throws_lets_the_tap_through_and_reports_the_failure()
    {
        var (d, rec) = Create();
        Exception? reported = null;
        d.ConfirmFailed += ex => reported = ex;
        d.ConfirmFileDrag = () => throw new EntryPointNotFoundException("FindWindowW");
        StartDrag(d);
        d.OnKeyDown(VkShift);
        d.OnKeyUp(VkShift);
        Assert.Contains(rec.Events, e => e.StartsWith("tap:Shift"));
        Assert.IsType<EntryPointNotFoundException>(reported);
    }

    // ---------------------------------------------------------------- (b) the button state is the truth when a button-up is missed

    [Fact]
    public void A_poll_that_finds_the_button_up_ends_a_drag_whose_mouse_up_was_missed()
    {
        var (d, rec) = Create();
        StartDrag(d);
        d.OnPoll(leftButtonDown: false, 200, 210);
        Assert.False(d.IsDragging);
        Assert.False(d.IsButtonDown);
        Assert.Equal("end@200,210", rec.Events[^1]);

        // a late real mouse-up does not end anything twice
        d.OnMouseUp(200, 210);
        Assert.Single(rec.Events, e => e.StartsWith("end"));
    }

    [Fact]
    public void A_poll_that_finds_the_button_up_before_any_drag_just_resets()
    {
        var (d, rec) = Create();
        d.OnMouseDown(100, 100);
        d.OnPoll(leftButtonDown: false, 100, 100);
        Assert.False(d.IsButtonDown);
        d.OnMouseMove(300, 300);                  // no button: not a drag
        Assert.Empty(rec.Events);
    }

    [Fact]
    public void Polling_without_a_button_down_does_nothing()
    {
        var (d, rec) = Create();
        d.OnPoll(leftButtonDown: true, 500, 500);
        d.OnPoll(leftButtonDown: false, 600, 600);
        Assert.Empty(rec.Events);
        Assert.False(d.IsButtonDown);
    }

    // ---------------------------------------------------------------- (c) movement that bypasses the hook still counts

    [Fact]
    public void Cursor_movement_seen_only_by_polling_starts_the_drag()
    {
        var (d, rec) = Create(4);
        d.OnMouseDown(100, 100);                  // remote desktop / pen: the hook saw the press but no moves
        d.OnPoll(leftButtonDown: true, 102, 101);
        Assert.False(d.IsDragging);
        d.OnPoll(leftButtonDown: true, 140, 100);
        Assert.True(d.IsDragging);
        Assert.Equal(["start@140,100"], rec.Events);

        d.OnPoll(leftButtonDown: true, 180, 120);
        d.OnKeyDown(VkShift);
        d.OnKeyUp(VkShift);
        Assert.Contains("tap:Shift@180,120", rec.Events);   // the wheel opens where the cursor really is
    }

    // ---------------------------------------------------------------- (d) only a press during the drag counts

    [Fact]
    public void A_modifier_held_at_the_button_press_whose_key_down_the_hook_never_saw_is_not_a_tap()
    {
        var (d, rec) = Create();
        // Shift was physically down when the button went down (read with GetAsyncKeyState), but its key-down happened before the hook
        // saw it (or went to an elevated window). Its auto-repeat key-downs during the drag are not a new press.
        d.OnMouseDown(100, 100, isKeyDown: vk => vk == VkShift);
        d.OnMouseMove(130, 130);
        d.OnKeyDown(VkShift);                     // auto-repeat
        d.OnKeyUp(VkShift);
        Assert.DoesNotContain(rec.Events, e => e.StartsWith("tap"));

        // released and pressed again during the same drag: that one is a real press
        d.OnKeyDown(VkShift);
        d.OnKeyUp(VkShift);
        Assert.Contains(rec.Events, e => e.StartsWith("tap:Shift"));
    }

    [Fact]
    public void A_key_whose_release_the_hook_missed_stops_blocking_taps_at_the_next_press()
    {
        var (d, rec) = Create();
        d.OnKeyDown(VkCtrl);                      // its key-up went to an elevated window and never reached the hook
        d.OnMouseDown(100, 100, isKeyDown: _ => false);
        d.OnMouseMove(130, 130);
        d.OnKeyDown(VkShift);
        d.OnKeyUp(VkShift);
        Assert.Contains(rec.Events, e => e.StartsWith("tap:Shift"));
    }

    [Fact]
    public void Generic_modifier_codes_are_never_remembered_from_the_key_state()
    {
        // GetAsyncKeyState(VK_SHIFT) is true together with VK_LSHIFT, but the hook only ever reports VK_LSHIFT's release.
        Assert.DoesNotContain(0x10, DragDetector.ModifierKeys);
        Assert.DoesNotContain(0x11, DragDetector.ModifierKeys);
        Assert.DoesNotContain(0x12, DragDetector.ModifierKeys);
    }

    [Fact]
    public void A_modifier_pressed_after_the_button_but_before_the_drag_threshold_is_not_a_tap()
    {
        var (d, rec) = Create(4);
        d.OnMouseDown(100, 100);
        d.OnKeyDown(VkShift);                     // Shift+click style input
        d.OnMouseMove(140, 100);                  // now it becomes a drag
        d.OnKeyUp(VkShift);
        Assert.DoesNotContain(rec.Events, e => e.StartsWith("tap"));
    }
}
