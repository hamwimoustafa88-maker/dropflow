// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using DropFlow.Core.Operations;
using ImageMagick;
using SharpCompress.Archives;
using SharpCompress.Readers;

namespace DropFlow.App.Tests.Matrix;

public sealed record CheckResult(string? Error, string? Warning, bool TextMiss = false)
{
    public static readonly CheckResult Ok = new(null, null);
    public static CheckResult Bad(string error) => new(error, null);
    public static CheckResult Warn(string warning) => new(null, warning);
}

/// <summary>Re-opens a produced file with an independent reader and checks that it is what its extension says, and that text survived.</summary>
public sealed partial class OutputChecker(IEngineProvider engines)
{
    private static readonly HashSet<string> Images = ["jpg", "jpeg", "png", "webp", "tiff", "tif", "avif", "bmp", "gif", "ico", "heic"];
    private static readonly HashSet<string> Media = ["mp3", "m4a", "wav", "flac", "ogg", "opus", "aiff", "wma", "aac", "mp4", "mov", "mkv", "webm", "avi", "wmv", "flv", "m4v"];
    private static readonly HashSet<string> Videos = ["mp4", "mov", "mkv", "webm", "avi", "wmv", "flv", "m4v"];
    private static readonly HashSet<string> PlainText = ["txt", "md", "html", "htm", "srt", "vtt", "ass", "csv", "tsv", "json", "xml", "svg", "xhtml"];

    /// <summary>Operations whose output must carry the input's words (a conversion between text formats, OCR, speech to text).</summary>
    private static bool CarriesText(string op, string outputExt)
    {
        if (op.EndsWith(".translate", StringComparison.Ordinal) || op is "gen.hash" or "gen.base64" or "file.hash" or "file.base64") return false;
        if (op.Contains("ocr") || op.Contains("transcribe") || op == "pdf.extract-text") return true;
        if (!Operation.IsConvert(op)) return false;
        return outputExt is "txt" or "md" or "html" or "htm" or "docx" or "odt" or "rtf" or "epub" or "pptx" or "odp" or "xlsx" or "ods" or "csv" or "tsv" or "json" or "srt" or "vtt" or "ass" or "pdf";
    }

    public bool ChecksText(string path, string op) => CarriesText(op, Path.GetExtension(path).TrimStart('.').ToLowerInvariant());

    public CheckResult Check(string path, string op, IReadOnlyList<string> markers, string? password = null)
    {
        var info = new FileInfo(path);
        if (!info.Exists) return CheckResult.Bad("output missing");
        if (info.Length == 0) return CheckResult.Bad("output is 0 bytes");
        var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();

        try
        {
            // A renamed copy is the original bytes (a Windows-1256 text stays Windows-1256): only its presence matters.
            if (op == "gen.rename") return CheckResult.Ok;
            var structural = Structure(path, ext, op == "pdf.protect" ? password : null);
            if (structural.Error is not null) return structural;

            if (markers.Count > 0 && CarriesText(op, ext))
            {
                var text = TextOf(path, ext);
                if (text is not null)
                {
                    var verdict = Markers(text, markers);
                    if (verdict is not null)
                    {
                        // A PDF's text layer is only a warning (reading order inside PDFs is fuzzy); for real text formats it is a defect.
                        return ext == "pdf" ? new CheckResult(null, verdict, true) : new CheckResult(verdict, null, true);
                    }
                }
            }
            return structural;
        }
        catch (Exception e)
        {
            return CheckResult.Bad($"cannot be opened: {e.GetType().Name}: {e.Message}");
        }
    }

    private CheckResult Structure(string path, string ext, string? password)
    {
        if (Images.Contains(ext))
        {
            using var image = new MagickImage(path);
            return image.Width > 0 && image.Height > 0 ? CheckResult.Ok : CheckResult.Bad("image has no size");
        }
        if (Media.Contains(ext)) return Probe(path, Videos.Contains(ext));
        switch (ext)
        {
            case "pdf":
            {
                using var doc = UglyToad.PdfPig.PdfDocument.Open(path, new UglyToad.PdfPig.ParsingOptions { Password = password ?? "" });
                return doc.NumberOfPages > 0 ? CheckResult.Ok : CheckResult.Bad("PDF has no pages");
            }
            case "docx": return OpenXml(() => WordprocessingDocument.Open(path, false));
            case "xlsx": return OpenXml(() => SpreadsheetDocument.Open(path, false));
            case "pptx": return OpenXml(() => PresentationDocument.Open(path, false));
            case "odt" or "ods" or "odp":
            {
                using var zip = ZipFile.OpenRead(path);
                return zip.GetEntry("mimetype") is not null && zip.GetEntry("content.xml") is not null ? CheckResult.Ok : CheckResult.Bad("ODF without mimetype/content.xml");
            }
            case "epub":
            {
                using var zip = ZipFile.OpenRead(path);
                if (zip.GetEntry("META-INF/container.xml") is null) return CheckResult.Bad("EPUB without META-INF/container.xml");
                using var reader = new StreamReader(zip.GetEntry("mimetype")?.Open() ?? Stream.Null);
                return reader.ReadToEnd().Trim() == "application/epub+zip" ? CheckResult.Ok : CheckResult.Bad("EPUB mimetype entry missing or wrong");
            }
            case "doc" or "xls" or "ppt":
            {
                var head = new byte[8];
                using (var s = File.OpenRead(path)) s.ReadExactly(head);
                return head.AsSpan().SequenceEqual(new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }) ? CheckResult.Ok : CheckResult.Bad("not an OLE2 (legacy Office) file");
            }
            case "rtf": return File.ReadAllText(path).StartsWith(@"{\rtf", StringComparison.Ordinal) ? CheckResult.Ok : CheckResult.Bad("not RTF");
            case "zip" or "7z" or "tar" or "rar":
            {
                using var archive = ArchiveFactory.OpenArchive(path, new ReaderOptions());
                return archive.Entries.Any() ? CheckResult.Ok : CheckResult.Bad("archive has no entries");
            }
            case "gz" or "tgz":
            {
                using var gz = new GZipStream(File.OpenRead(path), CompressionMode.Decompress);
                var buffer = new byte[16];
                return gz.Read(buffer) > 0 ? CheckResult.Ok : CheckResult.Bad("gzip decompresses to nothing");
            }
            case "json":
                using (JsonDocument.Parse(File.ReadAllText(path))) return CheckResult.Ok;
            case "srt": return ReadText(path).Contains("-->") ? CheckResult.Ok : CheckResult.Bad("SRT without cues");
            case "vtt": return ReadText(path).TrimStart('\uFEFF').StartsWith("WEBVTT", StringComparison.Ordinal) ? CheckResult.Ok : CheckResult.Bad("VTT without the WEBVTT header");
            case "ass": return ReadText(path).Contains("[Events]") ? CheckResult.Ok : CheckResult.Bad("ASS without [Events]");
            case "svg": return ReadText(path).Contains("<svg") ? CheckResult.Ok : CheckResult.Bad("SVG without <svg>");
        }
        if (PlainText.Contains(ext))
        {
            var text = ReadText(path);
            if (text.Contains('\uFFFD')) return CheckResult.Bad("text contains U+FFFD (broken encoding)");
            return string.IsNullOrWhiteSpace(text) ? CheckResult.Bad("text output is blank") : CheckResult.Ok;
        }
        return CheckResult.Ok;
    }

    private static CheckResult OpenXml(Func<OpenXmlPackage> open)
    {
        using var package = open();
        var errors = new OpenXmlValidator().Validate(package).Take(50).ToList();
        return errors.Count == 0 ? CheckResult.Ok : CheckResult.Warn($"OpenXML validator: {errors.Count}{(errors.Count == 50 ? "+" : "")} errors, first: {errors[0].Description} @ {errors[0].Path?.XPath}");
    }

    private CheckResult Probe(string path, bool needsVideo)
    {
        var ffprobe = engines.Locate(EngineId.Ffprobe);
        if (ffprobe is null) return CheckResult.Warn("ffprobe missing, media not probed");
        using var p = Process.Start(new ProcessStartInfo(ffprobe, $"-v error -show_entries format=duration:stream=codec_type,codec_name -of json \"{path}\"")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
        var json = p.StandardOutput.ReadToEnd();
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) return CheckResult.Bad($"ffprobe failed: {err.Trim()}");
        using var doc = JsonDocument.Parse(json);
        var streams = doc.RootElement.TryGetProperty("streams", out var s) ? s.EnumerateArray().Select(x => x.GetProperty("codec_type").GetString()).ToList() : [];
        var duration = doc.RootElement.TryGetProperty("format", out var f) && f.TryGetProperty("duration", out var d) && double.TryParse(d.GetString(), System.Globalization.CultureInfo.InvariantCulture, out var dd) ? dd : 0;
        if (streams.Count == 0) return CheckResult.Bad("no streams");
        if (needsVideo && !streams.Contains("video")) return CheckResult.Bad("video file without a video stream");
        if (duration <= 0.2) return CheckResult.Bad($"duration {duration:0.00}s");
        return CheckResult.Ok;
    }

    // ---------------- text extraction ----------------

    public static string? TextOf(string path, string ext)
    {
        switch (ext)
        {
            case "pdf":
            {
                using var doc = UglyToad.PdfPig.PdfDocument.Open(path);
                return string.Join("\n", doc.GetPages().Select(p => p.Text));
            }
            case "html" or "htm" or "xhtml" or "svg": return StripTags(ReadText(path));
            case "docx" or "pptx" or "xlsx" or "odt" or "odp" or "ods" or "epub":
            {
                using var zip = ZipFile.OpenRead(path);
                var sb = new StringBuilder();
                foreach (var entry in zip.Entries.Where(e => e.FullName.EndsWith(".xml") || e.FullName.EndsWith(".xhtml") || e.FullName.EndsWith(".html") || e.FullName.EndsWith(".htm")))
                {
                    using var r = new StreamReader(entry.Open(), Encoding.UTF8);
                    sb.Append(StripTags(r.ReadToEnd())).Append('\n');
                }
                return sb.ToString();
            }
            case "rtf": return Rtf(File.ReadAllText(path, Encoding.Latin1));
        }
        return PlainText.Contains(ext) ? ReadText(path) : null;
    }

    private static string ReadText(string path)
    {
        var bytes = File.ReadAllBytes(path);
        try { return new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF'); }
        catch (DecoderFallbackException)
        {
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return Encoding.Unicode.GetString(bytes[2..]);
            return Encoding.GetEncoding(1256).GetString(bytes) + "\uFFFD";
        }
    }

    private static string StripTags(string html) => WebUtility.HtmlDecode(TagRegex().Replace(html, " "));

    private static string Rtf(string rtf)
    {
        var codePage = AnsiCpg().Match(rtf) is { Success: true } m ? int.Parse(m.Groups[1].Value) : 1252;
        var encoding = Encoding.GetEncoding(codePage);
        var sb = new StringBuilder();
        var bytes = new List<byte>();
        void Flush() { if (bytes.Count > 0) { sb.Append(encoding.GetString(bytes.ToArray())); bytes.Clear(); } }
        for (var i = 0; i < rtf.Length; i++)
        {
            if (rtf[i] == '\\' && i + 1 < rtf.Length)
            {
                if (rtf[i + 1] == '\'' && i + 3 < rtf.Length) { bytes.Add(Convert.ToByte(rtf.Substring(i + 2, 2), 16)); i += 3; continue; }
                var u = URegex().Match(rtf, i);
                if (u.Success && u.Index == i) { Flush(); var n = int.Parse(u.Groups[1].Value); sb.Append((char)(n < 0 ? n + 65536 : n)); i += u.Length - 1; i += FallbackLength(rtf, i + 1); continue; }
            }
            Flush();
            sb.Append(rtf[i]);
        }
        Flush();
        return sb.ToString();
    }

    /// <summary>Length of the one-character fallback after a \uN escape (\uc1): an escaped byte such as \'3f, or one plain character.</summary>
    private static int FallbackLength(string rtf, int at)
    {
        if (at >= rtf.Length) return 0;
        if (rtf[at] == '\\') return at + 1 < rtf.Length && rtf[at + 1] == '\'' ? 4 : 0;
        return rtf[at] is '{' or '}' ? 0 : 1;
    }

    /// <summary>Null when a marker is found in logical order; otherwise what is wrong (reversed, broken letters, missing).</summary>
    public static string? Markers(string text, IReadOnlyList<string> markers)
    {
        var normal = Normalize(text);
        if (markers.Any(m => normal.Contains(Normalize(m), StringComparison.OrdinalIgnoreCase))) return null;

        var arabic = markers.Where(m => m.Any(IsArabic)).ToList();
        if (arabic.Any(m => normal.Contains(new string(Normalize(m).Reverse().ToArray()), StringComparison.Ordinal)))
            return $"Arabic text is REVERSED (found '{new string(arabic[0].Reverse().ToArray())}' instead of '{arabic[0]}')";
        if (arabic.Count > 0 && normal.Any(IsArabic))
        {
            var sample = new string(normal.Where(c => IsArabic(c) || c == ' ').Take(40).ToArray()).Trim();
            return $"Arabic letters present but none of the words in logical order (first Arabic: '{sample}')";
        }
        return $"none of [{string.Join(", ", markers)}] in the output (text: '{Shorten(normal)}')";
    }

    private static string Normalize(string s) =>
        new string(s.Normalize(NormalizationForm.FormKC).Where(c => c != '\u0640' && c != '\u200F' && c != '\u200E' && c != '\u200B' && !(c >= '\u064B' && c <= '\u065F')).ToArray());

    private static bool IsArabic(char c) => c is >= '\u0600' and <= '\u06FF';

    private static string Shorten(string s)
    {
        var flat = Regex.Replace(s, @"\s+", " ").Trim();
        return flat.Length > 80 ? flat[..80] + "…" : flat;
    }

    [GeneratedRegex("<[^>]+>")] private static partial Regex TagRegex();
    [GeneratedRegex(@"\\ansicpg(\d+)")] private static partial Regex AnsiCpg();
    [GeneratedRegex(@"\\u(-?\d+) ?")] private static partial Regex URegex();
}
