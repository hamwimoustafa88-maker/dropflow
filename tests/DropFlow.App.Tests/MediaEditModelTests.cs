using System.Text.Json;
using DropFlow.App.Views.Editors.Media;

namespace DropFlow.App.Tests;

public class MediaTrimModelTests
{
    [Fact]
    public void The_whole_file_is_kept_until_a_handle_moves_and_the_end_option_stays_empty_at_the_end()
    {
        var m = new MediaTrimModel(60);
        Assert.True(m.IsWholeFile);
        Assert.Equal("", m.ToOptions()["end"]);

        m.SetStart(12.5);
        Assert.False(m.IsWholeFile);
        Assert.Equal("12.5", m.ToOptions()["start"]);
        Assert.Equal("", m.ToOptions()["end"]);

        m.SetEnd(30.25);
        Assert.Equal("30.25", m.ToOptions()["end"]);
    }

    [Fact]
    public void Handles_cannot_cross_or_leave_the_file()
    {
        var m = new MediaTrimModel(10);
        m.Set(8, 3);                       // dragged past each other: swapped
        Assert.Equal(new MediaSpan(3, 8), m.Keep);
        m.SetStart(9.99);                   // start pushed against the end keeps a tenth of a second
        Assert.Equal(7.9, m.Keep.Start, 3);
        m.Set(-5, 50);
        Assert.Equal(new MediaSpan(0, 10), m.Keep);
    }
}

public class MediaSplitModelTests
{
    [Fact]
    public void Points_make_numbered_clips_and_removed_clips_become_the_remove_option()
    {
        var m = new MediaSplitModel(90);
        m.AddPoint(60);
        m.AddPoint(30);
        Assert.Equal([30.0, 60.0], m.Points);
        Assert.Equal(3, m.Clips.Count);

        m.ToggleRemoved(m.ClipAt(45));   // the middle clip
        var o = m.ToOptions(reencode: false, joinRest: true);
        Assert.Equal("30, 60", o["points"]);
        Assert.Equal("2", o["remove"]);
        Assert.Equal("join", o["output"]);
        Assert.Null(m.Problem(joinRest: true));
    }

    [Fact]
    public void Splitting_a_removed_clip_removes_both_halves_and_merging_keeps_only_a_shared_removal()
    {
        var m = new MediaSplitModel(90);
        m.AddPoint(30);
        m.ToggleRemoved(1);               // 30..90 removed
        m.AddPoint(60);
        Assert.Equal([false, true, true], m.Clips.Select(c => c.Removed));
        m.RemovePoint(0);                 // 0..30 (kept) + 30..60 (removed) => kept
        Assert.Equal([false, true], m.Clips.Select(c => c.Removed));
    }

    [Fact]
    public void Points_too_close_to_an_end_or_another_point_are_ignored_and_moves_stay_between_neighbours()
    {
        var m = new MediaSplitModel(10);
        Assert.Equal(-1, m.AddPoint(0.05));
        Assert.Equal(0, m.AddPoint(5));
        Assert.Equal(-1, m.AddPoint(5.05));
        m.AddPoint(8);
        m.MovePoint(0, 9.5);
        Assert.Equal(7.9, m.Points[0], 3);
        Assert.Equal(1, m.PointNear(8.2, 0.3));
        Assert.Equal(-1, m.PointNear(6.5, 0.3));
    }

    [Fact]
    public void Equal_clips_are_limited_to_two_to_fifty_and_clear_removals()
    {
        var m = new MediaSplitModel(100);
        m.AddPoint(10);
        m.ToggleRemoved(0);
        m.MakeEqual(4);
        Assert.Equal([25.0, 50.0, 75.0], m.Points);
        Assert.Equal(0, m.RemovedCount);
        m.MakeEqual(500);
        Assert.Equal(50, m.Clips.Count);
        m.MakeEqual(1);
        Assert.Equal(2, m.Clips.Count);
    }

    [Fact]
    public void Problems_name_what_is_missing()
    {
        var m = new MediaSplitModel(10);
        Assert.Equal("media.split.needPoint", m.Problem(false));
        m.AddPoint(5);
        Assert.Equal("media.split.nothingRemoved", m.Problem(true));
        Assert.Null(m.Problem(false));
        m.ToggleRemoved(0);
        m.ToggleRemoved(1);
        Assert.Equal("media.split.allRemoved", m.Problem(false));
        Assert.Equal("clips", new MediaSplitModel(10).ToOptions(false, joinRest: true)["output"]);   // nothing removed: never "join"
    }
}

public class MediaSpeedTests
{
    [Theory]
    [InlineData(1.96, 2)]
    [InlineData(0.27, 0.25)]
    [InlineData(1.82, 1.8)]
    [InlineData(9, 4)]
    [InlineData(0.01, 0.25)]
    public void Speeds_snap_to_common_factors(double input, double expected) => Assert.Equal(expected, MediaSpeed.Snap(input), 3);

    [Fact]
    public void The_new_length_is_the_old_length_divided_by_the_speed() => Assert.Equal(30, MediaSpeed.NewLength(60, 2));
}

public class MediaFrameListTests
{
    [Fact]
    public void Frames_are_sorted_unique_and_inside_the_video()
    {
        var f = new MediaFrameList(10, 0.04);
        Assert.True(f.Add(5));
        Assert.True(f.Add(1.5));
        Assert.False(f.Add(5.01));            // the same frame
        Assert.True(f.Add(20));               // past the end: the last frame
        Assert.Equal([1.5, 5, 9.98], f.Times.Select(t => Math.Round(t, 2)));
        Assert.Equal("1.5, 5, 9.98", f.ToOptions(0)["times"]);
        f.Remove(0);
        Assert.Equal(2, f.Times.Count);
    }
}

public class MediaBleepModelTests
{
    [Fact]
    public void Ranges_are_sorted_kept_inside_the_file_and_written_for_the_tool()
    {
        var m = new MediaBleepModel(30);
        m.Add(10, 11);
        m.Add(2.5, 1.5);                      // dragged leftwards
        m.AddAt(29.9);                         // at the end: moved back so it fits
        Assert.Equal("1.5-2.5; 10-11; 29.5-30", m.RangesText);
        Assert.Equal(2, m.Selected);
        var o = m.ToOptions(1000.4, 0.354);
        Assert.Equal(1000d, o["frequency"]);
        Assert.Equal(0.35, o["volume"]);
    }

    [Fact]
    public void Hit_testing_prefers_edges_and_moving_keeps_the_length()
    {
        var m = new MediaBleepModel(30);
        m.Add(10, 12);
        Assert.Equal((0, BleepPart.StartEdge), m.HitTest(10.05, 0.1));
        Assert.Equal((0, BleepPart.EndEdge), m.HitTest(11.95, 0.1));
        Assert.Equal((0, BleepPart.Body), m.HitTest(11, 0.1));
        Assert.Equal((-1, BleepPart.None), m.HitTest(20, 0.1));

        m.Move(0, 29);
        Assert.Equal(new MediaSpan(28, 30), m.Ranges[0]);
        m.Delete(0);
        Assert.Empty(m.Ranges);
        Assert.Equal(-1, m.Selected);
    }
}

public class BoxMathTests
{
    [Fact]
    public void A_preset_is_the_largest_centred_box_of_that_shape()
    {
        var b = BoxMath.CenteredAspect(1920, 1080, 1, 1);
        Assert.Equal(new PixelBox(420, 0, 1080, 1080), b);
        var tall = BoxMath.CenteredAspect(1920, 1080, 9, 16);
        Assert.Equal(1080, tall.H, 3);
        Assert.Equal(607.5, tall.W, 3);
    }

    [Fact]
    public void Moving_stays_inside_the_frame_and_resizing_never_inverts()
    {
        var start = new PixelBox(100, 100, 200, 100);
        Assert.Equal(new PixelBox(440, 0, 200, 100), BoxMath.Drag(start, BoxHandle.Move, 900, -500, 640, 360));
        var shrunk = BoxMath.Drag(start, BoxHandle.Right, -500, 0, 640, 360);
        Assert.Equal(BoxMath.MinSide, shrunk.W);
        Assert.Equal(100, shrunk.X);
    }

    [Fact]
    public void Resizing_with_an_aspect_keeps_it_and_the_opposite_corner()
    {
        var start = new PixelBox(100, 100, 100, 100);
        var b = BoxMath.Drag(start, BoxHandle.BottomRight, 100, 20, 640, 360, aspect: 1);
        Assert.Equal(b.W, b.H, 3);
        Assert.Equal((100d, 100d), (b.X, b.Y));
        var fromTopLeft = BoxMath.Drag(start, BoxHandle.TopLeft, -50, -50, 640, 360, aspect: 1);
        Assert.Equal((200d, 200d), (fromTopLeft.Right, fromTopLeft.Bottom));
        var clamped = BoxMath.Drag(start, BoxHandle.BottomRight, 1000, 1000, 640, 360, aspect: 16.0 / 9);
        Assert.True(clamped.Right <= 640.001 && clamped.Bottom <= 360.001, clamped.ToString());
        Assert.Equal(16.0 / 9, clamped.W / clamped.H, 3);
    }

    [Fact]
    public void Handles_are_found_under_the_pointer()
    {
        var b = new PixelBox(100, 100, 200, 100);
        Assert.Equal(BoxHandle.TopLeft, BoxMath.HitTest(b, 101, 99, 6));
        Assert.Equal(BoxHandle.Right, BoxMath.HitTest(b, 302, 150, 6));
        Assert.Equal(BoxHandle.Move, BoxMath.HitTest(b, 200, 150, 6));
        Assert.Equal(BoxHandle.None, BoxMath.HitTest(b, 20, 20, 6));
    }
}

public class MediaCropModelTests
{
    [Fact]
    public void The_box_starts_as_a_centred_square_and_is_sent_as_exact_even_pixels()
    {
        var m = new MediaCropModel(1921, 1081);
        Assert.Equal("1:1", m.Aspect);
        var o = m.ToOptions();
        Assert.Equal("custom", o["aspect"]);
        Assert.All(new[] { "x", "y", "w", "h" }, k => Assert.Equal(0, (int)o[k]! % 2));
        Assert.True((int)o["x"]! + (int)o["w"]! <= 1921);
    }

    [Fact]
    public void Typed_pixels_drop_the_preset_and_stay_inside()
    {
        var m = new MediaCropModel(640, 360);
        m.SetPixels(600, 10, 200, 5000);
        Assert.Equal("free", m.Aspect);
        Assert.Equal(new PixelBox(440, 0, 200, 360), m.Box);
        m.SetAspect("free");
        Assert.Equal(new PixelBox(440, 0, 200, 360), m.Box);   // free keeps the box
        m.SetAspect("16:9");
        Assert.True(m.IsWholeFrame);
    }
}

public class MediaRedactModelTests
{
    [Fact]
    public void Boxes_become_a_json_list_of_fractions_and_seconds()
    {
        var m = new MediaRedactModel(60, 1920, 1080);
        m.Add(new PixelBox(192, 108, 384, 216));
        m.Add(new PixelBox(0, 540, 960, 540));
        m.SetTimes(1, 2.5, 7);

        var (options, problem) = m.ToOptions("solid", 20, "black", supportsBoxes: true);

        Assert.Null(problem);
        using var doc = JsonDocument.Parse((string)options!["boxes"]!);
        var boxes = doc.RootElement.EnumerateArray().ToList();
        Assert.Equal(2, boxes.Count);
        Assert.Equal(0.1, boxes[0].GetProperty("x").GetDouble(), 5);
        Assert.Equal(0.2, boxes[0].GetProperty("w").GetDouble(), 5);
        Assert.Equal(JsonValueKind.Null, boxes[0].GetProperty("start").ValueKind);   // whole video
        Assert.Equal(2.5, boxes[1].GetProperty("start").GetDouble());
        Assert.Equal(7, boxes[1].GetProperty("end").GetDouble());
        Assert.Equal("solid", options["mode"]);
    }

    [Fact]
    public void A_single_box_also_fills_the_pixel_options_of_the_single_box_tool()
    {
        var m = new MediaRedactModel(60, 1920, 1080);
        m.Add(new PixelBox(100.4, 50.6, 300.2, 80));
        m.SetTimes(0, 1, 3);
        var (o, _) = m.ToOptions("blur", 25, "black", supportsBoxes: false);
        Assert.False(o!.ContainsKey("boxes"));
        Assert.Equal((100, 50), ((int)o["x"]!, (int)o["y"]!));
        Assert.True((int)o["w"]! >= 300);
        Assert.Equal("1", o["start"]);
        Assert.Equal("3", o["end"]);
    }

    [Fact]
    public void Several_boxes_are_refused_rather_than_dropped_when_the_tool_takes_only_one()
    {
        var m = new MediaRedactModel(60, 640, 360);
        Assert.Equal("media.redact.needBox", m.ToOptions("blur", 20, "black", true).Problem);
        m.Add(new PixelBox(0, 0, 50, 50));
        m.Add(new PixelBox(100, 100, 50, 50));
        Assert.Equal("media.redact.oneBoxOnly", m.ToOptions("blur", 20, "black", supportsBoxes: false).Problem);
    }

    [Fact]
    public void Times_are_kept_in_order_inside_the_video_and_both_null_means_whole_video()
    {
        var m = new MediaRedactModel(10, 640, 360);
        m.Add(new PixelBox(0, 0, 50, 50));
        m.SetTimes(0, 8, 2);
        Assert.Equal((2d, 8d), (m.Boxes[0].Start!.Value, m.Boxes[0].End!.Value));
        Assert.False(m.Boxes[0].VisibleAt(9));
        m.SetTimes(0, null, null);
        Assert.True(m.Boxes[0].WholeVideo);
        Assert.True(m.Boxes[0].VisibleAt(9));
    }
}

public class MediaOrderModelTests
{
    [Fact]
    public void Files_can_be_moved_to_any_position()
    {
        var m = new MediaOrderModel(["a", "b", "c"]);
        m.Move(2, 0);
        Assert.Equal(["c", "a", "b"], m.Files);
        m.Move(0, 99);
        Assert.Equal(["a", "b", "c"], m.Files);
    }
}
