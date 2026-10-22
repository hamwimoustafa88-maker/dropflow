using System.Globalization;
using DropFlow.Core.Operations;
using DropFlow.Engines.Pdf.Infrastructure;
using ImageMagick;
using ImageMagick.Drawing;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace DropFlow.Engines.Pdf.Tools;

/// <summary>
/// <c>pdf.sign</c>: puts a <b>visible</b> signature on chosen pages - a typed name in a handwriting-style font, or a picture of a
/// handwritten signature with its transparency kept. <b>This is not a cryptographic digital signature</b>: nothing is signed with a
/// certificate, the file is not locked, and the result cannot prove who signed it or that the document was left unchanged afterwards.
/// The successful result says so in its <see cref="ConversionResult.Note"/>.
/// Typed Latin names are rendered by Magick.NET from an installed Windows script font (Segoe Script, Lucida Handwriting, ...) because
/// PDFsharp can only use a handful of built-in font families; names with Arabic or other letters, and machines without such a font,
/// use Arial Italic drawn as real text (Arabic is shaped with <see cref="ArabicShaper"/>).
/// Positions are percentages of the page as displayed (rotation and CropBox applied); the signature never leaves the page.
/// </summary>
internal static class PdfSignTools
{
    public const string NotDigitalNote =
        "A visible signature was added: a picture or typed name placed on the page. It is not a digital signature - it has no certificate, " +
        "does not lock the document and cannot prove who signed or whether the file changed afterwards.";

    private const double Margin = 36;
    private const double DateFontSize = 8;
    private const double DateGap = 2;
    private const int MaxPictureSide = 1600;
    private const double MaxTextAspect = 0.5;

    private static readonly string[] Kinds = ["text", "image"];

    /// <summary>File names of handwriting-style fonts that ship with Windows or Office, best first.</summary>
    private static readonly string[] ScriptFontFiles = ["segoesc.ttf", "LHANDW.TTF", "Inkfree.ttf", "MTCORSVA.TTF", "BRUSHSCI.TTF", "segoepr.ttf"];

    private static readonly XColor Ink = XColor.FromArgb(16, 24, 96);

    public static readonly IReadOnlyList<ToolOption> Options =
    [
        new("kind", ToolOptionKind.Choice, "text", Kinds),
        new("text", ToolOptionKind.Text, ""),
        new("image", ToolOptionKind.Text, ""),
        new("position", ToolOptionKind.Choice, "bottom-right", PdfStampTools.Positions),
        new("x", ToolOptionKind.Number, null, Min: 0, Max: 100),
        new("y", ToolOptionKind.Number, null, Min: 0, Max: 100),
        new("width", ToolOptionKind.Number, 45, Min: 10, Max: 150),
        new("pages", ToolOptionKind.Range, "last"),
        new("stampDate", ToolOptionKind.Bool, false),
    ];

    public static IEnumerable<OperationSpec> Specs()
    {
        yield return OperationSpec.For("pdf.sign", "pdf", ctx => Task.FromResult(Sign(ctx)), Options);
    }

    private sealed record Plan(
        string Position, double? X, double? Y, double WidthPoints, bool Date, string DateText, byte[]? Picture, double PictureAspect, string? Name);

    private static ConversionResult Sign(JobContext ctx)
    {
        var kind = (ctx.Job.GetString("kind", "text") ?? "text").Trim().ToLowerInvariant();
        if (!Kinds.Contains(kind)) return ctx.Fail($"Unknown signature kind '{kind}'. Use text or image.");
        var position = (ctx.Job.GetString("position", "bottom-right") ?? "bottom-right").Trim().ToLowerInvariant();
        if (!PdfStampTools.Positions.Contains(position)) return ctx.Fail($"Unknown position '{position}'.");

        var x = Percent(ctx, "x");
        var y = Percent(ctx, "y");
        if (x is < 0 or > 100 || y is < 0 or > 100) return ctx.Fail("X and Y are percentages of the page, from 0 to 100.");
        var widthMm = ctx.Job.GetDouble("width", 45);
        if (!double.IsFinite(widthMm) || widthMm < 1 || widthMm > 300) return ctx.Fail("The signature width must be between 1 and 300 millimetres.");

        PdfSharpSupport.EnsureFonts();
        byte[]? picture = null;
        var aspect = 0.0;
        string? name = null;
        if (kind == "image")
        {
            var raw = ctx.Job.GetString("image")?.Trim().Trim('"').Trim();
            if (string.IsNullOrEmpty(raw)) return ctx.Fail("Choose the signature image (PNG or JPG).");
            if (!File.Exists(raw)) return ctx.Fail($"The signature image was not found: {raw}");
            (picture, aspect) = LoadPicture(raw);
        }
        else
        {
            name = ctx.Job.GetString("text")?.Trim();
            if (string.IsNullOrEmpty(name)) return ctx.Fail("Enter the name to sign with.");
            if (RenderScriptName(name) is { } rendered) (picture, aspect) = rendered;
        }

        var plan = new Plan(position, x, y, PageGeometry.MmToPoints(widthMm), ctx.Job.GetBool("stampDate"),
            DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), picture, aspect, name);

        using var document = PdfSharpSupport.OpenForModify(ctx.Input);
        var spec = ctx.Job.GetString("pages");
        if (!PageSpec.TryParse(string.IsNullOrWhiteSpace(spec) ? "last" : spec, document.PageCount, out var refs, out var error))
            return ctx.Fail(error);
        var targets = refs.Select(r => r.Page).Distinct().ToList();

        XImage? image = null;
        using var imageStream = picture is null ? null : new MemoryStream(picture);
        try
        {
            if (imageStream is not null) image = XImage.FromStream(imageStream);
            for (var i = 0; i < targets.Count; i++)
            {
                ctx.Ct.ThrowIfCancellationRequested();
                ctx.Report((double)i / targets.Count, $"Page {targets[i]}");
                Stamp(document.Pages[targets[i] - 1], plan, image);
            }

            var path = ctx.ReserveFile(ctx.Input, "pdf", "signed");
            document.Save(path);
            ctx.Report(1);
            return ctx.OkWithNote(NotDigitalNote, path);
        }
        finally
        {
            image?.Dispose();
        }
    }

    private static double? Percent(JobContext ctx, string key) =>
        ctx.Job.Opts.TryGetValue(key, out var value) && value is not null && !string.IsNullOrWhiteSpace(Convert.ToString(value, CultureInfo.InvariantCulture))
            ? ctx.Job.GetDouble(key)
            : null;

    // ---- drawing --------------------------------------------------------------------------------------------------------

    private static void Stamp(PdfPage page, Plan plan, XImage? image)
    {
        var geometry = PageGeometry.Of(page);
        double pageW = geometry.VisualWidth, pageH = geometry.VisualHeight;
        var targetWidth = Math.Min(plan.WidthPoints, pageW * 0.9);

        var dateFont = new XFont("Arial", DateFontSize, XFontStyleEx.Regular);
        using var measure = XGraphics.CreateMeasureContext(new XSize(100, 100), XGraphicsUnit.Point, XPageDirection.Downwards);
        var dateWidth = plan.Date ? measure.MeasureString(plan.DateText, dateFont).Width : 0;
        var dateHeight = plan.Date ? DateGap + dateFont.GetHeight() : 0;

        // The signature itself: a picture, or Arial Italic text sized to the requested width.
        double sigW, sigH;
        XFont? font = null;
        string? visual = null;
        var slant = false;
        if (image is not null)
        {
            sigW = targetWidth;
            sigH = sigW * plan.PictureAspect;
            // A short typed name would otherwise become huge: keep it within a sensible height.
            if (plan.Name is not null && sigH > sigW * MaxTextAspect)
            {
                var shrink = sigW * MaxTextAspect / sigH;
                (sigW, sigH) = (sigW * shrink, sigW * MaxTextAspect);
            }
        }
        else
        {
            visual = ArabicShaper.ToVisual(plan.Name!);
            // Arial Italic has no Arabic letters, so Arabic uses the upright face and is slanted with a shear when drawn.
            slant = ArabicShaper.ContainsRtl(plan.Name!);
            var style = slant ? XFontStyleEx.Regular : XFontStyleEx.Italic;
            var probe = new XFont("Arial", 100, style);
            var width100 = Math.Max(1, measure.MeasureString(visual, probe).Width);
            var size = Math.Clamp(targetWidth / width100 * 100, 6, 48);
            font = new XFont("Arial", size, style);
            sigW = measure.MeasureString(visual, font).Width;
            sigH = font.GetHeight();
        }

        var blockW = Math.Max(sigW, dateWidth);
        var blockH = sigH + dateHeight;
        var left = plan.X is { } px ? px / 100 * pageW : plan.Position switch
        {
            var p when p.EndsWith("left", StringComparison.Ordinal) => Margin,
            var p when p.EndsWith("right", StringComparison.Ordinal) => pageW - Margin - blockW,
            _ => (pageW - blockW) / 2,
        };
        var top = plan.Y is { } py ? py / 100 * pageH : plan.Position.StartsWith("top", StringComparison.Ordinal) ? Margin : pageH - Margin - blockH;
        left = Math.Clamp(left, 0, Math.Max(0, pageW - blockW));
        top = Math.Clamp(top, 0, Math.Max(0, pageH - blockH));

        using var gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);
        gfx.MultiplyTransform(geometry.VisualToGraphics(), XMatrixOrder.Prepend);
        var sigLeft = left + (blockW - sigW) / 2;
        if (image is not null)
        {
            gfx.DrawImage(image, sigLeft, top, sigW, sigH);
        }
        else
        {
            var state = gfx.Save();
            // Shear about the baseline (about 80 % of the way down the line) so the top of the letters leans right.
            if (slant) gfx.MultiplyTransform(new XMatrix(1, 0, -0.2, 1, 0.2 * (top + sigH * 0.8), 0), XMatrixOrder.Prepend);
            gfx.DrawString(visual!, font!, new XSolidBrush(Ink), new XPoint(sigLeft, top), XStringFormats.TopLeft);
            gfx.Restore(state);
        }

        if (plan.Date)
            gfx.DrawString(plan.DateText, dateFont, new XSolidBrush(XColor.FromArgb(70, 70, 70)), new XPoint(left + (blockW - dateWidth) / 2, top + sigH + DateGap), XStringFormats.TopLeft);
    }

    // ---- pictures -------------------------------------------------------------------------------------------------------

    /// <summary>Decodes any picture Magick can read and re-encodes it as plain 8-bit RGB or RGBA PNG (what PDFsharp imports), shrunk to a sensible size.</summary>
    private static (byte[] Png, double Aspect) LoadPicture(string path)
    {
        var fileName = Path.GetFileName(path);
        try
        {
            var bytes = File.ReadAllBytes(path);
            // Only ordinary picture formats: Magick can also run scripts and follow references (SVG, MVG, MSL...) and a signature never needs that.
            var format = new MagickImageInfo(bytes).Format;
            if (format is not (MagickFormat.Png or MagickFormat.Png8 or MagickFormat.Png24 or MagickFormat.Png32 or MagickFormat.Png48 or MagickFormat.Png64 or MagickFormat.Jpeg
                or MagickFormat.Gif or MagickFormat.Bmp or MagickFormat.Bmp3 or MagickFormat.Tiff or MagickFormat.WebP))
                throw new ConversionFailure($"{fileName} is not a PNG or JPG picture.");

            using var image = new MagickImage(bytes);
            image.AutoOrient();
            if (image.ColorSpace == ColorSpace.CMYK) image.TransformColorSpace(ColorProfiles.SRGB);
            if (image.Width > MaxPictureSide || image.Height > MaxPictureSide) image.Resize(new MagickGeometry(MaxPictureSide, MaxPictureSide));
            image.Depth = 8;
            if (image.Width == 0 || image.Height == 0) throw new ConversionFailure($"{fileName} is an empty picture.");
            var aspect = (double)image.Height / image.Width;
            return (image.ToByteArray(image.HasAlpha ? MagickFormat.Png32 : MagickFormat.Png24), aspect);
        }
        catch (MagickException ex)
        {
            throw new ConversionFailure($"{fileName} could not be read as an image ({ex.Message}). Use a PNG or JPG file.");
        }
    }

    /// <summary>The name drawn in a script font as a transparent PNG, or null when the text or the machine needs the Arial fallback.</summary>
    private static (byte[] Png, double Aspect)? RenderScriptName(string text)
    {
        if (!text.All(c => c is >= ' ' and <= '\u024F')) return null; // Basic Latin up to Latin Extended-B
        if (FindScriptFont() is not { } fontPath) return null;

        try
        {
            const int size = 200;
            using var probe = new MagickImage(MagickColors.Transparent, 16, 16);
            probe.Settings.Font = fontPath;
            probe.Settings.FontPointsize = size;
            if (probe.FontTypeMetrics(text, true) is not { } metrics) return null;

            var pad = size / 4;
            using var canvas = new MagickImage(MagickColors.Transparent, (uint)Math.Ceiling(metrics.TextWidth) + 2 * (uint)pad, (uint)Math.Ceiling(metrics.TextHeight) + 2 * (uint)pad);
            canvas.Settings.Font = fontPath;
            canvas.Settings.FontPointsize = size;
            canvas.Settings.FillColor = new MagickColor((byte)Ink.R, (byte)Ink.G, (byte)Ink.B);
            canvas.Settings.TextAntiAlias = true;
            canvas.Draw(new DrawableText(pad, pad + metrics.Ascent, text));
            canvas.Trim();
            if (canvas.Width < 4 || canvas.Height < 4) return null;
            return (canvas.ToByteArray(MagickFormat.Png32), (double)canvas.Height / canvas.Width);
        }
        catch (MagickException)
        {
            return null;
        }
    }

    /// <summary>macOS script fonts that ship with the system (Supplemental folder), in order of preference.</summary>
    private static readonly string[] MacScriptFonts =
    [
        "/System/Library/Fonts/Supplemental/Apple Chancery.ttf",
        "/System/Library/Fonts/Supplemental/Brush Script.ttf",
        "/System/Library/Fonts/Supplemental/Bradley Hand Bold.ttf",
    ];

    private static string? FindScriptFont()
    {
        if (OperatingSystem.IsMacOS()) return MacScriptFonts.FirstOrDefault(File.Exists);
        if (!OperatingSystem.IsWindows()) return null;   // Linux has no common script font: the name is drawn in the regular font
        var folders = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Fonts),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "Fonts"),
        };
        foreach (var file in ScriptFontFiles)
        {
            foreach (var folder in folders)
            {
                if (string.IsNullOrEmpty(folder)) continue;
                var path = Path.Combine(folder, file);
                if (File.Exists(path)) return path;
            }
        }
        return null;
    }
}

