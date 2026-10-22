using DropFlow.Engines.Common;
using DropFlow.Engines.Pdf.Infrastructure;
using DropFlow.TestSupport;

namespace DropFlow.Engines.Pdf.Tests;

/// <summary>The PDFsharp font source used on macOS and Linux, exercised on every OS with a real installed font copied to a test folder.</summary>
public class SystemFontResolverTests
{
    private static string RealFont() =>
        FontFiles.DefaultSans() ?? throw new InvalidOperationException("No TrueType font is installed (Linux: apt install fonts-dejavu-core).");

    [Fact]
    public void Reads_the_family_name_from_the_font_file()
    {
        var face = SystemFontResolver.Read(RealFont());

        Assert.NotNull(face);
        Assert.False(string.IsNullOrWhiteSpace(face.Family));
        Assert.False(face.Bold);
        Assert.False(face.Italic);
    }

    [Fact]
    public void A_windows_family_that_is_not_installed_falls_back_to_an_installed_font()
    {
        using var dir = new TempDir();
        var copy = dir.Combine("only-font.ttf");
        File.Copy(RealFont(), copy);
        var resolver = new SystemFontResolver([dir.Path]);

        var info = resolver.ResolveTypeface("Segoe UI", false, false);

        Assert.Equal(1, resolver.FamilyCount);
        Assert.NotNull(info);
        Assert.Equal(copy, info.FaceName);
        Assert.Equal(File.ReadAllBytes(copy), resolver.GetFont(info.FaceName));
    }

    [Fact]
    public void Bold_and_italic_are_simulated_when_only_the_regular_face_exists()
    {
        using var dir = new TempDir();
        File.Copy(RealFont(), dir.Combine("regular.ttf"));
        var family = SystemFontResolver.Read(dir.Combine("regular.ttf"))!.Family;

        var info = new SystemFontResolver([dir.Path]).ResolveTypeface(family, true, true)!;

        Assert.True(info.MustSimulateBold);
        Assert.True(info.MustSimulateItalic);
    }

    [Fact]
    public void Files_that_are_not_truetype_fonts_are_ignored()
    {
        using var dir = new TempDir();
        dir.WriteText("broken.ttf", "not a font at all");
        File.WriteAllBytes(dir.Combine("empty.ttf"), []);

        var resolver = new SystemFontResolver([dir.Path]);

        Assert.Equal(0, resolver.FamilyCount);
        Assert.Null(resolver.ResolveTypeface("Arial", false, false));
        Assert.Null(SystemFontResolver.Read(dir.Combine("broken.ttf")));
    }

    [Fact]
    public void Missing_font_folders_are_skipped()
    {
        var resolver = new SystemFontResolver([Path.Combine(Path.GetTempPath(), "no-such-fonts-" + Guid.NewGuid().ToString("N"))]);
        Assert.Equal(0, resolver.FamilyCount);
    }

    [Fact]
    public void The_default_folders_cover_macOS_and_Linux()
    {
        var folders = SystemFontResolver.DefaultFolders().ToList();
        Assert.Contains("/System/Library/Fonts/Supplemental", folders);   // Arial on macOS
        Assert.Contains("/usr/share/fonts", folders);                     // DejaVu, Noto, Liberation on Linux
    }
}
