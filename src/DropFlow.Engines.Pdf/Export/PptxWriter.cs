using System.Globalization;
using System.Text;
using System.Xml;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DropFlow.Engines.Pdf.Extraction;
using DropFlow.Engines.Pdf.Infrastructure;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;

namespace DropFlow.Engines.Pdf.Export;

/// <summary>
/// One slide per PDF page: the page drawn by PDFium fills the slide (so it looks like the PDF) and the extracted text sits on top in
/// invisible text boxes at the original positions, so the slide text can be searched, selected and copied. A slide is exactly the
/// size of its page, so a portrait PDF becomes portrait slides.
/// </summary>
internal static class PptxWriter
{
    private const string NsP = "http://schemas.openxmlformats.org/presentationml/2006/main";
    private const string NsA = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private const string NsR = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const double EmuPerPoint = 12700;
    private const long MinSlideEmu = 914_400;      // 1 inch
    private const long MaxSlideEmu = 51_206_400;   // 56 inches: PowerPoint's limit

    public const double PictureDpi = 150;
    public const int PictureQuality = 80;

    public static void Write(string pdfPath, PdfTextDocument source, string outputPath, string title, CancellationToken ct, Action<double>? progress = null)
    {
        using var pdfium = PdfiumDocument.Open(pdfPath);
        using var package = PresentationDocument.Create(outputPath, PresentationDocumentType.Presentation);
        var presentationPart = package.AddPresentationPart();

        var masterPart = presentationPart.AddNewPart<SlideMasterPart>();
        var themePart = masterPart.AddNewPart<ThemePart>();
        themePart.Theme = new A.Theme(ThemeXml);
        presentationPart.AddPart(themePart);

        var layoutPart = masterPart.AddNewPart<SlideLayoutPart>();
        layoutPart.SlideLayout = new P.SlideLayout(LayoutXml);
        layoutPart.AddPart(masterPart);
        masterPart.SlideMaster = new P.SlideMaster(MasterXml(masterPart.GetIdOfPart(layoutPart)));
        var masterId = presentationPart.GetIdOfPart(masterPart);

        var slideIds = new StringBuilder();
        long firstWidth = 0, firstHeight = 0;
        for (var i = 0; i < pdfium.PageCount; i++)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Invoke((double)i / pdfium.PageCount);

            var (widthPoints, heightPoints) = pdfium.PageSize(i);
            var (cx, cy) = SlideSize(widthPoints, heightPoints);
            if (i == 0) (firstWidth, firstHeight) = (cx, cy);

            var rendered = pdfium.Render(i, PictureDpi);
            var jpeg = RasterEncoder.ToJpeg(rendered, PictureQuality);

            var slidePart = presentationPart.AddNewPart<SlidePart>();
            slidePart.AddPart(layoutPart);
            var imagePart = slidePart.AddImagePart(ImagePartType.Jpeg);
            using (var stream = new MemoryStream(jpeg)) imagePart.FeedData(stream);

            var lines = i < source.Pages.Count ? source.Pages[i].Lines : [];
            slidePart.Slide = new P.Slide(SlideXml(i + 1, cx, cy, slidePart.GetIdOfPart(imagePart), lines, heightPoints));
            slideIds.Append($"<p:sldId id=\"{256 + i}\" r:id=\"{presentationPart.GetIdOfPart(slidePart)}\"/>");
        }

        presentationPart.Presentation = new P.Presentation(
            $"<p:presentation xmlns:a=\"{NsA}\" xmlns:r=\"{NsR}\" xmlns:p=\"{NsP}\" saveSubsetFonts=\"1\">" +
            $"<p:sldMasterIdLst><p:sldMasterId id=\"2147483648\" r:id=\"{masterId}\"/></p:sldMasterIdLst>" +
            $"<p:sldIdLst>{slideIds}</p:sldIdLst>" +
            $"<p:sldSz cx=\"{firstWidth}\" cy=\"{firstHeight}\"/>" +
            "<p:notesSz cx=\"6858000\" cy=\"9144000\"/></p:presentation>");

        package.PackageProperties.Title = title;
        package.PackageProperties.Creator = source.Author ?? "DropFlow";
        package.PackageProperties.Description = "Converted from PDF by DropFlow. Each slide shows one PDF page; the text is searchable but not editable.";
        package.PackageProperties.Created = DateTime.UtcNow;
        package.PackageProperties.Modified = DateTime.UtcNow;
    }

    internal static (long Cx, long Cy) SlideSize(double widthPoints, double heightPoints)
    {
        double cx = widthPoints * EmuPerPoint, cy = heightPoints * EmuPerPoint;
        var shrink = Math.Min(1.0, MaxSlideEmu / Math.Max(cx, cy));
        var grow = Math.Max(1.0, MinSlideEmu / Math.Min(cx, cy));
        var scale = shrink < 1.0 ? shrink : grow;
        return ((long)Math.Round(cx * scale), (long)Math.Round(cy * scale));
    }

    private static string SlideXml(int number, long cx, long cy, string imageRelationshipId, IReadOnlyList<TextLine> lines, double pageHeightPoints)
    {
        var sb = new StringBuilder();
        sb.Append($"<p:sld xmlns:a=\"{NsA}\" xmlns:r=\"{NsR}\" xmlns:p=\"{NsP}\"><p:cSld><p:spTree>");
        sb.Append("<p:nvGrpSpPr><p:cNvPr id=\"1\" name=\"\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr>");
        sb.Append("<p:grpSpPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"0\" cy=\"0\"/><a:chOff x=\"0\" y=\"0\"/><a:chExt cx=\"0\" cy=\"0\"/></a:xfrm></p:grpSpPr>");

        sb.Append("<p:pic><p:nvPicPr>");
        sb.Append($"<p:cNvPr id=\"2\" name=\"Page {number}\" descr=\"PDF page {number}\"/>");
        sb.Append("<p:cNvPicPr><a:picLocks noChangeAspect=\"1\"/></p:cNvPicPr><p:nvPr/></p:nvPicPr>");
        sb.Append($"<p:blipFill><a:blip r:embed=\"{imageRelationshipId}\"/><a:stretch><a:fillRect/></a:stretch></p:blipFill>");
        sb.Append($"<p:spPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"{cx}\" cy=\"{cy}\"/></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></p:spPr></p:pic>");

        // The slide may have been scaled to fit PowerPoint's limits, so text positions use the same factor as the picture.
        var scale = cy / (pageHeightPoints * EmuPerPoint);
        var id = 3;
        foreach (var line in lines)
        {
            var size = Math.Clamp(line.FontSize, 4, 400);
            var x = (long)Math.Round(line.Left * EmuPerPoint * scale);
            var y = (long)Math.Round((pageHeightPoints - line.BaseY - 0.8 * size) * EmuPerPoint * scale);
            var w = (long)Math.Round((line.Right - line.Left + 2) * EmuPerPoint * scale);
            var h = (long)Math.Round(size * 1.25 * EmuPerPoint * scale);
            var fontSize = (int)Math.Round(size * scale * 100);

            sb.Append($"<p:sp><p:nvSpPr><p:cNvPr id=\"{id++}\" name=\"Text {id - 3}\"/><p:cNvSpPr txBox=\"1\"/><p:nvPr/></p:nvSpPr>");
            sb.Append($"<p:spPr><a:xfrm><a:off x=\"{Math.Max(0, x)}\" y=\"{Math.Max(0, y)}\"/><a:ext cx=\"{Math.Max(1, w)}\" cy=\"{Math.Max(1, h)}\"/></a:xfrm>");
            sb.Append("<a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom><a:noFill/></p:spPr>");
            sb.Append("<p:txBody><a:bodyPr wrap=\"none\" lIns=\"0\" tIns=\"0\" rIns=\"0\" bIns=\"0\" rtlCol=\"0\" anchor=\"t\"><a:noAutofit/></a:bodyPr><a:lstStyle/>");
            sb.Append($"<a:p><a:pPr algn=\"{(line.Rtl ? "r" : "l")}\" rtl=\"{(line.Rtl ? 1 : 0)}\"/>");
            sb.Append($"<a:r><a:rPr lang=\"{(line.Rtl ? "ar-SA" : "en-US")}\" sz=\"{Math.Clamp(fontSize, 100, 400000)}\" b=\"{(line.Bold ? 1 : 0)}\" i=\"{(line.Italic ? 1 : 0)}\" dirty=\"0\">");
            sb.Append("<a:solidFill><a:srgbClr val=\"000000\"><a:alpha val=\"0\"/></a:srgbClr></a:solidFill>");
            sb.Append("<a:latin typeface=\"Arial\"/><a:cs typeface=\"Arial\"/></a:rPr>");
            sb.Append($"<a:t>{Escape(line.Text)}</a:t></a:r></a:p></p:txBody></p:sp>");
        }

        sb.Append("</p:spTree></p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:sld>");
        return sb.ToString();
    }

    private static string Escape(string text)
    {
        var clean = new StringBuilder(text.Length);
        foreach (var c in text) if (XmlConvert.IsXmlChar(c) || char.IsSurrogate(c)) clean.Append(c);
        return System.Security.SecurityElement.Escape(clean.ToString()) ?? "";
    }

    // ---- fixed parts ----------------------------------------------------------------------------------------------------

    private const string EmptyTree =
        "<p:spTree><p:nvGrpSpPr><p:cNvPr id=\"1\" name=\"\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr>" +
        "<p:grpSpPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"0\" cy=\"0\"/><a:chOff x=\"0\" y=\"0\"/><a:chExt cx=\"0\" cy=\"0\"/></a:xfrm></p:grpSpPr></p:spTree>";

    private static string MasterXml(string layoutRelationshipId) =>
        $"<p:sldMaster xmlns:a=\"{NsA}\" xmlns:r=\"{NsR}\" xmlns:p=\"{NsP}\">" +
        "<p:cSld><p:bg><p:bgRef idx=\"1001\"><a:schemeClr val=\"bg1\"/></p:bgRef></p:bg>" + EmptyTree + "</p:cSld>" +
        "<p:clrMap bg1=\"lt1\" tx1=\"dk1\" bg2=\"lt2\" tx2=\"dk2\" accent1=\"accent1\" accent2=\"accent2\" accent3=\"accent3\" accent4=\"accent4\" accent5=\"accent5\" accent6=\"accent6\" hlink=\"hlink\" folHlink=\"folHlink\"/>" +
        $"<p:sldLayoutIdLst><p:sldLayoutId id=\"2147483649\" r:id=\"{layoutRelationshipId}\"/></p:sldLayoutIdLst>" +
        // PowerPoint is happiest with a master that declares its text styles, even when no slide uses a placeholder.
        "<p:txStyles><p:titleStyle><a:lvl1pPr algn=\"l\"><a:defRPr sz=\"4400\"/></a:lvl1pPr></p:titleStyle>" +
        "<p:bodyStyle><a:lvl1pPr algn=\"l\"><a:defRPr sz=\"2800\"/></a:lvl1pPr></p:bodyStyle>" +
        "<p:otherStyle><a:lvl1pPr algn=\"l\"><a:defRPr sz=\"1800\"/></a:lvl1pPr></p:otherStyle></p:txStyles></p:sldMaster>";

    private static readonly string LayoutXml =
        $"<p:sldLayout xmlns:a=\"{NsA}\" xmlns:r=\"{NsR}\" xmlns:p=\"{NsP}\" type=\"blank\" preserve=\"1\">" +
        "<p:cSld name=\"Blank\">" + EmptyTree + "</p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:sldLayout>";

    private static readonly string ThemeXml =
        $"<a:theme xmlns:a=\"{NsA}\" name=\"DropFlow\"><a:themeElements>" +
        "<a:clrScheme name=\"DropFlow\">" +
        "<a:dk1><a:sysClr val=\"windowText\" lastClr=\"000000\"/></a:dk1><a:lt1><a:sysClr val=\"window\" lastClr=\"FFFFFF\"/></a:lt1>" +
        "<a:dk2><a:srgbClr val=\"1F2937\"/></a:dk2><a:lt2><a:srgbClr val=\"F3F4F6\"/></a:lt2>" +
        "<a:accent1><a:srgbClr val=\"4F46E5\"/></a:accent1><a:accent2><a:srgbClr val=\"06B6D4\"/></a:accent2><a:accent3><a:srgbClr val=\"10B981\"/></a:accent3>" +
        "<a:accent4><a:srgbClr val=\"F59E0B\"/></a:accent4><a:accent5><a:srgbClr val=\"EF4444\"/></a:accent5><a:accent6><a:srgbClr val=\"8B5CF6\"/></a:accent6>" +
        "<a:hlink><a:srgbClr val=\"2563EB\"/></a:hlink><a:folHlink><a:srgbClr val=\"7C3AED\"/></a:folHlink></a:clrScheme>" +
        "<a:fontScheme name=\"DropFlow\"><a:majorFont><a:latin typeface=\"Arial\"/><a:ea typeface=\"\"/><a:cs typeface=\"Arial\"/></a:majorFont>" +
        "<a:minorFont><a:latin typeface=\"Arial\"/><a:ea typeface=\"\"/><a:cs typeface=\"Arial\"/></a:minorFont></a:fontScheme>" +
        "<a:fmtScheme name=\"DropFlow\">" +
        "<a:fillStyleLst><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill></a:fillStyleLst>" +
        "<a:lnStyleLst>" + Line + Line + Line + "</a:lnStyleLst>" +
        "<a:effectStyleLst><a:effectStyle><a:effectLst/></a:effectStyle><a:effectStyle><a:effectLst/></a:effectStyle><a:effectStyle><a:effectLst/></a:effectStyle></a:effectStyleLst>" +
        "<a:bgFillStyleLst><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill></a:bgFillStyleLst>" +
        "</a:fmtScheme></a:themeElements></a:theme>";

    private const string Line =
        "<a:ln w=\"9525\" cap=\"flat\" cmpd=\"sng\" algn=\"ctr\"><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill><a:prstDash val=\"solid\"/></a:ln>";
}
