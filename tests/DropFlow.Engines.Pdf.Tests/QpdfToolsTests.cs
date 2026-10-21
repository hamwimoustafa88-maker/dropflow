using DropFlow.Core.Operations;
using DropFlow.Engines.Common;
using DropFlow.Engines.Pdf.Tools;
using DropFlow.TestSupport;
using ImageMagick;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Exceptions;

namespace DropFlow.Engines.Pdf.Tests;

public class QpdfArgumentTests
{
    [Fact]
    public void Protect_builds_an_aes_256_command_with_all_permissions_by_default()
    {
        var args = QpdfArguments.Protect("in.pdf", "out.pdf", "user", "owner", QpdfPermissions.AllowAll);

        Assert.Equal(
            ["--encrypt", "user", "owner", "256", "--print=full", "--extract=y", "--modify=all", "--accessibility=y", "--", "in.pdf", "out.pdf"],
            args);
    }

    [Fact]
    public void Protect_switches_off_what_is_not_allowed()
    {
        var args = QpdfArguments.Protect("in.pdf", "out.pdf", "u", "o", new QpdfPermissions(Print: false, Copy: false, Edit: false, Annotate: true));

        Assert.Contains("--print=none", args);
        Assert.Contains("--extract=n", args);
        Assert.Contains("--modify=annotate", args);

        var locked = QpdfArguments.Protect("in.pdf", "out.pdf", "u", "o", new QpdfPermissions(true, true, false, false));
        Assert.Contains("--modify=none", locked);
        Assert.Contains("--print=full", locked);
    }

    [Fact]
    public void Protect_always_keeps_accessibility_for_screen_readers()
    {
        var args = QpdfArguments.Protect("in.pdf", "out.pdf", "u", "o", new QpdfPermissions(false, false, false, false));
        Assert.Contains("--accessibility=y", args);
    }

    [Fact]
    public void Protect_passes_odd_passwords_as_single_arguments()
    {
        var args = QpdfArguments.Protect("in.pdf", "out.pdf", "pass word \"quoted\" كلمة", "", QpdfPermissions.AllowAll);
        Assert.Equal("pass word \"quoted\" كلمة", args[1]);
        Assert.Equal("", args[2]);
    }

    [Fact]
    public void Unlock_adds_the_password_only_when_one_was_given()
    {
        Assert.Equal(["--password=s3cret", "--decrypt", "in.pdf", "out.pdf"], QpdfArguments.Unlock("in.pdf", "out.pdf", "s3cret"));
        Assert.Equal(["--decrypt", "in.pdf", "out.pdf"], QpdfArguments.Unlock("in.pdf", "out.pdf", null));
        Assert.Equal(["--decrypt", "in.pdf", "out.pdf"], QpdfArguments.Unlock("in.pdf", "out.pdf", ""));
    }

    [Fact]
    public void Repair_and_optimize_arguments()
    {
        Assert.Equal(["in.pdf", "out.pdf"], QpdfArguments.Repair("in.pdf", "out.pdf"));
        Assert.Equal(
            ["--object-streams=generate", "--compress-streams=y", "--recompress-flate", "--compression-level=9", "in.pdf", "out.pdf"],
            QpdfArguments.Optimize("in.pdf", "out.pdf"));
    }

    [Theory]
    [InlineData("print,copy,edit,annotate", true, true, true, true)]
    [InlineData("", true, true, true, true)]
    [InlineData(null, true, true, true, true)]
    [InlineData("print", true, false, false, false)]
    [InlineData("Copy; Annotate", false, true, false, true)]
    [InlineData("none", false, false, false, false)]
    [InlineData("all", true, true, true, true)]
    public void Permissions_parse(string? text, bool print, bool copy, bool edit, bool annotate)
    {
        Assert.True(QpdfPermissions.TryParse(text, out var p, out _));
        Assert.Equal((print, copy, edit, annotate), (p.Print, p.Copy, p.Edit, p.Annotate));
    }

    [Fact]
    public void Unknown_permission_words_are_rejected()
    {
        Assert.False(QpdfPermissions.TryParse("print,fly", out _, out var error));
        Assert.Contains("fly", error);
    }

    [Theory]
    [InlineData(0, false, true)]
    [InlineData(3, true, true)]   // warnings, but the output was written
    [InlineData(3, false, false)] // warnings and nothing written
    [InlineData(2, true, false)]  // error
    public void Exit_codes_are_interpreted_like_qpdf_documents_them(int exitCode, bool outputWritten, bool expected)
    {
        var result = new ProcessResult(exitCode, "", "", TimeSpan.Zero, false, false);
        Assert.Equal(expected, QpdfArguments.Succeeded(result, outputWritten));
    }

    [Fact]
    public void Wrong_password_and_damaged_file_messages_are_friendly()
    {
        var wrong = new ProcessResult(2, "", "qpdf: locked.pdf: invalid password", TimeSpan.Zero, false, false);
        Assert.Contains("password", QpdfArguments.Explain(wrong, "locked.pdf"));

        var broken = new ProcessResult(2, "", "qpdf: broken.pdf: can't find PDF header", TimeSpan.Zero, false, false);
        Assert.Contains("Repair", QpdfArguments.Explain(broken, "broken.pdf"));

        var other = new ProcessResult(2, "", "something odd", TimeSpan.Zero, false, false);
        Assert.Contains("something odd", QpdfArguments.Explain(other, "x.pdf"));
    }

    [Fact]
    public void Generated_owner_passwords_are_long_and_different_every_time()
    {
        var a = QpdfArguments.GenerateOwnerPassword();
        var b = QpdfArguments.GenerateOwnerPassword();
        Assert.Equal(32, a.Length);
        Assert.NotEqual(a, b);
    }
}

public class QpdfToolsTests
{
    private static string Sample(TempDir dir, string name = "doc.pdf", int pages = 2) => Samples.Pdf(dir.Combine(name), pages);

    // ---- missing engine -------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("pdf.protect")]
    [InlineData("pdf.unlock")]
    [InlineData("pdf.repair")]
    [InlineData("pdf.compress")]
    public async Task A_missing_qpdf_fails_with_a_message_that_names_it(string operation)
    {
        using var dir = new TempDir();
        var pdf = Sample(dir);

        var result = await Kit.RunAsync(operation, [pdf], Kit.Opts(("userPassword", "x"), ("level", "less")));

        Assert.False(result.Success);
        Assert.Contains("qpdf", result.Error);
        Assert.Contains("not installed", result.Error);
        Assert.Equal(["doc.pdf"], Kit.FilesIn(dir.Path));
    }

    [Fact]
    public void The_qpdf_converter_declares_the_engine_it_needs()
    {
        foreach (var operation in new[] { "pdf.protect", "pdf.unlock", "pdf.repair", "pdf.compress" })
            Assert.Equal(EngineId.Qpdf, Kit.Converter(operation, "pdf").EngineFor(operation));
    }

    // ---- protect --------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Protect_runs_qpdf_with_the_right_arguments_and_publishes_the_result_beside_the_input()
    {
        using var dir = new TempDir();
        var pdf = Sample(dir);
        var runner = new FakeRunner(FakeRunner.CopyInputToLastArgument());
        var modules = FakeRunner.Module(runner, EngineId.Qpdf);

        var result = await Kit.Ok(Kit.RunAsync("pdf.protect", [pdf],
            Kit.Opts(("userPassword", "open-me"), ("ownerPassword", "boss"), ("permissions", "print")), converters: modules));

        Assert.Equal(dir.Combine("doc (protected).pdf"), result.Outputs[0]);
        var call = Assert.Single(runner.Calls);
        Assert.Equal(@"C:\fake\Qpdf.exe", call.Executable);
        Assert.Equal(["--encrypt", "open-me", "boss", "256", "--print=full", "--extract=n", "--modify=none", "--accessibility=y", "--"], call.Arguments.Take(9));
        Assert.Equal(pdf, call.Arguments[9]);
        Assert.NotEqual(result.Outputs[0], call.Arguments[10]); // qpdf writes to a scratch file first
        Assert.Equal(["doc (protected).pdf", "doc.pdf"], Kit.FilesIn(dir.Path));
    }

    [Fact]
    public async Task Protect_generates_an_owner_password_when_only_a_user_password_is_given_and_says_so()
    {
        using var dir = new TempDir();
        var pdf = Sample(dir);
        var runner = new FakeRunner(FakeRunner.CopyInputToLastArgument());
        var progress = new RecordingProgress();

        await Kit.Ok(Kit.RunAsync("pdf.protect", [pdf], Kit.Opts(("userPassword", "open-me"), ("permissions", "print")),
            progress: progress, converters: FakeRunner.Module(runner, EngineId.Qpdf)));

        var owner = runner.Calls[0].Arguments[2];
        Assert.Equal(32, owner.Length);
        Assert.Contains("owner password", progress.LastMessage);
    }

    [Fact]
    public async Task Protect_needs_at_least_one_password()
    {
        using var dir = new TempDir();
        var pdf = Sample(dir);
        var runner = new FakeRunner(FakeRunner.CopyInputToLastArgument());

        var result = await Kit.RunAsync("pdf.protect", [pdf], Kit.Opts(("userPassword", ""), ("ownerPassword", "")),
            converters: FakeRunner.Module(runner, EngineId.Qpdf));

        Assert.False(result.Success);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task Protect_rejects_unknown_permissions_before_running_anything()
    {
        using var dir = new TempDir();
        var pdf = Sample(dir);
        var runner = new FakeRunner(FakeRunner.CopyInputToLastArgument());

        var result = await Kit.RunAsync("pdf.protect", [pdf], Kit.Opts(("userPassword", "x"), ("permissions", "teleport")),
            converters: FakeRunner.Module(runner, EngineId.Qpdf));

        Assert.False(result.Success);
        Assert.Contains("teleport", result.Error);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task Protect_on_an_already_protected_pdf_says_to_unlock_first()
    {
        using var dir = new TempDir();
        var pdf = Sample(dir);
        var runner = new FakeRunner(FakeRunner.Fail(2, "qpdf: doc.pdf: invalid password"));

        var result = await Kit.RunAsync("pdf.protect", [pdf], Kit.Opts(("userPassword", "x")), converters: FakeRunner.Module(runner, EngineId.Qpdf));

        Assert.False(result.Success);
        Assert.Contains("Unlock", result.Error);
        Assert.Equal(["doc.pdf"], Kit.FilesIn(dir.Path));
    }

    // ---- unlock ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Unlock_uses_exactly_the_password_the_user_supplied()
    {
        using var dir = new TempDir();
        var pdf = Sample(dir);
        var runner = new FakeRunner(FakeRunner.CopyInputToLastArgument());

        var result = await Kit.Ok(Kit.RunAsync("pdf.unlock", [pdf], Kit.Opts(("password", "my-password")), converters: FakeRunner.Module(runner, EngineId.Qpdf)));

        Assert.Equal(dir.Combine("doc (unlocked).pdf"), result.Outputs[0]);
        var call = Assert.Single(runner.Calls); // one attempt: there is no password guessing
        Assert.Equal("--password=my-password", call.Arguments[0]);
        Assert.Equal("--decrypt", call.Arguments[1]);
    }

    [Fact]
    public async Task Unlock_with_a_wrong_password_fails_once_without_leaking_the_password()
    {
        using var dir = new TempDir();
        var pdf = Sample(dir);
        var runner = new FakeRunner(FakeRunner.Fail(2, "qpdf: doc.pdf: invalid password"));

        var result = await Kit.RunAsync("pdf.unlock", [pdf], Kit.Opts(("password", "wrong-guess")), converters: FakeRunner.Module(runner, EngineId.Qpdf));

        Assert.False(result.Success);
        Assert.Contains("password", result.Error);
        Assert.DoesNotContain("wrong-guess", result.Error);
        Assert.Single(runner.Calls);
        Assert.Equal(["doc.pdf"], Kit.FilesIn(dir.Path));
    }

    [Fact]
    public void The_source_never_tries_other_passwords()
    {
        // Guard rail for the project rule "Unlock PDF never cracks passwords": the unlock code path has no loop over candidates.
        var source = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "DropFlow.Engines.Pdf", "Tools", "QpdfTools.cs"));
        var unlock = source[source.IndexOf("private async Task<ConversionResult> Unlock", StringComparison.Ordinal)..];
        unlock = unlock[..unlock.IndexOf("private async Task<ConversionResult> Repair", StringComparison.Ordinal)];
        Assert.DoesNotContain("foreach", unlock);
        Assert.DoesNotContain("for (", unlock);
        Assert.DoesNotContain("while", unlock);
    }

    // ---- repair ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Repair_accepts_qpdf_warnings_when_the_output_was_written()
    {
        using var dir = new TempDir();
        var pdf = Sample(dir);
        var runner = new FakeRunner(FakeRunner.CopyInputToLastArgument(exitCode: 3, stderr: "WARNING: file is damaged"));
        var progress = new RecordingProgress();

        var result = await Kit.Ok(Kit.RunAsync("pdf.repair", [pdf], progress: progress, converters: FakeRunner.Module(runner, EngineId.Qpdf)));

        Assert.Equal(dir.Combine("doc (repaired).pdf"), result.Outputs[0]);
        Assert.Contains("repaired", progress.LastMessage);
        Assert.Equal(2, runner.Calls[0].Arguments.Count);
    }

    [Fact]
    public async Task Repair_fails_cleanly_when_qpdf_cannot_recover_the_file()
    {
        using var dir = new TempDir();
        var pdf = dir.WriteText("dead.pdf", "not a pdf");
        var runner = new FakeRunner(FakeRunner.Fail(2, "qpdf: dead.pdf: can't find PDF header"));

        var result = await Kit.RunAsync("pdf.repair", [pdf], converters: FakeRunner.Module(runner, EngineId.Qpdf));

        Assert.False(result.Success);
        Assert.Contains("dead.pdf", result.Error);
        Assert.Equal(["dead.pdf"], Kit.FilesIn(dir.Path));
    }

    [Fact]
    public async Task A_cancelled_job_stops_the_engine_and_leaves_no_output()
    {
        using var dir = new TempDir();
        var pdf = Sample(dir);
        var runner = new FakeRunner(FakeRunner.CopyInputToLastArgument());
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Kit.RunAsync("pdf.repair", [pdf], ct: cts.Token, converters: FakeRunner.Module(runner, EngineId.Qpdf)));

        Assert.Equal(["doc.pdf"], Kit.FilesIn(dir.Path));
    }

    [Fact]
    public async Task Unicode_paths_are_passed_to_qpdf_unchanged()
    {
        using var dir = new TempDir();
        var folder = Directory.CreateDirectory(dir.Combine("ملفات")).FullName;
        var pdf = Samples.Pdf(Path.Combine(folder, "عقد.pdf"), 1);
        var runner = new FakeRunner(FakeRunner.CopyInputToLastArgument());

        var result = await Kit.Ok(Kit.RunAsync("pdf.repair", [pdf], converters: FakeRunner.Module(runner, EngineId.Qpdf)));

        Assert.Equal(Path.Combine(folder, "عقد (repaired).pdf"), result.Outputs[0]);
        Assert.Equal(pdf, runner.Calls[0].Arguments[0]);
    }

    // ---- compress -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Compress_less_is_a_lossless_qpdf_pass()
    {
        using var dir = new TempDir();
        var pdf = Sample(dir);
        var runner = new FakeRunner(FakeRunner.WriteFileOfSize(new FileInfo(pdf).Length / 2));

        var result = await Kit.Ok(Kit.RunAsync("pdf.compress", [pdf], Kit.Opts(("level", "less")), converters: FakeRunner.Module(runner, EngineId.Qpdf)));

        Assert.Equal(dir.Combine("doc (compressed).pdf"), result.Outputs[0]);
        var call = Assert.Single(runner.Calls);
        Assert.Contains("--recompress-flate", call.Arguments);
        Assert.Equal(pdf, call.Arguments[^2]);
    }

    [Fact]
    public async Task Compress_never_returns_a_bigger_file_it_keeps_the_original_and_says_so()
    {
        using var dir = new TempDir();
        var pdf = Sample(dir);
        var before = File.ReadAllBytes(pdf);
        var runner = new FakeRunner(FakeRunner.WriteFileOfSize(before.Length * 2));

        var result = await Kit.RunAsync("pdf.compress", [pdf], Kit.Opts(("level", "less")), converters: FakeRunner.Module(runner, EngineId.Qpdf));

        Assert.False(result.Success);
        Assert.Contains("smaller", result.Error);
        Assert.Contains("original", result.Error);
        Assert.Equal(["doc.pdf"], Kit.FilesIn(dir.Path));
        Assert.Equal(before, File.ReadAllBytes(pdf));
    }

    [Fact]
    public async Task Compress_with_equal_size_also_counts_as_not_smaller()
    {
        using var dir = new TempDir();
        var pdf = Sample(dir);
        var runner = new FakeRunner(FakeRunner.CopyInputToLastArgument());

        var result = await Kit.RunAsync("pdf.compress", [pdf], Kit.Opts(("level", "less")), converters: FakeRunner.Module(runner, EngineId.Qpdf));

        Assert.False(result.Success);
        Assert.Equal(["doc.pdf"], Kit.FilesIn(dir.Path));
    }

    [Fact]
    public async Task Compress_rejects_an_unknown_level()
    {
        using var dir = new TempDir();
        var pdf = Sample(dir);

        var result = await Kit.RunAsync("pdf.compress", [pdf], Kit.Opts(("level", "ludicrous")), converters: FakeRunner.Module(new FakeRunner(FakeRunner.CopyInputToLastArgument()), EngineId.Qpdf));

        Assert.False(result.Success);
        Assert.Contains("ludicrous", result.Error);
    }

    [Fact]
    public async Task Recommended_re_encodes_big_pictures_before_the_qpdf_pass()
    {
        using var dir = new TempDir();
        var pdf = HeavyPdf(dir);
        var original = new FileInfo(pdf).Length;
        var runner = new FakeRunner(FakeRunner.CopyInputToLastArgument());
        var progress = new RecordingProgress();

        var result = await Kit.Ok(Kit.RunAsync("pdf.compress", [pdf], Kit.Opts(("level", "recommended")), progress: progress,
            converters: FakeRunner.Module(runner, EngineId.Qpdf)));

        var call = Assert.Single(runner.Calls);
        Assert.NotEqual(pdf, call.Arguments[^2]); // qpdf was given the already re-encoded intermediate file
        Assert.True(new FileInfo(result.Outputs[0]).Length < original / 2, "the photo was re-encoded at lower quality");
        Assert.Equal(1, Kit.PageCount(result.Outputs[0]));
        Assert.Contains("smaller", progress.LastMessage);
    }

    [Fact]
    public async Task Recommended_hands_the_original_to_qpdf_when_there_are_no_pictures_to_shrink()
    {
        using var dir = new TempDir();
        var pdf = Sample(dir);
        var runner = new FakeRunner(FakeRunner.WriteFileOfSize(new FileInfo(pdf).Length / 2));

        await Kit.Ok(Kit.RunAsync("pdf.compress", [pdf], Kit.Opts(("level", "recommended")), converters: FakeRunner.Module(runner, EngineId.Qpdf)));

        Assert.Equal(pdf, runner.Calls[0].Arguments[^2]);
    }

    [Fact]
    public async Task Extreme_needs_no_engine_turns_pages_into_pictures_and_warns_that_text_is_lost()
    {
        using var dir = new TempDir();
        var pdf = HeavyPdf(dir);
        var original = new FileInfo(pdf).Length;
        var progress = new RecordingProgress();

        var result = await Kit.Ok(Kit.RunAsync("pdf.compress", [pdf], Kit.Opts(("level", "extreme")), progress: progress)); // NoEngines: no qpdf

        var output = result.Outputs[0];
        Assert.Equal(dir.Combine("heavy (compressed).pdf"), output);
        Assert.True(new FileInfo(output).Length < original / 4);
        Assert.Equal(1, Kit.PageCount(output));
        Assert.Contains("no longer selectable", progress.LastMessage);

        using var doc = PdfDocument.Open(output);
        var page = doc.GetPage(1);
        Assert.Equal((612, 792), ((int)page.Width, (int)page.Height));
        Assert.Empty(page.Letters); // the text really is gone
        Assert.True(PixelKit.RenderPage(output, 1, 36).InkBounds(200) is not null, "but the page still shows the picture and the text as a picture");
    }

    [Fact]
    public async Task Extreme_keeps_every_page_and_its_size()
    {
        using var dir = new TempDir();
        var b = new PdfBuilder();
        b.AddPage(612, 792);
        b.AddPage(300, 400).Text("small page", 20, 50, 20);
        var pdf = b.Save(dir.Combine("mixed.pdf"));

        var result = await Kit.RunAsync("pdf.compress", [pdf], Kit.Opts(("level", "extreme")));

        // Tiny files may not shrink; either outcome must be consistent.
        if (result.Success)
        {
            using var doc = PdfDocument.Open(result.Outputs[0]);
            Assert.Equal([(612, 792), (300, 400)], doc.GetPages().Select(p => ((int)p.Width, (int)p.Height)));
        }
        else
        {
            Assert.Contains("smaller", result.Error);
            Assert.Equal(["mixed.pdf"], Kit.FilesIn(dir.Path));
        }
    }

    [Fact]
    public async Task Extreme_on_a_tiny_file_keeps_the_original_when_the_result_would_be_bigger()
    {
        using var dir = new TempDir();
        var pdf = Sample(dir, pages: 1); // about 600 bytes: a page picture can only be bigger

        var result = await Kit.RunAsync("pdf.compress", [pdf], Kit.Opts(("level", "extreme")));

        Assert.False(result.Success);
        Assert.Contains("original", result.Error);
        Assert.Equal(["doc.pdf"], Kit.FilesIn(dir.Path));
    }

    [Fact]
    public async Task Extreme_honours_cancellation_and_removes_scratch_files()
    {
        using var dir = new TempDir();
        var pdf = HeavyPdf(dir);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Kit.RunAsync("pdf.compress", [pdf], Kit.Opts(("level", "extreme")), ct: cts.Token));

        Assert.Equal(["heavy.pdf"], Kit.FilesIn(dir.Path));
    }

    // ---- real qpdf (skipped when qpdf is not installed) -----------------------------------------------------------------

    [EngineFact(EngineId.Qpdf)]
    public async Task Real_qpdf_protect_then_unlock_round_trip()
    {
        using var dir = new TempDir();
        var pdf = Samples.Pdf(dir.Combine("secret.pdf"), 2, n => $"Secret {n}");
        var modules = PdfModule.Create(DropFlow.TestSupport.Engines.Locator);

        var protectedFile = (await Kit.Ok(Kit.RunAsync("pdf.protect", [pdf],
            Kit.Opts(("userPassword", "open sesame"), ("ownerPassword", "boss"), ("permissions", "print")), converters: modules))).Outputs[0];

        Assert.Throws<PdfDocumentEncryptedException>(() => PdfDocument.Open(protectedFile));
        using (var opened = PdfDocument.Open(protectedFile, new ParsingOptions { Passwords = ["open sesame"] }))
            Assert.Equal(2, opened.NumberOfPages);

        var wrong = await Kit.RunAsync("pdf.unlock", [protectedFile], Kit.Opts(("password", "nope")), converters: modules);
        Assert.False(wrong.Success);

        var unlocked = (await Kit.Ok(Kit.RunAsync("pdf.unlock", [protectedFile], Kit.Opts(("password", "open sesame")), converters: modules))).Outputs[0];
        Assert.Equal(["Secret 1", "Secret 2"], Kit.PageTexts(unlocked));
    }

    [EngineFact(EngineId.Qpdf)]
    public async Task Real_qpdf_repairs_a_pdf_with_a_broken_cross_reference_table()
    {
        using var dir = new TempDir();
        var pdf = Samples.Pdf(dir.Combine("good.pdf"), 2, n => $"Page {n}");
        var broken = dir.Combine("broken.pdf");
        File.WriteAllText(broken, File.ReadAllText(pdf).Replace("startxref\n", "startxref\n9"), System.Text.Encoding.ASCII);
        var modules = PdfModule.Create(DropFlow.TestSupport.Engines.Locator);

        var result = await Kit.Ok(Kit.RunAsync("pdf.repair", [broken], converters: modules));

        Assert.Equal(["Page 1", "Page 2"], Kit.PageTexts(result.Outputs[0]));
    }

    [EngineFact(EngineId.Qpdf)]
    public async Task Real_qpdf_lossless_compression_keeps_the_text()
    {
        using var dir = new TempDir();
        var pdf = Samples.Pdf(dir.Combine("many.pdf"), 40, n => $"Line {n}");
        var modules = PdfModule.Create(DropFlow.TestSupport.Engines.Locator);

        var result = await Kit.RunAsync("pdf.compress", [pdf], Kit.Opts(("level", "less")), converters: modules);

        if (result.Success)
        {
            Assert.True(new FileInfo(result.Outputs[0]).Length < new FileInfo(pdf).Length);
            Assert.Equal(40, Kit.PageCount(result.Outputs[0]));
        }
        else
        {
            Assert.Contains("original", result.Error);
        }
    }

    // ---- helpers --------------------------------------------------------------------------------------------------------

    /// <summary>One letter-size page with text and a large high-entropy JPEG (several MB).</summary>
    internal static string HeavyPdf(TempDir dir)
    {
        var photo = dir.Combine("photo.jpg");
        using (var image = new MagickImage(MagickColors.SteelBlue, 2400, 1800))
        {
            image.AddNoise(NoiseType.Uniform);
            image.Quality = 98;
            image.Write(photo);
        }

        var b = new PdfBuilder();
        b.AddPage().Text("A heavy page", 72, 60, 20).Image(photo, 36, 100, 540, 405);
        var pdf = b.Save(dir.Combine("heavy.pdf"));
        File.Delete(photo);
        return pdf;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DropFlow.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("DropFlow.sln not found above the test binaries.");
    }
}
