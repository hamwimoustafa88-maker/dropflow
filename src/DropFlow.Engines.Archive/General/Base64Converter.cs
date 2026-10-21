using System.Diagnostics;
using System.Text;
using DropFlow.Core.Operations;
using DropFlow.Core.Output;

namespace DropFlow.Engines.Archive.General;

public enum Base64Mode { Auto, Encode, Decode }

/// <summary>
/// <c>gen.base64</c>. Encode: any file becomes <c>name (base64).txt</c> (UTF-8, one line, or wrapped at <c>lineWidth</c>).
/// Decode: a text file holding Base64 (plain, line-wrapped, URL-safe, or a <c>data:...;base64,</c> URI) becomes the original
/// bytes as <c>name (decoded).ext</c>, where the extension is recognised from the content (png, jpg, pdf, zip, ...; otherwise
/// <c>txt</c> for text and <c>bin</c> for anything else). Executable and script types are never given a runnable extension.
/// Option <c>mode</c>: <c>auto</c> (default), <c>encode</c>, <c>decode</c>. In auto mode only <c>.b64</c>, <c>.base64</c> and
/// <c>.txt</c> files whose whole content is valid Base64 are decoded; everything else is encoded.
/// </summary>
public sealed class Base64Converter : IConverter
{
    public const string OperationId = "gen.base64";

    private static readonly ToolOption ModeOption = new("mode", ToolOptionKind.Choice, "auto", ["auto", "encode", "decode"]);
    private static readonly ToolOption WidthOption = new("lineWidth", ToolOptionKind.Number, 0, null, 0, 1000);

    public IReadOnlyCollection<string> Operations { get; } = [OperationId];
    public bool Supports(string operation, string inputExtension) => operation == OperationId;
    public EngineId EngineFor(string operation) => EngineId.None;
    public IReadOnlyList<ToolOption> OptionsFor(string operation) => [ModeOption, WidthOption];

    public async Task<ConversionResult> RunAsync(ConversionJob job, IProgress<JobProgress> progress, CancellationToken cancellationToken)
    {
        if (job.Inputs.Count == 0) return ConversionResult.Fail("No input file.");
        var input = job.Inputs[0];
        if (Directory.Exists(input)) return ConversionResult.Fail("Folders cannot be Base64-encoded. Select a file.");
        if (!File.Exists(input)) return ConversionResult.Fail($"File not found: {input}");

        var modeText = job.GetString("mode", "auto")!.Trim().ToLowerInvariant();
        if (!Enum.TryParse<Base64Mode>(modeText, ignoreCase: true, out var mode)) return ConversionResult.Fail($"Unknown mode '{modeText}'. Use auto, encode or decode.");

        int width;
        try { width = Math.Clamp(job.GetInt("lineWidth", 0), 0, 1000); }
        catch (Exception ex) when (ex is FormatException or OverflowException or InvalidCastException) { return ConversionResult.Fail("lineWidth must be a number."); }

        var sw = Stopwatch.StartNew();
        var created = new List<string>();
        try
        {
            if (mode == Base64Mode.Auto)
                mode = await Base64Text.LooksEncodedAsync(input, cancellationToken).ConfigureAwait(false) ? Base64Mode.Decode : Base64Mode.Encode;

            var outputs = mode == Base64Mode.Encode
                ? await EncodeAsync(job, input, width, progress, created, cancellationToken).ConfigureAwait(false)
                : await DecodeAsync(job, input, progress, created, cancellationToken).ConfigureAwait(false);

            progress.Report(new JobProgress(1, Path.GetFileName(outputs[0])));
            return ConversionResult.Ok(outputs, sw.Elapsed);
        }
        catch (OperationCanceledException)
        {
            Cleanup(created);
            throw;
        }
        catch (FormatException ex)
        {
            Cleanup(created);
            return ConversionResult.Fail($"'{Path.GetFileName(input)}' is not valid Base64: {ex.Message}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Cleanup(created);
            return ConversionResult.Fail($"Could not process '{Path.GetFileName(input)}': {ex.Message}");
        }
    }

    private static async Task<IReadOnlyList<string>> EncodeAsync(
        ConversionJob job, string input, int width, IProgress<JobProgress> progress, List<string> created, CancellationToken ct)
    {
        await using var source = new FileStream(input, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, useAsync: true);
        var (path, target) = CreateOutput(job, input, "txt", "base64");
        created.Add(path);

        await using (target)
        {
            const int block = 3 * 16384; // a multiple of 3 so every block but the last encodes without padding
            var buffer = new byte[block];
            var chars = new char[block / 3 * 4];
            var writer = new StreamWriter(target, new UTF8Encoding(false), 1 << 16, leaveOpen: true);
            var column = 0;
            long done = 0;

            while (true)
            {
                var filled = await ReadFullAsync(source, buffer, ct).ConfigureAwait(false);
                if (filled == 0) break;

                Convert.TryToBase64Chars(buffer.AsSpan(0, filled), chars, out var count);
                if (width <= 0) writer.Write(chars, 0, count);
                else WriteWrapped(writer, chars, count, width, ref column);

                done += filled;
                if (source.Length > 0) progress.Report(new JobProgress(Math.Min(0.99, (double)done / source.Length)));
                if (filled < block) break;
            }

            await writer.FlushAsync(ct).ConfigureAwait(false);
        }

        return [path];
    }

    private static void WriteWrapped(StreamWriter writer, char[] chars, int count, int width, ref int column)
    {
        var offset = 0;
        while (offset < count)
        {
            var take = Math.Min(width - column, count - offset);
            writer.Write(chars, offset, take);
            offset += take;
            column += take;
            if (column >= width)
            {
                writer.Write('\n');
                column = 0;
            }
        }
    }

    private static async Task<IReadOnlyList<string>> DecodeAsync(
        ConversionJob job, string input, IProgress<JobProgress> progress, List<string> created, CancellationToken ct)
    {
        await using var source = new FileStream(input, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, useAsync: true);
        using var reader = new StreamReader(source, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, 1 << 16, leaveOpen: true);

        var decoder = new Base64Text.StreamDecoder();
        var charBuffer = new char[1 << 16];
        Stream? target = null;
        string? path = null;
        var firstBytes = new byte[0];

        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var read = await reader.ReadAsync(charBuffer.AsMemory(), ct).ConfigureAwait(false);
                var end = read == 0;

                var decoded = end ? decoder.Finish() : decoder.Add(charBuffer.AsSpan(0, read));
                if (decoded.Length > 0 || (end && target is null))
                {
                    if (target is null)
                    {
                        firstBytes = decoded;
                        var extension = decoder.MimeExtension ?? FileTypeSniffer.Detect(decoded);
                        (path, target) = CreateOutput(job, input, extension, "decoded");
                        created.Add(path);
                    }
                    await target.WriteAsync(decoded, ct).ConfigureAwait(false);
                }

                if (source.Length > 0) progress.Report(new JobProgress(Math.Min(0.99, (double)source.Position / source.Length)));
                if (end) break;
            }

            if (firstBytes.Length == 0 && target is not null && target.Length == 0)
                throw new FormatException("the file holds no Base64 data");
        }
        finally
        {
            if (target is not null) await target.DisposeAsync().ConfigureAwait(false);
        }

        return [path!];
    }

    private static (string Path, FileStream Stream) CreateOutput(ConversionJob job, string input, string extension, string suffix)
    {
        var resolver = new OutputPathResolver();
        for (var attempt = 0; ; attempt++)
        {
            var path = resolver.ForFile(input, extension, suffix, job.OutputDirectory);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            try
            {
                return (path, new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true));
            }
            catch (IOException) when (attempt < 8 && File.Exists(path))
            {
                // taken in the meantime: try the next free name
            }
        }
    }

    private static async Task<int> ReadFullAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(total), ct).ConfigureAwait(false);
            if (n == 0) break;
            total += n;
        }
        return total;
    }

    private static void Cleanup(List<string> created)
    {
        foreach (var path in created)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best effort */ }
        }
    }
}

/// <summary>Base64 text handling: the auto-mode decision and a chunked decoder that tolerates whitespace, URL-safe input and data: URIs.</summary>
public static class Base64Text
{
    private static readonly HashSet<string> DecodableExtensions = new(StringComparer.OrdinalIgnoreCase) { "txt", "b64", "base64" };

    /// <summary>Auto mode: should this file be decoded rather than encoded?</summary>
    public static async Task<bool> LooksEncodedAsync(string path, CancellationToken ct)
    {
        var ext = Path.GetExtension(path).TrimStart('.');
        if (!DecodableExtensions.Contains(ext)) return false;

        await using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, useAsync: true);
        using var reader = new StreamReader(source, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, 1 << 16, leaveOpen: true);

        var buffer = new char[1 << 16];
        long significant = 0;
        var distinctive = false;       // a digit or one of + / = - _ : a file of plain letters is a word, not Base64
        var padChars = 0;
        var dataUri = false;
        var first = true;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var read = await reader.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
            if (read == 0) break;

            var start = 0;
            if (first)
            {
                first = false;
                var head = new string(buffer, 0, Math.Min(read, 300));
                var marker = head.IndexOf(";base64,", StringComparison.OrdinalIgnoreCase);
                if (head.TrimStart().StartsWith("data:", StringComparison.OrdinalIgnoreCase) && marker >= 0)
                {
                    dataUri = true;
                    start = marker + ";base64,".Length;
                }
            }

            for (var i = start; i < read; i++)
            {
                var c = buffer[i];
                if (char.IsWhiteSpace(c)) continue;
                if (c == '=')
                {
                    if (++padChars > 2) return false;
                    distinctive = true;
                    significant++;
                    continue;
                }
                if (padChars > 0) return false;                  // data after the '=' padding
                if (!IsBase64Char(c)) return false;
                if (!char.IsAsciiLetter(c)) distinctive = true;
                significant++;
            }
        }

        if (dataUri) return significant > 0;
        if (significant < 4) return false;
        if (!ext.Equals("txt", StringComparison.OrdinalIgnoreCase)) return true;

        // A .txt is only decoded when it clearly is Base64: our own "(base64)" output, or enough distinctive content.
        var name = Path.GetFileNameWithoutExtension(path);
        var ours = name.EndsWith("(base64)", StringComparison.OrdinalIgnoreCase)
                   || (name.Contains("(base64 ", StringComparison.OrdinalIgnoreCase) && name.EndsWith(')'));
        return ours || (significant >= 16 && distinctive);
    }

    internal static bool IsBase64Char(char c) => char.IsAsciiLetterOrDigit(c) || c is '+' or '/' or '-' or '_';

    /// <summary>Decodes Base64 text arriving in arbitrary chunks. Whitespace is ignored; padding may only appear at the very end.</summary>
    public sealed class StreamDecoder
    {
        private readonly StringBuilder _pending = new();
        private bool _decided;
        private bool _padded;

        /// <summary>File extension taken from a <c>data:</c> URI's media type, when there was one.</summary>
        public string? MimeExtension { get; private set; }

        public byte[] Add(ReadOnlySpan<char> chunk)
        {
            foreach (var c in chunk)
                if (!char.IsWhiteSpace(c)) _pending.Append(c);

            if (!_decided && !TryDecidePrefix(final: false)) return [];
            return Drain(final: false);
        }

        public byte[] Finish()
        {
            if (!_decided) TryDecidePrefix(final: true);
            return Drain(final: true);
        }

        /// <summary>Looks for a leading "data:...;base64," and drops it. Returns false while more input is needed to decide.</summary>
        private bool TryDecidePrefix(bool final)
        {
            var head = _pending.ToString(0, Math.Min(_pending.Length, 300));
            if (head.Length < 5 && !final)
            {
                if ("data:".StartsWith(head, StringComparison.OrdinalIgnoreCase)) return false;
                _decided = true;
                return true;
            }

            if (!head.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                _decided = true;
                return true;
            }

            var marker = head.IndexOf(";base64,", StringComparison.OrdinalIgnoreCase);
            if (marker >= 0)
            {
                MimeExtension = FileTypeSniffer.ExtensionForMime(head[5..marker]);
                _pending.Remove(0, marker + ";base64,".Length);
                _decided = true;
                return true;
            }

            if (final || head.Length >= 300) throw new FormatException("malformed data: URI (no ';base64,' marker)");
            return false;
        }

        private byte[] Drain(bool final)
        {
            if (_pending.Length == 0) return [];
            var text = _pending.ToString();

            // Decode complete groups of four characters now; keep the remainder for the next chunk.
            var take = final ? text.Length : text.Length - text.Length % 4;
            if (take == 0) return [];
            var piece = text[..take].Replace('-', '+').Replace('_', '/');
            _pending.Remove(0, take);

            if (_padded) throw new FormatException("data found after the '=' padding");
            var firstPad = piece.IndexOf('=');
            if (firstPad >= 0)
            {
                if (piece.AsSpan(firstPad).ContainsAnyExcept('=') || piece.Length - firstPad > 2) throw new FormatException("misplaced '=' padding");
                _padded = true;
            }

            if (final && piece.Length % 4 != 0)
            {
                if (piece.Length % 4 == 1) throw new FormatException("the length is not valid for Base64");
                piece = piece.PadRight(piece.Length + (4 - piece.Length % 4), '='); // unpadded input is common
            }

            return Convert.FromBase64String(piece);
        }
    }
}

/// <summary>Recognises a few safe file types from their first bytes so decoded data gets a useful extension.</summary>
public static class FileTypeSniffer
{
    public static string Detect(ReadOnlySpan<byte> head)
    {
        if (head.Length >= 8 && head[..8].SequenceEqual<byte>([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A])) return "png";
        if (head.Length >= 3 && head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF) return "jpg";
        if (StartsWith(head, "GIF87a") || StartsWith(head, "GIF89a")) return "gif";
        if (StartsWith(head, "%PDF-")) return "pdf";
        if (StartsWith(head, "PK\u0003\u0004") || StartsWith(head, "PK\u0005\u0006")) return "zip";
        if (head.Length >= 6 && head[..6].SequenceEqual<byte>([0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C])) return "7z";
        if (StartsWith(head, "Rar!\u001A\u0007")) return "rar";
        if (head.Length >= 2 && head[0] == 0x1F && head[1] == 0x8B) return "gz";
        if (head.Length >= 12 && StartsWith(head, "RIFF"))
        {
            if (Slice(head, 8, 4) == "WEBP") return "webp";
            if (Slice(head, 8, 4) == "WAVE") return "wav";
            if (Slice(head, 8, 4) == "AVI ") return "avi";
        }
        if (head.Length >= 4 && ((StartsWith(head, "II") && head[2] == 0x2A && head[3] == 0) || (StartsWith(head, "MM") && head[2] == 0 && head[3] == 0x2A))) return "tiff";
        if (StartsWith(head, "ID3")) return "mp3";
        if (StartsWith(head, "OggS")) return "ogg";
        if (StartsWith(head, "fLaC")) return "flac";
        if (head.Length >= 12 && Slice(head, 4, 4) == "ftyp") return Slice(head, 8, 4) == "qt  " ? "mov" : "mp4";
        if (head.Length >= 4 && head[0] == 0x1A && head[1] == 0x45 && head[2] == 0xDF && head[3] == 0xA3) return "mkv";
        if (head.Length >= 4 && head[0] == 0 && head[1] == 0 && head[2] == 1 && head[3] == 0) return "ico";
        if (head.Length >= 26 && head[0] == (byte)'B' && head[1] == (byte)'M' && head[6] == 0 && head[7] == 0 && head[8] == 0 && head[9] == 0
            && head[14] is 12 or 40 or 56 or 108 or 124) return "bmp";

        return IsText(head) ? "txt" : "bin";
    }

    /// <summary>Extension for a media type from a data URI, restricted to the same safe set.</summary>
    public static string? ExtensionForMime(string mime) => mime.Split(';')[0].Trim().ToLowerInvariant() switch
    {
        "image/png" => "png",
        "image/jpeg" or "image/jpg" => "jpg",
        "image/gif" => "gif",
        "image/webp" => "webp",
        "image/bmp" => "bmp",
        "image/tiff" => "tiff",
        "image/x-icon" or "image/vnd.microsoft.icon" => "ico",
        "application/pdf" => "pdf",
        "application/zip" => "zip",
        "audio/mpeg" => "mp3",
        "audio/wav" or "audio/x-wav" => "wav",
        "audio/ogg" => "ogg",
        "video/mp4" => "mp4",
        "text/plain" => "txt",
        _ => null,
    };

    private static bool StartsWith(ReadOnlySpan<byte> head, string ascii)
    {
        if (head.Length < ascii.Length) return false;
        for (var i = 0; i < ascii.Length; i++)
            if (head[i] != (byte)ascii[i]) return false;
        return true;
    }

    private static string Slice(ReadOnlySpan<byte> head, int start, int length) => Encoding.ASCII.GetString(head.Slice(start, length));

    /// <summary>True when the bytes are valid UTF-8 without control characters other than tab, CR and LF.</summary>
    private static bool IsText(ReadOnlySpan<byte> head)
    {
        if (head.Length == 0) return false;
        var sample = head.Length > 4096 ? head[..4096] : head;
        try
        {
            var text = new UTF8Encoding(false, true).GetString(sample);
            // a sample cut in the middle of a character would throw above, which is fine: treat that as binary
            return !text.Any(c => char.IsControl(c) && c is not ('\t' or '\r' or '\n'));
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }
}
