using System.IO.Compression;
using System.Text;
using DropFlow.Core.Operations;
using DropFlow.Engines.Common;
using Xunit;

namespace DropFlow.TestSupport;

/// <summary>
/// A fact that is skipped (not failed, not silently passed) when the external engine it needs is not installed.
/// Use for integration tests that really run ffmpeg/qpdf/LibreOffice/…; pure-library tests should use a normal [Fact].
/// </summary>
public sealed class EngineFactAttribute : FactAttribute
{
    private static readonly EngineLocator Locator =
        new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DropFlow"));

    public EngineFactAttribute(EngineId engine)
    {
        if (Locator.Locate(engine) is null)
            Skip = $"{engine} is not installed (install it from the DropFlow engine installer or winget to run this test).";
    }
}

public static class Engines
{
    public static readonly EngineLocator Locator =
        new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DropFlow"));
}

/// <summary>A throw-away folder for one test.</summary>
public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dropflow-test-" + Guid.NewGuid().ToString("N"));

    public TempDir() => Directory.CreateDirectory(Path);

    public string Combine(string name) => System.IO.Path.Combine(Path, name);

    public string WriteText(string name, string content)
    {
        var p = Combine(name);
        File.WriteAllText(p, content, new UTF8Encoding(false));
        return p;
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { /* best effort */ }
    }
}

/// <summary>Generates small, valid sample files in code so tests need no binary fixtures.</summary>
public static class Samples
{
    /// <summary>RGB gradient PNG (decodable by any PNG reader).</summary>
    public static string Png(string path, int width = 64, int height = 48)
    {
        using var raw = new MemoryStream();
        for (var y = 0; y < height; y++)
        {
            raw.WriteByte(0); // filter: none
            for (var x = 0; x < width; x++)
            {
                raw.WriteByte((byte)(x * 255 / Math.Max(1, width - 1)));
                raw.WriteByte((byte)(y * 255 / Math.Max(1, height - 1)));
                raw.WriteByte(128);
            }
        }

        using var idat = new MemoryStream();
        using (var z = new ZLibStream(idat, CompressionLevel.Optimal, leaveOpen: true)) z.Write(raw.ToArray());

        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        WriteBigEndian(ihdr, 0, (uint)width);
        WriteBigEndian(ihdr, 4, (uint)height);
        ihdr[8] = 8; ihdr[9] = 2; // 8-bit RGB
        Chunk(png, "IHDR", ihdr);
        Chunk(png, "IDAT", idat.ToArray());
        Chunk(png, "IEND", []);
        File.WriteAllBytes(path, png.ToArray());
        return path;
    }

    /// <summary>16-bit mono PCM WAV containing a sine tone.</summary>
    public static string Wav(string path, double seconds = 1, int sampleRate = 8000, double frequency = 440)
    {
        var samples = (int)(seconds * sampleRate);
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8); w.Write(36 + samples * 2); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(sampleRate); w.Write(sampleRate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(samples * 2);
        for (var i = 0; i < samples; i++)
            w.Write((short)(Math.Sin(2 * Math.PI * frequency * i / sampleRate) * short.MaxValue * 0.5));
        w.Flush();
        File.WriteAllBytes(path, ms.ToArray());
        return path;
    }

    /// <summary>A valid multi-page PDF whose page <c>n</c> shows the text "Page n" (extractable text, Helvetica).</summary>
    public static string Pdf(string path, int pages = 1, Func<int, string>? text = null)
    {
        text ??= n => $"Page {n}";
        var objects = new List<string> { "<< /Type /Catalog /Pages 2 0 R >>" };
        var kids = string.Join(" ", Enumerable.Range(0, pages).Select(i => $"{3 + i * 2} 0 R"));
        objects.Add($"<< /Type /Pages /Kids [{kids}] /Count {pages} >>");
        var fontId = 3 + pages * 2;
        for (var i = 0; i < pages; i++)
        {
            objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents {4 + i * 2} 0 R /Resources << /Font << /F1 {fontId} 0 R >> >> >>");
            var content = $"BT /F1 24 Tf 72 700 Td ({Escape(text(i + 1))}) Tj ET";
            objects.Add($"<< /Length {content.Length} >>\nstream\n{content}\nendstream");
        }
        objects.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");

        var sb = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(sb.Length);
            sb.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        var xref = sb.Length;
        sb.Append($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append($"{o:D10} 00000 n \n");
        sb.Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        File.WriteAllText(path, sb.ToString(), Encoding.ASCII);
        return path;
    }

    public static string Srt(string path) =>
        Write(path, "1\n00:00:01,000 --> 00:00:02,500\nHello world\n\n2\n00:00:03,000 --> 00:00:04,000\nSecond line\n");

    private static string Write(string path, string content)
    {
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return path;
    }

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");

    private static void WriteBigEndian(byte[] buf, int offset, uint value)
    {
        buf[offset] = (byte)(value >> 24); buf[offset + 1] = (byte)(value >> 16);
        buf[offset + 2] = (byte)(value >> 8); buf[offset + 3] = (byte)value;
    }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4];
        WriteBigEndian(len, 0, (uint)data.Length);
        s.Write(len);
        var typeBytes = Encoding.ASCII.GetBytes(type);
        s.Write(typeBytes);
        s.Write(data);
        var crc = new byte[4];
        WriteBigEndian(crc, 0, Crc32(typeBytes, data));
        s.Write(crc);
    }

    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
    {
        var c = (uint)n;
        for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    private static uint Crc32(byte[] type, byte[] data)
    {
        var c = 0xFFFFFFFFu;
        foreach (var b in type) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        foreach (var b in data) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }
}
