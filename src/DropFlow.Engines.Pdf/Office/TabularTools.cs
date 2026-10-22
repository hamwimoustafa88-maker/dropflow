using DropFlow.Engines.Pdf.Infrastructure;

namespace DropFlow.Engines.Pdf.Office;

/// <summary>csv, tsv, xlsx and json converted to each other and to HTML tables, without any engine.</summary>
internal static class TabularTools
{
    /// <summary>Characters Windows forbids in file names (macOS and Linux forbid only '/'); used everywhere so outputs copy across systems.</summary>
    private static readonly char[] WindowsUnsafe = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    public static readonly string[] Inputs = ["csv", "tsv", "xlsx", "json"];
    public static readonly string[] Targets = ["csv", "tsv", "xlsx", "json", "html"];

    public static IEnumerable<OperationSpec> Specs()
    {
        foreach (var target in Targets)
        {
            var inputs = Inputs.Where(i => i != target).ToArray();
            yield return new OperationSpec($"convert:{target}", inputs, ctx => Task.FromResult(Convert(ctx, target)));
        }
    }

    private static Core.Operations.ConversionResult Convert(JobContext ctx, string target) => Convert(ctx, ctx.Input, ctx.Input, target);

    /// <summary>
    /// Converts <paramref name="source"/> (a csv, tsv, xlsx or json file) and names the result after <paramref name="nameSource"/> - the file the
    /// user dropped, which differs from the source when LibreOffice produced the spreadsheet in a temp folder.
    /// </summary>
    internal static Core.Operations.ConversionResult Convert(JobContext ctx, string source, string nameSource, string target)
    {
        var sourceExt = Path.GetExtension(source).TrimStart('.').ToLowerInvariant();
        var sheets = Tabular.Read(source);
        var inferTypes = sourceExt is "csv" or "tsv"; // text cells that are plainly numbers become numbers
        ctx.Report(0.4);
        ctx.Ct.ThrowIfCancellationRequested();

        string output;
        switch (target)
        {
            case "xlsx":
                output = ctx.ReserveFile(nameSource, "xlsx", "converted");
                Tabular.WriteXlsx(sheets, output, inferTypes);
                break;

            case "json":
                output = ctx.ReserveFile(nameSource, "json", "converted");
                File.WriteAllText(output, Tabular.ToJson(sheets, inferTypes), TextFile.Utf8);
                break;

            case "html":
                output = ctx.ReserveFile(nameSource, "html", "converted");
                File.WriteAllText(output, Tabular.ToHtml(sheets, Path.GetFileNameWithoutExtension(nameSource)), TextFile.Utf8);
                break;

            default: // csv, tsv
                if (sheets.Count == 1)
                {
                    output = ctx.ReserveFile(nameSource, target, "converted");
                    File.WriteAllText(output, Tabular.ToDelimited(sheets[0], target), TextFile.Utf8WithBom);
                }
                else
                {
                    // A text file holds one table, so a workbook with several sheets becomes a folder with one file per sheet.
                    output = ctx.ReserveFolder(nameSource, "converted");
                    var stem = Path.GetFileNameWithoutExtension(nameSource);
                    foreach (var sheet in sheets)
                    {
                        var safe = string.Concat(sheet.Name.Select(c => c < ' ' || WindowsUnsafe.Contains(c) ? '_' : c));   // the same names on every OS, valid on Windows too
                        var path = ctx.ReserveInFolder(output, $"{stem} - {safe}.{target}");
                        File.WriteAllText(path, Tabular.ToDelimited(sheet, target), TextFile.Utf8WithBom);
                    }
                }
                break;
        }

        ctx.Report(1);
        return ctx.Ok(output);
    }
}
