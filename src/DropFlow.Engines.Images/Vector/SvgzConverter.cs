// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO.Compression;
using System.Text;
using DropFlow.Core.Operations;

namespace DropFlow.Engines.Images.Vector;

/// <summary>
/// <c>convert:svgz</c> for an SVG (gzip it) and <c>convert:svg</c> for an SVGZ (unzip it). The drawing itself is not touched, so the
/// result is exactly the same picture. Both directions check that the content is an SVG document, so a renamed file fails clearly.
/// </summary>
internal sealed class SvgzConverter() : ImageTool("convert:svg", "convert:svgz")
{
    /// <summary>Gzip streams are refused beyond this size when unzipping (a "zip bomb" guard; real drawings are far smaller).</summary>
    private const long MaxSvgBytes = 256L * 1024 * 1024;

    public override bool Supports(string operation, string inputExtension) =>
        (operation == "convert:svgz" && inputExtension == "svg") || (operation == "convert:svg" && inputExtension == "svgz");

    protected override IReadOnlyList<string> Process(ConversionJob job, string input, CancellationToken ct, out string? note)
    {
        note = null;
        var name = Path.GetFileName(input);
        var data = File.ReadAllBytes(input);
        byte[] result;
        string ext;
        if (job.Operation == "convert:svgz")
        {
            RequireSvg(data, name);
            result = Gzip(data);
            ext = "svgz";
        }
        else
        {
            result = Gunzip(data, name, ct);
            RequireSvg(result, name);
            ext = "svg";
        }

        ct.ThrowIfCancellationRequested();
        return [ImageIo.SaveBytes(result, ImageIo.OutputFor(input, ext, "converted", job.OutputDirectory))];
    }

    public static byte[] Gzip(byte[] data)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true)) gzip.Write(data);
        return output.ToArray();
    }

    private static byte[] Gunzip(byte[] data, string name, CancellationToken ct)
    {
        if (data.Length < 2 || data[0] != 0x1F || data[1] != 0x8B) throw new InvalidDataException($"{name} is not a compressed SVG (no gzip header).");
        using var gzip = new GZipStream(new MemoryStream(data), CompressionMode.Decompress);
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        try
        {
            int read;
            while ((read = gzip.Read(buffer)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                output.Write(buffer, 0, read);
                if (output.Length > MaxSvgBytes) throw new InvalidDataException($"{name} unpacks to more than {MaxSvgBytes / (1024 * 1024)} MB.");
            }
        }
        catch (InvalidDataException ex) when (!ex.Message.StartsWith(name, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"{name} is damaged ({ex.Message}).");
        }
        return output.ToArray();
    }

    /// <summary>A light check that the bytes are an SVG document: an &lt;svg element within the first 64 KB.</summary>
    private static void RequireSvg(byte[] data, string name)
    {
        var head = Encoding.UTF8.GetString(data, 0, Math.Min(data.Length, 65536));
        if (!head.Contains("<svg", StringComparison.Ordinal)) throw new InvalidDataException($"{name} does not contain an SVG drawing.");
    }
}
