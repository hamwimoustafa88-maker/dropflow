using System.Text.Json;
using DropFlow.AI.Models;
using DropFlow.Core.Operations;
using DropFlow.TestSupport;

namespace DropFlow.Cli.Tests;

public class DoctorCommandTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private sealed class Engines(Dictionary<EngineId, string> found) : IEngineProvider
    {
        public string? Locate(EngineId engine) => found.GetValueOrDefault(engine);
    }

    private sealed class FakeSystem : IDoctorSystem
    {
        public Dictionary<string, string> Outputs { get; } = [];
        public List<string> Ran { get; } = [];
        public string? WebView { get; set; } = "141.0.3537.71";

        public Task<string?> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken ct)
        {
            var key = Path.GetFileName(executable) + " " + string.Join(" ", arguments);
            Ran.Add(key);
            return Task.FromResult(Outputs.TryGetValue(key, out var text) ? text : null);
        }

        public string? FileVersion(string path) => path.EndsWith("soffice.exe", StringComparison.OrdinalIgnoreCase) ? "24.8.2.1" : null;
        public string? WebView2Version() => WebView;
    }

    private const string Encoders = """
        Encoders:
         V..... = Video
         ------
         V....D libx264             libx264 H.264 / AVC / MPEG-4 AVC / MPEG-4 part 10 (codec h264)
         V....D h264_nvenc           NVIDIA NVENC H.264 encoder (codec h264)
         V....D libsvtav1            SVT-AV1(Scalable Video Technology for AV1) encoder (codec av1)
         A....D aac                  AAC (Advanced Audio Coding)
        """;

    private (DoctorReport Report, FakeSystem System) Gather(bool withFfmpeg = true)
    {
        var found = new Dictionary<EngineId, string>();
        if (withFfmpeg)
        {
            found[EngineId.Ffmpeg] = @"C:\DropFlow\engines\Ffmpeg\ffmpeg.exe";
            found[EngineId.Ffprobe] = @"C:\DropFlow\engines\Ffmpeg\ffprobe.exe";
        }
        found[EngineId.Qpdf] = @"C:\Program Files\qpdf 12.4.2\bin\qpdf.exe";
        found[EngineId.LibreOffice] = @"C:\Program Files\LibreOffice\program\soffice.exe";

        var system = new FakeSystem();
        system.Outputs["ffmpeg.exe -hide_banner -version"] = "ffmpeg version 9.0.2-essentials_build-www.gyan.dev Copyright (c) 2000-2026 the FFmpeg developers\nbuilt with gcc";
        system.Outputs["ffprobe.exe -hide_banner -version"] = "ffprobe version 9.0.2-essentials_build-www.gyan.dev Copyright (c) 2007-2026";
        system.Outputs["ffmpeg.exe -hide_banner -encoders"] = Encoders;
        system.Outputs["qpdf.exe --version"] = "qpdf version 12.4.2\nRun qpdf --copyright to see copyright and license information.";

        var models = new ModelManager(_dir.Combine("models"));
        var report = DoctorCommand.GatherAsync(new Engines(found), models, "9.9.9", _dir.Path, system, CancellationToken.None).GetAwaiter().GetResult();
        models.Dispose();
        return (report, system);
    }

    [Fact]
    public void Engines_are_listed_with_their_path_and_version()
    {
        var (report, _) = Gather();
        var ffmpeg = report.Engines.Single(e => e.Engine == "Ffmpeg");
        Assert.Equal(@"C:\DropFlow\engines\Ffmpeg\ffmpeg.exe", ffmpeg.Path);
        Assert.Equal("9.0.2-essentials_build-www.gyan.dev", ffmpeg.Version);
        Assert.Equal("12.4.2", report.Engines.Single(e => e.Engine == "Qpdf").Version);
        Assert.Equal("24.8.2.1", report.Engines.Single(e => e.Engine == "LibreOffice").Version);   // from the file, never by starting it
        var tesseract = report.Engines.Single(e => e.Engine == "Tesseract");
        Assert.Null(tesseract.Path);
        Assert.Null(tesseract.Version);
    }

    [Fact]
    public void LibreOffice_is_never_started_to_ask_its_version()
    {
        var (_, system) = Gather();
        Assert.DoesNotContain(system.Ran, r => r.StartsWith("soffice", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_encoders_that_matter_are_reported_present_or_missing()
    {
        var (report, _) = Gather();
        Assert.NotNull(report.Encoders);
        var map = report.Encoders!.ToDictionary(e => e.Name, e => e.Present);
        Assert.True(map["libx264"]);
        Assert.True(map["h264_nvenc"]);
        Assert.True(map["libsvtav1"]);
        Assert.False(map["libx265"]);
        Assert.False(map["h264_qsv"]);
        Assert.False(map["h264_amf"]);
        foreach (var name in new[] { "libx264", "libx265", "libsvtav1", "h264_nvenc", "hevc_nvenc", "h264_qsv", "hevc_qsv", "h264_amf", "hevc_amf" })
            Assert.Contains(name, map.Keys);
    }

    [Fact]
    public void Without_ffmpeg_there_is_no_encoder_list_and_nothing_is_run_for_it()
    {
        var (report, system) = Gather(withFfmpeg: false);
        Assert.Null(report.Encoders);
        Assert.DoesNotContain(system.Ran, r => r.StartsWith("ffmpeg", StringComparison.Ordinal));
    }

    [Fact]
    public void The_text_report_has_every_section()
    {
        var (report, _) = Gather();
        var text = DoctorCommand.Format(report);
        Assert.Contains("DropFlow 9.9.9", text);
        Assert.Contains("Windows", text);
        Assert.Contains(System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(), text);
        Assert.Contains("ffmpeg.exe", text);
        Assert.Contains("9.0.2-essentials_build-www.gyan.dev", text);
        Assert.Contains("Tesseract", text);
        Assert.Contains("not found", text);
        Assert.Contains("libx264", text);
        Assert.Contains("WebView2", text);
        Assert.Contains("141.0.3537.71", text);
        Assert.Contains("Models", text);
        Assert.Contains("ggml-", text);                    // the model catalog is listed, each installed or not
    }

    [Fact]
    public void A_missing_webview2_is_said_plainly()
    {
        var (report, _) = Gather();
        Assert.Contains("WebView2: not installed", DoctorCommand.Format(report with { WebView2 = null }));
    }

    [Fact]
    public void The_json_report_round_trips()
    {
        var (report, _) = Gather();
        using var doc = JsonDocument.Parse(DoctorCommand.Json(report));
        Assert.Equal("9.9.9", doc.RootElement.GetProperty("version").GetString());
        Assert.True(doc.RootElement.GetProperty("engines").GetArrayLength() >= 7);
        Assert.True(doc.RootElement.GetProperty("encoders").GetArrayLength() >= 9);
    }

    [Theory]
    [InlineData(EngineId.Ffmpeg, "ffmpeg version 7.1-full_build-www.gyan.dev Copyright (c) 2000-2024", "7.1-full_build-www.gyan.dev")]
    [InlineData(EngineId.Ffmpeg, "ffmpeg version n6.1.1 Copyright", "n6.1.1")]
    [InlineData(EngineId.Qpdf, "qpdf version 11.9.1", "11.9.1")]
    [InlineData(EngineId.Tesseract, "tesseract v5.4.0.20240606\n leptonica-1.84.1", "5.4.0.20240606")]
    [InlineData(EngineId.Tesseract, "tesseract 5.3.3", "5.3.3")]
    [InlineData(EngineId.Pandoc, "pandoc.exe 3.5\nFeatures: +server +lua", "3.5")]
    [InlineData(EngineId.SevenZip, "\n7-Zip 24.08 (x64) : Copyright (c) 1999-2024 Igor Pavlov", "24.08")]
    [InlineData(EngineId.Ffmpeg, "garbage", null)]
    [InlineData(EngineId.Ffmpeg, "", null)]
    public void Version_lines_are_parsed(EngineId engine, string output, string? expected) =>
        Assert.Equal(expected, DoctorCommand.ParseVersion(engine, output));

    [Fact]
    public async Task The_real_doctor_runs_on_this_pc_and_prints_a_report()
    {
        var output = new StringWriter();
        var code = await DoctorCommand.RunAsync([], new DropFlow.Engines.Common.EngineLocator(_dir.Path), new ModelManager(_dir.Combine("m")), "9.9.9", _dir.Path, output, CancellationToken.None);
        Assert.Equal(0, code);
        Assert.Contains("DropFlow 9.9.9", output.ToString());
        Assert.Contains("WebView2", output.ToString());
    }
}
