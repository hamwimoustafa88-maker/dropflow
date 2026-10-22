using DropFlow.App.Views.Editors;
using DropFlow.Engines.Images;

namespace DropFlow.App.Tests;

public class PreviewSchedulerTests
{
    [Fact]
    public async Task Only_the_newest_request_is_applied_and_stale_results_are_disposed()
    {
        var gate = new TaskCompletionSource();
        var started = new TaskCompletionSource();
        var scheduler = new PreviewScheduler<Result>(TimeSpan.Zero, (_, _) => Task.CompletedTask);
        var applied = new List<int>();
        var first = new Result(1);

        var slow = scheduler.RequestAsync(_ => { started.SetResult(); gate.Task.Wait(); return first; }, r => applied.Add(r.Id));
        await started.Task;                                    // the first render is running
        var fast = scheduler.RequestAsync(_ => new Result(2), r => applied.Add(r.Id));
        await fast;
        gate.SetResult();
        await slow;

        Assert.Equal([2], applied);
        Assert.True(first.Disposed, "the stale result is disposed");
        Assert.False(scheduler.IsBusy);
        Assert.Equal(1, scheduler.Applied);
    }

    [Fact]
    public async Task Requests_within_the_quiet_period_render_once()
    {
        var renders = 0;
        var scheduler = new PreviewScheduler<int>(TimeSpan.FromMilliseconds(80));
        var tasks = Enumerable.Range(0, 5).Select(i => scheduler.RequestAsync(_ => Interlocked.Increment(ref renders), _ => { })).ToList();
        await Task.WhenAll(tasks);
        Assert.Equal(1, renders);
    }

    [Fact]
    public async Task A_failure_of_the_newest_render_is_reported()
    {
        Exception? seen = null;
        var scheduler = new PreviewScheduler<int>(TimeSpan.Zero, (_, _) => Task.CompletedTask);
        await scheduler.RequestAsync(_ => throw new InvalidOperationException("boom"), _ => { }, ex => seen = ex);
        Assert.Equal("boom", seen?.Message);
    }

    [Fact]
    public async Task Cancel_drops_what_is_pending()
    {
        var applied = false;
        var scheduler = new PreviewScheduler<int>(TimeSpan.FromMilliseconds(200));
        var pending = scheduler.RequestAsync(_ => 1, _ => applied = true);
        scheduler.Cancel();
        await pending;
        Assert.False(applied);
    }

    private sealed class Result(int id) : IDisposable
    {
        public int Id { get; } = id;
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }
}

public class BoxGeometryTests
{
    [Theory]
    [InlineData("16:9", 16.0 / 9)]
    [InlineData("1:1", 1.0)]
    [InlineData("3:4", 0.75)]
    public void Ratios_parse(string aspect, double expected) => Assert.Equal(expected, BoxGeometry.Ratio(aspect)!.Value, 6);

    [Theory]
    [InlineData("free")]
    [InlineData("original")]
    [InlineData("x:0")]
    public void Free_and_bad_aspects_have_no_ratio(string aspect) => Assert.Null(BoxGeometry.Ratio(aspect));

    [Fact]
    public void The_initial_box_is_the_largest_centred_one_with_the_ratio()
    {
        var box = BoxGeometry.Initial(1200, 800, 1);
        Assert.Equal(new Box(200, 0, 800, 800), box);
        Assert.Equal(new Box(0, 0, 1200, 800), BoxGeometry.Initial(1200, 800, null));
    }

    [Fact]
    public void Dragging_a_corner_with_a_ratio_keeps_the_ratio_and_the_opposite_corner()
    {
        var start = new Box(100, 100, 400, 225);
        var box = BoxGeometry.Drag(start, BoxHandle.BottomRight, 160, 10, 16.0 / 9, 2000, 2000);
        Assert.Equal(100, box.X);
        Assert.Equal(100, box.Y);
        Assert.Equal(16.0 / 9, box.W / box.H, 6);
        Assert.Equal(560, box.W, 6);
    }

    [Fact]
    public void A_box_never_leaves_the_picture()
    {
        var moved = BoxGeometry.Drag(new Box(10, 10, 100, 100), BoxHandle.Move, 5000, -5000, null, 800, 600);
        Assert.Equal(new Box(700, 0, 100, 100), moved);

        var grown = BoxGeometry.Drag(new Box(600, 400, 100, 100), BoxHandle.BottomRight, 900, 900, 1, 800, 600);
        Assert.True(grown.Right <= 800 && grown.Bottom <= 600);
        Assert.Equal(grown.W, grown.H, 6);
    }

    [Fact]
    public void Dragging_past_the_anchor_keeps_a_minimum_size()
    {
        var box = BoxGeometry.Drag(new Box(100, 100, 50, 50), BoxHandle.TopLeft, 500, 500, null, 800, 600, minSize: 8);
        Assert.Equal(8, box.W);
        Assert.Equal(8, box.H);
        Assert.Equal(150, box.Right);
    }

    [Fact]
    public void Hit_testing_finds_corners_before_the_inside()
    {
        var box = new Box(100, 100, 200, 100);
        Assert.Equal(BoxHandle.TopLeft, BoxGeometry.HitTest(box, 103, 97, 6));
        Assert.Equal(BoxHandle.BottomRight, BoxGeometry.HitTest(box, 300, 200, 6));
        Assert.Equal(BoxHandle.Move, BoxGeometry.HitTest(box, 200, 150, 6));
        Assert.Equal(BoxHandle.None, BoxGeometry.HitTest(box, 10, 10, 6));
    }

    [Fact]
    public void Changing_the_ratio_keeps_the_centre_and_fits_inside()
    {
        var box = BoxGeometry.Fit(new Box(0, 0, 1200, 800), 9.0 / 16, 1200, 800);
        Assert.Equal(9.0 / 16, box.W / box.H, 6);
        Assert.True(box.H <= 800);
        Assert.Equal(600, box.X + box.W / 2, 6);
    }

    [Fact]
    public void Pixels_are_whole_and_inside_the_picture()
    {
        Assert.Equal((0, 0, 100, 50), new Box(-3.4, -1, 100.4, 49.6).ToPixels(100, 80));
        Assert.Equal((99, 79, 1, 1), new Box(140, 90, 10, 10).ToPixels(100, 80));
    }
}

public class AnnotationDocumentTests
{
    [Fact]
    public void Items_are_relative_so_they_fit_any_picture_size()
    {
        var doc = new AnnotationDocument(1000, 500);
        doc.Add(new AnnotationShape("rect", 600, 400, 100, 100, "#ff0000", 10, Fill: true));
        doc.Add(new AnnotationShape("arrow", 0, 0, 500, 250, "#00ff00", 5));

        var items = doc.ToItems(_ => "unused");

        var rect = items[0];
        Assert.Equal(("rect", 0.1, 0.2, 0.5, 0.6), (rect.Type, rect.X, rect.Y, rect.W, rect.H));
        Assert.Equal(0.01, rect.Stroke, 6);
        Assert.True(rect.Fill);
        Assert.Equal((0.5, 0.5), (items[1].X2, items[1].Y2));
    }

    [Fact]
    public void Text_becomes_a_picture_overlay_with_its_box()
    {
        var doc = new AnnotationDocument(200, 100);
        doc.Add(new AnnotationShape("text", 20, 10, 120, 40, "#000000", 2, Text: "مرحبا", Size: 20));
        var items = doc.ToItems(s => $"C:\\overlay-{s.Text}.png");
        var item = Assert.Single(items);
        Assert.Equal("image", item.Type);
        Assert.Equal("C:\\overlay-مرحبا.png", item.Path);
        Assert.Equal((0.1, 0.1, 0.5, 0.3), (item.X, item.Y, item.W, item.H));
    }

    [Fact]
    public void Undo_restores_the_previous_list_step_by_step()
    {
        var doc = new AnnotationDocument(100, 100);
        doc.Add(new AnnotationShape("rect", 0, 0, 10, 10, "#000000", 1));
        doc.Add(new AnnotationShape("line", 0, 0, 10, 10, "#000000", 1));
        doc.Remove(0);
        Assert.Single(doc.Shapes);

        Assert.True(doc.Undo());
        Assert.Equal(2, doc.Shapes.Count);
        Assert.True(doc.Undo());
        Assert.Single(doc.Shapes);
        Assert.True(doc.Undo());
        Assert.Empty(doc.Shapes);
        Assert.False(doc.Undo());
    }

    [Fact]
    public void Numbers_count_up_and_hit_testing_finds_the_topmost_shape()
    {
        var doc = new AnnotationDocument(400, 400);
        doc.Add(new AnnotationShape("number", 50, 50, 50, 50, "#000000", 1, Size: 30, N: doc.NextNumber));
        doc.Add(new AnnotationShape("number", 100, 50, 100, 50, "#000000", 1, Size: 30, N: doc.NextNumber));
        doc.Add(new AnnotationShape("rect", 0, 0, 400, 400, "#000000", 1));
        doc.Add(new AnnotationShape("line", 0, 300, 400, 300, "#000000", 4));

        Assert.Equal([1, 2], doc.Shapes.Where(s => s.Type == "number").Select(s => s.N));
        Assert.Equal(3, doc.HitTest(200, 302, 2));   // the line on top
        Assert.Equal(2, doc.HitTest(200, 200, 2));   // inside the box
        Assert.Equal(-1, doc.HitTest(500, 500, 2));
    }

    [Fact]
    public void Moving_a_magnifier_moves_the_spot_it_enlarges_not_the_lens()
    {
        var lens = new AnnotationShape("magnify", 10, 10, 200, 200, "#000000", 2, Size: 20).Moved(5, 5);
        Assert.Equal((15.0, 15.0, 200.0, 200.0), (lens.X, lens.Y, lens.X2, lens.Y2));
        var pen = new AnnotationShape("pen", 0, 0, 0, 0, "#000000", 2, Points: [1, 2, 3, 4]).Moved(10, 20);
        Assert.Equal([11, 22, 13, 24], pen.Points!);
    }

    [Theory]
    [InlineData("مرحبا", true)]
    [InlineData("Hello مرحبا", true)]
    [InlineData("Hello", false)]
    [InlineData("", false)]
    public void Arabic_text_is_typeset_right_to_left(string text, bool rtl) => Assert.Equal(rtl, AnnotationDocument.IsRightToLeft(text));
}

public class MetadataEditModelTests
{
    private static MetadataEditModel Model() => new(
    [
        new(MetadataGroup.File, "name", "Name", "a.jpg", false, false),
        new(MetadataGroup.Exif, "Artist", "Artist", "Old", true, true),
        new(MetadataGroup.Exif, "ExposureTime", "Exposure Time", "1/125", false, true),
        new(MetadataGroup.Gps, "GPSLatitude", "GPS Latitude", "33/1", false, true),
        new(MetadataGroup.Xmp, "dc:title", "Title", "غروب", true, true),
    ]);

    [Fact]
    public void Edits_removals_and_additions_become_changes()
    {
        var m = Model();
        m.Edit(MetadataGroup.Exif, "Artist", "New");
        m.Remove(MetadataGroup.Exif, "ExposureTime");
        Assert.True(m.Add(MetadataGroup.Iptc, "City", "بيروت"));

        var changes = m.Changes();
        Assert.Contains(new MetadataChange(MetadataGroup.Exif, "Artist", "New"), changes);
        Assert.Contains(new MetadataChange(MetadataGroup.Exif, "ExposureTime", null), changes);
        Assert.Contains(new MetadataChange(MetadataGroup.Iptc, "City", "بيروت"), changes);
        Assert.Single(m.Rows(MetadataGroup.Iptc));
    }

    [Fact]
    public void Removing_exif_also_removes_gps_and_drops_edits_inside_it()
    {
        var m = Model();
        m.Edit(MetadataGroup.Exif, "Artist", "New");
        m.RemoveGroup(MetadataGroup.Exif);
        var changes = m.Changes();
        Assert.Equal([new MetadataChange(MetadataGroup.Exif, "*", null), new MetadataChange(MetadataGroup.Gps, "*", null)], changes);
        Assert.All(m.Rows(MetadataGroup.Exif), r => Assert.True(r.Removed));
    }

    [Fact]
    public void Remove_all_never_touches_the_file_group()
    {
        var m = Model();
        m.RemoveAll();
        Assert.DoesNotContain(m.Changes(), c => c.Group == MetadataGroup.File);
        Assert.Equal(4, m.Changes().Count);
        Assert.False(m.Rows(MetadataGroup.File).Single().Removed);
    }

    [Fact]
    public void Typing_a_value_back_to_what_it_was_is_no_change()
    {
        var m = Model();
        m.Edit(MetadataGroup.Exif, "Artist", "Old");
        Assert.Empty(m.Changes());
    }

    [Fact]
    public void Search_matches_names_keys_and_values()
    {
        var m = Model();
        Assert.Single(m.Rows(MetadataGroup.Exif, "artist"));
        Assert.Single(m.Rows(MetadataGroup.Exif, "1/125"));
        Assert.Single(m.Rows(MetadataGroup.Xmp, "غروب"));
        Assert.Empty(m.Rows(MetadataGroup.Exif, "nothing like this"));
    }

    [Fact]
    public void Restoring_undoes_a_removal()
    {
        var m = Model();
        m.Remove(MetadataGroup.Xmp, "dc:title");
        Assert.True(m.Rows(MetadataGroup.Xmp).Single().Removed);
        m.Restore(MetadataGroup.Xmp, "dc:title");
        Assert.Empty(m.Changes());
    }
}
