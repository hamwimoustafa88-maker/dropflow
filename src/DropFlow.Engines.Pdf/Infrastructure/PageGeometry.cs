using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace DropFlow.Engines.Pdf.Infrastructure;

/// <summary>
/// Where a page really is, in the two coordinate systems that matter. <b>User space</b> is the PDF's own (origin bottom-left, y up,
/// before /Rotate). <b>Visual space</b> is what the reader sees: origin top-left, y down, after /Rotate and after the CropBox, in points.
/// Every tool that takes a position from the user ("top margin", "bottom-right corner", "30 % from the left") speaks visual space, so the
/// mapping lives here once and is tested for all four rotations.
/// </summary>
internal readonly record struct PageGeometry(double X1, double Y1, double X2, double Y2, int Rotate, double PdfSharpHeight)
{
    public const double PointsPerMm = 72.0 / 25.4;

    /// <summary>The box the reader shows, in user space: the CropBox clipped to the MediaBox (the MediaBox when there is no CropBox).</summary>
    public double BoxWidth => X2 - X1;

    public double BoxHeight => Y2 - Y1;

    public double VisualWidth => Rotate is 90 or 270 ? BoxHeight : BoxWidth;

    public double VisualHeight => Rotate is 90 or 270 ? BoxWidth : BoxHeight;

    public static double MmToPoints(double mm) => mm * PointsPerMm;

    public static double PointsToMm(double points) => points / PointsPerMm;

    public static PageGeometry Of(PdfPage page)
    {
        var media = Normalise(page.MediaBox) ?? (0, 0, 612, 792);
        var visible = media;
        // Reading page.CropBox on a page without one would create the entry, so look for the key first.
        if (page.Elements.ContainsKey("/CropBox") && Normalise(page.CropBox) is { } crop)
        {
            var (cx1, cy1, cx2, cy2) = (Math.Max(crop.X1, media.X1), Math.Max(crop.Y1, media.Y1), Math.Min(crop.X2, media.X2), Math.Min(crop.Y2, media.Y2));
            if (cx2 - cx1 > 0 && cy2 - cy1 > 0) visible = (cx1, cy1, cx2, cy2);
        }

        var rotate = ((page.Rotate % 360) + 360) % 360;
        if (rotate % 90 != 0) rotate = 0;
        return new PageGeometry(visible.X1, visible.Y1, visible.X2, visible.Y2, rotate, page.Height.Point);
    }

    private static (double X1, double Y1, double X2, double Y2)? Normalise(PdfRectangle rectangle)
    {
        var (x1, x2) = (Math.Min(rectangle.X1, rectangle.X2), Math.Max(rectangle.X1, rectangle.X2));
        var (y1, y2) = (Math.Min(rectangle.Y1, rectangle.Y2), Math.Max(rectangle.Y1, rectangle.Y2));
        return x2 - x1 > 0 && y2 - y1 > 0 ? (x1, y1, x2, y2) : null;
    }

    /// <summary>A point given as (right, down) from the top-left corner of the page as displayed, converted to user space.</summary>
    public (double X, double Y) VisualToUser(double vx, double vy) => Rotate switch
    {
        90 => (X1 + vy, Y1 + vx),
        180 => (X2 - vx, Y1 + vy),
        270 => (X2 - vy, Y2 - vx),
        _ => (X1 + vx, Y2 - vy),
    };

    /// <summary>The user-space rectangle (x1, y1 lower-left; x2, y2 upper-right) that covers a visual rectangle.</summary>
    public (double X1, double Y1, double X2, double Y2) UserRect(double vx, double vy, double vw, double vh)
    {
        var a = VisualToUser(vx, vy);
        var b = VisualToUser(vx + vw, vy + vh);
        return (Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
    }

    /// <summary>
    /// The matrix that makes an <see cref="XGraphics"/> opened on this page draw in visual space. PDFsharp draws in the unrotated page,
    /// y down, and flips with the page height only (it ignores a MediaBox that does not start at 0,0), so that is undone here too.
    /// </summary>
    public XMatrix VisualToGraphics()
    {
        var self = this;
        (double X, double Y) ToGraphics(double vx, double vy)
        {
            var user = self.VisualToUser(vx, vy);
            return (user.X, self.PdfSharpHeight - user.Y);
        }

        var origin = ToGraphics(0, 0);
        var right = ToGraphics(1, 0);
        var down = ToGraphics(0, 1);
        return new XMatrix(right.X - origin.X, right.Y - origin.Y, down.X - origin.X, down.Y - origin.Y, origin.X, origin.Y);
    }
}
