using DropFlow.Engines.Pdf.Infrastructure;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Security;

namespace DropFlow.Engines.Pdf.Tests;

/// <summary>
/// Builds test PDFs with real fonts (Arial from Windows) so sizes, bold text, tables and Arabic can be controlled exactly. The
/// production code never uses this builder, and the files are read back with PdfPig / PDFium, which are different libraries.
/// </summary>
internal sealed class PdfBuilder
{
    private readonly PdfDocument _document = new();
    private readonly List<PageBuilder> _pages = [];
    private readonly List<XImage> _images = [];

    public PdfBuilder()
    {
        PdfSharpSupport.EnsureFonts();
    }

    public PageBuilder AddPage(double width = 612, double height = 792, int rotate = 0)
    {
        var page = _document.AddPage();
        page.Width = XUnitPt.FromPoint(width);
        page.Height = XUnitPt.FromPoint(height);
        if (rotate != 0) page.Rotate = rotate;
        var builder = new PageBuilder(page, _images);
        _pages.Add(builder);
        return builder;
    }

    public PdfBuilder Title(string title)
    {
        _document.Info.Title = title;
        return this;
    }

    public PdfBuilder Encrypt(string userPassword, string ownerPassword = "owner-secret", bool allowAssemble = true, bool allowExtract = true)
    {
        _document.SecuritySettings.PermitAssembleDocument = allowAssemble;
        _document.SecuritySettings.PermitExtractContent = allowExtract;
        _document.SecuritySettings.UserPassword = userPassword;
        _document.SecuritySettings.OwnerPassword = ownerPassword;
        return this;
    }

    public string Save(string path)
    {
        foreach (var page in _pages) page.Dispose(); // flushes the drawn content into the page
        _document.Save(path);
        foreach (var image in _images) image.Dispose();
        return path;
    }

    public static bool FontsAvailable()
    {
        try
        {
            PdfSharpSupport.EnsureFonts();
            _ = new XFont("Arial", 12);
            return true;
        }
        catch
        {
            return false;
        }
    }

    internal sealed class PageBuilder : IDisposable
    {
        private readonly XGraphics _gfx;
        private readonly PdfPage _page;

        private readonly List<XImage> _images;

        public PageBuilder(PdfPage page, List<XImage> images)
        {
            _page = page;
            _images = images;
            _gfx = XGraphics.FromPdfPage(page);
        }

        public void Dispose() => _gfx.Dispose();

        /// <summary>Places a picture file (PNG/JPEG) on the page.</summary>
        public PageBuilder Image(string path, double x, double y, double width, double height)
        {
            var image = XImage.FromFile(path);
            _images.Add(image);
            _gfx.DrawImage(image, x, y, width, height);
            return this;
        }

        /// <summary>Draws one line of text with its baseline <paramref name="baseline"/> points from the top of the page.</summary>
        public PageBuilder Text(string text, double x, double baseline, double size = 12, bool bold = false, bool italic = false)
        {
            var style = (bold ? XFontStyleEx.Bold : XFontStyleEx.Regular) | (italic ? XFontStyleEx.Italic : XFontStyleEx.Regular);
            _gfx.DrawString(text, new XFont("Arial", size, style), XBrushes.Black, new XPoint(x, baseline), XStringFormats.BaseLineLeft);
            return this;
        }

        /// <summary>Text running upwards on the sheet (rotated 90 degrees counter-clockwise): reads horizontally once the page has /Rotate 90.</summary>
        public PageBuilder TextAlongY(string text, double x, double baseline, double size = 12)
        {
            var state = _gfx.Save();
            _gfx.RotateAtTransform(-90, new XPoint(x, baseline));
            _gfx.DrawString(text, new XFont("Arial", size, XFontStyleEx.Regular), XBrushes.Black, new XPoint(x, baseline), XStringFormats.BaseLineLeft);
            _gfx.Restore(state);
            return this;
        }

        /// <summary>Arabic/RTL text the way a real producer lays it out: shaped glyphs in visual order, right edge at <paramref name="rightEdge"/>.</summary>
        public PageBuilder Arabic(string logicalText, double rightEdge, double baseline, double size = 14)
        {
            var visual = ArabicShaper.ToVisual(logicalText);
            var font = new XFont("Arial", size, XFontStyleEx.Regular);
            var width = _gfx.MeasureString(visual, font).Width;
            _gfx.DrawString(visual, font, XBrushes.Black, new XPoint(rightEdge - width, baseline), XStringFormats.BaseLineLeft);
            return this;
        }

        /// <summary>A grid of cells, one text per cell, columns starting at the given x positions.</summary>
        public PageBuilder Table(string[][] rows, double[] columnX, double firstBaseline, double rowHeight = 18, double size = 11)
        {
            for (var r = 0; r < rows.Length; r++)
                for (var c = 0; c < rows[r].Length; c++)
                    Text(rows[r][c], columnX[c], firstBaseline + r * rowHeight, size);
            return this;
        }
    }
}
