namespace DropFlow.Engines.Archive.Images;

/// <summary>How the data modules (the small squares) of a QR code are drawn.</summary>
public enum QrModuleShape
{
    Square,
    /// <summary>Separate squares with rounded corners.</summary>
    Rounded,
    /// <summary>Round dots.</summary>
    Dots,
    Diamond,
    /// <summary>Neighbouring modules merge into blobs; only free corners are rounded.</summary>
    Fluid,
    /// <summary>Vertical runs of modules become rounded capsules.</summary>
    Bars,
}

/// <summary>How the three 7x7 finder patterns ("eyes") in the corners are drawn.</summary>
public enum QrEyeShape
{
    Square,
    Rounded,
    Circle,
    /// <summary>Two opposite corners strongly rounded, the other two square.</summary>
    Leaf,
}

/// <summary>The look of a QR code. Colours are "#rrggbb" or "#rrggbbaa" ("#rgb" is accepted too).
/// <paramref name="Background"/> "transparent" means no background.</summary>
/// <param name="Foreground2">Non-null: a linear gradient from <paramref name="Foreground"/> to this colour across the code.</param>
/// <param name="GradientAngle">Degrees: 0 runs left to right, 90 top to bottom.</param>
/// <param name="EyeColor">Null: the eyes use the module colour (gradient included).</param>
/// <param name="LogoPath">A picture placed in the centre (any format Magick.NET reads).</param>
/// <param name="LogoScale">Width of the logo box as a fraction of the code width (clamped to 0.1..0.3).</param>
/// <param name="LogoPlate">A rounded plate in the background colour (white when transparent) behind the logo; modules under it are not drawn.</param>
public sealed record QrStyle(
    QrModuleShape Modules = QrModuleShape.Square,
    QrEyeShape Eyes = QrEyeShape.Square,
    string Foreground = "#000000",
    string? Foreground2 = null,
    double GradientAngle = 45,
    string? EyeColor = null,
    string Background = "#ffffff",
    string? LogoPath = null,
    double LogoScale = 0.22,
    bool LogoPlate = true)
{
    internal bool HasLogo => !string.IsNullOrWhiteSpace(LogoPath);
}

/// <summary>Everything needed to draw one QR code.</summary>
/// <param name="ErrorCorrection">Null: automatic (<see cref="QrDesigner.EffectiveLevel"/>).</param>
/// <param name="Size">Width and height of the PNG in pixels (and of the SVG's width/height attributes).</param>
/// <param name="QuietZone">Blank border in modules (the QR standard asks for 4).</param>
public sealed record QrDesign(string Text, QrStyle Style, QrErrorCorrection? ErrorCorrection = null, int Size = 1024, int QuietZone = 4);

/// <param name="Scans">The picture decodes to exactly the expected text.</param>
/// <param name="DecodedText">What was read (the expected text when it was among several), or null when nothing was found.</param>
public sealed record QrScanCheck(bool Scans, string? DecodedText);
