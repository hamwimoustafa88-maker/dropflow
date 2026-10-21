using DropFlow.Core.Operations;
using DropFlow.Engines.Common;
using DropFlow.Engines.Pdf.Infrastructure;

namespace DropFlow.Engines.Pdf.Tools;

/// <summary>Protect, unlock, repair and compress. qpdf does the PDF structure work; compression adds managed picture re-encoding.</summary>
internal sealed class QpdfTools(IEngineProvider engines, IEngineRunner runner)
{
    public static readonly string[] Levels = ["less", "recommended", "extreme"];

    public static readonly IReadOnlyList<ToolOption> ProtectOptions =
    [
        new("userPassword", ToolOptionKind.Password),
        new("ownerPassword", ToolOptionKind.Password),
        new("permissions", ToolOptionKind.Text, "print,copy,edit,annotate"),
    ];

    public static readonly IReadOnlyList<ToolOption> UnlockOptions =
    [
        new("password", ToolOptionKind.Password),
    ];

    public static readonly IReadOnlyList<ToolOption> CompressOptions =
    [
        new("level", ToolOptionKind.Choice, "recommended", Levels, Required: true),
    ];

    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(10);

    public IEnumerable<OperationSpec> Specs()
    {
        yield return OperationSpec.For("pdf.protect", "pdf", Protect, ProtectOptions);
        yield return OperationSpec.For("pdf.unlock", "pdf", Unlock, UnlockOptions);
        yield return OperationSpec.For("pdf.repair", "pdf", Repair);
        yield return OperationSpec.For("pdf.compress", "pdf", Compress, CompressOptions);
    }

    // ---- protect / unlock / repair --------------------------------------------------------------------------------------

    private async Task<ConversionResult> Protect(JobContext ctx)
    {
        var user = ctx.Job.GetString("userPassword") ?? "";
        var owner = ctx.Job.GetString("ownerPassword") ?? "";
        if (!QpdfPermissions.TryParse(ctx.Job.GetString("permissions"), out var permissions, out var error)) return ctx.Fail(error);
        if (user.Length == 0 && owner.Length == 0) return ctx.Fail("Enter a password to protect the PDF with.");

        var generatedOwner = false;
        if (owner.Length == 0)
        {
            owner = QpdfArguments.GenerateOwnerPassword(); // qpdf needs a non-empty owner secret for the permission bits to mean anything
            generatedOwner = true;
        }

        var qpdf = RequireQpdf();
        var temp = ctx.TempFile("protected.pdf");
        var result = await RunQpdf(qpdf, QpdfArguments.Protect(ctx.Input, temp, user, owner, permissions), ctx);
        if (!QpdfArguments.Succeeded(result, File.Exists(temp)))
        {
            var name = Path.GetFileName(ctx.Input);
            return result.StdErr.Contains("invalid password", StringComparison.OrdinalIgnoreCase)
                ? ctx.Fail($"{name} is already password-protected. Unlock it first (Unlock PDF), then protect it again.")
                : ctx.Fail(QpdfArguments.Explain(result, name));
        }

        var path = Publish(ctx, temp, "protected");
        ctx.Report(1, generatedOwner && permissions.RestrictsSomething
            ? "The permission restrictions are locked with a random owner password that is not stored. The PDF opens with the password you chose."
            : null);
        return ctx.Ok(path);
    }

    private async Task<ConversionResult> Unlock(JobContext ctx)
    {
        // The password must come from the user. Nothing here ever tries other passwords.
        var password = ctx.Job.GetString("password");
        var qpdf = RequireQpdf();
        var temp = ctx.TempFile("unlocked.pdf");
        var result = await RunQpdf(qpdf, QpdfArguments.Unlock(ctx.Input, temp, password), ctx);
        if (!QpdfArguments.Succeeded(result, File.Exists(temp)))
            return ctx.Fail(QpdfArguments.Explain(result, Path.GetFileName(ctx.Input)));

        return ctx.Ok(Publish(ctx, temp, "unlocked"));
    }

    private async Task<ConversionResult> Repair(JobContext ctx)
    {
        var qpdf = RequireQpdf();
        var temp = ctx.TempFile("repaired.pdf");
        var result = await RunQpdf(qpdf, QpdfArguments.Repair(ctx.Input, temp), ctx);
        if (!QpdfArguments.Succeeded(result, File.Exists(temp)))
            return ctx.Fail(QpdfArguments.Explain(result, Path.GetFileName(ctx.Input)));

        var path = Publish(ctx, temp, "repaired");
        ctx.Report(1, result.ExitCode == 3 ? "The file had problems that qpdf repaired; check the result." : null);
        return ctx.Ok(path);
    }

    // ---- compress -------------------------------------------------------------------------------------------------------

    private async Task<ConversionResult> Compress(JobContext ctx)
    {
        var level = (ctx.Job.GetString("level", "recommended") ?? "recommended").ToLowerInvariant();
        if (!Levels.Contains(level)) return ctx.Fail($"Unknown compression level '{level}'. Use less, recommended or extreme.");

        var originalSize = new FileInfo(ctx.Input).Length;
        string candidate;
        string? note = null;

        switch (level)
        {
            case "extreme":
                candidate = ctx.TempFile("extreme.pdf");
                PdfRasterCompressor.Rebuild(ctx.Input, candidate, ctx.Ct, f => ctx.Report(f * 0.95));
                note = "Extreme compression turned every page into a picture: the text is no longer selectable or searchable.";
                break;

            case "less":
                candidate = await OptimizeAsync(ctx, ctx.Input);
                break;

            default:
                candidate = await RecompressThenOptimizeAsync(ctx);
                if (new FileInfo(candidate).Length >= originalSize)
                {
                    // The picture pass did not help; plain lossless optimisation may still.
                    var lossless = await OptimizeAsync(ctx, ctx.Input);
                    if (new FileInfo(lossless).Length < new FileInfo(candidate).Length) candidate = lossless;
                }
                break;
        }

        var newSize = new FileInfo(candidate).Length;
        if (newSize >= originalSize)
        {
            return ctx.Fail(
                $"{Path.GetFileName(ctx.Input)} could not be made smaller ({FormatSize(originalSize)} stays {FormatSize(originalSize)}; " +
                $"the best attempt was {FormatSize(newSize)}). The original was kept unchanged.");
        }

        var path = Publish(ctx, candidate, "compressed");
        var saved = 100.0 * (originalSize - newSize) / originalSize;
        ctx.Report(1, $"{FormatSize(originalSize)} -> {FormatSize(newSize)} ({saved:0}% smaller). " + note);
        return ctx.Ok(path);
    }

    private async Task<string> RecompressThenOptimizeAsync(JobContext ctx)
    {
        var qpdf = RequireQpdf();
        var stage1 = ctx.TempFile("images.pdf");
        var outcome = PdfImageRecompressor.Recompress(ctx.Input, stage1, quality: 72, maxDpi: 150, ctx.Ct);
        ctx.Report(0.4, outcome.ImagesRecompressed > 0 ? $"{outcome.ImagesRecompressed} of {outcome.ImagesFound} pictures re-encoded." : null);

        var source = outcome.ImagesRecompressed > 0 ? stage1 : ctx.Input;
        return await OptimizeAsync(ctx, source, qpdf);
    }

    private async Task<string> OptimizeAsync(JobContext ctx, string source, string? qpdf = null)
    {
        qpdf ??= RequireQpdf();
        var temp = ctx.TempFile("optimized.pdf");
        var result = await RunQpdf(qpdf, QpdfArguments.Optimize(source, temp), ctx);
        if (!QpdfArguments.Succeeded(result, File.Exists(temp)))
            throw new ConversionFailure(QpdfArguments.Explain(result, Path.GetFileName(ctx.Input)));
        return temp;
    }

    // ---- shared ---------------------------------------------------------------------------------------------------------

    private string RequireQpdf() =>
        engines.Locate(EngineId.Qpdf)
        ?? throw new ConversionFailure("qpdf is not installed. Install it from DropFlow's engine installer, then try again.");

    private async Task<ProcessResult> RunQpdf(string qpdf, IReadOnlyList<string> arguments, JobContext ctx)
    {
        var result = await runner.RunAsync(qpdf, arguments, new ProcessOptions { Timeout = Timeout }, ctx.Ct).ConfigureAwait(false);
        ctx.Ct.ThrowIfCancellationRequested();
        return result;
    }

    /// <summary>Moves a finished temp file onto its reserved final name next to the input.</summary>
    private static string Publish(JobContext ctx, string tempFile, string suffix)
    {
        var path = ctx.ReserveFile(ctx.Input, "pdf", suffix);
        File.Move(tempFile, path, overwrite: true);
        return path;
    }

    internal static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / 1024.0 / 1024.0:0.##} MB",
    };
}
