// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml;
using ClosedXML.Excel;
using DropFlow.Core.Catalog;
using DropFlow.Core.Localization;
using DropFlow.Engines.Data.Formats;
using DropFlow.Engines.Data.Model;
using YamlDotNet.Core;

namespace DropFlow.Engines.Data;

/// <summary>A data file read into the tree, and the table view of it when the format is a table already.</summary>
internal sealed record DataDocument(DataNode Tree, Table? Table);

/// <summary>Reading any supported data file and writing results without ever replacing an existing file.</summary>
internal static class DataFiles
{
    /// <summary>Refuse files larger than this (they would not fit comfortably in memory as a tree).</summary>
    public const long DefaultMaxBytes = 200L * 1024 * 1024;

    public static void CheckSize(string path, long maxBytes)
    {
        var length = new FileInfo(path).Length;
        if (length > maxBytes)
            throw new DataFailure(NoteText.Key(DataFailure.TooLarge, DataFailure.Clean(Path.GetFileName(path)), Math.Max(1, maxBytes / (1024 * 1024))));
    }

    /// <summary>Reads a file of a supported format (canonical extension). Parser errors become a <see cref="DataFailure"/> that names the file.</summary>
    public static DataDocument Read(string path, string extension)
    {
        try
        {
            switch (extension)
            {
                case "json":
                    return new(JsonFormat.Parse(Text(path)), null);
                case "jsonl":
                    var lines = JsonFormat.ParseLines(Text(path), out var badLine, out var error);
                    if (error is not null) throw DataFailure.BadFile(path, string.Create(CultureInfo.InvariantCulture, $"line {badLine}: {error}"));
                    return new(lines, null);
                case "xml":
                    using (var stream = File.OpenRead(path)) return new(XmlFormat.Read(stream), null);
                case "yaml":
                    return new(YamlFormat.Parse(Text(path)), null);
                case "csv":
                case "tsv":
                    var table = DelimitedFormat.Read(Text(path), extension == "tsv" ? '\t' : null);
                    return new(Tables.ToTree(table), table);
                case "vcf":
                    var contacts = VCardFormat.Read(Text(path));
                    return new(Tables.ToTree(contacts), contacts);
                case "ics":
                    var events = ICalendarFormat.Read(Text(path));
                    return new(Tables.ToTree(events), events);
                case "xlsx":
                    var sheet = ReadXlsx(path);
                    return new(Tables.ToTree(sheet), sheet);
                default:
                    throw DataFailure.BadFile(path, $".{extension} is not a data format");
            }
        }
        catch (Exception ex) when (ex is JsonException or XmlException or YamlException or DecoderFallbackException or InvalidDataException or FormatException)
        {
            throw DataFailure.BadFile(path, ex.Message);
        }
    }

    public static string Text(string path) => TextDecoding.Decode(File.ReadAllBytes(path));

    // The first visible sheet, first row = header. Cells are text as shown (numbers in invariant form, dates as ISO).
    private static Table ReadXlsx(string path)
    {
        using var workbook = new XLWorkbook(new MemoryStream(File.ReadAllBytes(path)));
        var sheet = workbook.Worksheets.FirstOrDefault(w => w.Visibility == XLWorksheetVisibility.Visible) ?? workbook.Worksheets.First();
        var used = sheet.RangeUsed();
        if (used is null) return new Table([], []);
        var rows = used.Rows().Select(r => r.Cells().Select(CellText).ToList()).ToList();
        var columns = Tables.UniqueNames(rows[0]);
        var body = rows.Skip(1).Select(r => r.Select(t => t.Length == 0 ? DataValue.Null : DataValue.String(t)).ToArray()).ToList();
        return new Table(columns, body);
    }

    private static string CellText(IXLCell cell)
    {
        XLCellValue value;
        try { value = cell.Value; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return cell.CachedValue.ToString(CultureInfo.InvariantCulture); }
        if (value.IsBlank) return "";
        if (value.IsNumber) return value.GetNumber().ToString("R", CultureInfo.InvariantCulture);
        if (value.IsBoolean) return value.GetBoolean() ? "true" : "false";
        if (value.IsDateTime)
        {
            var d = value.GetDateTime();
            return d.TimeOfDay == TimeSpan.Zero ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : d.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
        }
        return value.ToString(CultureInfo.InvariantCulture);
    }

    public static string ExtensionOf(string path) => ToolCatalog.ExtensionOf(path);

    /// <summary>Creates <paramref name="path"/> (it must not exist) and lets <paramref name="write"/> fill it; deletes it again on failure.</summary>
    public static void WriteNew(string path, Action<Stream> write)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var created = false;
        try
        {
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            created = true;
            write(stream);
        }
        catch
        {
            if (created) TryDelete(path);
            throw;
        }
    }

    public static void WriteNewText(string path, string text, Encoding encoding) =>
        WriteNew(path, stream =>
        {
            var preamble = encoding.GetPreamble();
            stream.Write(preamble);
            stream.Write(encoding.GetBytes(text));
        });

    public static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
