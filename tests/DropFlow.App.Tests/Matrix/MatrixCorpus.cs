// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using ClosedXML.Excel;
using DropFlow.Core.Operations;
using ImageMagick;
using ImageMagick.Drawing;

namespace DropFlow.App.Tests.Matrix;

/// <summary>One real file of the conversion matrix.</summary>
/// <param name="Markers">Words the file contains; text-bearing outputs must contain at least one of them (Arabic words in logical order).</param>
/// <param name="Note">What makes this sample special (shown in the report).</param>
public sealed record MatrixSample(string Path, string Note, IReadOnlyList<string> Markers, IReadOnlyDictionary<string, object?>? Options = null)
{
    public string Name => System.IO.Path.GetFileName(Path);
}

/// <summary>
/// Builds the matrix corpus with independent producers where possible (Edge prints PDFs, LibreOffice makes Office files, FFmpeg makes
/// media, 7-Zip/WinRAR make archives), so DropFlow is tested against files it did not write itself. A producer that is missing is
/// reported, and its samples are left out (never faked).
/// </summary>
public sealed class MatrixCorpus(IEngineProvider engines, string folder)
{
    public const string ArabicWord = "مرحبا";
    public const string ArabicWord2 = "الفاتورة";
    public const string EnglishWord = "Invoice";
    public const string Password = "Secret-123";

    private static readonly string[] Bilingual = [ArabicWord, ArabicWord2, EnglishWord];
    private static readonly string[] English = [EnglishWord, "DropFlow"];
    private static readonly string[] ArabicOnly = [ArabicWord, ArabicWord2];

    public List<MatrixSample> Samples { get; } = [];
    public List<string> Problems { get; } = [];

    private string P(string name) => System.IO.Path.Combine(folder, name);

    private const string ArabicHtml = """
        <!doctype html><html lang="ar" dir="rtl"><head><meta charset="utf-8"><title>الفاتورة</title>
        <style>body{font-family:Tahoma,Arial;font-size:16pt;margin:2cm} table{border-collapse:collapse;width:60%} td,th{border:1px solid #444;padding:6px}</style></head>
        <body><h1>مرحبا بالعالم</h1>
        <p>هذه الفاتورة رقم 4821 بتاريخ 2026-11-12. المبلغ الإجمالي 350 دولاراً.</p>
        <p>Invoice number 4821 — DropFlow converts files locally.</p>
        <table width="60%"><tr><th width="40%">الصنف</th><th>الكمية</th><th>السعر</th></tr><tr><td>قلم</td><td>3</td><td>15</td></tr><tr><td>دفتر</td><td>2</td><td>40</td></tr></table>
        </body></html>
        """;

    private static string EnglishHtml(int pages) =>
        "<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>Invoice</title><style>body{font-family:Arial;font-size:14pt;margin:2cm} .p{page-break-after:always}</style></head><body>" +
        string.Concat(Enumerable.Range(1, pages).Select(i => $"<div class=\"p\"><h1>Invoice page {i}</h1><p>DropFlow converts files locally. Invoice number 48{i}1, total {i * 100} USD.</p><p>The quick brown fox jumps over the lazy dog.</p></div>")) +
        "</body></html>";

    public void Build()
    {
        Directory.CreateDirectory(folder);
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Try("images", BuildImages);
        Try("pdf", BuildPdfs);
        Try("documents", BuildDocuments);
        Try("spreadsheets", BuildSpreadsheets);
        Try("slides", BuildSlides);
        Try("epub", BuildEpub);
        Try("subtitles", BuildSubtitles);
        Try("audio", BuildAudio);
        Try("video", BuildVideo);
        Try("archives", BuildArchives);
    }

    private void Try(string what, Action build)
    {
        try { build(); }
        catch (Exception e) { Problems.Add($"{what}: {e.GetType().Name}: {e.Message}"); }
    }

    private void Add(string path, string note, IReadOnlyList<string>? markers = null, IReadOnlyDictionary<string, object?>? options = null)
    {
        if (!File.Exists(path) || new FileInfo(path).Length == 0) { Problems.Add($"sample not produced: {System.IO.Path.GetFileName(path)}"); return; }
        Samples.Add(new MatrixSample(path, note, markers ?? [], options));
    }

    // ---------------- images ----------------

    private static MagickImage Photo(int width = 640, int height = 427)
    {
        var image = new MagickImage("gradient:#1e3a8a-#f59e0b", (uint)width, (uint)height);
        new Drawables().FillColor(MagickColors.White).Ellipse(width * 0.3, height * 0.5, 80, 60, 0, 360)
            .FillColor(new MagickColor("#10b981")).Rectangle(width * 0.55, height * 0.25, width * 0.85, height * 0.75)
            .FontPointSize(36).FillColor(MagickColors.Black).Text(30, 50, "DropFlow 2026").Draw(image);
        return image;
    }

    private static MagickImage TextImage(string text, string font = "Arial", int width = 1000, int height = 260)
    {
        var settings = new MagickReadSettings
        {
            Font = font, FontPointsize = 56, FillColor = MagickColors.Black, BackgroundColor = MagickColors.White,
            Width = (uint)width, Height = (uint)height, TextGravity = Gravity.Center,
        };
        return new MagickImage("caption:" + text, settings);
    }

    private void BuildImages()
    {
        using (var photo = Photo())
        {
            var exif = new ExifProfile();
            exif.SetValue(ExifTag.Orientation, (ushort)6);
            exif.SetValue(ExifTag.GPSLatitude, [new Rational(33, 1), new Rational(53, 1), new Rational(0, 1)]);
            exif.SetValue(ExifTag.GPSLatitudeRef, "N");
            exif.SetValue(ExifTag.GPSLongitude, [new Rational(35, 1), new Rational(30, 1), new Rational(0, 1)]);
            exif.SetValue(ExifTag.GPSLongitudeRef, "E");
            exif.SetValue(ExifTag.Make, "MatrixCam");
            using var jpg = (MagickImage)photo.Clone();
            jpg.SetProfile(exif);
            jpg.Quality = 90;
            jpg.Write(P("photo-exif-gps.jpg"), MagickFormat.Jpeg);
            Add(P("photo-exif-gps.jpg"), "JPEG, EXIF orientation 6 + GPS");

            foreach (var (ext, fmt) in new[] { ("webp", MagickFormat.WebP), ("tiff", MagickFormat.Tiff), ("avif", MagickFormat.Avif), ("bmp", MagickFormat.Bmp) })
            {
                using var copy = (MagickImage)photo.Clone();
                copy.Write(P($"photo.{ext}"), fmt);
                Add(P($"photo.{ext}"), $"{ext.ToUpperInvariant()} photo 640x427");
            }

            using var big = Photo(4000, 3000);
            big.Quality = 95;
            big.Write(P("large-4000x3000.jpg"), MagickFormat.Jpeg);
            Add(P("large-4000x3000.jpg"), "large JPEG 12 MP");
        }

        using (var transparent = new MagickImage(MagickColors.Transparent, 400, 300))
        {
            new Drawables().FillColor(new MagickColor("#e11d48")).Circle(200, 150, 290, 150).Draw(transparent);
            transparent.Write(P("transparent.png"), MagickFormat.Png);
            Add(P("transparent.png"), "PNG with alpha");
        }

        using (var collection = new MagickImageCollection())
        {
            foreach (var color in new[] { "#ef4444", "#22c55e", "#3b82f6" })
            {
                var frame = new MagickImage(new MagickColor(color), 160, 120) { AnimationDelay = 30 };
                collection.Add(frame);
            }
            collection.Write(P("animated.gif"), MagickFormat.Gif);
            Add(P("animated.gif"), "animated GIF, 3 frames");
        }

        File.WriteAllText(P("vector.svg"), """
            <svg xmlns="http://www.w3.org/2000/svg" width="300" height="200" viewBox="0 0 300 200">
              <rect width="300" height="200" fill="#0ea5e9"/><circle cx="150" cy="100" r="60" fill="#fde047"/>
              <text x="150" y="190" font-size="20" text-anchor="middle" font-family="Arial">DropFlow</text></svg>
            """);
        Add(P("vector.svg"), "SVG with text");

        using (var ico = Photo(256, 256))
        {
            ico.Write(P("icon.ico"), MagickFormat.Ico);
            Add(P("icon.ico"), "ICO 256");
        }

        var heic = FindRepoFile(Path.Combine("tests", "samples", "heic", "RGB_8__128x128.heic"));
        if (heic is not null) { File.Copy(heic, P("sample.heic"), true); Add(P("sample.heic"), "real HEVC HEIC 128x128"); }
        else Problems.Add("HEIC fixture not found");

        using (var en = TextImage("Invoice number 4821\nDropFlow"))
        {
            en.Write(P("text-en.png"), MagickFormat.Png);
            Add(P("text-en.png"), "picture of English text (OCR)", English);
        }
        using (var ar = TextImage("مرحبا بالعالم\nالفاتورة رقم 4821", "Tahoma"))
        {
            ar.Write(P("text-ar.png"), MagickFormat.Png);
            Add(P("text-ar.png"), "picture of Arabic text (OCR)", ArabicOnly);
        }
    }

    // ---------------- PDF ----------------

    private void BuildPdfs()
    {
        File.WriteAllText(P("src-ar.html"), ArabicHtml, Encoding.UTF8);
        File.WriteAllText(P("src-en.html"), EnglishHtml(3), Encoding.UTF8);

        if (EdgePrint(P("src-en.html"), P("en-3pages.pdf"))) Add(P("en-3pages.pdf"), "English, 3 pages, printed by Edge", English);
        if (EdgePrint(P("src-ar.html"), P("arabic-edge.pdf"))) Add(P("arabic-edge.pdf"), "Arabic + English text and a table, printed by Edge", Bilingual);

        // LibreOffice-made Arabic PDF (different producer, different font embedding).
        if (Soffice(P("src-ar.html"), "odt:writer8", folder, "src-ar.odt") is { } odt && Soffice(odt, "pdf:writer_pdf_Export", folder, "arabic-libreoffice.pdf") is { } lo)
            Add(lo, "Arabic + English, exported by LibreOffice Writer", Bilingual);

        // Image-only PDF: a picture of Arabic + English text, no text layer.
        using (var scan = new MagickImageCollection())
        {
            var page = TextImage("مرحبا بالعالم\nInvoice 4821", "Tahoma", 1240, 1754);
            page.Density = new Density(150);
            page.AddNoise(NoiseType.Gaussian, 0.3);
            page.Rotate(0.8);
            scan.Add(page);
            scan.Write(P("scanned-image-only.pdf"), MagickFormat.Pdf);
            Add(P("scanned-image-only.pdf"), "scanned page: picture only, no text layer", Bilingual);
        }

        var qpdf = engines.Locate(EngineId.Qpdf);
        if (qpdf is not null && File.Exists(P("en-3pages.pdf")))
        {
            Run(qpdf, $"--encrypt {Password} {Password}-owner 256 -- \"{P("en-3pages.pdf")}\" \"{P("protected.pdf")}\"");
            Add(P("protected.pdf"), $"password-protected (AES-256, password {Password})", English, new Dictionary<string, object?> { ["password"] = Password });
        }
        else Problems.Add("qpdf missing: no protected PDF sample");
    }

    private bool EdgePrint(string html, string pdf)
    {
        var edge = @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe";
        if (!File.Exists(edge)) { Problems.Add("Edge missing: no Edge-printed PDFs"); return false; }
        var profile = P("edge-profile");
        Run(edge, $"--headless=new --disable-gpu --no-pdf-header-footer --user-data-dir=\"{profile}\" --print-to-pdf=\"{pdf}\" \"{new Uri(html).AbsoluteUri}\"", 90);
        return File.Exists(pdf);
    }

    // ---------------- documents ----------------

    private void BuildDocuments()
    {
        File.WriteAllText(P("bilingual.txt"), "مرحبا بالعالم\nهذه الفاتورة رقم 4821.\n\nInvoice number 4821 — DropFlow converts files locally.\n", new UTF8Encoding(false));
        Add(P("bilingual.txt"), "UTF-8 text, Arabic + English", Bilingual);
        File.WriteAllText(P("cp1256.txt"), "مرحبا بالعالم الفاتورة 4821\r\n", Encoding.GetEncoding(1256));
        Add(P("cp1256.txt"), "legacy Windows-1256 Arabic text", ArabicOnly);

        File.WriteAllText(P("notes.md"), "# مرحبا بالعالم\n\nهذه **الفاتورة** رقم 4821.\n\n## Invoice\n\n- DropFlow converts files locally\n- second point\n\n| الصنف | السعر |\n|---|---|\n| قلم | 15 |\n\n```\ncode block\n```\n", new UTF8Encoding(false));
        Add(P("notes.md"), "Markdown, Arabic + English, table, code", Bilingual);

        File.Copy(P("src-ar.html"), P("page.html"), true);
        Add(P("page.html"), "HTML page, RTL Arabic + English, table", Bilingual);

        if (File.Exists(P("src-ar.odt")))
        {
            File.Copy(P("src-ar.odt"), P("arabic.odt"), true);
            Add(P("arabic.odt"), "ODT (LibreOffice), Arabic + English", Bilingual);
            if (Soffice(P("arabic.odt"), "docx:MS Word 2007 XML", folder, "arabic.docx") is { } docx) Add(docx, "DOCX (LibreOffice), Arabic + English, table", Bilingual);
            if (Soffice(P("arabic.odt"), "doc:MS Word 97", folder, "arabic.doc") is { } doc) Add(doc, "legacy DOC, Arabic + English", Bilingual);
            if (Soffice(P("arabic.odt"), "rtf:Rich Text Format", folder, "arabic.rtf") is { } rtf) Add(rtf, "RTF, Arabic + English", Bilingual);
        }
        if (Soffice(P("src-en.html"), "odt:writer8", folder, "src-en.odt") is { } enOdt && Soffice(enOdt, "docx:MS Word 2007 XML", folder, "english.docx") is { } enDocx)
            Add(enDocx, "DOCX, English, 3 page breaks", English);
    }

    private void BuildSpreadsheets()
    {
        using (var book = new XLWorkbook())
        {
            var sheet = book.AddWorksheet("الفاتورة");
            sheet.Cell(1, 1).Value = "الصنف"; sheet.Cell(1, 2).Value = "Invoice"; sheet.Cell(1, 3).Value = "Total";
            sheet.Cell(2, 1).Value = "مرحبا"; sheet.Cell(2, 2).Value = 4821; sheet.Cell(2, 3).FormulaA1 = "B2*2";
            sheet.Cell(3, 1).Value = "قلم"; sheet.Cell(3, 2).Value = 15.5; sheet.Cell(3, 3).FormulaA1 = "B3*2";
            var second = book.AddWorksheet("Sheet2");
            second.Cell(1, 1).Value = "DropFlow";
            book.SaveAs(P("invoice.xlsx"));
        }
        Add(P("invoice.xlsx"), "XLSX, 2 sheets, Arabic sheet name, formulas", Bilingual);
        if (Soffice(P("invoice.xlsx"), "xls:MS Excel 97", folder, "invoice.xls") is { } xls) Add(xls, "legacy XLS", Bilingual);
        if (Soffice(P("invoice.xlsx"), "ods:calc8", folder, "invoice.ods") is { } ods) Add(ods, "ODS", Bilingual);

        File.WriteAllText(P("table.csv"), "الصنف,Invoice,Total\nمرحبا,4821,9642\n\"قلم, أزرق\",15.5,31\n", new UTF8Encoding(true));
        Add(P("table.csv"), "CSV with BOM, Arabic, quoted comma", Bilingual);
        File.WriteAllText(P("table.tsv"), "name\tInvoice\nمرحبا\t4821\n", new UTF8Encoding(false));
        Add(P("table.tsv"), "TSV, Arabic", Bilingual);
        File.WriteAllText(P("data.json"), """[{"name":"مرحبا","Invoice":4821,"paid":true},{"name":"DropFlow","Invoice":15.5,"paid":false}]""", new UTF8Encoding(false));
        Add(P("data.json"), "JSON array of objects, Arabic", Bilingual);
    }

    private void BuildSlides()
    {
        if (!File.Exists(P("arabic-edge.pdf"))) return;
        if (Soffice(P("arabic-edge.pdf"), "pptx:Impress MS PowerPoint 2007 XML", folder, "slides.pptx", "--infilter=impress_pdf_import") is { } pptx)
        {
            Add(pptx, "PPTX (LibreOffice from a PDF), Arabic + English", Bilingual);
            if (Soffice(pptx, "ppt:MS PowerPoint 97", folder, "slides.ppt") is { } ppt) Add(ppt, "legacy PPT", Bilingual);
            if (Soffice(pptx, "odp:impress8", folder, "slides.odp") is { } odp) Add(odp, "ODP", Bilingual);
        }
    }

    private void BuildEpub()
    {
        if (File.Exists(P("arabic.odt")) && Soffice(P("arabic.odt"), "epub:EPUB", folder, "arabic.epub") is { } ar) Add(ar, "EPUB 3 (LibreOffice), Arabic + English", Bilingual);
        if (File.Exists(P("src-en.odt")) && Soffice(P("src-en.odt"), "epub:EPUB", folder, "english.epub") is { } en) Add(en, "EPUB 3, English, 3 chapters", English);
    }

    private void BuildSubtitles()
    {
        File.WriteAllText(P("arabic.srt"), "1\n00:00:00,500 --> 00:00:02,000\nمرحبا بالعالم\n\n2\n00:00:02,500 --> 00:00:04,000\n<i>الفاتورة</i> رقم 4821\nسطر ثانٍ\n\n", new UTF8Encoding(false));
        Add(P("arabic.srt"), "SRT, Arabic, italics, two-line cue", ArabicOnly);
        File.WriteAllText(P("english.vtt"), "WEBVTT\n\n00:00.500 --> 00:02.000\nInvoice number 4821\n\n00:02.500 --> 00:04.000 align:start\nDropFlow converts files\n", new UTF8Encoding(false));
        Add(P("english.vtt"), "WebVTT, English, cue setting", English);
        File.WriteAllText(P("styled.ass"), "[Script Info]\nScriptType: v4.00+\nPlayResX: 640\nPlayResY: 360\n\n[V4+ Styles]\nFormat: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding\nStyle: Default,Arial,28,&H00FFFFFF,&H000000FF,&H00000000,&H64000000,0,0,0,0,100,100,0,0,1,2,1,2,10,10,10,1\n\n[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\nDialogue: 0,0:00:00.50,0:00:02.00,Default,,0,0,0,,{\\b1}Invoice{\\b0} number 4821\nDialogue: 0,0:00:02.50,0:00:04.00,Default,,0,0,0,,مرحبا بالعالم\\NDropFlow\n", new UTF8Encoding(false));
        Add(P("styled.ass"), "ASS with override tags and \\N, Arabic + English", Bilingual);
    }

    // ---------------- media ----------------

    private void BuildAudio()
    {
        var ffmpeg = engines.Locate(EngineId.Ffmpeg) ?? throw new InvalidOperationException("FFmpeg missing");
        var tone = P("src-tone.wav");
        // 1 s silence, 3 s of two tones, 1 s silence (so trim-silence has something to do).
        Run(ffmpeg, $"-y -hide_banner -loglevel error -f lavfi -i \"sine=frequency=440:duration=3:sample_rate=44100\" -f lavfi -i \"sine=frequency=660:duration=3:sample_rate=44100\" -filter_complex \"[0][1]amerge=inputs=2,adelay=1000|1000,apad=pad_dur=1\" -ac 2 \"{tone}\"");
        foreach (var (ext, args) in new[]
        {
            ("mp3", "-c:a libmp3lame -b:a 128k"), ("m4a", "-c:a aac -b:a 128k"), ("wav", "-c:a pcm_s16le"), ("flac", "-c:a flac"),
            ("ogg", "-c:a libvorbis"), ("opus", "-c:a libopus -b:a 64k"), ("aiff", "-c:a pcm_s16be"), ("wma", "-c:a wmav2"), ("aac", "-c:a aac -b:a 128k"),
        })
        {
            var output = P($"tone.{ext}");
            Run(ffmpeg, $"-y -hide_banner -loglevel error -i \"{tone}\" {args} -metadata title=\"مرحبا Matrix\" \"{output}\"");
            Add(output, $"{ext.ToUpperInvariant()}, 5 s stereo, silence at both ends, Arabic title tag");
        }

        // Real English speech (Windows text-to-speech) for speech to text and bleeping.
        var speech = P("speech-en.wav");
        var script = $"Add-Type -AssemblyName System.Speech; $s = New-Object System.Speech.Synthesis.SpeechSynthesizer; $s.Rate = -1; $s.SetOutputToWaveFile('{speech}'); $s.Speak('Hello. This invoice number is four eight two one. DropFlow converts files locally.'); $s.Dispose()";
        Run("powershell.exe", $"-NoProfile -NonInteractive -Command \"{script}\"", 60);
        if (File.Exists(speech))
        {
            Run(ffmpeg, $"-y -hide_banner -loglevel error -i \"{speech}\" -c:a libmp3lame -b:a 96k \"{P("speech-en.mp3")}\"");
            Add(P("speech-en.mp3"), "spoken English (Windows TTS)", ["invoice", "DropFlow", "Drop"]);
        }
    }

    private void BuildVideo()
    {
        var ffmpeg = engines.Locate(EngineId.Ffmpeg) ?? throw new InvalidOperationException("FFmpeg missing");
        var src = P("src-video.mkv");
        Run(ffmpeg, $"-y -hide_banner -loglevel error -f lavfi -i \"testsrc2=size=640x360:rate=25:duration=4\" -f lavfi -i \"sine=frequency=500:duration=4\" -c:v libx264 -pix_fmt yuv420p -c:a aac -shortest \"{src}\"");
        foreach (var (ext, args) in new[]
        {
            ("mp4", "-c:v libx264 -pix_fmt yuv420p -c:a aac -movflags +faststart"), ("mov", "-c:v libx264 -pix_fmt yuv420p -c:a aac"),
            ("mkv", "-c copy"), ("webm", "-c:v libvpx-vp9 -b:v 300k -c:a libopus"), ("avi", "-c:v mpeg4 -q:v 5 -c:a libmp3lame"),
            ("wmv", "-c:v wmv2 -b:v 600k -c:a wmav2"), ("flv", "-c:v flv1 -b:v 600k -c:a libmp3lame -ar 44100"), ("m4v", "-c:v libx264 -pix_fmt yuv420p -c:a aac"),
        })
        {
            var output = P($"clip.{ext}");
            Run(ffmpeg, $"-y -hide_banner -loglevel error -i \"{src}\" {args} \"{output}\"", 120);
            Add(output, $"{ext.ToUpperInvariant()}, 640x360 4 s with sound");
        }

        var vertical = P("vertical-rotated.mp4");
        Run(ffmpeg, $"-y -hide_banner -loglevel error -display_rotation 90 -i \"{P("clip.mp4")}\" -c copy \"{vertical}\"");
        Add(vertical, "MP4 with a 90° display rotation (phone video)");

        var silent = P("no-audio.mp4");
        Run(ffmpeg, $"-y -hide_banner -loglevel error -f lavfi -i \"testsrc=size=320x240:rate=25:duration=3\" -c:v libx264 -pix_fmt yuv420p \"{silent}\"");
        Add(silent, "MP4 with no audio track");
    }

    private void BuildArchives()
    {
        var content = P("archive-content");
        Directory.CreateDirectory(Path.Combine(content, "مجلد"));
        File.WriteAllText(Path.Combine(content, "readme.txt"), "Invoice 4821 DropFlow\n");
        File.WriteAllText(Path.Combine(content, "مجلد", "ملف عربي.txt"), "مرحبا بالعالم\n");
        File.Copy(P("transparent.png"), Path.Combine(content, "picture.png"), true);

        var zip = P("bundle.zip");
        if (File.Exists(zip)) File.Delete(zip);
        ZipFile.CreateFromDirectory(content, zip, CompressionLevel.Optimal, includeBaseDirectory: false, Encoding.UTF8);
        Add(zip, "ZIP with an Arabic folder and file name");

        var sevenZip = engines.Locate(EngineId.SevenZip);
        if (sevenZip is not null)
        {
            Run(sevenZip, $"a -bso0 -bsp0 \"{P("bundle.7z")}\" \"{content}\\*\"", 60);
            Add(P("bundle.7z"), "7z (7-Zip)");
            Run(sevenZip, $"a -bso0 -bsp0 -ttar \"{P("bundle.tar")}\" \"{content}\\*\"", 60);
            Add(P("bundle.tar"), "TAR");
            Run(sevenZip, $"a -bso0 -bsp0 -tgzip \"{P("bundle.tgz")}\" \"{P("bundle.tar")}\"", 60);
            Add(P("bundle.tgz"), "TGZ (gzip of a tar)");
        }
        else Problems.Add("7-Zip missing: no 7z/tar/tgz samples");

        using (var input = File.OpenRead(P("bilingual.txt")))
        using (var output = File.Create(P("single.txt.gz")))
        using (var gz = new GZipStream(output, CompressionLevel.Optimal))
            input.CopyTo(gz);
        Add(P("single.txt.gz"), "GZ of one text file");

        var rar = @"C:\Program Files\WinRAR\Rar.exe";
        if (File.Exists(rar))
        {
            Run(rar, $"a -ep1 -r -idq \"{P("bundle.rar")}\" \"{content}\\*\"", 60);
            Add(P("bundle.rar"), "RAR 5 (WinRAR)");
        }
        else Problems.Add("WinRAR missing: no RAR sample");
    }

    // ---------------- helpers ----------------

    private string? Soffice(string input, string filter, string outDir, string expectedName, string extra = "")
    {
        var soffice = engines.Locate(EngineId.LibreOffice);
        if (soffice is null) { Problems.Add($"LibreOffice missing: {expectedName} not produced"); return null; }
        var work = Path.Combine(folder, "lo-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);
        var profile = new Uri(Path.Combine(folder, "lo-profile")).AbsoluteUri;
        Run(soffice, $"-env:UserInstallation={profile} --headless --norestore {extra} --convert-to \"{filter}\" --outdir \"{work}\" \"{input}\"", 180);
        var produced = Directory.GetFiles(work).FirstOrDefault();
        var target = Path.Combine(outDir, expectedName);
        if (produced is null) { Problems.Add($"LibreOffice produced nothing for {expectedName}"); return null; }
        File.Move(produced, target, true);
        Directory.Delete(work, true);
        return target;
    }

    private void Run(string exe, string args, int timeoutSeconds = 60)
    {
        using var p = Process.Start(new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true,
        })!;
        var err = p.StandardError.ReadToEndAsync();
        _ = p.StandardOutput.ReadToEndAsync();
        if (!p.WaitForExit(timeoutSeconds * 1000)) { try { p.Kill(true); } catch { } Problems.Add($"timeout: {Path.GetFileName(exe)} {args[..Math.Min(80, args.Length)]}"); return; }
        if (p.ExitCode != 0) Problems.Add($"{Path.GetFileName(exe)} exit {p.ExitCode}: {err.Result.Trim()[..Math.Min(300, err.Result.Trim().Length)]}");
    }

    internal static string? FindRepoFile(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}
