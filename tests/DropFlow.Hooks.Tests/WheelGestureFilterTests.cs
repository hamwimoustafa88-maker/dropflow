using DropFlow.Hooks;

namespace DropFlow.Hooks.Tests;

public class WheelGestureFilterTests
{
    private const int Tab = WheelGestureFilter.VkTab;
    private static readonly ScreenRect Wheel = new(100, 100, 400, 400);

    private static WheelGestureFilter Armed()
    {
        var f = new WheelGestureFilter();
        f.Arm(Wheel);
        return f;
    }

    [Fact]
    public void Nothing_is_swallowed_when_the_wheel_is_not_armed()
    {
        var f = new WheelGestureFilter();
        Assert.Equal(GestureDecision.Pass, f.OnKey(Tab, down: true, modifierHeld: false, dragInProgress: true));
        Assert.Equal(GestureDecision.Pass, f.OnKey(Tab, down: false, modifierHeld: false, dragInProgress: true));
        Assert.Equal(GestureDecision.Pass, f.OnWheel(300, 300, 120, 0, dragInProgress: true));
    }

    [Fact]
    public void Tab_during_a_drag_flips_once_and_its_key_up_is_swallowed_too()
    {
        var f = Armed();
        Assert.Equal(new GestureDecision(true, true), f.OnKey(Tab, true, false, true));
        Assert.Equal(new GestureDecision(true, false), f.OnKey(Tab, true, false, true));   // auto-repeat: swallowed, no second flip
        Assert.Equal(new GestureDecision(true, false), f.OnKey(Tab, false, false, true));  // its key-up never reaches the app
        Assert.Equal(new GestureDecision(true, true), f.OnKey(Tab, true, false, true));    // a new press flips again
    }

    [Fact]
    public void Tab_with_Ctrl_Alt_or_Win_is_never_touched_so_Alt_Tab_works_mid_drag()
    {
        var f = Armed();
        Assert.Equal(GestureDecision.Pass, f.OnKey(Tab, true, modifierHeld: true, dragInProgress: true));
        Assert.Equal(GestureDecision.Pass, f.OnKey(Tab, false, modifierHeld: true, dragInProgress: true));
    }

    [Fact]
    public void Other_keys_and_tab_outside_a_drag_pass()
    {
        var f = Armed();
        Assert.Equal(GestureDecision.Pass, f.OnKey(0x41, true, false, true));
        Assert.Equal(GestureDecision.Pass, f.OnKey(Tab, true, false, dragInProgress: false));
        Assert.Equal(GestureDecision.Pass, f.OnKey(Tab, false, false, dragInProgress: false));
    }

    [Fact]
    public void A_key_up_after_disarming_is_still_swallowed_when_its_key_down_was()
    {
        var f = Armed();
        f.OnKey(Tab, true, false, true);
        f.Disarm();
        Assert.Equal(new GestureDecision(true, false), f.OnKey(Tab, false, false, true));
    }

    [Fact]
    public void The_mouse_wheel_flips_only_over_the_wheel_once_per_notch()
    {
        var f = Armed();
        Assert.Equal(GestureDecision.Pass, f.OnWheel(50, 50, 120, 0, true));                 // outside the wheel: the app scrolls
        Assert.Equal(new GestureDecision(true, true), f.OnWheel(300, 300, -120, 1000, true));
        Assert.Equal(new GestureDecision(true, false), f.OnWheel(300, 300, -120, 1100, true)); // too soon after the last flip
        Assert.Equal(new GestureDecision(true, true), f.OnWheel(300, 300, -120, 1400, true));
    }

    [Fact]
    public void Small_touchpad_steps_add_up_to_one_flip()
    {
        var f = Armed();
        Assert.False(f.OnWheel(300, 300, 40, 0, true).Flip);
        Assert.False(f.OnWheel(300, 300, 40, 10, true).Flip);
        Assert.True(f.OnWheel(300, 300, 40, 20, true).Flip);
        Assert.False(f.OnWheel(300, 300, -40, 30, true).Flip);   // direction change starts a new sum
    }

    [Fact]
    public void The_kill_switch_and_disarming_turn_everything_off()
    {
        var f = Armed();
        f.Enabled = false;
        Assert.Equal(GestureDecision.Pass, f.OnKey(Tab, true, false, true));
        Assert.Equal(GestureDecision.Pass, f.OnWheel(300, 300, 120, 0, true));
        f.Enabled = true;
        f.Disarm();
        Assert.False(f.IsArmed);
        Assert.Equal(GestureDecision.Pass, f.OnWheel(300, 300, 120, 0, true));
    }
}
