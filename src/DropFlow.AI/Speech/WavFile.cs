using System.Buffers.Binary;
using System.Text;

namespace DropFlow.AI.Speech;

/// <param name="DataOffset">Byte offset of the first sample in the file.</param>
/// <param name="DataLength">Bytes of sample data (clamped to what the file really contains).</param>
public sealed record WavInfo(int SampleRate, int Channels, int BitsPerSample, bool IsFloat, long DataOffset, long DataLength)
{
    /// <summary>Whisper's input: 16 kHz, mono, 16-bit integer PCM.</summary>
    public bool IsWhisperReady => SampleRate == 16000 && Channels == 1 && BitsPerSample == 16 && !IsFloat;

    public TimeSpan Duration => Channels <= 0 || SampleRate <= 0 || BitsPerSample <= 0
        ? TimeSpan.Zero
        : TimeSpan.FromSeconds((double)DataLength / (Channels * (BitsPerSample / 8) * SampleRate));
}

/// <summary>Minimal RIFF/WAVE reader for the one format Whisper needs (what ffmpeg is asked to produce).</summary>
public static class WavFile
{
    /// <summary>Parses the header chunks. Returns null when the file is not a PCM or float WAV.</summary>
    public static WavInfo? TryReadInfo(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return TryReadInfo(stream);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    public static WavInfo? TryReadInfo(Stream stream)
    {
        Span<byte> header = stackalloc byte[12];
        if (stream.Read(header) != 12 || !header[..4].SequenceEqual("RIFF"u8) || !header[8..12].SequenceEqual("WAVE"u8)) return null;

        int rate = 0, channels = 0, bits = 0;
        var isFloat = false;
        var haveFormat = false;
        Span<byte> chunk = stackalloc byte[8];
        while (stream.Read(chunk) == 8)
        {
            var id = Encoding.ASCII.GetString(chunk[..4]);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..]);
            switch (id)
            {
                case "fmt ":
                {
                    if (size < 16) return null;
                    var body = new byte[Math.Min(size, 40)];
                    stream.ReadExactly(body);
                    var tag = BinaryPrimitives.ReadUInt16LittleEndian(body);
                    channels = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(2));
                    rate = (int)BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(4));
                    bits = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(14));
                    if (tag == 0xFFFE && body.Length >= 26) tag = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(24)); // extensible: sub-format
                    if (tag is not (1 or 3)) return null; // only PCM and IEEE float
                    isFloat = tag == 3;
                    haveFormat = true;
                    stream.Seek(size - body.Length + (size & 1), SeekOrigin.Current);
                    break;
                }
                case "data":
                {
                    if (!haveFormat) return null;
                    var available = stream.Length - stream.Position;
                    var length = size == uint.MaxValue || size > available ? available : size; // streamed WAVs declare 0xFFFFFFFF
                    return new WavInfo(rate, channels, bits, isFloat, stream.Position, length);
                }
                default:
                    stream.Seek(size + (size & 1), SeekOrigin.Current);
                    break;
            }
        }
        return null;
    }

    /// <summary>Reads 16 kHz mono 16-bit PCM as floats in [-1, 1]. Throws <see cref="InvalidDataException"/> for any other format.</summary>
    public static float[] ReadWhisperSamples(string path, CancellationToken cancellationToken = default)
    {
        var info = TryReadInfo(path) ?? throw new InvalidDataException("Not a PCM WAV file.");
        if (!info.IsWhisperReady)
            throw new InvalidDataException($"Expected 16 kHz mono 16-bit PCM but found {info.SampleRate} Hz, {info.Channels} ch, {info.BitsPerSample}-bit{(info.IsFloat ? " float" : "")}.");

        var samples = new float[info.DataLength / 2];
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        stream.Seek(info.DataOffset, SeekOrigin.Begin);

        var buffer = new byte[1 << 20];
        var written = 0;
        while (written < samples.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var want = Math.Min(buffer.Length, (samples.Length - written) * 2);
            var got = stream.Read(buffer, 0, want);
            if (got < 2) break;
            for (var i = 0; i + 1 < got; i += 2)
                samples[written++] = BinaryPrimitives.ReadInt16LittleEndian(buffer.AsSpan(i)) / 32768f;
        }
        return written == samples.Length ? samples : samples[..written];
    }
}
