using DropFlow.Core.Operations;
using DropFlow.Engines.Common;
using DropFlow.Engines.Pdf.Infrastructure;
using DropFlow.Engines.Pdf.Tools;

namespace DropFlow.Engines.Pdf.Office;

/// <summary>Builds the soffice command line. Pure, so every flag is covered by a unit test.</summary>
public static class LibreOfficeArguments
{
    /// <summary>
    /// <c>soffice --headless --convert-to ext:filter --outdir out input</c> with a private profile folder, so two conversions
    /// running at once (or LibreOffice already open for the user) never share a profile lock.
    /// </summary>
    public static IReadOnlyList<string> Build(string inputPath, string outputDirectory, string profileDirectory, LibreOfficePlan plan)
    {
        var args = new List<string>
        {
            "--headless", "--invisible", "--nologo", "--norestore", "--nolockcheck", "--nodefault",
            "-env:UserInstallation=" + ProfileUrl(profileDirectory),
        };
        if (plan.InputFilter is { Length: > 0 } inFilter) args.Add("--infilter=" + inFilter);
        args.Add("--convert-to");
        args.Add(plan.ExportFilter is { Length: > 0 } filter ? $"{plan.ProducedExtension}:{filter}" : plan.ProducedExtension);
        args.Add("--outdir");
        args.Add(outputDirectory);
        args.Add(inputPath);
        return args;
    }

    /// <summary>The <c>file:///</c> URL LibreOffice wants for its user-installation folder (spaces and non-ASCII characters percent-encoded).</summary>
    public static string ProfileUrl(string profileDirectory) => new Uri(Path.GetFullPath(profileDirectory)).AbsoluteUri;
}

/// <summary>Runs the LibreOffice conversions listed by <see cref="LibreOfficePlanner"/>, with DropFlow's pre- and post-steps around the engine.</summary>
internal sealed class LibreOfficeTools(IEngineProvider engines, IEngineRunner runner, bool browserRenders = false)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(15);

    public IEnumerable<OperationSpec> Specs()
    {
        foreach (var group in LibreOfficePlanner.PairsFor(browserRenders).GroupBy(p => p.Target))
        {
            var target = group.Key;
            yield return new OperationSpec($"convert:{target}", group.Select(p => p.Input).ToArray(), ctx => RunAsync(ctx, target));
        }
    }

    private async Task<ConversionResult> RunAsync(JobContext ctx, string target)
    {
        var inputExt = Path.GetExtension(ctx.Input).TrimStart('.').ToLowerInvariant();
        var plan = LibreOfficePlanner.Plan(inputExt, target, browserRenders)
                   ?? throw new ConversionFailure($"'.{inputExt}' cannot be converted to .{target}.");

        var soffice = engines.Locate(EngineId.LibreOffice)
                      ?? throw new ConversionFailure("LibreOffice is not installed. Install it from DropFlow's engine installer, then try again.");

        var work = ctx.TempFolder();
        var inputDir = Directory.CreateDirectory(Path.Combine(work, "in")).FullName;
        var outputDir = Directory.CreateDirectory(Path.Combine(work, "out")).FullName;
        var profileDir = Path.Combine(work, "profile");

        ctx.Report(0.05, "Preparing the file");
        var loInput = Prepare(ctx, plan.Pre, inputDir);
        ctx.Ct.ThrowIfCancellationRequested();

        ctx.Report(0.15, "LibreOffice is converting");
        var arguments = LibreOfficeArguments.Build(loInput, outputDir, profileDir, plan);
        var result = await runner.RunAsync(soffice, arguments, new ProcessOptions { Timeout = Timeout }, ctx.Ct).ConfigureAwait(false);
        ctx.Ct.ThrowIfCancellationRequested();

        var produced = Directory.GetFiles(outputDir, "*." + plan.ProducedExtension).FirstOrDefault(f => new FileInfo(f).Length > 0);
        if (produced is null)
        {
            var name = Path.GetFileName(ctx.Input);
            return ctx.Fail(result.TimedOut
                ? $"{name}: LibreOffice took too long and was stopped."
                : $"{name}: LibreOffice could not convert this file ({result.LastMessage}). It may be damaged or password-protected.");
        }

        ctx.Report(0.8, "Finishing");
        return plan.Post switch
        {
            LoPost.PageImages => PdfImageTools.RenderToImages(ctx, produced, ctx.Input, plan.Target, PdfImageTools.DefaultDpi, PdfImageTools.DefaultQuality, null, "converted", "converted"),
            LoPost.Tabular => TabularTools.Convert(ctx, produced, ctx.Input, plan.Target),
            LoPost.DocxToMarkdown => WriteMarkdown(ctx, produced),
            _ => Publish(ctx, produced, plan.Target),
        };
    }

    private static ConversionResult Publish(JobContext ctx, string produced, string extension)
    {
        var path = ctx.ReserveFile(ctx.Input, extension, "converted");
        File.Move(produced, path, overwrite: true);
        ctx.Report(1);
        return ctx.Ok(path);
    }

    private static ConversionResult WriteMarkdown(JobContext ctx, string docx)
    {
        var path = ctx.ReserveFile(ctx.Input, "md", "converted");
        File.WriteAllText(path, RichMarkdownWriter.Write(DocxReader.Read(docx)), TextFile.Utf8);
        ctx.Report(1);
        return ctx.Ok(path);
    }

    /// <summary>Produces the file LibreOffice will open. Temp copies get plain ASCII names; the user's original is never touched.</summary>
    private static string Prepare(JobContext ctx, LoPre pre, string inputDir)
    {
        var input = ctx.Input;
        var stem = Path.GetFileNameWithoutExtension(input);
        switch (pre)
        {
            case LoPre.TextToUtf8:
            {
                var path = Path.Combine(inputDir, "input.txt");
                File.WriteAllText(path, TextFile.Read(input), TextFile.Utf8WithBom);
                return path;
            }
            case LoPre.MarkdownToHtml:
            {
                var path = Path.Combine(inputDir, "input.html");
                File.WriteAllText(path, DocumentTools.MarkdownToHtml(TextFile.Read(input), stem), TextFile.Utf8);
                return path;
            }
            case LoPre.TabularToXlsx:
            {
                var path = Path.Combine(inputDir, "input.xlsx");
                var ext = Path.GetExtension(input).TrimStart('.').ToLowerInvariant();
                Tabular.WriteXlsx(Tabular.Read(input), path, inferTypes: ext is "csv" or "tsv");
                return path;
            }
            case LoPre.EpubToHtml:
            {
                var book = EpubReader.Read(input, EpubImages.ToFolder, Path.Combine(inputDir, "images"));
                var path = Path.Combine(inputDir, "input.html");
                File.WriteAllText(path, EpubReader.ToHtmlDocument(book), TextFile.Utf8);
                return path;
            }
            default:
            {
                // LibreOffice picks the import filter from the extension; keep it but under an ASCII name in a private folder.
                var path = Path.Combine(inputDir, "input" + Path.GetExtension(input).ToLowerInvariant());
                File.Copy(input, path, overwrite: true);
                return path;
            }
        }
    }
}
