using System.IO;
using System.Windows.Media;
using DropFlow.Core.Operations;
using DropFlow.Engines.Fonts;

namespace DropFlow.App.Tests;

/// <summary>A test on a font installed in Windows (fonts are not redistributable); skipped, visibly, when the font is missing.</summary>
public sealed class WindowsFontFactAttribute : FactAttribute
{
    public WindowsFontFactAttribute(string name)
    {
        if (!File.Exists(FontOutputsInWpfTests.WindowsFont(name))) Skip = $"The font {name} is not installed.";
    }
}

/// <summary>
/// Fonts made by the font converter open in WPF's own font engine (GlyphTypeface), an independent reader of TrueType and CFF outlines.
/// WPF keeps one font per family and style in a process (a second file with the same names, even an identical copy, throws inside
/// GlyphTypeface), so each test converts a different installed font and loads only the converted file. The shapes themselves are
/// compared with the originals by FreeType in DropFlow.Engines.Fonts.Tests.
/// </summary>
public sealed class FontOutputsInWpfTests : IDisposable
{
    private readonly string _dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "dropflow-test-" + Guid.NewGuid().ToString("N"))).FullName;

    /// <summary>Best effort: WPF keeps a font file it opened mapped until the process ends, so those files may stay in the temp folder.</summary>
    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* locked by WPF's font cache */ }
    }

    internal static string WindowsFont(string name) => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts", name);

    [WindowsFontFact("Amiri-Regular.ttf")]
    public async Task A_truetype_font_converted_to_otf_loads_with_its_arabic_glyphs() =>
        AssertLoads(await Convert("Amiri-Regular.ttf", "otf"));

    [WindowsFontFact("AdobeArabic-Regular.otf")]
    public async Task A_cff_font_converted_to_ttf_loads_with_its_arabic_glyphs() =>
        AssertLoads(await Convert("AdobeArabic-Regular.otf", "ttf"));

    [WindowsFontFact("NotoNaskhArabic-Regular.ttf")]
    public async Task A_ttf_back_from_woff2_loads_with_its_arabic_glyphs()
    {
        var woff2 = await Convert("NotoNaskhArabic-Regular.ttf", "woff2");
        AssertLoads(await Run(woff2, "ttf"));
    }

    private static void AssertLoads(string path)
    {
        var face = new GlyphTypeface(new Uri(path));
        Assert.True(face.GlyphCount > 100);
        Assert.True(face.CharacterToGlyphMap.TryGetValue('ا', out var alef), "Arabic alef is mapped");
        Assert.InRange(face.AdvanceWidths[alef], 0.05, 2.0);
        var bounds = face.GetGlyphOutline(alef, 100, 100).Bounds;
        Assert.False(bounds.IsEmpty);
        Assert.InRange(bounds.Height, 20, 200); // a letter between a fifth and two ems tall at 100 units per em
    }

    private async Task<string> Convert(string font, string target)
    {
        var source = Path.Combine(_dir, font);
        File.Copy(WindowsFont(font), source);
        return await Run(source, target);
    }

    private static async Task<string> Run(string input, string target)
    {
        var converter = FontsModule.Create(TestSupport.Engines.Locator).Single();
        var result = await converter.RunAsync(new ConversionJob([input], Operation.Convert(target)), new Progress<JobProgress>(), CancellationToken.None);
        Assert.True(result.Success, result.Error);
        return result.Outputs[0];
    }
}
