// SPDX-License-Identifier: GPL-3.0-or-later
using DropFlow.Core.Operations;
using DropFlow.Engines.Common;
using ImageMagick;

namespace DropFlow.Engines.Images.Heic;

/// <summary>
/// Saves a picture as HEIC: the pixels go to FFmpeg's libx265 as one intra-coded 4:2:0 frame (full range), and
/// <see cref="HeifContainer"/> wraps the result with the picture's EXIF and colour profile. Transparency is flattened onto white
/// (HEIC alpha needs an auxiliary image that most viewers ignore). Odd sizes are padded by one pixel and cropped back with <c>clap</c>.
/// </summary>
internal static class HeicEncoder
{
    /// <summary>Set by <see cref="ImagesModule.Create"/>; HEIC writing needs FFmpeg (it is shipped with the app).</summary>
    public static IEngineProvider? Engines { get; set; }

    /// <summary>Largest side libx265 accepts for a single picture.</summary>
    public const int MaxSide = 16384;

    public static string NeedsFfmpegMessage => "Saving as HEIC needs FFmpeg (it is part of the DropFlow installer).";

    /// <summary>Quality 1-100 (as for JPEG) to an x265 CRF: 92 → 14, 85 → 18, 50 → 35.</summary>
    public static int CrfFor(uint quality) => Math.Clamp((int)Math.Round((100 - Math.Clamp(quality, 1u, 100u)) * 0.5) + 10, 0, 51);

    public static byte[] Encode(MagickImage source, uint quality, CancellationToken ct = default)
    {
        var ffmpeg = Engines?.Locate(EngineId.Ffmpeg) ?? throw new InvalidOperationException(NeedsFfmpegMessage);
        var width = (int)source.Width;
        var height = (int)source.Height;
        if (width > MaxSide || height > MaxSide)
            throw new InvalidOperationException($"HEIC pictures can be at most {MaxSide} pixels on a side; this one is {width}x{height}.");

        var codedWidth = width + (width & 1);
        var codedHeight = height + (height & 1);

        using var image = (MagickImage)source.Clone();
        if (image.ColorSpace != ColorSpace.sRGB) image.ColorSpace = ColorSpace.sRGB;
        if (image.HasAlpha) image.ColorAlpha(MagickColors.White);
        if (codedWidth != width || codedHeight != height)
        {
            // The extra row/column is cropped away again by the clap property.
            image.Extent(new MagickGeometry((uint)codedWidth, (uint)codedHeight) { IgnoreAspectRatio = true }, Gravity.Northwest, MagickColors.White);
        }
        var rgb = image.GetPixels().ToByteArray(PixelMapping.RGB) ?? throw new InvalidDataException("The picture has no pixels.");

        var work = Path.Combine(Path.GetTempPath(), "dropflow-heic-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var raw = Path.Combine(work, "in.rgb");
            var hevc = Path.Combine(work, "out.hevc");
            File.WriteAllBytes(raw, rgb);
            string[] args =
            [
                "-hide_banner", "-nostdin", "-loglevel", "error", "-y",
                "-f", "rawvideo", "-pix_fmt", "rgb24", "-s", $"{codedWidth}x{codedHeight}", "-i", raw,
                "-frames:v", "1",
                "-vf", "scale=out_color_matrix=bt601:out_range=full,format=yuv420p",
                "-color_range", "pc", "-colorspace", "smpte170m", "-color_primaries", "bt709", "-color_trc", "iec61966-2-1",
                "-c:v", "libx265", "-preset", "medium", "-crf", CrfFor(quality).ToString(System.Globalization.CultureInfo.InvariantCulture),
                "-x265-params", "keyint=1:min-keyint=1:info=0:log-level=error:repeat-headers=1",
                "-f", "hevc", hevc,
            ];
            var result = ProcessRunner.RunAsync(ffmpeg, args, new ProcessOptions { Timeout = TimeSpan.FromMinutes(5) }, ct).GetAwaiter().GetResult();
            ct.ThrowIfCancellationRequested();
            if (!result.Success || !File.Exists(hevc))
                throw new InvalidOperationException("FFmpeg could not encode the HEIC picture: " + result.LastMessage);

            return HeifContainer.Build(new HeifContainer.Picture(
                File.ReadAllBytes(hevc), codedWidth, codedHeight, width, height,
                ExifTiff(source), source.GetColorProfile()?.ToByteArray()));
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>The EXIF block as a TIFF stream, with the orientation reset (the pixels are already upright).</summary>
    private static byte[]? ExifTiff(MagickImage image)
    {
        var exif = image.GetExifProfile();
        if (exif is null) return null;
        var copy = new ExifProfile(exif.ToByteArray() ?? []);
        if (copy.GetValue(ExifTag.Orientation) is not null) copy.SetValue(ExifTag.Orientation, (ushort)1);
        copy.RemoveThumbnail();
        var bytes = copy.ToByteArray();
        if (bytes is null || bytes.Length == 0) return null;
        ReadOnlySpan<byte> header = "Exif\0\0"u8;
        return bytes.AsSpan().StartsWith(header) ? bytes[header.Length..] : bytes;
    }
}
