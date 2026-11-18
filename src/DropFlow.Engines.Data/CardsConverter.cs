// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using DropFlow.Core.Catalog;
using DropFlow.Core.Localization;
using DropFlow.Core.Operations;
using DropFlow.Core.Output;
using DropFlow.Engines.Data.Formats;
using DropFlow.Engines.Data.Model;

namespace DropFlow.Engines.Data;

/// <summary>
/// <c>data.to-vcard</c> and <c>data.to-ical</c>: a table (CSV, TSV or the first sheet of an XLSX workbook) becomes one .vcf file of
/// contacts or one .ics calendar. The columns are those the vCard/iCalendar readers write (docs/data-formats.md), plus common other
/// spellings, Outlook/Google export headers and a few Arabic ones. Rows that are not a contact or an event are skipped and counted.
/// </summary>
public sealed class CardsConverter : IConverter
{
    public const string ToVCard = "data.to-vcard";
    public const string ToICalendar = "data.to-ical";

    private static readonly string[] Inputs = ["csv", "tsv", "xlsx"];

    private readonly long _maxBytes;
    private readonly Func<DateTime> _clock;

    public CardsConverter() : this(DataFiles.DefaultMaxBytes, () => DateTime.UtcNow) { }

    internal CardsConverter(long maxBytes, Func<DateTime> clock)
    {
        _maxBytes = maxBytes;
        _clock = clock;
    }

    public IReadOnlyCollection<string> Operations { get; } = [ToVCard, ToICalendar];

    public bool Supports(string operation, string inputExtension) =>
        operation is ToVCard or ToICalendar && Inputs.Contains(ToolCatalog.Normalize(inputExtension));

    public EngineId EngineFor(string operation) => EngineId.None;

    public async Task<ConversionResult> RunAsync(ConversionJob job, IProgress<JobProgress> progress, CancellationToken cancellationToken)
    {
        if (job.Inputs.Count == 0) return ConversionResult.Fail("No input file.");
        var input = job.Inputs[0];
        if (!File.Exists(input)) return ConversionResult.Fail($"File not found: {input}");
        var extension = DataFiles.ExtensionOf(input);
        if (!Supports(job.Operation, extension)) return ConversionResult.Fail($"{job.Operation} does not apply to .{extension} files.");

        var sw = Stopwatch.StartNew();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            DataFiles.CheckSize(input, _maxBytes);
            progress.Report(new JobProgress(0.1));
            var (output, skipped) = await Task.Run(() => Make(job.Operation, input, extension, job.OutputDirectory, cancellationToken), cancellationToken).ConfigureAwait(false);
            progress.Report(new JobProgress(1, Path.GetFileName(output)));
            return ConversionResult.Ok([output], sw.Elapsed, skipped > 0 ? NoteText.Key("note.data.rowsSkipped", skipped) : null);
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

    private (string Output, int Skipped) Make(string operation, string input, string extension, string? outputDirectory, CancellationToken ct)
    {
        var table = DataFiles.Read(input, extension).Table!;
        ct.ThrowIfCancellationRequested();

        string text;
        int written, skipped;
        string target, suffix, failure;
        if (operation == ToVCard)
        {
            text = VCardFormat.Write(table, out written, out skipped);
            (target, suffix, failure) = ("vcf", "contacts", DataFailure.NoContacts);
        }
        else
        {
            text = ICalendarFormat.Write(table, _clock(), out written, out skipped);
            (target, suffix, failure) = ("ics", "calendar", DataFailure.NoEvents);
        }
        if (written == 0) throw new DataFailure(NoteText.Key(failure, DataFailure.Clean(Path.GetFileName(input))));

        ct.ThrowIfCancellationRequested();
        var output = new OutputPathResolver().ForFile(input, target, suffix, outputDirectory);
        DataFiles.WriteNewText(output, text, TextDecoding.Utf8);
        return (output, skipped);
    }
}
