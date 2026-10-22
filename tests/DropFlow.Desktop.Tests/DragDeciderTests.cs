using DropFlow.Core.Settings;
using DropFlow.Desktop.Drag;

namespace DropFlow.Desktop.Tests;

public class DragDeciderTests
{
    private static DragSample Up(int x = 0, int y = 0, Mods m = Mods.None) => new(false, m, x, y, false);
    private static DragSample Down(int x, int y, Mods m = Mods.None, bool source = true) => new(true, m, x, y, source);

    private static List<DragSignal> Run(DragDecider d, params DragSample[] samples) => samples.SelectMany(d.Feed).ToList();

    [Fact]
    public void Small_movement_with_the_button_down_is_not_a_drag()
    {
        var d = new DragDecider(threshold: 5);
        var signals = Run(d, Up(), Down(100, 100), Down(103, 104, Mods.Shift), Up(103, 104));
        Assert.Empty(signals);
    }

    [Fact]
    public void Moving_past_the_threshold_while_a_file_drag_is_active_starts_a_drag()
    {
        var d = new DragDecider(threshold: 5);
        var signals = Run(d, Up(), Down(100, 100), Down(110, 100));
        var started = Assert.Single(signals);
        Assert.Equal(DragSignalKind.Started, started.Kind);
        Assert.Equal((110, 100), (started.X, started.Y));
        Assert.True(d.IsDragging);
    }

    [Fact]
    public void Without_a_drag_source_moving_is_only_a_selection_or_a_window_move()
    {
        var d = new DragDecider(threshold: 5);
        var signals = Run(d, Up(), Down(100, 100, source: false), Down(200, 200, Mods.Shift, source: false), Up(200, 200));
        Assert.Empty(signals);
    }

    [Fact]
    public void A_modifier_pressed_during_the_drag_is_reported_once_on_its_rising_edge()
    {
        var d = new DragDecider(threshold: 5);
        var signals = Run(d, Up(), Down(0, 0), Down(20, 0), Down(25, 0, Mods.Shift), Down(30, 0, Mods.Shift), Down(35, 0, Mods.Shift));
        var taps = signals.Where(s => s.Kind == DragSignalKind.KeyTapped).ToList();
        var tap = Assert.Single(taps);
        Assert.Equal(TriggerKey.Shift, tap.Key);
        Assert.Equal((25, 0), (tap.X, tap.Y));
    }

    [Fact]
    public void A_key_held_when_the_button_went_down_does_not_count_until_pressed_again()
    {
        var d = new DragDecider(threshold: 5);
        var signals = Run(d, Up(m: Mods.Ctrl), Down(0, 0, Mods.Ctrl), Down(20, 0, Mods.Ctrl), Down(25, 0), Down(30, 0, Mods.Ctrl));
        var tap = Assert.Single(signals, s => s.Kind == DragSignalKind.KeyTapped);
        Assert.Equal(TriggerKey.Ctrl, tap.Key);
        Assert.Equal(30, tap.X);
    }

    [Fact]
    public void Pressing_a_second_key_while_holding_the_first_switches_wheels()
    {
        var d = new DragDecider(threshold: 5);
        var signals = Run(d, Up(), Down(0, 0), Down(20, 0), Down(21, 0, Mods.Shift), Down(22, 0, Mods.Shift | Mods.Alt));
        var keys = signals.Where(s => s.Kind == DragSignalKind.KeyTapped).Select(s => s.Key).ToList();
        Assert.Equal([TriggerKey.Shift, TriggerKey.Alt], keys);
    }

    [Fact]
    public void A_key_pressed_before_the_drag_is_recognised_is_ignored()
    {
        var d = new DragDecider(threshold: 5);
        // moved, but the drag source only shows up one sample later: the Shift that was already down then does not fire
        var signals = Run(d, Up(), Down(0, 0, source: false), Down(20, 0, Mods.Shift, source: false), Down(25, 0, Mods.Shift));
        Assert.DoesNotContain(signals, s => s.Kind == DragSignalKind.KeyTapped);
        Assert.Contains(signals, s => s.Kind == DragSignalKind.Started);
    }

    [Fact]
    public void A_drag_source_that_appears_late_still_starts_the_drag()
    {
        var d = new DragDecider(threshold: 5);
        var signals = Run(d, Up(), Down(0, 0, source: false), Down(30, 0, source: false), Down(40, 0, source: true), Down(41, 0, Mods.Alt));
        Assert.Equal([DragSignalKind.Started, DragSignalKind.KeyTapped], signals.Select(s => s.Kind).ToList());
    }

    [Fact]
    public void Releasing_the_button_ends_a_drag_and_resets()
    {
        var d = new DragDecider(threshold: 5);
        var signals = Run(d, Up(), Down(0, 0), Down(20, 0), Up(50, 60));
        Assert.Equal(DragSignalKind.Ended, signals[^1].Kind);
        Assert.Equal((50, 60), (signals[^1].X, signals[^1].Y));
        Assert.False(d.IsDragging);

        // the next press starts from scratch
        Assert.Empty(Run(d, Down(50, 60), Down(51, 60, Mods.Shift)));
    }

    [Fact]
    public void Releasing_without_a_drag_reports_nothing()
    {
        var d = new DragDecider(threshold: 5);
        Assert.Empty(Run(d, Up(), Down(0, 0, source: false), Up(1, 1)));
    }

    [Fact]
    public void Several_keys_on_one_sample_report_the_first_in_shift_ctrl_alt_order()
    {
        var d = new DragDecider(threshold: 5);
        var signals = Run(d, Up(), Down(0, 0), Down(20, 0), Down(20, 0, Mods.Alt | Mods.Ctrl));
        Assert.Equal(TriggerKey.Ctrl, Assert.Single(signals, s => s.Kind == DragSignalKind.KeyTapped).Key);
    }

    [Fact]
    public void A_negative_threshold_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DragDecider(-1));
    }
}

public class ModifierMapTests
{
    [Theory]
    [InlineData(1UL << 17, Mods.Shift)]           // NSEventModifierFlagShift
    [InlineData(1UL << 20, Mods.Ctrl)]            // Command takes the Ctrl slot on a Mac
    [InlineData(1UL << 18, Mods.Alt)]             // Control takes the Alt slot on a Mac
    [InlineData(1UL << 19, Mods.None)]            // Option is the Finder's copy key and is not a wheel key
    [InlineData((1UL << 17) | (1UL << 20), Mods.Shift | Mods.Ctrl)]
    public void Mac_flags(ulong flags, Mods expected) => Assert.Equal(expected, ModifierMap.FromMacFlags(flags));

    [Theory]
    [InlineData(1u, Mods.Shift)]
    [InlineData(4u, Mods.Ctrl)]
    [InlineData(8u, Mods.Alt)]
    [InlineData(2u, Mods.None)]                   // Caps Lock
    [InlineData(16u, Mods.None)]                  // Num Lock (Mod2) must not look like a key press
    [InlineData(1u | 4u | 256u, Mods.Shift | Mods.Ctrl)]
    public void X11_masks(uint mask, Mods expected) => Assert.Equal(expected, ModifierMap.FromX11Mask(mask));

    [Fact]
    public void X11_button_one_is_bit_eight() => Assert.True(ModifierMap.X11ButtonOneDown(1u << 8));

    [Fact]
    public void Mac_left_button_is_bit_zero()
    {
        Assert.True(ModifierMap.MacLeftButtonDown(1));
        Assert.False(ModifierMap.MacLeftButtonDown(2));
    }

    [Theory]
    [InlineData(TriggerKey.Shift, true, "⇧ Shift")]
    [InlineData(TriggerKey.Ctrl, true, "⌘ Command")]
    [InlineData(TriggerKey.Alt, true, "⌃ Control")]
    [InlineData(TriggerKey.Ctrl, false, "Ctrl")]
    [InlineData(TriggerKey.Alt, false, "Alt")]
    public void Key_names(TriggerKey key, bool mac, string expected) => Assert.Equal(expected, ModifierMap.KeyName(key, mac));
}
