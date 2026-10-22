using System.Drawing;
using System.Text.Json;
using DropFlow.AI.Ocr;
using DropFlow.Core.Operations;
using DropFlow.TestSupport;
using Xunit;

namespace DropFlow.AI.Tests;

/// <summary>Reading order for right-to-left text, tested on word boxes that GDI+ really produced for Arabic and mixed lines.</summary>
public class OcrOrderingTests
{
    private static OcrWord W(string text, double x, double y = 10, double w = 60, double h = 30) => new(text, new OcrRect(x, y, w, h));

    [Fact]
    public void Arabic_words_listed_left_to_right_come_out_right_to_left()
    {
        // visually: [three][two][one] from the left; reading order is one two three
        var line = OcrOrdering.BuildLine([W("ثلاثة", 10), W("اثنان", 100), W("واحد", 200)]);
        Assert.True(line.IsRightToLeft);
        Assert.Equal("واحد اثنان ثلاثة", line.Text);
        Assert.Equal(["واحد", "اثنان", "ثلاثة"], line.Words.Select(w => w.Text));
    }

    [Fact]
    public void The_order_the_engine_lists_words_in_does_not_matter()
    {
        var words = new[] { W("واحد", 200), W("ثلاثة", 10), W("اثنان", 100) };
        foreach (var permutation in Permutations(words))
            Assert.Equal("واحد اثنان ثلاثة", OcrOrdering.BuildLine(permutation).Text);
    }

    [Fact]
    public void English_stays_left_to_right()
    {
        var line = OcrOrdering.BuildLine([W("world", 150), W("Hello", 10), W("again", 300)]);
        Assert.False(line.IsRightToLeft);
        Assert.Equal("Hello world again", line.Text);
    }

    [Fact]
    public void A_latin_phrase_inside_arabic_keeps_its_own_left_to_right_order()
    {
        // visual (left to right): [tail][LA][NY][head]  with Arabic head/tail around an English abbreviation
        var line = OcrOrdering.BuildLine([W("آخرها", 10), W("LA", 100), W("NY", 170), W("أولها", 240)]);
        Assert.True(line.IsRightToLeft);
        Assert.Equal("أولها LA NY آخرها", line.Text);
    }

    [Fact]
    public void Numbers_and_neutral_punctuation_sit_between_their_neighbours()
    {
        // "السعر 250 دولار" with a dash: visual [دولار][250][-][السعر]
        var line = OcrOrdering.BuildLine([W("دولار", 10), W("250", 80), W("-", 150, w: 15), W("السعر", 180)]);
        Assert.Equal("السعر - 250 دولار", line.Text);
    }

    [Fact]
    public void A_neutral_between_two_latin_words_in_an_arabic_line_stays_with_them()
    {
        // visual: [tail][A][&][B][head]; the "A & B" phrase reads left to right inside the right-to-left sentence
        var line = OcrOrdering.BuildLine([W("آخر", 10), W("A", 80, w: 20), W("&", 110, w: 15), W("B", 135, w: 20), W("أول", 190)]);
        Assert.Equal("أول A & B آخر", line.Text);
    }

    [Fact]
    public void Hebrew_is_right_to_left_too()
    {
        var line = OcrOrdering.BuildLine([W("עולם", 10), W("שלום", 100)]);
        Assert.Equal("שלום עולם", line.Text);
    }

    [Fact]
    public void Unspaced_scripts_are_joined_without_spaces()
    {
        var line = OcrOrdering.BuildLine([W("今天", 10), W("天气", 80), W("good", 150)]);
        Assert.Equal("今天天气 good", line.Text);
    }

    [Fact]
    public void Loose_words_are_grouped_into_lines_top_to_bottom()
    {
        var words = new[]
        {
            W("second", 10, y: 100), W("Hello", 10, y: 10), W("line", 100, y: 102), W("world", 100, y: 12),
        };
        var lines = OcrOrdering.GroupIntoLines(words);
        Assert.Equal(["Hello world", "second line"], lines.Select(l => l.Text));
        Assert.True(lines[0].Bounds.Y < lines[1].Bounds.Y);
    }

    [Theory]
    [InlineData("اشتريت الكتاب من المكتبة القريبة")]
    [InlineData("ذهبت إلى السوق اليوم صباحا")]
    [InlineData("اشتريت iPhone من المتجر الجديد")]
    [InlineData("السعر 250 دولار فقط")]
    [InlineData("مرحبا بالعالم")]
    public void Real_gdi_layout_of_arabic_is_read_back_in_logical_order(string sentence)
    {
        // Lay the sentence out with GDI+ (a full bidi implementation), take the real pixel box of each word, shuffle
        // them, and check that reading order is recovered from the geometry alone.
        var laidOut = TextImage.MeasureOnly(sentence, 48, "Arial", 1200, 200, rtl: true);
        Assert.Equal(sentence.Split(' ').Length, laidOut.Words.Count);

        var boxes = laidOut.Words.Select(w => new OcrWord(w.Word, new OcrRect(w.Bounds.X, w.Bounds.Y, w.Bounds.Width, w.Bounds.Height))).ToList();
        foreach (var permutation in Permutations(boxes.ToArray()).Take(24))
        {
            var line = OcrOrdering.BuildLine(permutation);
            Assert.True(line.IsRightToLeft);
            Assert.Equal(sentence, line.Text);
        }
    }

    [Fact]
    public void Real_gdi_layout_of_english_is_read_back_in_order()
    {
        var laidOut = TextImage.MeasureOnly("The quick brown fox", 48, "Arial", 1200, 200, rtl: false);
        var boxes = laidOut.Words.Select(w => new OcrWord(w.Word, new OcrRect(w.Bounds.X, w.Bounds.Y, w.Bounds.Width, w.Bounds.Height))).Reverse().ToList();
        Assert.Equal("The quick brown fox", OcrOrdering.BuildLine(boxes).Text);
    }

    private static IEnumerable<T[]> Permutations<T>(T[] items)
    {
        if (items.Length <= 1) { yield return items; yield break; }
        for (var i = 0; i < items.Length; i++)
        {
            var rest = items.Where((_, j) => j != i).ToArray();
            foreach (var tail in Permutations(rest)) yield return [items[i], .. tail];
        }
    }
}

public class OcrModelTests
{
    [Fact]
    public void Rect_math()
    {
        var a = new OcrRect(10, 20, 30, 40);
        Assert.Equal(40, a.Right);
        Assert.Equal(60, a.Bottom);
        Assert.Equal(25, a.CenterX);
        Assert.Equal(new OcrRect(5, 15, 40, 50), a.Inflate(5));
        Assert.Equal(new OcrRect(20, 40, 60, 80), a.Scale(2));
        Assert.Equal(new OcrRect(0, 0, 50, 60), OcrRect.Union([new OcrRect(0, 0, 10, 10), new OcrRect(40, 50, 10, 10)]));
        Assert.Equal(1.0, new OcrRect(0, 0, 100, 20).VerticalOverlap(new OcrRect(500, 0, 10, 20)));
        Assert.Equal(0.0, new OcrRect(0, 0, 100, 20).VerticalOverlap(new OcrRect(0, 50, 10, 20)));
    }

    [Fact]
    public void Result_scales_all_rectangles_and_serialises_to_json()
    {
        var word = new OcrWord("مرحبا", new OcrRect(10, 20, 30, 40), 0.9);
        var line = new OcrLine("مرحبا", [word], new OcrRect(10, 20, 30, 40), true);
        var result = new OcrResult("tesseract", "ara", 100, 50, [line]);

        var scaled = result.Scaled(2, 200, 100);
        Assert.Equal(new OcrRect(20, 40, 60, 80), scaled.Words.Single().Bounds);
        Assert.Equal(new OcrRect(20, 40, 60, 80), scaled.Lines[0].Bounds);
        Assert.Equal(200, scaled.ImageWidth);

        using var doc = JsonDocument.Parse(result.ToJson());
        var root = doc.RootElement;
        Assert.Equal("tesseract", root.GetProperty("engine").GetString());
        Assert.Equal("مرحبا", root.GetProperty("text").GetString());
        var w = root.GetProperty("lines")[0].GetProperty("words")[0];
        Assert.Equal(10, w.GetProperty("bounds")[0].GetDouble());
        Assert.Equal(0.9, w.GetProperty("confidence").GetDouble());
        Assert.True(root.GetProperty("lines")[0].GetProperty("rtl").GetBoolean());
        Assert.Contains("مرحبا", result.ToJson()); // not escaped
        Assert.False(result.IsEmpty);
        Assert.True(new OcrResult("x", "y", 1, 1, []).IsEmpty);
    }
}

public class TesseractParsingTests
{
    private const string ArabicTsv =
        "level\tpage_num\tblock_num\tpar_num\tline_num\tword_num\tleft\ttop\twidth\theight\tconf\ttext\n" +
        "1\t1\t0\t0\t0\t0\t0\t0\t800\t200\t-1\t\n" +
        "2\t1\t1\t0\t0\t0\t40\t40\t700\t60\t-1\t\n" +
        "3\t1\t1\t1\t0\t0\t40\t40\t700\t60\t-1\t\n" +
        "4\t1\t1\t1\t1\t0\t40\t40\t700\t60\t-1\t\n" +
        "5\t1\t1\t1\t1\t1\t500\t40\t240\t60\t96.5\tمرحبا\n" +
        "5\t1\t1\t1\t1\t2\t300\t40\t180\t60\t91.2\tبالعالم\n" +
        "5\t1\t1\t1\t1\t3\t100\t40\t150\t60\t88\t \n" +                    // blank word: ignored
        "4\t1\t1\t1\t2\t0\t40\t120\t300\t50\t-1\t\n" +
        "5\t1\t1\t1\t2\t1\t40\t120\t100\t50\t95\tHello\n" +
        "5\t1\t1\t1\t2\t2\t160\t120\t120\t50\t94\tworld\n";

    [Fact]
    public void Tsv_rows_become_lines_with_rectangles_confidence_and_reading_order()
    {
        var result = TesseractOcr.ParseTsv(ArabicTsv, "ara+eng", 800, 200);
        Assert.Equal("tesseract", result.Engine);
        Assert.Equal(2, result.Lines.Count);

        var arabic = result.Lines[0];
        Assert.True(arabic.IsRightToLeft);
        Assert.Equal("مرحبا بالعالم", arabic.Text);                       // rightmost word first
        Assert.Equal(new OcrRect(500, 40, 240, 60), arabic.Words[0].Bounds);
        Assert.Equal(0.965, arabic.Words[0].Confidence!.Value, 3);
        Assert.Equal(new OcrRect(300, 40, 440, 60), arabic.Bounds);

        var english = result.Lines[1];
        Assert.False(english.IsRightToLeft);
        Assert.Equal("Hello world", english.Text);
        Assert.Equal("مرحبا بالعالم\nHello world", result.Text);
    }

    [Fact]
    public void Tsv_in_visual_order_gives_the_same_answer()
    {
        // Some Tesseract builds list a right-to-left line left to right. The boxes tell the truth either way.
        var visual = ArabicTsv.Replace(
            "5\t1\t1\t1\t1\t1\t500\t40\t240\t60\t96.5\tمرحبا\n5\t1\t1\t1\t1\t2\t300\t40\t180\t60\t91.2\tبالعالم\n",
            "5\t1\t1\t1\t1\t1\t300\t40\t180\t60\t91.2\tبالعالم\n5\t1\t1\t1\t1\t2\t500\t40\t240\t60\t96.5\tمرحبا\n");
        Assert.NotEqual(ArabicTsv, visual);
        Assert.Equal("مرحبا بالعالم", TesseractOcr.ParseTsv(visual, "ara", 800, 200).Lines[0].Text);
    }

    [Fact]
    public void Tsv_with_crlf_garbage_and_no_words_is_handled()
    {
        Assert.True(TesseractOcr.ParseTsv("", "eng", 1, 1).IsEmpty);
        Assert.True(TesseractOcr.ParseTsv("junk line\r\nnot\ttsv\r\n", "eng", 1, 1).IsEmpty);
        var crlf = "5\t1\t1\t1\t1\t1\t10\t20\t30\t40\t90\tOK\r\n";
        Assert.Equal("OK", TesseractOcr.ParseTsv(crlf, "eng", 100, 100).Text);
        Assert.Null(TesseractOcr.ParseTsv("5\t1\t1\t1\t1\t1\t10\t20\t30\t40\t-1\tNoConf\n", "eng", 1, 1).Words.Single().Confidence);
    }

    [Fact]
    public void Language_list_is_parsed_from_both_tesseract_4_and_5_output()
    {
        const string v5 = "List of available languages in \"C:\\Program Files\\Tesseract-OCR/tessdata/\" (3):\nara\neng\nosd\n";
        const string v4 = "List of available languages (2):\neng\nara\n";
        Assert.Equal(["ara", "eng", "osd"], TesseractOcr.ParseLanguageList(v5));
        Assert.Equal(["eng", "ara"], TesseractOcr.ParseLanguageList(v4));
        Assert.Equal(["chi_sim", "script/Arabic"], TesseractOcr.ParseLanguageList("List of available languages (2):\nchi_sim\nscript/Arabic\r\n"));
        Assert.Empty(TesseractOcr.ParseLanguageList(""));
        Assert.Empty(TesseractOcr.ParseLanguageList("Error: something went wrong with spaces"));
    }

    [Fact]
    public void Recognise_arguments_match_the_tesseract_command_line()
    {
        Assert.Equal(
            [@"C:\w\in.png", @"C:\w\out", "-l", "ara+eng", "tsv"],
            TesseractOcr.BuildRecognizeArguments(@"C:\w\in.png", @"C:\w\out", ["ara", "eng"]));
        Assert.Equal(
            ["a.png", "o", "-l", "eng", "--dpi", "300", "tsv"],
            TesseractOcr.BuildRecognizeArguments("a.png", "o", ["eng"], dpi: 300));
    }

    [Fact]
    public void Pdf_arguments_use_list_mode_with_an_explicit_dpi_and_the_pdf_renderer_last()
    {
        var args = TesseractOcr.BuildPdfArguments(@"C:\w\pages.txt", @"C:\w\searchable", ["ara", "eng"], 300);
        Assert.Equal([@"C:\w\pages.txt", @"C:\w\searchable", "-l", "ara+eng", "--dpi", "300", "pdf"], args);
        Assert.Equal("pdf", args[^1]);
    }
}

public class OcrLanguageServiceTests
{
    private sealed class FakeSource(params string[] tags) : IWindowsOcrLanguageSource
    {
        public IReadOnlyList<string> InstalledTags() => tags;
    }

    [Fact]
    public void Bare_codes_match_any_region_and_full_tags_match_exactly()
    {
        var svc = new OcrLanguageService(new FakeSource("en-US", "ar-EG"));
        Assert.True(svc.IsInstalled("en"));
        Assert.True(svc.IsInstalled("en-US"));
        Assert.False(svc.IsInstalled("en-GB"));
        Assert.True(svc.IsInstalled("ar"));
        Assert.False(svc.IsInstalled("ar-SA"));
        Assert.False(svc.IsInstalled("fr"));
        Assert.False(svc.IsInstalled("xx"));
        Assert.Equal("ar-EG", svc.InstalledTagFor("ar"));
        Assert.Null(svc.InstalledTagFor("fr"));
    }

    [Fact]
    public void Nothing_is_assumed_installed()
    {
        var svc = new OcrLanguageService(new FakeSource());
        foreach (var lang in OcrLanguages.All) Assert.False(svc.IsInstalled(lang.Iso1), lang.Iso1);
    }

    [Fact]
    public void Arabic_install_hint_has_the_exact_windows_capability_and_powershell_command()
    {
        var hint = OcrLanguageService.InstallHint("ar")!;
        Assert.Equal("Language.OCR~~~ar-SA~0.0.1.0", hint.CapabilityName);
        Assert.Equal("Add-WindowsCapability -Online -Name Language.OCR~~~ar-SA~0.0.1.0", hint.PowerShellCommand);
        Assert.True(hint.RequiresAdmin);
        Assert.Contains("administrator", hint.Instructions);
        Assert.Contains(hint.PowerShellCommand, hint.Instructions);
        Assert.Equal("Arabic", hint.Language.DisplayName);
        Assert.Equal("ara", hint.Language.Tesseract);
    }

    [Fact]
    public void Hints_accept_full_tags_and_names_and_exist_only_for_languages_windows_has()
    {
        Assert.Equal("Language.OCR~~~ar-SA~0.0.1.0", OcrLanguageService.InstallHint("ar-SA")!.CapabilityName);
        Assert.Equal("Language.OCR~~~en-US~0.0.1.0", OcrLanguageService.InstallHint("en")!.CapabilityName);
        Assert.Equal("Language.OCR~~~fr-FR~0.0.1.0", OcrLanguageService.InstallHint("fr")!.CapabilityName);
        Assert.Null(OcrLanguageService.InstallHint("fa")); // Persian: Tesseract only
        Assert.Null(OcrLanguageService.InstallHint("he"));
        Assert.Null(OcrLanguageService.InstallHint("klingon"));
        foreach (var lang in OcrLanguages.All.Where(l => l.WindowsTag is not null))
        {
            Assert.Matches(@"^Language\.OCR~~~[A-Za-z\-]+~0\.0\.1\.0$", lang.WindowsCapabilityName);
            Assert.Equal($"Add-WindowsCapability -Online -Name {lang.WindowsCapabilityName}", lang.WindowsInstallCommand);
        }
    }

    [Fact]
    public void Language_codes_resolve_from_all_the_spellings_users_and_engines_use()
    {
        Assert.Equal("ar", OcrLanguages.Find("ARA")!.Iso1);
        Assert.Equal("ar", OcrLanguages.Find("ar-SA")!.Iso1);
        Assert.Equal("en", OcrLanguages.Find("eng")!.Iso1);
        Assert.Equal("zh", OcrLanguages.Find("chi_sim")!.Iso1);
        Assert.Null(OcrLanguages.Find(""));
        Assert.Null(OcrLanguages.Find(null));

        var list = OcrLanguages.ParseList("ar+en, ARA ,xx,auto", out var unknown);
        Assert.Equal(["ar", "en"], list.Select(l => l.Iso1));
        Assert.Equal(["xx"], unknown);
        Assert.Empty(OcrLanguages.ParseList("auto", out _));
    }

#if WINDOWS
    [Fact]
    public void The_real_windows_source_never_throws_and_returns_bcp47_tags()
    {
        var tags = new WindowsOcrLanguageSource().InstalledTags();
        Assert.All(tags, t => Assert.Matches(@"^[a-z]{2,3}(-[A-Za-z0-9]+)*$", t));
    }
#else
    [Fact]
    public void Without_windows_no_system_ocr_pack_is_reported()
    {
        Assert.Empty(new OcrLanguageService().InstalledTags());
        Assert.False(new OcrLanguageService().IsInstalled("en"));
    }
#endif

    [OcrFact("en")]
    public void English_is_installed_on_this_machine_as_expected()
    {
        var svc = new OcrLanguageService();
        Assert.True(svc.IsInstalled("en"));
        Assert.NotNull(svc.InstalledTagFor("en"));
    }
}

public class OcrPlannerTests
{
    private static OcrCapabilities Caps(string[] windows, bool tess = false, params string[] tessLangs) => new(windows, tess, tessLangs);
    private static IReadOnlyList<OcrLanguage> Req(string spec) => OcrLanguages.ParseList(spec, out _);

    [Fact]
    public void English_with_the_windows_pack_uses_windows()
    {
        var plan = OcrPlanner.Plan(Req("en"), OcrEnginePreference.Auto, Caps(["en-US"])).Plan!;
        Assert.Equal(OcrEngineKind.Windows, plan.Engine);
        Assert.Equal("en-US", plan.WindowsTag);
    }

    [Fact]
    public void Arabic_without_its_pack_or_tesseract_fails_with_the_exact_install_command()
    {
        var result = OcrPlanner.Plan(Req("ar"), OcrEnginePreference.Auto, Caps(["en-US"]));
        Assert.Null(result.Plan);
        Assert.Contains("Add-WindowsCapability -Online -Name Language.OCR~~~ar-SA~0.0.1.0", result.Error);
        Assert.Contains("Tesseract is not installed", result.Error);
    }

    [Fact]
    public void Arabic_falls_back_to_tesseract_when_windows_lacks_the_pack()
    {
        var plan = OcrPlanner.Plan(Req("ar"), OcrEnginePreference.Auto, Caps(["en-US"], true, "eng", "ara", "osd")).Plan!;
        Assert.Equal(OcrEngineKind.Tesseract, plan.Engine);
        Assert.Equal(["ara"], plan.TesseractLanguages);
    }

    [Fact]
    public void Arabic_plus_english_goes_to_tesseract_when_windows_only_has_one_of_them()
    {
        var plan = OcrPlanner.Plan(Req("ar+en"), OcrEnginePreference.Auto, Caps(["en-US"], true, "eng", "ara")).Plan!;
        Assert.Equal(OcrEngineKind.Tesseract, plan.Engine);
        Assert.Equal(["ara", "eng"], plan.TesseractLanguages);
    }

    [Fact]
    public void Several_languages_all_in_windows_use_the_first_as_primary()
    {
        var plan = OcrPlanner.Plan(Req("ar+en"), OcrEnginePreference.Auto, Caps(["en-US", "ar-SA"])).Plan!;
        Assert.Equal(OcrEngineKind.Windows, plan.Engine);
        Assert.Equal("ar-SA", plan.WindowsTag);
    }

    [Fact]
    public void Tesseract_missing_language_data_is_named()
    {
        var result = OcrPlanner.Plan(Req("ar"), OcrEnginePreference.Auto, Caps([], true, "eng"));
        Assert.Null(result.Plan);
        Assert.Contains("ara", result.Error);
        Assert.Contains("traineddata", result.Error);
    }

    [Fact]
    public void Persian_has_no_windows_pack_so_the_message_says_so()
    {
        var result = OcrPlanner.Plan(Req("fa"), OcrEnginePreference.Auto, Caps(["en-US"], true, "eng"));
        Assert.Null(result.Plan);
        Assert.Contains("no OCR pack", result.Error);
        Assert.Contains("fas", result.Error);
        Assert.Equal(OcrEngineKind.Tesseract, OcrPlanner.Plan(Req("fa"), OcrEnginePreference.Auto, Caps([], true, "fas")).Plan!.Engine);
    }

    [Fact]
    public void Forcing_an_engine_never_falls_back_to_the_other()
    {
        var forcedWindows = OcrPlanner.Plan(Req("ar"), OcrEnginePreference.Windows, Caps(["en-US"], true, "ara"));
        Assert.Null(forcedWindows.Plan);
        var forcedTess = OcrPlanner.Plan(Req("en"), OcrEnginePreference.Tesseract, Caps(["en-US"]));
        Assert.Null(forcedTess.Plan);
        Assert.Contains("Tesseract is not installed", forcedTess.Error);
        Assert.Equal(OcrEngineKind.Tesseract, OcrPlanner.Plan(Req("en"), OcrEnginePreference.Tesseract, Caps(["en-US"], true, "eng")).Plan!.Engine);
    }

    [Fact]
    public void Auto_language_uses_windows_profile_languages_else_tesseract_else_explains()
    {
        var win = OcrPlanner.Plan([], OcrEnginePreference.Auto, Caps(["en-US"])).Plan!;
        Assert.Equal(OcrEngineKind.Windows, win.Engine);
        Assert.Null(win.WindowsTag);

        var tess = OcrPlanner.Plan([], OcrEnginePreference.Auto, Caps([], true, "osd", "eng", "ara")).Plan!;
        Assert.Equal(OcrEngineKind.Tesseract, tess.Engine);
        Assert.Equal(["ara", "eng"], tess.TesseractLanguages);

        var none = OcrPlanner.Plan([], OcrEnginePreference.Auto, Caps([]));
        Assert.Null(none.Plan);
        Assert.Contains("Language.OCR~~~en-US~0.0.1.0", none.Error);
    }
}
