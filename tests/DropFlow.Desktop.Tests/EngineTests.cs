using DropFlow.Core.Operations;
using DropFlow.Desktop.Platform;

namespace DropFlow.Desktop.Tests;

public class UnixEngineProviderTests
{
    private static UnixEngineProvider Provider(OsKind os, string appDir, IEnumerable<string> existing, string path = "/usr/bin:/bin", string? dataDir = null)
    {
        var set = existing.ToHashSet();
        return new UnixEngineProvider(os, appDir, dataDir, path.Split(':'), set.Contains);
    }

    [Fact]
    public void The_engine_inside_the_mac_app_bundle_wins()
    {
        var p = Provider(OsKind.MacOS, "/Applications/DropFlow.app/Contents/MacOS",
            ["/Applications/DropFlow.app/Contents/Resources/engines/ffmpeg", "/opt/homebrew/bin/ffmpeg"]);
        Assert.Equal("/Applications/DropFlow.app/Contents/Resources/engines/ffmpeg", p.Locate(EngineId.Ffmpeg));
    }

    [Fact]
    public void Homebrew_is_found_even_though_apps_started_from_finder_get_a_short_path()
    {
        var p = Provider(OsKind.MacOS, "/Applications/DropFlow.app/Contents/MacOS", ["/opt/homebrew/bin/qpdf", "/usr/local/bin/tesseract"]);
        Assert.Equal("/opt/homebrew/bin/qpdf", p.Locate(EngineId.Qpdf));
        Assert.Equal("/usr/local/bin/tesseract", p.Locate(EngineId.Tesseract));
    }

    [Fact]
    public void LibreOffice_on_a_mac_lives_inside_its_app()
    {
        var p = Provider(OsKind.MacOS, "/x", ["/Applications/LibreOffice.app/Contents/MacOS/soffice"]);
        Assert.Equal("/Applications/LibreOffice.app/Contents/MacOS/soffice", p.Locate(EngineId.LibreOffice));
    }

    [Fact]
    public void AppImage_and_packages_ship_ffmpeg_in_the_engines_folder()
    {
        var p = Provider(OsKind.Linux, "/tmp/.mount_DropFl/usr/lib/dropflow", ["/tmp/.mount_DropFl/usr/lib/dropflow/engines/ffprobe", "/usr/bin/ffprobe"]);
        Assert.Equal("/tmp/.mount_DropFl/usr/lib/dropflow/engines/ffprobe", p.Locate(EngineId.Ffprobe));
    }

    [Fact]
    public void Linux_path_then_known_folders()
    {
        var p = Provider(OsKind.Linux, "/usr/lib/dropflow", ["/home/u/bin/qpdf", "/usr/bin/qpdf", "/usr/bin/libreoffice", "/snap/bin/7zz"], "/home/u/bin:/usr/bin");
        Assert.Equal("/home/u/bin/qpdf", p.Locate(EngineId.Qpdf));
        Assert.Equal("/usr/bin/libreoffice", p.Locate(EngineId.LibreOffice));
        Assert.Equal("/snap/bin/7zz", p.Locate(EngineId.SevenZip));
    }

    [Fact]
    public void Missing_engines_are_null_and_none_is_never_located()
    {
        var p = Provider(OsKind.Linux, "/usr/lib/dropflow", []);
        Assert.Null(p.Locate(EngineId.Ffmpeg));
        Assert.Null(p.Locate(EngineId.None));
    }

    [Fact]
    public void Data_folder_engines_are_found_before_the_path()
    {
        var p = Provider(OsKind.Linux, "/usr/lib/dropflow", ["/home/u/.local/share/dropflow/engines/Pandoc/pandoc", "/usr/bin/pandoc"], dataDir: "/home/u/.local/share/dropflow");
        Assert.Equal("/home/u/.local/share/dropflow/engines/Pandoc/pandoc", p.Locate(EngineId.Pandoc));
    }
}

public class EngineHintsTests
{
    [Fact]
    public void Mac_uses_homebrew() =>
        Assert.Equal("brew install tesseract tesseract-lang", EngineHints.InstallCommand(EngineId.Tesseract, OsKind.MacOS, PackageManager.Homebrew));

    [Fact]
    public void LibreOffice_on_a_mac_is_a_cask() =>
        Assert.Equal("brew install --cask libreoffice", EngineHints.InstallCommand(EngineId.LibreOffice, OsKind.MacOS, PackageManager.Homebrew));

    [Theory]
    [InlineData(PackageManager.Apt, "sudo apt install tesseract-ocr tesseract-ocr-ara")]
    [InlineData(PackageManager.Dnf, "sudo dnf install tesseract tesseract-langpack-ara")]
    [InlineData(PackageManager.Pacman, "sudo pacman -S tesseract tesseract-data-ara")]
    [InlineData(PackageManager.Zypper, "sudo zypper install tesseract-ocr tesseract-ocr-traineddata-arabic")]
    public void Linux_package_managers(PackageManager pm, string expected) =>
        Assert.Equal(expected, EngineHints.InstallCommand(EngineId.Tesseract, OsKind.Linux, pm));

    [Fact]
    public void Ffprobe_comes_with_ffmpeg() =>
        Assert.Equal("sudo apt install ffmpeg", EngineHints.InstallCommand(EngineId.Ffprobe, OsKind.Linux, PackageManager.Apt));

    [Fact]
    public void Unknown_package_manager_gives_no_command() =>
        Assert.Null(EngineHints.InstallCommand(EngineId.Qpdf, OsKind.Linux, PackageManager.Unknown));

    [Theory]
    [InlineData("ID=ubuntu\nID_LIKE=debian\n", PackageManager.Apt)]
    [InlineData("ID=linuxmint\nID_LIKE=\"ubuntu debian\"\n", PackageManager.Apt)]
    [InlineData("ID=fedora\n", PackageManager.Dnf)]
    [InlineData("ID=\"rocky\"\nID_LIKE=\"rhel centos fedora\"\n", PackageManager.Dnf)]
    [InlineData("ID=arch\n", PackageManager.Pacman)]
    [InlineData("ID=manjaro\nID_LIKE=arch\n", PackageManager.Pacman)]
    [InlineData("ID=\"opensuse-tumbleweed\"\nID_LIKE=\"opensuse suse\"\n", PackageManager.Zypper)]
    [InlineData("ID=nixos\n", PackageManager.Unknown)]
    [InlineData("", PackageManager.Unknown)]
    public void Os_release(string content, PackageManager expected) => Assert.Equal(expected, EngineHints.FromOsRelease(content));
}
