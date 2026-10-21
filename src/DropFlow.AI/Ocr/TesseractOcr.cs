using System.Globalization;
using System.Text.RegularExpressions;
using DropFlow.Core.Operations;
using DropFlow.Engines.Common;

namespace DropFlow.AI.Ocr;

public interface ITesseractOcr
{
    bool IsInstalled { get; }

    /// <summary>Traineddata names Tesseract can use ("eng", "ara", "osd"...).</summary>
    Task<IReadOnlyList<string>> LanguagesAsync(CancellationToken cancellationToken);

    Task<OcrResult> RecognizeAsync(string pngPath, IReadOnlyList<string> languages, CancellationToken cancellationToken);
}

/// <summary>
/// Drives <c>tesseract.exe</c> through <see cref="ProcessRunner"/>. Argument building and output parsing are pure static methods
/// so they are tested without Tesseract installed.
/// </summary>
public sealed class TesseractOcr(IEngineProvider engines) : ITesseractOcr
{
    private string? _languagesFor;
    private IReadOnlyList<string>? _languages;

    public bool IsInstalled => engines.Locate(EngineId.Tesseract) is not null;

    // ---- pure helpers -------------------------------------------------------------------------------------------

    /// <summary>
    /// <c>tesseract IMAGE OUTBASE -l ara+eng [--dpi N] tsv</c>: writes OUTBASE.tsv with one row per word, boxes in pixels.
    /// </summary>
    public static IReadOnlyList<string> BuildRecognizeArguments(string imagePath, string outputBase, IReadOnlyList<string> languages, int? dpi = null)
    {
        var args = new List<string> { imagePath, outputBase, "-l", string.Join('+', languages) };
        if (dpi is > 0) args.AddRange(["--dpi", dpi.Value.ToString(CultureInfo.InvariantCulture)]);
        args.Add("tsv");
        return args;
    }

    /// <summary>
    /// <c>tesseract LIST OUTBASE -l ara+eng --dpi N pdf</c>: LIST is a text file with one image path per line; the result is a
    /// searchable PDF, OUTBASE.pdf, with one page per listed image. The dpi is passed explicitly because PNGs rendered from a PDF
    /// carry none, and without it Tesseract guesses 70 dpi and sizes the PDF pages wrongly.
    /// </summary>
    public static IReadOnlyList<string> BuildPdfArguments(string listFile, string outputBase, IReadOnlyList<string> languages, int dpi) =>
        [listFile, outputBase, "-l", string.Join('+', languages), "--dpi", dpi.ToString(CultureInfo.InvariantCulture), "pdf"];

    public static IReadOnlyList<string> ListLanguagesArguments { get; } = ["--list-langs"];

    /// <summary>Parses the output of <c>tesseract --list-langs</c> (stdout in 5.x, stderr in 4.x; pass both joined).</summary>
    public static IReadOnlyList<string> ParseLanguageList(string output)
    {
        var langs = new List<string>();
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("List of available", StringComparison.OrdinalIgnoreCase)) continue;
            if (Regex.IsMatch(line, @"^[A-Za-z][A-Za-z0-9_]*(/[A-Za-z0-9_]+)?$") && !langs.Contains(line)) langs.Add(line);
        }
        return langs;
    }

    /// <summary>
    /// Parses Tesseract's TSV: columns <c>level page block par line word left top width height conf text</c>. Only word rows (level 5)
    /// with text are used; rows are grouped into lines by (block, paragraph, line) and each line is put into reading order from the
    /// word boxes. Confidence 0..100 becomes 0..1.
    /// </summary>
    public static OcrResult ParseTsv(string tsv, string language, int imageWidth, int imageHeight)
    {
        var groups = new List<((int Block, int Par, int Line) Key, List<OcrWord> Words)>();
        foreach (var raw in tsv.Split('\n'))
        {
            var cols = raw.TrimEnd('\r').Split('\t');
            if (cols.Length < 12 || cols[0] != "5") continue;
            var text = string.Join('\t', cols.Skip(11)).Trim();
            if (text.Length == 0) continue;
            if (!TryInt(cols[2], out var block) || !TryInt(cols[3], out var par) || !TryInt(cols[4], out var line)
                || !TryInt(cols[6], out var left) || !TryInt(cols[7], out var top) || !TryInt(cols[8], out var width) || !TryInt(cols[9], out var height))
                continue;
            double? confidence = double.TryParse(cols[10], NumberStyles.Float, CultureInfo.InvariantCulture, out var c) && c >= 0 ? c / 100.0 : null;

            var key = (block, par, line);
            var word = new OcrWord(text, new OcrRect(left, top, width, height), confidence);
            var group = groups.FindIndex(g => g.Key == key);
            if (group < 0) groups.Add((key, [word]));
            else groups[group].Words.Add(word);
        }

        var lines = groups.Select(g => OcrOrdering.BuildLine(g.Words)).Where(l => l.Words.Count > 0).ToList();
        return new OcrResult("tesseract", language, imageWidth, imageHeight, lines);

        static bool TryInt(string s, out int value) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    // ---- process wrappers ---------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<string>> LanguagesAsync(CancellationToken cancellationToken)
    {
        var exe = engines.Locate(EngineId.Tesseract) ?? throw new OcrUnavailableException(AiFailures.MissingEngine("Tesseract"));
        if (_languages is not null && _languagesFor == exe) return _languages;

        var run = await ProcessRunner.RunAsync(exe, ListLanguagesArguments, new ProcessOptions { Timeout = TimeSpan.FromSeconds(30) }, cancellationToken).ConfigureAwait(false);
        if (run.Cancelled) throw new OperationCanceledException(cancellationToken);
        var langs = ParseLanguageList(run.StdOut + "\n" + run.StdErr);
        _languagesFor = exe;
        return _languages = langs;
    }

    public async Task<OcrResult> RecognizeAsync(string pngPath, IReadOnlyList<string> languages, CancellationToken cancellationToken)
    {
        var exe = engines.Locate(EngineId.Tesseract) ?? throw new OcrUnavailableException(AiFailures.MissingEngine("Tesseract"));
        var dir = System.IO.Path.GetDirectoryName(pngPath)!;
        var outputBase = System.IO.Path.Combine(dir, "tess-" + Guid.NewGuid().ToString("N")[..8]);

        var run = await ProcessRunner.RunAsync(
            exe, BuildRecognizeArguments(pngPath, outputBase, languages),
            new ProcessOptions { Timeout = TimeSpan.FromMinutes(10), WorkingDirectory = dir }, cancellationToken).ConfigureAwait(false);
        if (run.Cancelled) throw new OperationCanceledException(cancellationToken);
        if (run.TimedOut) throw new OcrUnavailableException("Tesseract took too long.");
        var tsvPath = outputBase + ".tsv";
        if (!run.Success || !File.Exists(tsvPath))
            throw new OcrUnavailableException("Tesseract failed: " + run.LastMessage);

        var tsv = await File.ReadAllTextAsync(tsvPath, cancellationToken).ConfigureAwait(false);
        var (width, height) = ImageSize(tsv);
        return ParseTsv(tsv, string.Join('+', languages), width, height);
    }

    // The TSV's level-1 row carries the page size.
    private static (int Width, int Height) ImageSize(string tsv)
    {
        foreach (var raw in tsv.Split('\n'))
        {
            var cols = raw.TrimEnd('\r').Split('\t');
            if (cols.Length >= 10 && cols[0] == "1"
                && int.TryParse(cols[8], NumberStyles.Integer, CultureInfo.InvariantCulture, out var w)
                && int.TryParse(cols[9], NumberStyles.Integer, CultureInfo.InvariantCulture, out var h))
                return (w, h);
        }
        return (0, 0);
    }
}
