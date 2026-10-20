using DropFlow.Overlay;

namespace DropFlow.Overlay.Tests;

public class WheelGeometryTests
{
    private static WheelGeometry Wheel(int n, bool mirrored = false) => new(n, 150, 50, mirrored);

    [Theory]
    [InlineData(1, 360)]
    [InlineData(3, 120)]
    [InlineData(6, 60)]
    [InlineData(8, 45)]
    [InlineData(12, 30)]
    public void Wedges_share_the_circle_equally(int count, double step) => Assert.Equal(step, Wheel(count).Step, 6);

    [Fact]
    public void The_first_wedge_is_centred_on_up_and_the_rest_follow_clockwise()
    {
        var w = Wheel(6);
        Assert.Equal(0, w.CenterAngle(0));
        Assert.Equal(60, w.CenterAngle(1));
        Assert.Equal(300, w.CenterAngle(5));
    }

    [Fact]
    public void A_mirrored_wheel_runs_counter_clockwise()
    {
        var w = Wheel(6, mirrored: true);
        Assert.Equal(0, w.CenterAngle(0));
        Assert.Equal(300, w.CenterAngle(1));
        Assert.Equal(60, w.CenterAngle(5));
    }

    // Angles are clockwise from up; 6 wedges, so wedge i covers (60·i − 30°, 60·i + 30°).
    [Theory]
    [InlineData(0, 0)]
    [InlineData(25, 0)]
    [InlineData(35, 1)]
    [InlineData(60, 1)]
    [InlineData(95, 2)]
    [InlineData(180, 3)]
    [InlineData(250, 4)]
    [InlineData(300, 5)]
    [InlineData(335, 0)]
    [InlineData(359, 0)]
    public void Hit_testing_picks_the_wedge_under_the_point(double angle, int expected)
    {
        var (x, y) = WheelGeometry.OnCircle(100, angle);
        Assert.Equal(expected, Wheel(6).HitTest(x, y));
    }

    [Fact]
    public void Every_wedge_centre_hits_its_own_wedge_for_many_counts_and_both_directions()
    {
        foreach (var mirrored in new[] { false, true })
            for (var n = 1; n <= 14; n++)
            {
                var w = Wheel(n, mirrored);
                for (var i = 0; i < n; i++)
                {
                    var (x, y) = w.LabelCenter(i);
                    Assert.Equal(i, w.HitTest(x, y));
                }
            }
    }

    [Fact]
    public void Points_in_the_hub_or_far_outside_hit_nothing()
    {
        var w = Wheel(6);
        Assert.Equal(-1, w.HitTest(0, 0));
        Assert.Equal(-1, w.HitTest(30, 30));         // r = 42 < inner 50
        Assert.Equal(-1, w.HitTest(400, 0));
        Assert.Equal(-1, w.HitTest(0, -400));
    }

    [Fact]
    public void Overshooting_the_rim_slightly_still_selects_the_nearest_wedge()
    {
        var w = Wheel(6);
        Assert.Equal(0, w.HitTest(0, -165));          // 10 % past the rim
        Assert.Equal(-1, w.HitTest(0, -200));         // beyond the slack
        Assert.Equal(-1, w.HitTest(0, -165, outerSlack: 0));
    }

    [Fact]
    public void Mirroring_swaps_left_and_right()
    {
        var normal = Wheel(6);
        var mirrored = Wheel(6, mirrored: true);
        // a point at 60° clockwise (upper right) is wedge 1 normally and wedge 5 when mirrored
        var (x, y) = WheelGeometry.OnCircle(100, 60);
        Assert.Equal(1, normal.HitTest(x, y));
        Assert.Equal(5, mirrored.HitTest(x, y));
    }

    [Fact]
    public void Spans_cover_the_circle_without_overlap_and_honour_the_gap()
    {
        var w = Wheel(6);
        var (s0, e0) = w.Span(0, gapDegrees: 4);
        Assert.Equal(332, s0, 6);                      // -28
        Assert.Equal(28, e0, 6);
        var (s1, e1) = w.Span(1, gapDegrees: 4);
        Assert.Equal(32, s1, 6);
        Assert.Equal(88, e1, 6);
    }

    [Fact]
    public void Keyboard_navigation_wraps_and_respects_mirroring()
    {
        var w = Wheel(4);
        Assert.Equal(1, w.Move(0, +1));
        Assert.Equal(3, w.Move(0, -1));
        Assert.Equal(0, w.Move(3, +1));
        Assert.Equal(0, w.Move(-1, +1));
        Assert.Equal(3, w.Move(-1, -1));

        var m = Wheel(4, mirrored: true);
        Assert.Equal(3, m.Move(0, +1));     // "clockwise" key moves visually clockwise = decreasing index when mirrored
        Assert.Equal(1, m.Move(0, -1));
    }

    [Fact]
    public void Invalid_geometry_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new WheelGeometry(0, 150, 50));
        Assert.Throws<ArgumentException>(() => new WheelGeometry(6, 50, 50));
        Assert.Throws<ArgumentException>(() => new WheelGeometry(6, 150, 0));
    }

    [Fact]
    public void A_single_wedge_covers_everything_outside_the_hub()
    {
        var w = Wheel(1);
        foreach (var angle in new[] { 0, 90, 180, 270, 359 })
        {
            var (x, y) = WheelGeometry.OnCircle(100, angle);
            Assert.Equal(0, w.HitTest(x, y));
        }
    }
}
