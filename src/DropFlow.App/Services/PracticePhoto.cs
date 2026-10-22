// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using ImageMagick;
using ImageMagick.Drawing;

namespace DropFlow.App.Services;

/// <summary>
/// A small, made-up landscape the welcome guide offers for practice ("Practice photo.png" in DropFlow's data folder), so a first-time
/// user can try the drag-and-tap gesture on a real file without touching their own. Drawn in code: no bundled or downloaded asset.
/// </summary>
internal static class PracticePhoto
{
    public const string FileName = "Practice photo.png";

    /// <summary>Returns the practice photo in <paramref name="folder"/>, drawing it first when it is missing.</summary>
    public static string Ensure(string folder)
    {
        var path = Path.Combine(folder, FileName);
        if (File.Exists(path)) return path;
        Directory.CreateDirectory(folder);
        var temp = path + ".part";
        using (var image = Draw(1200, 800)) image.Write(temp, MagickFormat.Png);
        File.Move(temp, path, overwrite: true);
        return path;
    }

    public static MagickImage Draw(uint width, uint height)
    {
        var image = new MagickImage("gradient:#4338ca-#22d3ee", new MagickReadSettings { Width = width, Height = height });
        image.ColorSpace = ColorSpace.sRGB;
        double w = width, h = height;
        new Drawables().FillColor(new MagickColor("#fde68a")).StrokeColor(MagickColors.Transparent)
            .Circle(w * 0.74, h * 0.30, w * 0.74 + h * 0.11, h * 0.30).Draw(image);
        Hill(image, w, h, 0.62, "#0e7490", 0.18);
        Hill(image, w, h, 0.72, "#155e75", 0.12);
        Hill(image, w, h, 0.82, "#0a0f24", 0.08);
        return image;
    }

    private static void Hill(MagickImage image, double w, double h, double level, string color, double amplitude)
    {
        var points = new List<PointD> { new(0, h) };
        for (var i = 0; i <= 48; i++)
        {
            var x = w * i / 48;
            var y = h * (level - amplitude * 0.5 * (1 + Math.Sin(i / 48.0 * Math.PI * 2.3 + level * 9)));
            points.Add(new PointD(x, y));
        }
        points.Add(new PointD(w, h));
        new Drawables().FillColor(new MagickColor(color)).StrokeColor(MagickColors.Transparent).Polygon(points).Draw(image);
    }
}
