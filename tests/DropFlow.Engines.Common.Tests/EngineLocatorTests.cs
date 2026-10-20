using DropFlow.Core.Operations;
using DropFlow.Engines.Common;

namespace DropFlow.Engines.Common.Tests;

public class EngineLocatorTests
{
    private static EngineLocator With(IEnumerable<string> files, string? data = null, string[]? path = null, string[]? known = null, Func<string, IEnumerable<string>>? subDirs = null)
    {
        var set = files.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new EngineLocator(data, path ?? [], known ?? [], set.Contains, subDirs ?? (_ => []));
    }

    [Fact]
    public void Finds_an_engine_on_PATH()
    {
        var l = With([@"C:\tools\ffmpeg.exe"], path: [@"C:\tools"]);
        Assert.Equal(@"C:\tools\ffmpeg.exe", l.Locate(EngineId.Ffmpeg));
    }

    [Fact]
    public void Reports_null_when_the_engine_is_missing()
    {
        Assert.Null(With([], path: [@"C:\tools"]).Locate(EngineId.Qpdf));
        Assert.Null(With([]).Locate(EngineId.None));
    }

    [Fact]
    public void DropFlows_own_folder_wins_over_PATH()
    {
        var l = With([@"D:\data\engines\Ffmpeg\ffmpeg.exe", @"C:\tools\ffmpeg.exe"], data: @"D:\data", path: [@"C:\tools"]);
        Assert.Equal(@"D:\data\engines\Ffmpeg\ffmpeg.exe", l.Locate(EngineId.Ffmpeg));
    }

    [Fact]
    public void Finds_an_engine_unpacked_into_a_versioned_subfolder_with_bin()
    {
        var own = @"D:\data\engines\Ffmpeg";
        var l = With([@"D:\data\engines\Ffmpeg\ffmpeg-7.1-essentials\bin\ffmpeg.exe"], data: @"D:\data",
            subDirs: d => d == own ? [@"D:\data\engines\Ffmpeg\ffmpeg-7.1-essentials"] : []);
        Assert.Equal(@"D:\data\engines\Ffmpeg\ffmpeg-7.1-essentials\bin\ffmpeg.exe", l.Locate(EngineId.Ffmpeg));
    }

    [Fact]
    public void Falls_back_to_known_install_folders()
    {
        var l = With([@"C:\Program Files\LibreOffice\program\soffice.exe"], known: [@"C:\Program Files\LibreOffice\program"]);
        Assert.Equal(@"C:\Program Files\LibreOffice\program\soffice.exe", l.Locate(EngineId.LibreOffice));
    }

    [Fact]
    public void Seven_zip_accepts_either_executable_name()
    {
        Assert.Equal(@"C:\z\7za.exe", With([@"C:\z\7za.exe"], path: [@"C:\z"]).Locate(EngineId.SevenZip));
    }

    [Fact]
    public void Results_are_cached_until_refreshed()
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var l = new EngineLocator(null, [@"C:\t"], [], files.Contains, _ => []);

        Assert.Null(l.Locate(EngineId.Pandoc));
        files.Add(@"C:\t\pandoc.exe");
        Assert.Null(l.Locate(EngineId.Pandoc));       // still cached
        l.Refresh();
        Assert.Equal(@"C:\t\pandoc.exe", l.Locate(EngineId.Pandoc));
    }

    [Theory]
    [InlineData(EngineId.Ffmpeg, "Gyan.FFmpeg")]
    [InlineData(EngineId.Qpdf, "QPDF.QPDF")]
    [InlineData(EngineId.LibreOffice, "TheDocumentFoundation.LibreOffice")]
    [InlineData(EngineId.Tesseract, "UB-Mannheim.TesseractOCR")]
    public void Winget_ids_match_the_verified_packages(EngineId engine, string id) =>
        Assert.Equal(id, EngineInfo.WingetId(engine));

    [Fact]
    public void Winget_install_command_is_silent_exact_and_accepts_agreements()
    {
        var args = WingetCommand.Install(EngineId.Ffmpeg)!;
        Assert.Equal("install", args[0]);
        Assert.Contains("--exact", args);
        Assert.Contains("--silent", args);
        Assert.Contains("--accept-package-agreements", args);
        Assert.Equal("Gyan.FFmpeg", args[args.ToList().IndexOf("--id") + 1]);
    }

    [Fact]
    public void Engines_without_a_winget_package_have_no_install_command() =>
        Assert.Null(WingetCommand.Install(EngineId.None));

    [Fact]
    public void Every_real_engine_has_an_executable_name_and_a_size_hint()
    {
        foreach (var e in Enum.GetValues<EngineId>().Where(e => e != EngineId.None))
        {
            Assert.NotEmpty(EngineInfo.ExecutableNames(e));
            Assert.True(EngineInfo.ApproxSizeMb(e) > 0, e.ToString());
            Assert.NotNull(EngineInfo.WingetId(e));
        }
    }
}
