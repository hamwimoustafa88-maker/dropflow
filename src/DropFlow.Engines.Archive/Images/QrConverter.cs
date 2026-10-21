using System.Diagnostics;
using System.Text;
using DropFlow.Core.Operations;
using DropFlow.Core.Output;
using ImageMagick;

namespace DropFlow.Engines.Archive.Images;

/// <summary>
/// <c>img.qr</c>. Option <c>mode</c>:
/// <list type="bullet">
/// <item><c>read</c> (default for images): decodes every QR code and barcode in the picture into <c>name (qr).txt</c>.
/// One result is written as plain text. Several are written as blocks "[QR_CODE]" followed by the text, separated by a blank line.</item>
/// <item><c>create</c>: makes <c>name (qr).png</c> from option <c>text</c> (a URL or any text) or, when empty, from the input
/// <c>.txt</c> file. Options <c>errorCorrection</c> (L, M default, Q, H), <c>size</c> (pixels, default 512), <c>quietZone</c> (modules, default 4).</item>
/// </list>
/// When <c>mode</c> is missing it is inferred: a .txt input creates, an image reads.
/// </summary>
public sealed class QrConverter : IConverter
{
    public const string OperationId = "img.qr";

    private static readonly string[] ImageExtensions = ["jpg", "png", "webp", "heic", "tiff", "avif", "bmp", "gif", "svg", "ico"];

    private static readonly IReadOnlyList<ToolOption> Options =
    [
        new("mode", ToolOptionKind.Choice, "read", ["read", "create"]),
        new("text", ToolOptionKind.Text, ""),
        new("errorCorrection", ToolOptionKind.Choice, "M", ["L", "M", "Q", "H"]),
        new("size", ToolOptionKind.Number, 512, null, QrCodeService.MinSize, QrCodeService.MaxSize),
        new("quietZone", ToolOptionKind.Number, 4, null, 0, 16),
    ];

    public IReadOnlyCollection<string> Operations { get; } = [OperationId];

    public bool Supports(string operation, string inputExtension)
    {
        if (operation != OperationId) return false;
        var ext = inputExtension.TrimStart('.').ToLowerInvariant();
        return ext == "txt" || ImageExtensions.Contains(ext);
    }

    public EngineId EngineFor(string operation) => EngineId.None;
    public IReadOnlyList<ToolOption> OptionsFor(string operation) => Options;

    public Task<ConversionResult> RunAsync(ConversionJob job, IProgress<JobProgress> progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(() => Run(job, progress, cancellationToken), cancellationToken);
    }

    private static ConversionResult Run(ConversionJob job, IProgress<JobProgress> progress, CancellationToken ct)
    {
        if (job.Inputs.Count == 0) return ConversionResult.Fail("No input file.");
        var input = job.Inputs[0];
        if (!File.Exists(input)) return ConversionResult.Fail($"File not found: {input}");

        var isText = Path.GetExtension(input).Equals(".txt", StringComparison.OrdinalIgnoreCase);
        var mode = (job.GetString("mode") ?? (isText ? "create" : "read")).Trim().ToLowerInvariant();
        if (mode is not ("read" or "create")) return ConversionResult.Fail($"Unknown mode '{mode}'. Use read or create.");
        if (mode == "read" && isText) return ConversionResult.Fail("A text file has no picture to read. Use mode=create to turn it into a QR code.");

        var sw = Stopwatch.StartNew();
        try
        {
            progress.Report(new JobProgress(0.1));
            var result = mode == "read" ? ReadQr(job, input, ct) : CreateQr(job, input, isText);
            progress.Report(new JobProgress(1));
            return result with { Duration = sw.Elapsed };
        }
        catch (MagickException ex)
        {
            return ConversionResult.Fail($"The image could not be read: {ex.Message}");
        }
        catch (QrTooLargeException ex)
        {
            return ConversionResult.Fail(ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ConversionResult.Fail($"Could not process '{Path.GetFileName(input)}': {ex.Message}");
        }
    }

    private static ConversionResult ReadQr(ConversionJob job, string input, CancellationToken ct)
    {
        var found = QrCodeService.Read(input, ct);
        if (found.Count == 0) return ConversionResult.Fail("No QR code or barcode was found in the image.");

        var text = found.Count == 1
            ? found[0].Text
            : string.Join("\n\n", found.Select(f => $"[{f.Format}]\n{f.Text}"));

        var output = WriteNew(job, input, "txt", new UTF8Encoding(false).GetBytes(text));
        return ConversionResult.Ok([output], default, found.Count > 1 ? $"{found.Count} codes found" : null);
    }

    private static ConversionResult CreateQr(ConversionJob job, string input, bool isText)
    {
        var text = job.GetString("text");
        if (string.IsNullOrEmpty(text) && isText)
        {
            using var reader = new StreamReader(input, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var buffer = new char[16 * 1024];
            var read = reader.ReadBlock(buffer, 0, buffer.Length);
            text = new string(buffer, 0, read).TrimEnd('\r', '\n');
        }

        if (string.IsNullOrEmpty(text)) return ConversionResult.Fail("Enter the text or URL to turn into a QR code.");

        var level = Enum.TryParse<QrErrorCorrection>(job.GetString("errorCorrection", "M"), ignoreCase: true, out var parsed) ? parsed : QrErrorCorrection.M;
        int size, quiet;
        try
        {
            size = job.GetInt("size", 512);
            quiet = job.GetInt("quietZone", 4);
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or InvalidCastException)
        {
            return ConversionResult.Fail("size and quietZone must be numbers.");
        }

        var png = QrCodeService.CreatePng(text, new QrCreateOptions(level, size, quiet));
        return ConversionResult.Ok([WriteNew(job, input, "png", png)]);
    }

    private static string WriteNew(ConversionJob job, string input, string extension, byte[] content)
    {
        var resolver = new OutputPathResolver();
        for (var attempt = 0; ; attempt++)
        {
            var path = resolver.ForFile(input, extension, "qr", job.OutputDirectory);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            try
            {
                using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                stream.Write(content);
                return path;
            }
            catch (IOException) when (attempt < 8 && File.Exists(path))
            {
                // taken in the meantime: next name
            }
        }
    }
}
