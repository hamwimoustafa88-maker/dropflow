namespace DropFlow.Engines.Fonts.Tests;

/// <summary>A test on a real font of this PC (not redistributable, so not in the repository); skipped, visibly, when the font is absent.</summary>
public sealed class SystemFontFactAttribute : FactAttribute
{
    public SystemFontFactAttribute(params string[] names)
    {
        if (TestFonts.SystemFont(names) is null) Skip = $"None of these fonts is installed: {string.Join(", ", names)}.";
    }
}

/// <summary>A test on a third-party web font set (.ttf + .woff + .woff2 + .eot made by other tools); skipped when it is not on this PC.</summary>
public sealed class WebFontSetFactAttribute : FactAttribute
{
    public WebFontSetFactAttribute(string stem)
    {
        if (TestFonts.WebFontSet(stem) is null) Skip = $"The web font set '{stem}' is not on this PC.";
    }
}
