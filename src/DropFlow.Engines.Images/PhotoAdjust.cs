// SPDX-License-Identifier: GPL-3.0-or-later
using DropFlow.Core.Operations;
using ImageMagick;

namespace DropFlow.Engines.Images;

/// <summary>
/// Every adjustment of <c>img.edit</c>, in the units of its option. Sliders are -100..100 (0 = unchanged) unless noted:
/// <c>Saturation</c> 0..200 (100 = unchanged), <c>Hue</c> -180..180 degrees, <c>Sharpen</c>/<c>Noise</c>/<c>Grain</c>/<c>Fade</c> 0..100,
/// <c>Blur</c> 0..20 (pixels of the full-size picture).
/// </summary>
public sealed record PhotoAdjust
{
    public double Exposure { get; init; }
    public double Brightness { get; init; }
    public double Contrast { get; init; }
    public double Highlights { get; init; }
    public double Shadows { get; init; }
    public double Whites { get; init; }
    public double Blacks { get; init; }
    public double Gamma { get; init; }
    public double Temperature { get; init; }
    public double Tint { get; init; }
    public double Saturation { get; init; } = 100;
    public double Vibrance { get; init; }
    public double Hue { get; init; }
    public double Clarity { get; init; }
    public double Sharpen { get; init; }
    public double Noise { get; init; }
    public double Blur { get; init; }
    public double Vignette { get; init; }
    public double Grain { get; init; }
    public double Fade { get; init; }
    public string Filter { get; init; } = "none";
    public int Rotate { get; init; }
    public bool FlipH { get; init; }
    public bool FlipV { get; init; }

    public static PhotoAdjust None { get; } = new();

    /// <summary>Option keys in the order the generic form and the editor show them (also the keys <see cref="FromOptions"/> reads).</summary>
    public static readonly string[] LightKeys = ["exposure", "brightness", "contrast", "highlights", "shadows", "whites", "blacks", "gamma"];
    public static readonly string[] ColorKeys = ["temperature", "tint", "saturation", "vibrance", "hue"];
    public static readonly string[] DetailKeys = ["clarity", "sharpen", "noise", "blur"];
    public static readonly string[] EffectKeys = ["vignette", "grain", "fade"];

    /// <summary>(min, max, neutral) of each numeric option.</summary>
    public static (double Min, double Max, double Neutral) RangeOf(string key) => key switch
    {
        "saturation" => (0, 200, 100),
        "hue" => (-180, 180, 0),
        "sharpen" or "noise" or "grain" or "fade" => (0, 100, 0),
        "blur" => (0, 20, 0),
        _ => (-100, 100, 0),
    };

    public bool IsIdentity => this == None;

    public static PhotoAdjust FromOptions(ConversionJob job) => new()
    {
        Exposure = job.GetDouble("exposure"),
        Brightness = job.GetDouble("brightness"),
        Contrast = job.GetDouble("contrast"),
        Highlights = job.GetDouble("highlights"),
        Shadows = job.GetDouble("shadows"),
        Whites = job.GetDouble("whites"),
        Blacks = job.GetDouble("blacks"),
        Gamma = job.GetDouble("gamma"),
        Temperature = job.GetDouble("temperature"),
        Tint = job.GetDouble("tint"),
        Saturation = job.GetDouble("saturation", 100),
        Vibrance = job.GetDouble("vibrance"),
        Hue = job.GetDouble("hue"),
        Clarity = job.GetDouble("clarity"),
        Sharpen = job.GetDouble("sharpen"),
        Noise = job.GetDouble("noise"),
        Blur = job.GetDouble("blur"),
        Vignette = job.GetDouble("vignette"),
        Grain = job.GetDouble("grain"),
        Fade = job.GetDouble("fade"),
        Filter = job.GetString("filter", "none") ?? "none",
        Rotate = job.GetInt("rotate"),
        FlipH = job.GetBool("flipH"),
        FlipV = job.GetBool("flipV"),
    };

    /// <summary>The options dictionary for a job (only what differs from neutral, plus nothing else).</summary>
    public IReadOnlyDictionary<string, object?> ToOptions()
    {
        var o = new Dictionary<string, object?>();
        void Num(string key, double value)
        {
            if (value != RangeOf(key).Neutral) o[key] = value;
        }
        Num("exposure", Exposure); Num("brightness", Brightness); Num("contrast", Contrast); Num("highlights", Highlights);
        Num("shadows", Shadows); Num("whites", Whites); Num("blacks", Blacks); Num("gamma", Gamma);
        Num("temperature", Temperature); Num("tint", Tint); Num("saturation", Saturation); Num("vibrance", Vibrance); Num("hue", Hue);
        Num("clarity", Clarity); Num("sharpen", Sharpen); Num("noise", Noise); Num("blur", Blur);
        Num("vignette", Vignette); Num("grain", Grain); Num("fade", Fade);
        if (Filter != "none") o["filter"] = Filter;
        if (Rotate != 0) o["rotate"] = Rotate.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (FlipH) o["flipH"] = true;
        if (FlipV) o["flipV"] = true;
        return o;
    }

    public double Get(string key) => key switch
    {
        "exposure" => Exposure, "brightness" => Brightness, "contrast" => Contrast, "highlights" => Highlights, "shadows" => Shadows,
        "whites" => Whites, "blacks" => Blacks, "gamma" => Gamma, "temperature" => Temperature, "tint" => Tint, "saturation" => Saturation,
        "vibrance" => Vibrance, "hue" => Hue, "clarity" => Clarity, "sharpen" => Sharpen, "noise" => Noise, "blur" => Blur,
        "vignette" => Vignette, "grain" => Grain, "fade" => Fade,
        _ => throw new ArgumentException($"Unknown adjustment '{key}'.", nameof(key)),
    };

    public PhotoAdjust With(string key, double v) => key switch
    {
        "exposure" => this with { Exposure = v }, "brightness" => this with { Brightness = v }, "contrast" => this with { Contrast = v },
        "highlights" => this with { Highlights = v }, "shadows" => this with { Shadows = v }, "whites" => this with { Whites = v },
        "blacks" => this with { Blacks = v }, "gamma" => this with { Gamma = v }, "temperature" => this with { Temperature = v },
        "tint" => this with { Tint = v }, "saturation" => this with { Saturation = v }, "vibrance" => this with { Vibrance = v },
        "hue" => this with { Hue = v }, "clarity" => this with { Clarity = v }, "sharpen" => this with { Sharpen = v },
        "noise" => this with { Noise = v }, "blur" => this with { Blur = v }, "vignette" => this with { Vignette = v },
        "grain" => this with { Grain = v }, "fade" => this with { Fade = v },
        _ => throw new ArgumentException($"Unknown adjustment '{key}'.", nameof(key)),
    };
}

/// <summary>
/// Applies a <see cref="PhotoAdjust"/> to a picture. The editor's preview and the saved file use this same code: the preview passes
/// <c>scale</c> = preview width / original width, so effects measured in pixels (sharpening, blur, clarity, grain) look the same on the
/// small copy as on the full-size result.
/// </summary>
public static class PhotoEditor
{
    private const int GrainCellsAcrossLongSide = 900;

    public static void Apply(MagickImage image, PhotoAdjust a, double scale = 1, CancellationToken cancellationToken = default)
    {
        scale = scale is > 0 and <= 1 ? scale : 1;
        ImageIo.EnsureColor(image);

        // 1. tone and colour balance: one lookup table per channel
        if (NeedsTone(a)) ApplyLookup(image, BuildLookup(a));
        cancellationToken.ThrowIfCancellationRequested();

        if (a.Brightness != 0 || a.Contrast != 0) image.BrightnessContrast(new Percentage(a.Brightness), new Percentage(a.Contrast));
        if (a.Saturation != 100 || a.Hue != 0)
            image.Modulate(new Percentage(100), new Percentage(Math.Clamp(a.Saturation, 0, 200)), new Percentage(100 + Math.Clamp(a.Hue, -180, 180) / 180 * 100));
        if (a.Vibrance != 0) ApplyVibrance(image, Math.Clamp(a.Vibrance, -100, 100) / 100);
        cancellationToken.ThrowIfCancellationRequested();

        // 2. detail (radii are in full-size pixels, so they shrink with the preview)
        var longSide = Math.Max(image.Width, image.Height);
        if (a.Clarity != 0)
            image.UnsharpMask(0, Math.Max(1, longSide * 0.02), Math.Clamp(a.Clarity, -100, 100) / 100 * 0.7, 0); // clarity = wide, gentle unsharp mask
        if (a.Noise > 0) image.WaveletDenoise(new Percentage(Math.Clamp(a.Noise, 0, 100) / 100 * 12), 0.4);
        if (a.Sharpen > 0) image.UnsharpMask(0, Math.Max(0.3, 1.2 * scale), Math.Clamp(a.Sharpen, 0, 100) / 100 * 2.2, 0.01);
        if (a.Blur > 0) image.Blur(0, Math.Max(0.3, Math.Clamp(a.Blur, 0, 20) * scale));
        cancellationToken.ThrowIfCancellationRequested();

        switch (a.Filter)
        {
            case "grayscale": image.Grayscale(); ImageIo.EnsureColor(image); break;
            case "sepia": image.SepiaTone(); break;
            case "invert": image.Negate(Channels.RGB); break;
        }

        // 3. effects
        if (a.Fade > 0 || a.Vignette != 0 || a.Grain > 0) ApplyEffects(image, a, scale);
        cancellationToken.ThrowIfCancellationRequested();

        // 4. geometry
        if (a.Rotate is 90 or 180 or 270) image.Rotate(a.Rotate);
        if (a.FlipH) image.Flop();
        if (a.FlipV) image.Flip();
    }

    private static bool NeedsTone(PhotoAdjust a) =>
        a.Exposure != 0 || a.Highlights != 0 || a.Shadows != 0 || a.Whites != 0 || a.Blacks != 0 || a.Gamma != 0 || a.Temperature != 0 || a.Tint != 0;

    /// <summary>The per-channel curve (R, G, B), 256 entries each. Pure, so the curve shape is unit-tested.</summary>
    public static byte[][] BuildLookup(PhotoAdjust a)
    {
        var ev = Math.Pow(2, Math.Clamp(a.Exposure, -100, 100) / 100 * 2);
        var h = Math.Clamp(a.Highlights, -100, 100) / 100;
        var s = Math.Clamp(a.Shadows, -100, 100) / 100;
        var w = Math.Clamp(a.Whites, -100, 100) / 100;
        var b = Math.Clamp(a.Blacks, -100, 100) / 100;
        var gamma = Math.Pow(2, -Math.Clamp(a.Gamma, -100, 100) / 100);
        var t = Math.Clamp(a.Temperature, -100, 100) / 100;
        var tint = Math.Clamp(a.Tint, -100, 100) / 100;
        double[] gains = [1 + 0.18 * t, 1 - 0.14 * tint, 1 - 0.18 * t];

        var lut = new byte[3][];
        for (var c = 0; c < 3; c++)
        {
            lut[c] = new byte[256];
            for (var i = 0; i < 256; i++)
            {
                var x = i / 255.0 * gains[c] * ev;
                x = Math.Clamp(x, 0, 1);
                x += h * x * x * (1 - x) * 1.6;           // highlights: bright tones (peak near 2/3)
                x += s * x * (1 - x) * (1 - x) * 1.6;     // shadows: dark tones (peak near 1/3)
                x = Math.Clamp(x, 0, 1);
                x += w * 0.25 * x * x * x;                // whites: the top end
                x += b * 0.25 * (1 - x) * (1 - x) * (1 - x); // blacks: the bottom end
                x = Math.Pow(Math.Clamp(x, 0, 1), gamma);
                lut[c][i] = (byte)Math.Round(Math.Clamp(x, 0, 1) * 255);
            }
        }
        return lut;
    }

    private static void ApplyLookup(MagickImage image, byte[][] lut)
    {
        var (w, h, rgb) = ReadRgb(image);
        for (var i = 0; i < rgb.Length; i += 3)
        {
            rgb[i] = lut[0][rgb[i]];
            rgb[i + 1] = lut[1][rgb[i + 1]];
            rgb[i + 2] = lut[2][rgb[i + 2]];
        }
        WriteRgb(image, w, h, rgb);
    }

    /// <summary>Saturation that favours dull colours (skin and already vivid colours change least).</summary>
    private static void ApplyVibrance(MagickImage image, double amount)
    {
        var (w, h, rgb) = ReadRgb(image);
        for (var i = 0; i < rgb.Length; i += 3)
        {
            double r = rgb[i], g = rgb[i + 1], bl = rgb[i + 2];
            var max = Math.Max(r, Math.Max(g, bl));
            var min = Math.Min(r, Math.Min(g, bl));
            var sat = max <= 0 ? 0 : (max - min) / 255.0;
            var k = 1 + amount * (amount > 0 ? (1 - sat) * 1.2 : 1);
            var lum = 0.299 * r + 0.587 * g + 0.114 * bl;
            rgb[i] = Byte(lum + (r - lum) * k);
            rgb[i + 1] = Byte(lum + (g - lum) * k);
            rgb[i + 2] = Byte(lum + (bl - lum) * k);
        }
        WriteRgb(image, w, h, rgb);
    }

    /// <summary>Fade, vignette and film grain. All three are defined relative to the picture's size, so they are scale-free.</summary>
    private static void ApplyEffects(MagickImage image, PhotoAdjust a, double scale)
    {
        var (w, h, rgb) = ReadRgb(image);
        var fade = Math.Clamp(a.Fade, 0, 100) / 100;
        var vignette = Math.Clamp(a.Vignette, -100, 100) / 100;
        var grain = Math.Clamp(a.Grain, 0, 100) / 100;

        // Grain lives on a fixed grid relative to the long side and is sampled bilinearly, so a preview shows the same grain.
        var longSide = Math.Max(w, h);
        // a grain cell is never smaller than one pixel of the full-size picture
        var cell = Math.Max(1, longSide / scale / GrainCellsAcrossLongSide) * scale;
        var gw = (int)Math.Ceiling(w / cell) + 2;
        var gh = (int)Math.Ceiling(h / cell) + 2;
        float[]? noise = null;
        if (grain > 0)
        {
            var random = new Random(20261022);
            noise = new float[gw * gh];
            for (var i = 0; i < noise.Length; i++)
            {
                // Box-Muller: Gaussian grain
                var u1 = 1.0 - random.NextDouble();
                var u2 = random.NextDouble();
                noise[i] = (float)(Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2));
            }
        }

        double cx = (w - 1) / 2.0, cy = (h - 1) / 2.0;
        var halfDiagonal = Math.Sqrt(cx * cx + cy * cy);
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var i = (y * w + x) * 3;
                var factor = 1.0;
                if (vignette != 0)
                {
                    var d = halfDiagonal <= 0 ? 0 : Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy)) / halfDiagonal;
                    var t = SmoothStep(0.35, 1.05, d);
                    factor = vignette > 0 ? 1 - vignette * 0.85 * t : 1 + -vignette * 0.6 * t;
                }
                var add = 0.0;
                if (noise is not null)
                {
                    var gx = x / cell;
                    var gy = y / cell;
                    int x0 = (int)gx, y0 = (int)gy;
                    double fx = gx - x0, fy = gy - y0;
                    var n00 = noise[y0 * gw + x0];
                    var n10 = noise[y0 * gw + x0 + 1];
                    var n01 = noise[(y0 + 1) * gw + x0];
                    var n11 = noise[(y0 + 1) * gw + x0 + 1];
                    var n = (n00 * (1 - fx) + n10 * fx) * (1 - fy) + (n01 * (1 - fx) + n11 * fx) * fy;
                    add = n * grain * 28 * Math.Min(1, cell);
                }
                for (var c = 0; c < 3; c++)
                {
                    var v = rgb[i + c] / 255.0;
                    if (fade > 0) v = 0.11 * fade + v * (1 - 0.2 * fade);
                    rgb[i + c] = Byte(v * 255 * factor + add);
                }
            }
        }
        WriteRgb(image, w, h, rgb);
    }

    private static double SmoothStep(double edge0, double edge1, double x)
    {
        var t = Math.Clamp((x - edge0) / (edge1 - edge0), 0, 1);
        return t * t * (3 - 2 * t);
    }

    private static byte Byte(double v) => (byte)Math.Clamp(Math.Round(v), 0, 255);

    private static (int W, int H, byte[] Rgb) ReadRgb(MagickImage image)
    {
        var w = (int)image.Width;
        var h = (int)image.Height;
        var rgb = image.GetPixels().ToByteArray(PixelMapping.RGB) ?? throw new InvalidDataException("The picture has no pixels.");
        return (w, h, rgb);
    }

    private static void WriteRgb(MagickImage image, int w, int h, byte[] rgb)
    {
        // Only the colour channels are replaced; transparency stays as it was.
        using var pixels = image.GetPixels();
        var channels = (int)image.ChannelCount;
        var all = pixels.ToByteArray(0, 0, (uint)w, (uint)h, channels == 4 ? "RGBA" : "RGB") ?? throw new InvalidDataException("The picture has no pixels.");
        if (channels == 4)
        {
            for (int p = 0, q = 0; q < rgb.Length; p += 4, q += 3)
            {
                all[p] = rgb[q];
                all[p + 1] = rgb[q + 1];
                all[p + 2] = rgb[q + 2];
            }
            pixels.SetArea(0, 0, (uint)w, (uint)h, all);
        }
        else
        {
            pixels.SetArea(0, 0, (uint)w, (uint)h, rgb);
        }
    }
}
