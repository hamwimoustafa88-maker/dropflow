// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text.Json;
using DropFlow.Engines.Media.Preview;

namespace DropFlow.App.Views.Editors.Media;

/// <summary>A time span on a media timeline, in seconds.</summary>
internal readonly record struct MediaSpan(double Start, double End)
{
    public double Length => End - Start;
    public bool Contains(double t) => t >= Start && t <= End;
}

/// <summary>
/// The pure state behind the media editors: what the user marked, and the option values that state means for the tool's converter.
/// No WPF here, so every rule is unit-tested; the windows only draw this state and feed it mouse and keyboard input.
/// </summary>
internal static class MediaEditMath
{
    /// <summary>The shortest piece any editor creates (a tenth of a second): shorter cuts are not meaningful and hard to see.</summary>
    public const double MinSpan = 0.1;

    /// <summary>A range kept inside 0..duration, at least <see cref="MinSpan"/> long (or the whole file when it is shorter).</summary>
    public static MediaSpan Clamp(double start, double end, double duration)
    {
        var min = Math.Min(MinSpan, duration);
        if (end < start) (start, end) = (end, start);
        start = Math.Clamp(start, 0, Math.Max(0, duration - min));
        end = Math.Clamp(end, start + min, duration);
        return new MediaSpan(start, end);
    }

    public static string Inv(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}

/// <summary>Trim: the part to keep.</summary>
internal sealed class MediaTrimModel(double duration)
{
    public double Duration { get; } = duration;
    public MediaSpan Keep { get; private set; } = new(0, duration);

    public void Set(double start, double end) => Keep = MediaEditMath.Clamp(start, end, Duration);
    /// <summary>Moves only the start handle: it stops a tenth of a second before the end instead of swapping with it.</summary>
    public void SetStart(double start) => Keep = MediaEditMath.Clamp(Math.Min(start, Keep.End - Math.Min(MediaEditMath.MinSpan, Duration)), Keep.End, Duration);

    /// <summary>Moves only the end handle, which stops a tenth of a second after the start.</summary>
    public void SetEnd(double end) => Keep = MediaEditMath.Clamp(Keep.Start, Math.Max(end, Keep.Start + Math.Min(MediaEditMath.MinSpan, Duration)), Duration);

    public bool IsWholeFile => Keep.Start <= 0.0005 && Keep.End >= Duration - 0.0005;

    /// <summary>vid.trim / aud.trim options. The end is left empty when it is the end of the file (the tools then cut to the end).</summary>
    public Dictionary<string, object?> ToOptions()
    {
        var o = new Dictionary<string, object?>
        {
            ["start"] = MediaTimeText.ForOption(Keep.Start),
            ["end"] = Keep.End >= Duration - 0.0005 ? "" : MediaTimeText.ForOption(Keep.End),
        };
        return o;
    }
}

/// <summary>Split: cut points, the clips between them, and which clips are left out.</summary>
internal sealed class MediaSplitModel(double duration)
{
    private readonly List<double> _points = [];
    private readonly List<bool> _removed = [false];

    public double Duration { get; } = duration;
    public IReadOnlyList<double> Points => _points;

    /// <summary>The clips in order, numbered from 1 as the tool numbers them.</summary>
    public IReadOnlyList<(int Number, MediaSpan Span, bool Removed)> Clips
    {
        get
        {
            var list = new List<(int, MediaSpan, bool)>();
            var start = 0.0;
            for (var i = 0; i <= _points.Count; i++)
            {
                var end = i < _points.Count ? _points[i] : Duration;
                list.Add((i + 1, new MediaSpan(start, end), _removed[i]));
                start = end;
            }
            return list;
        }
    }

    public int RemovedCount => _removed.Count(r => r);

    /// <summary>Adds a cut. Ignored too close to the ends or to another cut (it would make an empty clip). Returns the new point's index or -1.</summary>
    public int AddPoint(double t)
    {
        if (t < MediaEditMath.MinSpan || t > Duration - MediaEditMath.MinSpan) return -1;
        if (_points.Any(p => Math.Abs(p - t) < MediaEditMath.MinSpan)) return -1;
        var index = _points.BinarySearch(t);
        if (index < 0) index = ~index;
        _points.Insert(index, t);
        _removed.Insert(index + 1, _removed[index]); // both halves of a split clip keep its state
        return index;
    }

    /// <summary>Moves a cut between its neighbours (never past them).</summary>
    public void MovePoint(int index, double t)
    {
        var lo = (index > 0 ? _points[index - 1] : 0) + MediaEditMath.MinSpan;
        var hi = (index < _points.Count - 1 ? _points[index + 1] : Duration) - MediaEditMath.MinSpan;
        if (lo > hi) return;
        _points[index] = Math.Clamp(t, lo, hi);
    }

    /// <summary>Removes a cut; the two clips become one, which is left out only if both were.</summary>
    public void RemovePoint(int index)
    {
        var merged = _removed[index] && _removed[index + 1];
        _points.RemoveAt(index);
        _removed.RemoveAt(index + 1);
        _removed[index] = merged;
    }

    /// <summary>Replaces the cuts with <paramref name="count"/> clips of equal length (2-50). Nothing is left out afterwards.</summary>
    public void MakeEqual(int count)
    {
        count = Math.Clamp(count, 2, 50);
        _points.Clear();
        _removed.Clear();
        _removed.Add(false);
        for (var i = 1; i < count; i++)
        {
            _points.Add(Math.Round(Duration * i / count, 3));
            _removed.Add(false);
        }
    }

    /// <summary>Index (0-based) of the clip that contains <paramref name="t"/>.</summary>
    public int ClipAt(double t)
    {
        var i = 0;
        while (i < _points.Count && t >= _points[i]) i++;
        return i;
    }

    public void ToggleRemoved(int clipIndex)
    {
        if (clipIndex < 0 || clipIndex >= _removed.Count) return;
        _removed[clipIndex] = !_removed[clipIndex];
    }

    /// <summary>Index of the cut within <paramref name="tolerance"/> seconds of <paramref name="t"/>, or -1.</summary>
    public int PointNear(double t, double tolerance)
    {
        var best = -1;
        var bestDistance = tolerance;
        for (var i = 0; i < _points.Count; i++)
        {
            var d = Math.Abs(_points[i] - t);
            if (d <= bestDistance) { best = i; bestDistance = d; }
        }
        return best;
    }

    /// <summary>Why the current state cannot run (a string key), or null.</summary>
    public string? Problem(bool joinRest)
    {
        if (_points.Count == 0) return "media.split.needPoint";
        if (RemovedCount == _removed.Count) return "media.split.allRemoved";
        if (joinRest && RemovedCount == 0) return "media.split.nothingRemoved";
        return null;
    }

    public Dictionary<string, object?> ToOptions(bool reencode, bool joinRest) => new()
    {
        ["points"] = string.Join(", ", _points.Select(MediaTimeText.ForOption)),
        ["segment"] = 0,
        ["reencode"] = reencode,
        ["remove"] = string.Join(", ", Clips.Where(c => c.Removed).Select(c => c.Number.ToString(CultureInfo.InvariantCulture))),
        ["output"] = joinRest && RemovedCount > 0 ? "join" : "clips",
    };
}

/// <summary>Speed: the factor, with snapping to the values people mean.</summary>
internal static class MediaSpeed
{
    public static readonly double[] Snaps = [0.25, 0.5, 0.75, 1, 1.25, 1.5, 2, 3, 4];

    /// <summary>Snaps to a common factor within 0.06, otherwise rounds to 0.05; always 0.25..4.</summary>
    public static double Snap(double value)
    {
        value = Math.Clamp(value, 0.25, 4);
        foreach (var s in Snaps)
            if (Math.Abs(value - s) <= 0.06) return s;
        return Math.Round(value * 20) / 20;
    }

    public static double NewLength(double duration, double speed) => duration / speed;

    public static string Label(double speed) => speed.ToString("0.##", CultureInfo.InvariantCulture) + "×";
}

/// <summary>Save frames: the chosen times.</summary>
internal sealed class MediaFrameList(double duration, double frameStep)
{
    private readonly List<double> _times = [];

    public IReadOnlyList<double> Times => _times;

    /// <summary>Adds a time (not past the end; a frame already in the list is not added twice). Returns false when nothing was added.</summary>
    public bool Add(double t)
    {
        t = Math.Clamp(t, 0, Math.Max(0, duration - frameStep / 2));
        if (_times.Any(x => Math.Abs(x - t) < frameStep / 2)) return false;
        var i = _times.BinarySearch(t);
        _times.Insert(i < 0 ? ~i : i, t);
        return true;
    }

    public void Remove(int index) => _times.RemoveAt(index);

    public Dictionary<string, object?> ToOptions(double every) => new()
    {
        ["times"] = string.Join(", ", _times.Select(MediaTimeText.ForOption)),
        ["every"] = Math.Max(0, every),
    };
}

/// <summary>Which part of a bleep range a pointer is over.</summary>
internal enum BleepPart { None, Body, StartEdge, EndEdge }

/// <summary>Bleep: the ranges to beep over, drawn on a lane above the waveform.</summary>
internal sealed class MediaBleepModel(double duration)
{
    private readonly List<MediaSpan> _ranges = [];

    public double Duration { get; } = duration;
    public IReadOnlyList<MediaSpan> Ranges => _ranges;
    public int Selected { get; set; } = -1;

    /// <summary>Adds a range (kept inside the file, at least 0.1 s) and selects it. Returns its index.</summary>
    public int Add(double start, double end)
    {
        var span = MediaEditMath.Clamp(start, end, Duration);
        var i = 0;
        while (i < _ranges.Count && _ranges[i].Start < span.Start) i++;
        _ranges.Insert(i, span);
        Selected = i;
        return i;
    }

    /// <summary>A half-second range starting at the playhead (or ending at the file's end).</summary>
    public int AddAt(double t, double length = 0.5) => Add(Math.Min(t, Duration - length), Math.Min(t, Duration - length) + length);

    public void Replace(int index, double start, double end)
    {
        _ranges[index] = MediaEditMath.Clamp(start, end, Duration);
        var span = _ranges[index];
        _ranges.Sort((a, b) => a.Start.CompareTo(b.Start));
        Selected = _ranges.IndexOf(span);
    }

    /// <summary>Moves a whole range by <paramref name="delta"/> seconds without changing its length.</summary>
    public void Move(int index, double newStart)
    {
        var length = _ranges[index].Length;
        var start = Math.Clamp(newStart, 0, Duration - length);
        Replace(index, start, start + length);
    }

    public void Delete(int index)
    {
        if (index < 0 || index >= _ranges.Count) return;
        _ranges.RemoveAt(index);
        Selected = -1;
    }

    /// <summary>The range and part under <paramref name="t"/>; edges win within <paramref name="edge"/> seconds.</summary>
    public (int Index, BleepPart Part) HitTest(double t, double edge)
    {
        for (var i = _ranges.Count - 1; i >= 0; i--)
        {
            var r = _ranges[i];
            if (Math.Abs(t - r.Start) <= edge) return (i, BleepPart.StartEdge);
            if (Math.Abs(t - r.End) <= edge) return (i, BleepPart.EndEdge);
            if (r.Contains(t)) return (i, BleepPart.Body);
        }
        return (-1, BleepPart.None);
    }

    /// <summary>The aud.bleep "ranges" text: "1.5-2.3; 10-11" (overlaps are merged by the tool).</summary>
    public string RangesText => string.Join("; ", _ranges.Select(r => $"{MediaTimeText.ForOption(r.Start)}-{MediaTimeText.ForOption(r.End)}"));

    public Dictionary<string, object?> ToOptions(double frequency, double volume) => new()
    {
        ["ranges"] = RangesText,
        ["frequency"] = Math.Round(frequency),
        ["volume"] = Math.Round(volume, 2),
    };
}

/// <summary>A rectangle in displayed-picture pixels.</summary>
internal readonly record struct PixelBox(double X, double Y, double W, double H)
{
    public double Right => X + W;
    public double Bottom => Y + H;
}

/// <summary>Which handle of a box the pointer holds.</summary>
internal enum BoxHandle { None, Move, Left, Top, Right, Bottom, TopLeft, TopRight, BottomLeft, BottomRight }

/// <summary>Moving and resizing a box inside a frame, optionally keeping an aspect ratio. Shared by crop and redact.</summary>
internal static class BoxMath
{
    public const double MinSide = 16;

    /// <summary>The largest centred box of aspect <paramref name="aw"/>:<paramref name="ah"/> in the frame.</summary>
    public static PixelBox CenteredAspect(double frameW, double frameH, double aw, double ah)
    {
        var target = aw / ah;
        double w = frameW, h = frameH;
        if (frameW / frameH > target) w = frameH * target;
        else h = frameW / target;
        return new PixelBox((frameW - w) / 2, (frameH - h) / 2, w, h);
    }

    /// <summary>The box after dragging <paramref name="handle"/> by (dx, dy), kept inside the frame and at least <see cref="MinSide"/>.</summary>
    public static PixelBox Drag(PixelBox start, BoxHandle handle, double dx, double dy, double frameW, double frameH, double? aspect = null)
    {
        if (handle == BoxHandle.Move)
            return start with { X = Math.Clamp(start.X + dx, 0, frameW - start.W), Y = Math.Clamp(start.Y + dy, 0, frameH - start.H) };

        double l = start.X, t = start.Y, r = start.Right, b = start.Bottom;
        if (handle is BoxHandle.Left or BoxHandle.TopLeft or BoxHandle.BottomLeft) l = Math.Clamp(l + dx, 0, r - MinSide);
        if (handle is BoxHandle.Right or BoxHandle.TopRight or BoxHandle.BottomRight) r = Math.Clamp(r + dx, l + MinSide, frameW);
        if (handle is BoxHandle.Top or BoxHandle.TopLeft or BoxHandle.TopRight) t = Math.Clamp(t + dy, 0, b - MinSide);
        if (handle is BoxHandle.Bottom or BoxHandle.BottomLeft or BoxHandle.BottomRight) b = Math.Clamp(b + dy, t + MinSide, frameH);

        if (aspect is { } a && a > 0)
        {
            var w = r - l;
            var h = b - t;
            var horizontalOnly = handle is BoxHandle.Left or BoxHandle.Right;
            var verticalOnly = handle is BoxHandle.Top or BoxHandle.Bottom;
            if (horizontalOnly) h = w / a;
            else if (verticalOnly) w = h * a;
            else if (w / h > a) w = h * a;
            else h = w / a;

            // keep the side opposite the handle fixed and stay inside the frame
            var anchorRight = handle is BoxHandle.Left or BoxHandle.TopLeft or BoxHandle.BottomLeft;
            var anchorBottom = handle is BoxHandle.Top or BoxHandle.TopLeft or BoxHandle.TopRight;
            var maxW = anchorRight ? start.Right : frameW - start.X;
            var maxH = anchorBottom ? start.Bottom : frameH - start.Y;
            if (w > maxW) { w = maxW; h = w / a; }
            if (h > maxH) { h = maxH; w = h * a; }
            l = anchorRight ? start.Right - w : start.X;
            t = anchorBottom ? start.Bottom - h : start.Y;
            r = l + w;
            b = t + h;
        }
        return new PixelBox(l, t, r - l, b - t);
    }

    /// <summary>The box's handle under a point, given a grab margin in the same pixels.</summary>
    public static BoxHandle HitTest(PixelBox box, double x, double y, double margin)
    {
        bool Near(double a, double b) => Math.Abs(a - b) <= margin;
        var insideX = x >= box.X - margin && x <= box.Right + margin;
        var insideY = y >= box.Y - margin && y <= box.Bottom + margin;
        if (!insideX || !insideY) return BoxHandle.None;
        var left = Near(x, box.X); var right = Near(x, box.Right); var top = Near(y, box.Y); var bottom = Near(y, box.Bottom);
        if (top && left) return BoxHandle.TopLeft;
        if (top && right) return BoxHandle.TopRight;
        if (bottom && left) return BoxHandle.BottomLeft;
        if (bottom && right) return BoxHandle.BottomRight;
        if (left) return BoxHandle.Left;
        if (right) return BoxHandle.Right;
        if (top) return BoxHandle.Top;
        if (bottom) return BoxHandle.Bottom;
        return x > box.X && x < box.Right && y > box.Y && y < box.Bottom ? BoxHandle.Move : BoxHandle.None;
    }
}

/// <summary>Crop: one box in displayed-picture pixels, with an optional aspect preset.</summary>
internal sealed class MediaCropModel
{
    public static readonly string[] Aspects = ["free", "1:1", "4:5", "3:4", "4:3", "16:9", "9:16", "21:9"];

    public MediaCropModel(int frameWidth, int frameHeight)
    {
        FrameWidth = frameWidth;
        FrameHeight = frameHeight;
        SetAspect("1:1");
    }

    public int FrameWidth { get; }
    public int FrameHeight { get; }
    public string Aspect { get; private set; } = "free";
    public PixelBox Box { get; private set; }

    public double? AspectRatio => Aspect == "free" ? null : AspectOf(Aspect);

    private static double AspectOf(string aspect)
    {
        var parts = aspect.Split(':');
        return double.Parse(parts[0], CultureInfo.InvariantCulture) / double.Parse(parts[1], CultureInfo.InvariantCulture);
    }

    /// <summary>A preset centres the largest box of that shape; "free" keeps the box and drops the constraint.</summary>
    public void SetAspect(string aspect)
    {
        Aspect = Aspects.Contains(aspect) ? aspect : "free";
        if (Aspect == "free")
        {
            if (Box.W <= 0) Box = new PixelBox(0, 0, FrameWidth, FrameHeight);
            return;
        }
        var parts = Aspect.Split(':');
        Box = BoxMath.CenteredAspect(FrameWidth, FrameHeight, double.Parse(parts[0], CultureInfo.InvariantCulture), double.Parse(parts[1], CultureInfo.InvariantCulture));
    }

    public void Drag(PixelBox start, BoxHandle handle, double dx, double dy) =>
        Box = BoxMath.Drag(start, handle, dx, dy, FrameWidth, FrameHeight, AspectRatio);

    /// <summary>Typed pixel values; kept inside the frame (a preset is dropped, since typed numbers rarely keep it exactly).</summary>
    public void SetPixels(double x, double y, double w, double h)
    {
        Aspect = "free";
        w = Math.Clamp(w, BoxMath.MinSide, FrameWidth);
        h = Math.Clamp(h, BoxMath.MinSide, FrameHeight);
        Box = new PixelBox(Math.Clamp(x, 0, FrameWidth - w), Math.Clamp(y, 0, FrameHeight - h), w, h);
    }

    /// <summary>Whole even pixels, as vid.crop wants them, never outside the frame.</summary>
    public (int X, int Y, int W, int H) EvenPixels()
    {
        static int Even(double v) => Math.Max(0, (int)Math.Floor(v) & ~1);
        var x = Even(Box.X);
        var y = Even(Box.Y);
        var w = Math.Max(2, Math.Min(Even(Box.W), (FrameWidth - x) & ~1));
        var h = Math.Max(2, Math.Min(Even(Box.H), (FrameHeight - y) & ~1));
        return (x, y, w, h);
    }

    public bool IsWholeFrame => Box.X < 1 && Box.Y < 1 && Box.W > FrameWidth - 2 && Box.H > FrameHeight - 2;

    /// <summary>vid.crop with aspect "custom": the exact box the user placed (a preset only shapes it).</summary>
    public Dictionary<string, object?> ToOptions()
    {
        var (x, y, w, h) = EvenPixels();
        return new() { ["aspect"] = "custom", ["x"] = x, ["y"] = y, ["w"] = w, ["h"] = h };
    }
}

/// <summary>One area hidden over a stretch of time. Position and size are fractions (0..1) of the displayed picture.</summary>
internal sealed record RedactBox(double X, double Y, double W, double H, double? Start, double? End)
{
    public bool WholeVideo => Start is null && End is null;
    public bool VisibleAt(double t) => (Start is null || t >= Start) && (End is null || t <= End);
}

/// <summary>
/// Redact: several boxes, each with its own time range. The option format sent to vid.redact is documented on <see cref="BoxesJson"/>.
/// </summary>
internal sealed class MediaRedactModel(double duration, int frameWidth, int frameHeight)
{
    private readonly List<RedactBox> _boxes = [];

    public double Duration { get; } = duration;
    public int FrameWidth { get; } = frameWidth;
    public int FrameHeight { get; } = frameHeight;
    public IReadOnlyList<RedactBox> Boxes => _boxes;
    public int Selected { get; set; } = -1;

    /// <summary>Adds a box from displayed-picture pixels, for the whole video, and selects it.</summary>
    public int Add(PixelBox pixels)
    {
        _boxes.Add(FromPixels(pixels, null, null));
        Selected = _boxes.Count - 1;
        return Selected;
    }

    public RedactBox FromPixels(PixelBox p, double? start, double? end) =>
        new(Math.Clamp(p.X / FrameWidth, 0, 1), Math.Clamp(p.Y / FrameHeight, 0, 1), Math.Clamp(p.W / FrameWidth, 0, 1), Math.Clamp(p.H / FrameHeight, 0, 1), start, end);

    public PixelBox ToPixels(RedactBox b) => new(b.X * FrameWidth, b.Y * FrameHeight, b.W * FrameWidth, b.H * FrameHeight);

    public void SetPixels(int index, PixelBox pixels)
    {
        var b = _boxes[index];
        _boxes[index] = FromPixels(pixels, b.Start, b.End);
    }

    /// <summary>Sets the time range; both null means the whole video. Kept inside the file and in order.</summary>
    public void SetTimes(int index, double? start, double? end)
    {
        if (start is not null || end is not null)
        {
            var span = MediaEditMath.Clamp(start ?? 0, end ?? Duration, Duration);
            start = span.Start;
            end = span.End;
        }
        _boxes[index] = _boxes[index] with { Start = start, End = end };
    }

    public void Delete(int index)
    {
        if (index < 0 || index >= _boxes.Count) return;
        _boxes.RemoveAt(index);
        Selected = Math.Min(Selected, _boxes.Count - 1);
    }

    /// <summary>
    /// The multi-box option for <c>vid.redact</c> (key <c>boxes</c>): a JSON list such as
    /// <c>[{"x":0.1,"y":0.2,"w":0.3,"h":0.1,"start":2.5,"end":7}]</c>. x, y, w, h are fractions (0..1) of the displayed picture;
    /// start and end are seconds of the original file, and a missing (null) start or end means from the beginning or to the end.
    /// </summary>
    public string BoxesJson()
    {
        var items = _boxes.Select(b => new Dictionary<string, object?>
        {
            ["x"] = Math.Round(b.X, 5), ["y"] = Math.Round(b.Y, 5), ["w"] = Math.Round(b.W, 5), ["h"] = Math.Round(b.H, 5),
            ["start"] = b.Start is { } s ? Math.Round(s, 3) : null,
            ["end"] = b.End is { } e ? Math.Round(e, 3) : null,
        });
        return JsonSerializer.Serialize(items);
    }

    /// <summary>
    /// The options for vid.redact, or a problem key. <paramref name="supportsBoxes"/> says whether the installed converter declares
    /// <c>boxes</c>; when it does not (single-box version) only one box can be sent, as pixels, and more boxes are refused rather than
    /// silently dropped (hiding fewer areas than the user drew would be a privacy failure).
    /// </summary>
    public (Dictionary<string, object?>? Options, string? Problem) ToOptions(string mode, double strength, string color, bool supportsBoxes)
    {
        if (_boxes.Count == 0) return (null, "media.redact.needBox");
        if (!supportsBoxes && _boxes.Count > 1) return (null, "media.redact.oneBoxOnly");

        var o = new Dictionary<string, object?>
        {
            ["mode"] = mode,
            ["strength"] = (int)Math.Round(strength),
            ["color"] = color,
        };
        if (supportsBoxes) o["boxes"] = BoxesJson();
        if (_boxes.Count == 1)
        {
            var b = _boxes[0];
            var p = ToPixels(b);
            var x = (int)Math.Floor(p.X); var y = (int)Math.Floor(p.Y);
            o["x"] = x; o["y"] = y;
            o["w"] = Math.Max(2, Math.Min((int)Math.Ceiling(p.W), FrameWidth - x));
            o["h"] = Math.Max(2, Math.Min((int)Math.Ceiling(p.H), FrameHeight - y));
            o["start"] = b.Start is { } s ? MediaTimeText.ForOption(s) : "";
            o["end"] = b.End is { } e ? MediaTimeText.ForOption(e) : "";
        }
        return (o, null);
    }
}

/// <summary>Join / merge: the order of the files.</summary>
internal sealed class MediaOrderModel(IReadOnlyList<string> files)
{
    private readonly List<string> _files = [.. files];

    public IReadOnlyList<string> Files => _files;

    public void Move(int from, int to)
    {
        if (from < 0 || from >= _files.Count) return;
        to = Math.Clamp(to, 0, _files.Count - 1);
        var item = _files[from];
        _files.RemoveAt(from);
        _files.Insert(to, item);
    }
}
