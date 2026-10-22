using System.Text;
using DropFlow.Core.Localization;
using DropFlow.Core.Operations;

namespace DropFlow.AI.Translation;

/// <summary>What the converters share: reading the direction/quality options, choosing the direction, running the translator, reading and writing text files.</summary>
internal static class TranslationJobSupport
{
    public const string DirectionKey = "direction";
    public const string QualityKey = "quality";

    /// <summary>Longest a single file may take before it is stopped.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(45);

    public static IReadOnlyList<ToolOption> CommonOptions() =>
    [
        new(DirectionKey, ToolOptionKind.Choice, TranslationInfo.AutoDirection, TranslationInfo.DirectionChoices),
        new(QualityKey, ToolOptionKind.Choice, "balanced", TranslationInfo.QualityChoices),
    ];

    /// <summary>The direction to use. <paramref name="auto"/> is true when it was judged from the text (the user left it on "auto").</summary>
    public sealed record Chosen(TranslationDirection Direction, bool Auto);

    public static (Chosen? Chosen, string? Error) ChooseDirection(string? option, IEnumerable<string> units)
    {
        if (!TranslationInfo.TryParseDirection(option, out var explicitDirection))
            return (null, $"Unknown translation direction '{option}'. Use auto, en-ar or ar-en.");
        if (explicitDirection is { } d) return (new Chosen(d, false), null);

        var total = new ScriptCounts();
        foreach (var u in units) total += ScriptDetector.Count(u);
        return ScriptDetector.DetectDirection(total) is { } detected
            ? (new Chosen(detected, true), null)
            : (null, "There is no English or Arabic text to translate.");
    }

    public static string NoteFor(Chosen chosen, int skipped)
    {
        var notes = new List<string>();
        if (chosen.Auto)
            notes.Add(NoteText.Key(chosen.Direction == TranslationDirection.EnglishToArabic ? "note.translate.detectedEnAr" : "note.translate.detectedArEn"));
        if (skipped > 0) notes.Add(NoteText.Key("note.translate.skipped", skipped));
        return string.Join("; ", notes);
    }

    /// <summary>Strings that contain letters but do not look like the source language (already translated, or another script): left as they are.</summary>
    public static int CountSkipped(IReadOnlyList<string> units, TranslationDirection direction) =>
        units.Count(u => ScriptDetector.Count(u).Letters > 0 && !ScriptDetector.NeedsTranslation(u, direction));

    public static bool AnyToTranslate(IReadOnlyList<string> units, TranslationDirection direction) =>
        units.Any(u => ScriptDetector.NeedsTranslation(u, direction));

    public static string NothingToTranslate(TranslationDirection direction) =>
        direction == TranslationDirection.EnglishToArabic
            ? "No English text was found to translate (the file may already be in Arabic)."
            : "No Arabic text was found to translate (the file may already be in English).";

    /// <summary>
    /// Runs the translator with a timeout. Expected problems come back as an error message; a real cancellation is rethrown.
    /// </summary>
    public static async Task<(IReadOnlyList<string>? Result, string? Error)> TranslateAsync(
        ITextTranslator translator, TranslationDirection direction, IReadOnlyList<string> units, TranslationQuality quality,
        IProgress<JobProgress> progress, double from, double to, string message, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        try
        {
            var inner = new InlineProgress<double>(p => progress.Report(new JobProgress(from + (to - from) * p, message)));
            var result = await translator.TranslateAsync(direction, units, quality, inner, limit.Token).ConfigureAwait(false);
            return (result, null);
        }
        catch (TranslationUnavailableException ex)
        {
            return (null, ex.Message);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (null, $"The translation took longer than {timeout.TotalMinutes:0} minutes and was stopped.");
        }
    }

    // ---------------------------------------------------------------------------------------------------------- files

    internal sealed record SourceText(string Text, bool HadBom, string NewLine);

    /// <summary>Reads a text file: UTF-8 (with or without BOM) or UTF-16 with BOM, else the Windows Arabic or Western code page.</summary>
    public static SourceText ReadText(string path)
    {
        var bytes = File.ReadAllBytes(path);
        string text;
        var bom = false;
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) { text = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3); bom = true; }
        else if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) { text = Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2); bom = true; }
        else if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) { text = Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2); bom = true; }
        else
        {
            try
            {
                text = new UTF8Encoding(false, true).GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                var arabic = Encoding.GetEncoding(1256).GetString(bytes);
                text = ScriptDetector.Count(arabic).ArabicRatio >= 0.3 ? arabic : Encoding.GetEncoding(1252).GetString(bytes);
            }
        }

        var crlf = CountOf(text, "\r\n");
        var lf = CountOf(text, "\n") - crlf;
        var newline = crlf > lf ? "\r\n" : "\n";
        text = text.Replace("\r\n", "\n").Replace('\r', '\n');
        return new SourceText(text, bom, newline);
    }

    private static int CountOf(string text, string what)
    {
        var n = 0;
        for (var i = text.IndexOf(what, StringComparison.Ordinal); i >= 0; i = text.IndexOf(what, i + what.Length, StringComparison.Ordinal)) n++;
        return n;
    }

    /// <summary>Writes UTF-8 (with a BOM when the source had one), refusing to overwrite an existing file.</summary>
    public static void WriteNew(string path, string content, string newLine, bool bom)
    {
        if (newLine != "\n") content = content.Replace("\n", newLine);
        var encoding = new UTF8Encoding(bom);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        var preamble = encoding.GetPreamble();
        stream.Write(preamble, 0, preamble.Length);
        var bytes = encoding.GetBytes(content);
        stream.Write(bytes, 0, bytes.Length);
    }
}
