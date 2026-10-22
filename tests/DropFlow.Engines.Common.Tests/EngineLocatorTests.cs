using DropFlow.Core.Operations;
using DropFlow.Engines.Common;

namespace DropFlow.Engines.Common.Tests;

/// <summary>
/// The locator with a fake file system. Paths are built with <see cref="Path.Combine(string[])"/> under a fake root, so every test runs
/// on Windows, macOS and Linux; <c>windows</c> picks which naming scheme (ffmpeg.exe or ffmpeg) is exercised, independent of the host.
/// </summary>
public class EngineLocatorTests
{
    private static readonly string Root = OperatingSystem.IsWindows() ? @"C:\" : "/";

    private static string P(params string[] parts) => Path.Combine([Root, .. parts]);

    private static EngineLocator With(IEnumerable<string> files, string? data = null, string[]? path = null, string[]? known = null,
        Func<string, IEnumerable<string>>? subDirs = null, string? bundled = null, bool windows = true)
    {
        var set = files.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new EngineLocator(data, path ?? [], known ?? [], set.Contains, subDirs ?? (_ => []), bundled ?? P("nowhere", "engines"), windows);
    }

    [Fact]
    public void Finds_an_engine_on_PATH()
    {
        var l = With([P("tools", "ffmpeg.exe")], path: [P("tools")]);
        Assert.Equal(P("tools", "ffmpeg.exe"), l.Locate(EngineId.Ffmpeg));
    }

    [Fact]
    public void An_engine_shipped_inside_the_app_wins_over_PATH_and_the_data_folder()
    {
        var l = With([P("App", "engines", "Qpdf", "bin", "qpdf.exe"), P("tools", "qpdf.exe"), P("data", "engines", "Qpdf", "qpdf.exe")],
            data: P("data"), path: [P("tools")], bundled: P("App", "engines"));
        Assert.Equal(P("App", "engines", "Qpdf", "bin", "qpdf.exe"), l.Locate(EngineId.Qpdf));
    }

    [Fact]
    public void Ffprobe_is_found_in_the_shipped_Ffmpeg_folder()
    {
        var l = With([P("App", "engines", "Ffmpeg", "ffprobe.exe")], bundled: P("App", "engines"));
        Assert.Equal(P("App", "engines", "Ffmpeg", "ffprobe.exe"), l.Locate(EngineId.Ffprobe));
    }

    [Fact]
    public void Reports_null_when_the_engine_is_missing()
    {
        Assert.Null(With([], path: [P("tools")]).Locate(EngineId.Qpdf));
        Assert.Null(With([]).Locate(EngineId.None));
    }

    [Fact]
    public void DropFlows_own_folder_wins_over_PATH()
    {
        var l = With([P("data", "engines", "Ffmpeg", "ffmpeg.exe"), P("tools", "ffmpeg.exe")], data: P("data"), path: [P("tools")]);
        Assert.Equal(P("data", "engines", "Ffmpeg", "ffmpeg.exe"), l.Locate(EngineId.Ffmpeg));
    }

    [Fact]
    public void Finds_an_engine_unpacked_into_a_versioned_subfolder_with_bin()
    {
        var own = P("data", "engines", "Ffmpeg");
        var l = With([P("data", "engines", "Ffmpeg", "ffmpeg-7.1-essentials", "bin", "ffmpeg.exe")], data: P("data"),
            subDirs: d => d == own ? [P("data", "engines", "Ffmpeg", "ffmpeg-7.1-essentials")] : []);
        Assert.Equal(P("data", "engines", "Ffmpeg", "ffmpeg-7.1-essentials", "bin", "ffmpeg.exe"), l.Locate(EngineId.Ffmpeg));
    }

    [Fact]
    public void An_install_folder_that_carries_the_version_is_found_newest_first()
    {
        // the qpdf installer creates "C:\Program Files\qpdf 12.4.2", not "qpdf"
        var pf = P("Program Files");
        var dirs = new[] { Path.Combine(pf, "7-Zip"), Path.Combine(pf, "qpdf 12.4.1"), Path.Combine(pf, "qpdf 12.4.2"), Path.Combine(pf, "qpdfx") };

        var found = EngineLocator.VersionedInstallDirs(pf, "qpdf", dirs).ToList();

        Assert.Equal([Path.Combine(pf, "qpdf 12.4.2"), Path.Combine(pf, "qpdf 12.4.1")], found);
    }

    [Fact]
    public void Falls_back_to_known_install_folders()
    {
        var l = With([P("Program Files", "LibreOffice", "program", "soffice.exe")], known: [P("Program Files", "LibreOffice", "program")]);
        Assert.Equal(P("Program Files", "LibreOffice", "program", "soffice.exe"), l.Locate(EngineId.LibreOffice));
    }

    [Fact]
    public void Seven_zip_accepts_either_executable_name()
    {
        Assert.Equal(P("z", "7za.exe"), With([P("z", "7za.exe")], path: [P("z")]).Locate(EngineId.SevenZip));
    }

    [Fact]
    public void Results_are_cached_until_refreshed()
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var l = new EngineLocator(null, [P("t")], [], files.Contains, _ => [], P("nowhere"), windows: true);

        Assert.Null(l.Locate(EngineId.Pandoc));
        files.Add(P("t", "pandoc.exe"));
        Assert.Null(l.Locate(EngineId.Pandoc));       // still cached
        l.Refresh();
        Assert.Equal(P("t", "pandoc.exe"), l.Locate(EngineId.Pandoc));
    }

    // ---- macOS and Linux ------------------------------------------------------------------------------------------

    [Fact]
    public void Unix_names_have_no_exe_suffix()
    {
        foreach (var e in Enum.GetValues<EngineId>().Where(e => e != EngineId.None))
        {
            Assert.NotEmpty(EngineInfo.ExecutableNames(e, windows: false));
            Assert.All(EngineInfo.ExecutableNames(e, windows: false), n => Assert.False(n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase), n));
            Assert.All(EngineInfo.ExecutableNames(e, windows: true), n => Assert.EndsWith(".exe", n));
        }
    }

    [Fact]
    public void The_default_names_follow_the_running_operating_system() =>
        Assert.Equal(EngineInfo.ExecutableNames(EngineId.Ffmpeg, OperatingSystem.IsWindows()), EngineInfo.ExecutableNames(EngineId.Ffmpeg));

    [Fact]
    public void Unix_finds_ffmpeg_and_ffprobe_without_exe_on_PATH()
    {
        var l = With([P("usr", "bin", "ffmpeg"), P("usr", "bin", "ffprobe"), P("usr", "bin", "ffmpeg.exe")], path: [P("usr", "bin")], windows: false);
        Assert.Equal(P("usr", "bin", "ffmpeg"), l.Locate(EngineId.Ffmpeg));
        Assert.Equal(P("usr", "bin", "ffprobe"), l.Locate(EngineId.Ffprobe));
    }

    [Fact]
    public void Unix_ignores_a_Windows_executable()
    {
        var l = With([P("tools", "qpdf.exe")], path: [P("tools")], windows: false);
        Assert.Null(l.Locate(EngineId.Qpdf));
    }

    [Fact]
    public void Unix_finds_LibreOffice_as_soffice_or_libreoffice()
    {
        var l = With([P("usr", "bin", "libreoffice")], known: [P("usr", "bin")], windows: false);
        Assert.Equal(P("usr", "bin", "libreoffice"), l.Locate(EngineId.LibreOffice));
    }

    [Fact]
    public void Unix_prefers_the_official_7zz()
    {
        var l = With([P("opt", "homebrew", "bin", "7zz"), P("opt", "homebrew", "bin", "7z")], known: [P("opt", "homebrew", "bin")], windows: false);
        Assert.Equal(P("opt", "homebrew", "bin", "7zz"), l.Locate(EngineId.SevenZip));
    }

    [Fact]
    public void Unix_known_folders_cover_Homebrew_on_both_chips_and_the_system_bin()
    {
        var dirs = EngineLocator.DefaultKnownDirs(windows: false).ToList();
        Assert.Contains("/opt/homebrew/bin", dirs);
        Assert.Contains("/usr/local/bin", dirs);
        Assert.Contains("/usr/bin", dirs);
        Assert.Contains("/Applications/LibreOffice.app/Contents/MacOS", dirs);
        // Apple Silicon Homebrew is searched before Intel Homebrew, and both before the system folder.
        Assert.True(dirs.IndexOf("/opt/homebrew/bin") < dirs.IndexOf("/usr/local/bin"));
        Assert.True(dirs.IndexOf("/usr/local/bin") < dirs.IndexOf("/usr/bin"));
        Assert.DoesNotContain(dirs, d => d.Contains("WinGet", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Unix_PATH_wins_over_Homebrew_and_usr_bin()
    {
        var l = With([P("home", "me", "bin", "tesseract"), P("opt", "homebrew", "bin", "tesseract"), P("usr", "bin", "tesseract")],
            path: [P("home", "me", "bin")], known: [P("opt", "homebrew", "bin"), P("usr", "bin")], windows: false);
        Assert.Equal(P("home", "me", "bin", "tesseract"), l.Locate(EngineId.Tesseract));
    }

    [Fact]
    public void The_bundled_folders_are_next_to_the_app_and_inside_a_macOS_bundle()
    {
        var macOs = P("Applications", "DropFlow.app", "Contents", "MacOS");

        var dirs = EngineLocator.BundledDirectories(macOs + Path.DirectorySeparatorChar);

        Assert.Equal(Path.Combine(macOs, "engines"), dirs[0]);
        Assert.Equal(Path.GetFullPath(P("Applications", "DropFlow.app", "Contents", "Resources", "engines")), dirs[1]);
    }

    [Fact]
    public void Finds_engines_in_a_real_macOS_style_app_bundle_on_disk()
    {
        using var dir = new TempFolder();
        var macOs = Directory.CreateDirectory(Path.Combine(dir.Path, "DropFlow.app", "Contents", "MacOS")).FullName;
        var engines = Directory.CreateDirectory(Path.Combine(dir.Path, "DropFlow.app", "Contents", "Resources", "engines", "Ffmpeg")).FullName;
        var ffmpeg = Path.Combine(engines, "ffmpeg");
        File.WriteAllText(ffmpeg, "");

        var l = new EngineLocator(pathDirs: [], knownDirs: [], windows: false, appDirectory: macOs);

        Assert.Equal(Path.GetFullPath(ffmpeg), Path.GetFullPath(l.Locate(EngineId.Ffmpeg)!));
        Assert.Null(l.Locate(EngineId.Qpdf));
    }

    [Fact]
    public void Finds_engines_in_the_engines_folder_next_to_a_real_app_on_disk()
    {
        using var dir = new TempFolder();
        var qpdf = Path.Combine(Directory.CreateDirectory(Path.Combine(dir.Path, "engines", "Qpdf", "bin")).FullName, "qpdf");
        File.WriteAllText(qpdf, "");

        var l = new EngineLocator(pathDirs: [], knownDirs: [], windows: false, appDirectory: dir.Path);

        Assert.Equal(qpdf, l.Locate(EngineId.Qpdf));
    }

    // ---- winget (Windows) ----------------------------------------------------------------------------------------

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

    private sealed class TempFolder : IDisposable
    {
        public string Path { get; } = Directory.CreateDirectory(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dropflow-loc-" + Guid.NewGuid().ToString("N"))).FullName;

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
        }
    }
}
