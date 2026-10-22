using System.Text;
using ImageMagick;
using ImageMagick.Drawing;
using ZXing;
using ZXing.QrCode.Internal;
using QrEncoder = ZXing.QrCode.Internal.Encoder;

namespace DropFlow.Engines.Archive.Images;

/// <summary>
/// Styled QR codes: module and eye shapes, colours and gradients, a centred logo, as PNG and SVG. The shapes are laid out once in
/// module units (<see cref="QrLayout"/>) and both outputs draw the same outlines. In-process and offline.
/// </summary>
public static class QrDesigner
{
    /// <summary>With a logo the code is made at least this version (37x37 modules), so the hidden centre stays a small share of the data.</summary>
    internal const int MinVersionWithLogo = 5;

    private const int MaxEmbeddedLogo = 512;

    /// <summary>The explicit level when one is given; otherwise H when there is a logo (it hides part of the code), M without.</summary>
    public static QrErrorCorrection EffectiveLevel(QrDesign design) =>
        design.ErrorCorrection ?? (design.Style.HasLogo ? QrErrorCorrection.H : QrErrorCorrection.M);

    /// <summary>The module grid as <c>[row, column]</c> (that is <c>[y, x]</c>), true = dark, without the quiet zone.</summary>
    /// <exception cref="ArgumentException">The text is empty.</exception>
    /// <exception cref="QrTooLargeException">The text does not fit at this level.</exception>
    public static bool[,] Matrix(string text, QrErrorCorrection level) => Encode(text, level, 1);

    internal static bool[,] Encode(string text, QrErrorCorrection level, int minVersion)
    {
        if (string.IsNullOrEmpty(text)) throw new ArgumentException("Nothing to encode.", nameof(text));

        // Same rule as QrCodeService: plain ASCII without a character-set marker, anything else UTF-8 with an ECI header.
        var hints = new Dictionary<EncodeHintType, object>();
        if (text.Any(c => c > 127)) hints[EncodeHintType.CHARACTER_SET] = "UTF-8";

        QRCode code;
        try
        {
            code = QrEncoder.encode(text, ToZxing(level), hints);
            if (code.Version.VersionNumber < minVersion)
            {
                hints[EncodeHintType.QR_VERSION] = minVersion;
                code = QrEncoder.encode(text, ToZxing(level), hints);
            }
        }
        catch (WriterException ex)
        {
            throw new QrTooLargeException($"The text is too long for a QR code at error-correction level {level}: {ex.Message}");
        }

        var source = code.Matrix;
        var n = source.Width;
        var matrix = new bool[n, n];
        for (var row = 0; row < n; row++)
            for (var col = 0; col < n; col++)
                matrix[row, col] = source[col, row] == 1;
        return matrix;
    }

    /// <summary>A PNG of exactly <see cref="QrDesign.Size"/> pixels square, antialiased; RGBA when the background is not opaque, RGB otherwise.</summary>
    /// <exception cref="ArgumentException">Empty text or a bad colour.</exception>
    /// <exception cref="QrTooLargeException">The text does not fit.</exception>
    /// <exception cref="FileNotFoundException">The logo file does not exist.</exception>
    public static byte[] RenderPng(QrDesign design)
    {
        var (layout, size) = Plan(design);
        var scale = size / layout.Units;
        var background = layout.Background;

        using var canvas = new MagickImage(new MagickColor(background.R, background.G, background.B, background.A), (uint)size, (uint)size);
        foreach (var layer in layout.Layers)
        {
            if (layer.Path.IsEmpty) continue;
            using var paint = Paint(layer, size, scale);
            canvas.Composite(paint, CompositeOperator.Over);
        }

        if (layout.LogoBox is not null)
        {
            using var logo = LoadLogo(design.Style.LogoPath!);
            var box = layout.FitLogo(logo.Width, logo.Height);
            var x = (int)Math.Round(box.X * scale);
            var y = (int)Math.Round(box.Y * scale);
            var w = Math.Max(1, (int)Math.Round(box.Right * scale) - x);
            var h = Math.Max(1, (int)Math.Round(box.Bottom * scale) - y);
            logo.Resize(new MagickGeometry((uint)w, (uint)h) { IgnoreAspectRatio = true });
            canvas.Composite(logo, x, y, CompositeOperator.Over);
        }

        canvas.Settings.SetDefine(MagickFormat.Png, "exclude-chunks", "date,time");
        if (background.IsOpaque)
        {
            canvas.Alpha(AlphaAction.Off);
            canvas.Format = MagickFormat.Png24;
        }
        else
        {
            canvas.Format = MagickFormat.Png32;
        }
        return canvas.ToByteArray();
    }

    /// <summary>A standalone SVG: viewBox in modules (quiet zone included), width/height = <see cref="QrDesign.Size"/>,
    /// gradients as &lt;linearGradient&gt;, the logo embedded as a PNG data: URI.</summary>
    /// <exception cref="ArgumentException">Empty text or a bad colour.</exception>
    /// <exception cref="QrTooLargeException">The text does not fit.</exception>
    /// <exception cref="FileNotFoundException">The logo file does not exist.</exception>
    public static string RenderSvg(QrDesign design)
    {
        var (layout, size) = Plan(design);
        var units = QrPath.Num(layout.Units);
        var svg = new StringBuilder();
        svg.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
        svg.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" xmlns:xlink=\"http://www.w3.org/1999/xlink\" version=\"1.1\" width=\"{size}\" height=\"{size}\" viewBox=\"0 0 {units} {units}\">\n");

        var gradients = new Dictionary<QrPaint, string>(ReferenceEqualityComparer.Instance);
        foreach (var paint in layout.Layers.Select(l => l.Paint).Where(p => p.IsGradient))
            if (!gradients.ContainsKey(paint)) gradients[paint] = gradients.Count == 0 ? "qr-gradient" : $"qr-gradient-{gradients.Count + 1}";
        if (gradients.Count > 0)
        {
            svg.Append("<defs>\n");
            foreach (var (paint, id) in gradients)
            {
                svg.Append($"<linearGradient id=\"{id}\" gradientUnits=\"userSpaceOnUse\" x1=\"{QrPath.Num(paint.X1)}\" y1=\"{QrPath.Num(paint.Y1)}\" x2=\"{QrPath.Num(paint.X2)}\" y2=\"{QrPath.Num(paint.Y2)}\">");
                svg.Append($"<stop offset=\"0\" stop-color=\"{paint.From.Hex}\"{Opacity("stop-opacity", paint.From)}/>");
                svg.Append($"<stop offset=\"1\" stop-color=\"{paint.To!.Value.Hex}\"{Opacity("stop-opacity", paint.To.Value)}/>");
                svg.Append("</linearGradient>\n");
            }
            svg.Append("</defs>\n");
        }

        if (layout.Background.A > 0)
            svg.Append($"<rect x=\"0\" y=\"0\" width=\"{units}\" height=\"{units}\" fill=\"{layout.Background.Hex}\"{Opacity("fill-opacity", layout.Background)}/>\n");

        foreach (var layer in layout.Layers)
        {
            if (layer.Path.IsEmpty) continue;
            var fill = gradients.TryGetValue(layer.Paint, out var id) ? $"url(#{id})" : layer.Paint.From.Hex;
            var opacity = layer.Paint.IsGradient ? "" : Opacity("fill-opacity", layer.Paint.From);
            svg.Append($"<path fill=\"{fill}\"{opacity} fill-rule=\"{(layer.EvenOdd ? "evenodd" : "nonzero")}\" d=\"{layer.Path.ToSvgData()}\"/>\n");
        }

        if (layout.LogoBox is not null)
        {
            using var logo = LoadLogo(design.Style.LogoPath!);
            var box = layout.FitLogo(logo.Width, logo.Height);
            if (Math.Max(logo.Width, logo.Height) > MaxEmbeddedLogo) logo.Resize(new MagickGeometry(MaxEmbeddedLogo, MaxEmbeddedLogo));
            logo.Format = MagickFormat.Png;
            logo.Settings.SetDefine(MagickFormat.Png, "exclude-chunks", "date,time");
            var data = Convert.ToBase64String(logo.ToByteArray());
            svg.Append($"<image x=\"{QrPath.Num(box.X)}\" y=\"{QrPath.Num(box.Y)}\" width=\"{QrPath.Num(box.W)}\" height=\"{QrPath.Num(box.H)}\" preserveAspectRatio=\"xMidYMid meet\" xlink:href=\"data:image/png;base64,{data}\"/>\n");
        }

        svg.Append("</svg>\n");
        return svg.ToString();
    }

    /// <summary>Decodes the picture with ZXing (the same reader as the <c>img.qr</c> read mode). Never throws for unreadable bytes.</summary>
    public static QrScanCheck Check(byte[] png, string expectedText)
    {
        IReadOnlyList<BarcodeReadResult> found;
        try
        {
            found = QrCodeService.Read(png);
        }
        catch (MagickException)
        {
            return new QrScanCheck(false, null);
        }

        if (found.Any(f => f.Text == expectedText)) return new QrScanCheck(true, expectedText);
        return new QrScanCheck(false, found.Count > 0 ? found[0].Text : null);
    }

    // ---------------------------------------------------------------- internals

    private static (QrLayout Layout, int Size) Plan(QrDesign design)
    {
        ArgumentNullException.ThrowIfNull(design);
        var style = design.Style ?? throw new ArgumentException("A style is required.", nameof(design));
        if (string.IsNullOrEmpty(design.Text)) throw new ArgumentException("Nothing to encode.", nameof(design));

        var foreground = QrRgba.Parse(style.Foreground, "foreground");
        QrRgba? foreground2 = string.IsNullOrWhiteSpace(style.Foreground2) ? null : QrRgba.Parse(style.Foreground2, "gradient end");
        QrRgba? eye = string.IsNullOrWhiteSpace(style.EyeColor) ? null : QrRgba.Parse(style.EyeColor, "eye colour");
        var background = QrRgba.Parse(style.Background, "background");
        if (style.HasLogo && !File.Exists(style.LogoPath)) throw new FileNotFoundException($"Logo not found: {style.LogoPath}", style.LogoPath);

        var size = Math.Clamp(design.Size, QrCodeService.MinSize, QrCodeService.MaxSize);
        var quiet = Math.Clamp(design.QuietZone, 0, 16);
        var matrix = Encode(design.Text, EffectiveLevel(design), style.HasLogo ? MinVersionWithLogo : 1);
        return (QrLayout.Build(matrix, style, quiet, foreground, foreground2, eye, background), size);
    }

    private static ErrorCorrectionLevel ToZxing(QrErrorCorrection level) => level switch
    {
        QrErrorCorrection.L => ErrorCorrectionLevel.L,
        QrErrorCorrection.Q => ErrorCorrectionLevel.Q,
        QrErrorCorrection.H => ErrorCorrectionLevel.H,
        _ => ErrorCorrectionLevel.M,
    };

    private static string Opacity(string attribute, QrRgba colour) => colour.IsOpaque ? "" : $" {attribute}=\"{colour.Opacity}\"";

    private static MagickImage LoadLogo(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException($"Logo not found: {path}", path);
        var logo = new MagickImage(path);
        logo.AutoOrient();
        return logo;
    }

    /// <summary>The layer's outlines rendered as a coverage mask, then its colour or gradient put through that mask.</summary>
    private static MagickImage Paint(QrLayer layer, int size, double scale)
    {
        byte[] coverage;
        using (var mask = new MagickImage(MagickColors.Black, (uint)size, (uint)size))
        {
            new Drawables()
                .StrokeColor(MagickColors.Transparent)
                .StrokeWidth(0)
                .FillColor(MagickColors.White)
                .FillRule(layer.EvenOdd ? FillRule.EvenOdd : FillRule.NonZero)
                .Path(ToMagick(layer.Path, scale))
                .Draw(mask);
            coverage = mask.GetPixels().ToByteArray("R") ?? throw new InvalidOperationException("The mask could not be read.");
        }

        var rgba = new byte[size * size * 4];
        var paint = layer.Paint;
        for (var py = 0; py < size; py++)
        {
            var v = (py + 0.5) / scale;
            for (var px = 0; px < size; px++)
            {
                var i = py * size + px;
                var cover = coverage[i];
                if (cover == 0) continue;
                var colour = paint.At((px + 0.5) / scale, v);
                var o = i * 4;
                rgba[o] = colour.R;
                rgba[o + 1] = colour.G;
                rgba[o + 2] = colour.B;
                rgba[o + 3] = (byte)((cover * colour.A + 127) / 255);
            }
        }

        var image = new MagickImage();
        image.ReadPixels(rgba, new PixelReadSettings((uint)size, (uint)size, StorageType.Char, PixelMapping.RGBA));
        return image;
    }

    /// <summary>Module units to Magick pixel coordinates (Magick puts pixel centres on whole numbers, hence the half-pixel shift).</summary>
    private static List<IPath> ToMagick(QrPath path, double scale)
    {
        double X(double u) => u * scale - 0.5;
        var result = new List<IPath>(path.Commands.Count);
        foreach (var c in path.Commands)
        {
            result.Add(c.Op switch
            {
                QrPathOp.Move => new PathMoveToAbs(X(c.X), X(c.Y)),
                QrPathOp.Line => new PathLineToAbs(X(c.X), X(c.Y)),
                QrPathOp.Arc => new PathArcAbs(new PathArc(X(c.X), X(c.Y), c.Radius * scale, c.Radius * scale, 0, false, true)),
                _ => new PathClose(),
            });
        }
        return result;
    }
}
