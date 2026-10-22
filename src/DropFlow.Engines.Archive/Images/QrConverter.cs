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
/// <item><c>create</c>: makes <c>name (qr).png</c> and/or <c>name (qr).svg</c> from option <c>text</c> (a URL or any text) or, when empty,
/// from the input <c>.txt</c> file. Options <c>errorCorrection</c> (L, M default, Q, H, auto = H with a logo else M), <c>size</c> (pixels,
/// default 512), <c>quietZone</c> (modules, default 4), and the style: <c>modules</c>, <c>eyes</c>, <c>color</c>, <c>color2</c> (gradient end),
/// <c>eyeColor</c>, <c>background</c> ("transparent" allowed), <c>logo</c> (picture path) and <c>format</c> (png, svg, both).
/// A plain PNG is drawn by <see cref="QrCodeService"/> exactly as before; anything styled (and every SVG) by <see cref="QrDesigner"/>.</item>
/// </list>
/// When <c>mode</c> is missing it is inferred: a .txt input creates, an image reads.
/// </summary>
public sealed class QrConverter : IConverter
{
    public const string OperationId = "img.qr";

    private static readonly string[] ImageExtensions = ["jpg", "png", "webp", "heic", "tiff", "avif", "bmp", "gif", "svg", "ico"];

    private static readonly string[] ModuleChoices = ["square", "rounded", "dots", "diamond", "fluid", "bars"];
    private static readonly string[] EyeChoices = ["square", "rounded", "circle", "leaf"];
    private static readonly string[] FormatChoices = ["png", "svg", "both"];

    private static readonly IReadOnlyList<ToolOption> Options =
    [
        new("mode", ToolOptionKind.Choice, "read", ["read", "create"]),
        new("text", ToolOptionKind.Text, ""),
        new("errorCorrection", ToolOptionKind.Choice, "M", ["L", "M", "Q", "H", "auto"]),
        new("size", ToolOptionKind.Number, 512, null, QrCodeService.MinSize, QrCodeService.MaxSize),
        new("quietZone", ToolOptionKind.Number, 4, null, 0, 16),
        new("modules", ToolOptionKind.Choice, "square", ModuleChoices),
        new("eyes", ToolOptionKind.Choice, "square", EyeChoices),
        new("color", ToolOptionKind.Text, "#000000"),
        new("color2", ToolOptionKind.Text, ""),
        new("eyeColor", ToolOptionKind.Text, ""),
        new("background", ToolOptionKind.Text, "#ffffff"),
        new("logo", ToolOptionKind.Text, ""),
        new("format", ToolOptionKind.Choice, "png", FormatChoices),
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

        var levelText = (job.GetString("errorCorrection") ?? "M").Trim();
        QrErrorCorrection? level = null;
        if (!levelText.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            if (!Enum.TryParse<QrErrorCorrection>(levelText, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
                return ConversionResult.Fail($"Unknown error-correction level '{levelText}'. Use L, M, Q, H or auto.");
            level = parsed;
        }

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

        if (!TryChoice(job, "modules", ModuleChoices, out var modules, out var error)
            || !TryChoice(job, "eyes", EyeChoices, out var eyes, out error)
            || !TryChoice(job, "format", FormatChoices, out var format, out error))
            return ConversionResult.Fail(error);

        var logo = Blank(job.GetString("logo"));
        if (logo is not null && !File.Exists(logo)) return ConversionResult.Fail($"Logo not found: {logo}");

        var style = new QrStyle(
            Modules: Enum.Parse<QrModuleShape>(modules, ignoreCase: true),
            Eyes: Enum.Parse<QrEyeShape>(eyes, ignoreCase: true),
            Foreground: (job.GetString("color") ?? "").Trim() is { Length: > 0 } color ? color : "#000000",
            Foreground2: Blank(job.GetString("color2")),
            EyeColor: Blank(job.GetString("eyeColor")),
            Background: (job.GetString("background") ?? "").Trim() is { Length: > 0 } background ? background : "#ffffff",
            LogoPath: logo);
        var design = new QrDesign(text, style, level, size, quiet);

        // Everything is rendered before anything is written, so a failure leaves no half result behind.
        var files = new List<(string Extension, byte[] Content)>();
        string? note = null;
        try
        {
            if (format is "png" or "both")
            {
                var plain = style == new QrStyle();
                var png = plain
                    ? QrCodeService.CreatePng(text, new QrCreateOptions(QrDesigner.EffectiveLevel(design), size, quiet))
                    : QrDesigner.RenderPng(design);
                if (!plain && !QrDesigner.Check(png, text).Scans)
                    note = "This design may be hard to scan. Try darker colours, a smaller logo or a higher error-correction level.";
                files.Add(("png", png));
            }
            if (format is "svg" or "both") files.Add(("svg", new UTF8Encoding(false).GetBytes(QrDesigner.RenderSvg(design))));
        }
        catch (ArgumentException ex)
        {
            return ConversionResult.Fail(ex.Message);
        }

        var outputs = new List<string>();
        try
        {
            foreach (var (extension, content) in files) outputs.Add(WriteNew(job, input, extension, content));
        }
        catch
        {
            foreach (var written in outputs) TryDelete(written);
            throw;
        }
        return ConversionResult.Ok(outputs, default, note);
    }

    private static bool TryChoice(ConversionJob job, string key, string[] choices, out string value, out string error)
    {
        value = (job.GetString(key) ?? choices[0]).Trim().ToLowerInvariant();
        if (value.Length == 0) value = choices[0];
        error = choices.Contains(value) ? "" : $"Unknown {key} '{value}'. Use {string.Join(", ", choices)}.";
        return error.Length == 0;
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best effort */ }
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
