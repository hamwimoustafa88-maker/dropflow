// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using DropFlow.Core.Catalog;
using DropFlow.Core.Operations;
using DropFlow.Core.Output;
using DropFlow.Engines.Data.Formats;
using DropFlow.Engines.Data.Model;

namespace DropFlow.Engines.Data;

/// <summary>
/// <c>convert:*</c> between data formats through one tree (docs/data-formats.md): JSON, JSON Lines, XML, YAML, CSV, TSV, contacts (vCard)
/// and calendars (iCalendar) in; JSON, JSON Lines, XML, YAML, CSV, TSV, XLSX and HTML out. All in-process, nothing is installed.
/// CSV/TSV/JSON to CSV/TSV/JSON/XLSX/HTML stay with the spreadsheet code of the PDF engine (one owner per pair).
/// </summary>
public sealed class DataConverter : IConverter
{
    internal static readonly string[] Readers = ["json", "jsonl", "xml", "yaml", "csv", "tsv", "vcf", "ics"];
    internal static readonly string[] Writers = ["json", "jsonl", "xml", "yaml", "csv", "tsv", "xlsx", "html"];

    // Pairs DropFlow.Engines.Pdf (Office/TabularTools) already converts; claiming them here too would give one pair two owners.
    private static readonly string[] SpreadsheetInputs = ["csv", "tsv", "json"];
    private static readonly string[] SpreadsheetTargets = ["csv", "tsv", "json", "xlsx", "html"];

    private static readonly string[] TableTargets = ["csv", "tsv", "xlsx", "html"];

    private readonly long _maxBytes;

    public DataConverter() : this(DataFiles.DefaultMaxBytes) { }

    internal DataConverter(long maxBytes) => _maxBytes = maxBytes;

    public IReadOnlyCollection<string> Operations { get; } = Writers.Select(Operation.Convert).ToArray();

    public bool Supports(string operation, string inputExtension)
    {
        if (!Operation.IsConvert(operation)) return false;
        var target = Operation.TargetOf(operation);
        var input = ToolCatalog.Normalize(inputExtension);
        if (!Writers.Contains(target) || !Readers.Contains(input) || target == input) return false;
        return !(SpreadsheetInputs.Contains(input) && SpreadsheetTargets.Contains(target));
    }

    public EngineId EngineFor(string operation) => EngineId.None;

    public async Task<ConversionResult> RunAsync(ConversionJob job, IProgress<JobProgress> progress, CancellationToken cancellationToken)
    {
        if (job.Inputs.Count == 0) return ConversionResult.Fail("No input file.");
        var input = job.Inputs[0];
        if (!File.Exists(input)) return ConversionResult.Fail($"File not found: {input}");
        var extension = DataFiles.ExtensionOf(input);
        if (!Supports(job.Operation, extension)) return ConversionResult.Fail($"{job.Operation} does not apply to .{extension} files.");
        var target = Operation.TargetOf(job.Operation);

        var sw = Stopwatch.StartNew();
        string? output = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            DataFiles.CheckSize(input, _maxBytes);
            progress.Report(new JobProgress(0.1));

            output = await Task.Run(() => Convert(input, extension, target, job.OutputDirectory, progress, cancellationToken), cancellationToken).ConfigureAwait(false);
            progress.Report(new JobProgress(1, Path.GetFileName(output)));
            return ConversionResult.Ok([output], sw.Elapsed);
        }
        catch (DataFailure failure)
        {
            return ConversionResult.Fail(failure.Message, sw.Elapsed);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ConversionResult.Fail($"Could not convert '{Path.GetFileName(input)}': {ex.Message}", sw.Elapsed);
        }
    }

    private static string Convert(string input, string extension, string target, string? outputDirectory, IProgress<JobProgress> progress, CancellationToken ct)
    {
        var document = DataFiles.Read(input, extension);
        ct.ThrowIfCancellationRequested();
        progress.Report(new JobProgress(0.5));

        Table? table = null;
        if (TableTargets.Contains(target))
            table = document.Table ?? Tables.ToTable(document.Tree) ?? throw DataFailure.NotATable(input);

        var output = new OutputPathResolver().ForFile(input, target, "converted", outputDirectory);
        ct.ThrowIfCancellationRequested();
        switch (target)
        {
            case "json":
                DataFiles.WriteNewText(output, JsonFormat.Write(document.Tree), TextDecoding.Utf8);
                break;
            case "jsonl":
                DataFiles.WriteNewText(output, JsonFormat.WriteLines(Tables.Records(document.Tree)), TextDecoding.Utf8);
                break;
            case "xml":
                DataFiles.WriteNewText(output, XmlFormat.Write(document.Tree), TextDecoding.Utf8);
                break;
            case "yaml":
                DataFiles.WriteNewText(output, YamlFormat.Write(document.Tree), TextDecoding.Utf8);
                break;
            case "csv":
                DataFiles.WriteNewText(output, DelimitedFormat.Write(table!, ','), TextDecoding.Utf8WithBom);
                break;
            case "tsv":
                DataFiles.WriteNewText(output, DelimitedFormat.Write(table!, '\t'), TextDecoding.Utf8WithBom);
                break;
            case "html":
                DataFiles.WriteNewText(output, SheetFormats.ToHtml(table!, Path.GetFileNameWithoutExtension(input)), TextDecoding.Utf8);
                break;
            case "xlsx":
                DataFiles.WriteNew(output, stream => SheetFormats.WriteXlsx(table!, stream, Path.GetFileNameWithoutExtension(input)));
                break;
        }
        return output;
    }
}
