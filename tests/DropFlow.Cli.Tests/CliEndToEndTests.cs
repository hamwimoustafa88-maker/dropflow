using System.Text.Json;
using DropFlow.Core.Automation;
using DropFlow.Core.Catalog;
using DropFlow.Core.Cli;
using DropFlow.Core.Operations;
using DropFlow.Core.Registry;
using DropFlow.Engines.Common;
using DropFlow.Engines.Images;
using DropFlow.TestSupport;
using ImageMagick;

namespace DropFlow.Cli.Tests;

public class CliEndToEndTests
{
    private sealed class NoEngines : IEngineProvider { public string? Locate(EngineId e) => null; }
    private sealed class Delete : IFileDisposer { public void Dispose(string p) { if (File.Exists(p)) File.Delete(p); } }

    private static async Task<(int Code, string Out, string Err)> Run(params string[] args)
    {
        var registry = new ToolRegistry().Register(ImagesModule.Create(new NoEngines()));
        var recipes = BuiltInRecipes.Available(registry).ToList();
        var o = new StringWriter();
        var e = new StringWriter();
        var code = await new CliRunner(registry, ToolCatalog.LoadEmbedded(), new NoEngines(), recipes, new Delete(), o, e, "9.9.9").RunAsync(args);
        return (code, o.ToString(), e.ToString());
    }

    /// <summary>Needs qpdf unless the chosen level is "extreme" (like PDF compression).</summary>
    private sealed class OptionDependentEngine : IConverter
    {
        public IReadOnlyCollection<string> Operations => ["pdf.compress"];
        public bool Supports(string operation, string inputExtension) => operation == "pdf.compress" && inputExtension == "pdf";
        public EngineId EngineFor(string operation) => EngineId.Qpdf;
        public EngineId EngineFor(string operation, IReadOnlyDictionary<string, object?> options) =>
            options.TryGetValue("level", out var level) && Equals(level, "extreme") ? EngineId.None : EngineId.Qpdf;
        public IReadOnlyList<ToolOption> OptionsFor(string operation) => [];
        public Task<ConversionResult> RunAsync(ConversionJob job, IProgress<JobProgress> progress, CancellationToken cancellationToken) =>
            Task.FromResult(ConversionResult.Ok([job.Inputs[0]]));
    }

    [Theory]
    [InlineData("extreme", 0, "")]
    [InlineData("less", 1, "Qpdf is required")]
    public async Task The_engine_check_uses_the_options_given_on_the_command_line(string level, int expectedCode, string expectedError)
    {
        using var dir = new TempDir();
        var pdf = dir.WriteText("a.pdf", "%PDF-1.4");
        var registry = new ToolRegistry().Register(new OptionDependentEngine());
        var o = new StringWriter();
        var e = new StringWriter();

        var code = await new CliRunner(registry, ToolCatalog.LoadEmbedded(), new NoEngines(), [], new Delete(), o, e, "9.9.9")
            .RunAsync(["run", "pdf.compress", pdf, "--opt", $"level={level}"]);

        Assert.Equal(expectedCode, code);
        Assert.Contains(expectedError, e.ToString());
    }

    [Fact]
    public async Task Convert_writes_the_result_prints_its_path_and_exits_zero()
    {
        using var dir = new TempDir();
        var png = Samples.Png(dir.Combine("photo.png"));

        var (code, stdout, _) = await Run("convert", png, "--to", "webp");

        Assert.Equal(0, code);
        var expected = Path.Combine(dir.Path, "photo (converted).webp");
        Assert.Equal(expected, stdout.Trim());
        Assert.True(File.Exists(expected));
        Assert.True(File.Exists(png));
    }

    [Fact]
    public async Task Json_mode_prints_one_machine_readable_object()
    {
        using var dir = new TempDir();
        var png = Samples.Png(dir.Combine("a.png"));
        var (code, stdout, _) = await Run("convert", png, "--to", "jpg", "--opt", "quality=70", "--json");

        Assert.Equal(0, code);
        using var doc = JsonDocument.Parse(stdout);
        Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
        Assert.Single(doc.RootElement.GetProperty("outputs").EnumerateArray());
    }

    [Fact]
    public async Task The_output_folder_option_is_honoured()
    {
        using var dir = new TempDir();
        var png = Samples.Png(dir.Combine("a.png"));
        var outDir = Path.Combine(dir.Path, "out");
        Directory.CreateDirectory(outDir);

        var (_, stdout, _) = await Run("convert", png, "--to", "bmp", "--out", outDir);
        Assert.StartsWith(outDir, stdout.Trim());
    }

    [Fact]
    public async Task Tools_run_with_options_from_the_command_line()
    {
        using var dir = new TempDir();
        var png = Samples.Png(dir.Combine("wide.png"), 200, 100);
        var (code, stdout, _) = await Run("run", "img.resize", png, "--opt", "percent=50");

        Assert.Equal(0, code);
        using var img = new MagickImage(stdout.Trim());
        Assert.Equal(100u, img.Width);
        Assert.Equal(50u, img.Height);
    }

    [Fact]
    public async Task A_recipe_chains_steps_from_the_command_line()
    {
        using var dir = new TempDir();
        var png = Samples.Png(dir.Combine("p.png"), 400, 300);
        var (code, stdout, _) = await Run("recipe", "run", "web-photo", png);

        Assert.Equal(0, code);
        var final = stdout.Trim();
        Assert.EndsWith(".webp", final);
        Assert.True(File.Exists(final));
    }

    [Fact]
    public async Task Missing_files_and_unsupported_operations_exit_with_code_two_and_explain()
    {
        var (code1, _, err1) = await Run("convert", @"C:\definitely\missing.png", "--to", "jpg");
        Assert.Equal(2, code1);
        Assert.Contains("not found", err1);

        using var dir = new TempDir();
        var txt = dir.WriteText("a.txt", "x");
        var (code2, _, err2) = await Run("run", "img.crop", txt);
        Assert.Equal(2, code2);
        Assert.Contains("cannot run", err2);

        var (code3, _, _) = await Run("convert", "a.png");
        Assert.Equal(2, code3);
    }

    [Fact]
    public async Task A_failing_job_exits_one()
    {
        using var dir = new TempDir();
        var bad = dir.WriteText("broken.png", "not a png");
        var (code, _, err) = await Run("convert", bad, "--to", "jpg");
        Assert.Equal(1, code);
        Assert.Contains("Failed", err);
    }

    [Fact]
    public async Task List_shows_only_what_is_really_implemented()
    {
        var (code, stdout, _) = await Run("list", "--category", "image");
        Assert.Equal(0, code);
        Assert.Contains("\"id\":\"convert:webp\"", stdout);
        Assert.Contains("\"id\":\"img.compress\"", stdout);
        Assert.DoesNotContain("pdf.merge", stdout);          // not loaded in this test registry
    }

    [Fact]
    public async Task Engines_and_version_and_recipes_work()
    {
        Assert.Contains("Ffmpeg", (await Run("engines")).Out);
        Assert.Equal("dropflow 9.9.9", (await Run("version")).Out.Trim());
        Assert.Contains("web-photo", (await Run("recipe", "list")).Out);
    }

    [Fact]
    public async Task The_real_executable_converts_a_file_and_returns_exit_code_zero()
    {
        var exe = Path.Combine(AppContext.BaseDirectory, "dropflow.exe");
        Assert.True(File.Exists(exe), "dropflow.exe was not copied next to the tests: " + exe);
        using var dir = new TempDir();
        var png = Samples.Png(dir.Combine("نموذج.png"));          // Arabic file name through a real process boundary

        var r = await ProcessRunner.RunAsync(exe, ["convert", png, "--to", "jpg"]);

        Assert.True(r.Success, r.StdErr);
        Assert.True(File.Exists(Path.Combine(dir.Path, "نموذج (converted).jpg")));
    }
}
