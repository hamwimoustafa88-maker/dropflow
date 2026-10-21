using DropFlow.Core.Operations;
using DropFlow.Engines.Common;

namespace DropFlow.AI.Ocr;

public enum OcrEngineKind { Windows, Tesseract }

public enum OcrEnginePreference { Auto, Windows, Tesseract }

/// <summary>What this PC can do right now.</summary>
/// <param name="WindowsTags">Installed Windows OCR packs.</param>
/// <param name="TesseractLanguages">Installed traineddata names; empty when Tesseract is missing.</param>
public sealed record OcrCapabilities(IReadOnlyList<string> WindowsTags, bool TesseractInstalled, IReadOnlyList<string> TesseractLanguages);

/// <param name="WindowsTag">The Windows pack to use; null means "the user's profile languages, then English".</param>
/// <param name="TesseractLanguages">Traineddata names, e.g. ["ara", "eng"].</param>
public sealed record OcrPlan(OcrEngineKind Engine, string? WindowsTag, IReadOnlyList<string> TesseractLanguages, string Description);

public sealed record OcrPlanResult(OcrPlan? Plan, string? Error);

/// <summary>
/// Decides which engine and language to use. Windows OCR is preferred (no install, good quality); Tesseract takes over when a
/// requested language has no Windows pack. When nothing fits, the error says exactly what to install.
/// Windows OCR recognises one language per run: with several requested it uses the first, so ask for Tesseract to read a mix.
/// </summary>
public static class OcrPlanner
{
    public static OcrPlanResult Plan(IReadOnlyList<OcrLanguage> requested, OcrEnginePreference preference, OcrCapabilities caps)
    {
        var hasWindowsPacks = caps.WindowsTags.Count > 0;

        // ---- "auto": no language named --------------------------------------------------------------------------
        if (requested.Count == 0)
        {
            if (preference != OcrEnginePreference.Tesseract && hasWindowsPacks)
                return Ok(new OcrPlan(OcrEngineKind.Windows, null, [], "Windows OCR with your Windows languages (English fallback)"));
            if (preference != OcrEnginePreference.Windows && caps.TesseractInstalled)
            {
                var langs = new[] { "ara", "eng" }.Where(caps.TesseractLanguages.Contains).ToList();
                if (langs.Count == 0) langs = caps.TesseractLanguages.Where(l => l != "osd").Take(1).ToList();
                if (langs.Count > 0)
                    return Ok(new OcrPlan(OcrEngineKind.Tesseract, null, langs, $"Tesseract ({string.Join('+', langs)})"));
            }
            return Fail("No OCR language is available. Install the English OCR pack for Windows (administrator PowerShell: "
                + OcrLanguages.Find("en")!.WindowsInstallCommand + ") or install Tesseract.");
        }

        var windowsMissing = requested.Where(l => !HasWindowsPack(l, caps)).ToList();
        var tessMissing = requested.Where(l => !caps.TesseractLanguages.Contains(l.Tesseract)).ToList();

        if (preference == OcrEnginePreference.Windows)
            return windowsMissing.Count == 0 ? WindowsPlan(requested[0], caps) : Fail(WindowsMissingMessage(windowsMissing, caps));
        if (preference == OcrEnginePreference.Tesseract)
            return tessMissing.Count == 0 && caps.TesseractInstalled ? TesseractPlan(requested) : Fail(TesseractMissingMessage(tessMissing, caps));

        // ---- Auto engine ----------------------------------------------------------------------------------------
        if (windowsMissing.Count == 0) return WindowsPlan(requested[0], caps);
        if (caps.TesseractInstalled && tessMissing.Count == 0) return TesseractPlan(requested);

        return Fail(string.Join(" ", new[] { WindowsMissingMessage(windowsMissing, caps), TesseractMissingMessage(tessMissing, caps) }.Where(s => s.Length > 0)));
    }

    private static bool HasWindowsPack(OcrLanguage language, OcrCapabilities caps) =>
        caps.WindowsTags.Any(t => PrimarySubtag(t).Equals(language.Iso1, StringComparison.OrdinalIgnoreCase));

    private static OcrPlanResult WindowsPlan(OcrLanguage language, OcrCapabilities caps)
    {
        var tags = caps.WindowsTags.Where(t => PrimarySubtag(t).Equals(language.Iso1, StringComparison.OrdinalIgnoreCase)).ToList();
        var tag = tags.FirstOrDefault(t => t.Equals(language.WindowsTag, StringComparison.OrdinalIgnoreCase)) ?? tags[0];
        return Ok(new OcrPlan(OcrEngineKind.Windows, tag, [], $"Windows OCR ({tag})"));
    }

    private static OcrPlanResult TesseractPlan(IReadOnlyList<OcrLanguage> requested)
    {
        var langs = requested.Select(l => l.Tesseract).ToList();
        return Ok(new OcrPlan(OcrEngineKind.Tesseract, null, langs, $"Tesseract ({string.Join('+', langs)})"));
    }

    private static string WindowsMissingMessage(IReadOnlyList<OcrLanguage> missing, OcrCapabilities caps)
    {
        if (missing.Count == 0) return "";
        var items = missing.Select(l => l.WindowsInstallCommand is { } cmd
            ? $"{l.DisplayName}: run in an administrator PowerShell: {cmd}"
            : $"{l.DisplayName}: Windows has no OCR pack for it");
        return "Windows OCR is missing: " + string.Join("; ", items) + ".";
    }

    private static string TesseractMissingMessage(IReadOnlyList<OcrLanguage> missing, OcrCapabilities caps)
    {
        if (!caps.TesseractInstalled)
            return "Tesseract is not installed, so it cannot be used as a fallback (install it from the engine installer; choose the Arabic language data for Arabic).";
        if (missing.Count == 0) return "";
        return "Tesseract lacks language data: " + string.Join(", ", missing.Select(l => $"{l.Tesseract} ({l.DisplayName})"))
            + ". Add the .traineddata files to Tesseract's tessdata folder.";
    }

    private static string PrimarySubtag(string tag)
    {
        var cut = tag.IndexOfAny(['-', '_']);
        return cut > 0 ? tag[..cut] : tag;
    }

    private static OcrPlanResult Ok(OcrPlan plan) => new(plan, null);
    private static OcrPlanResult Fail(string error) => new(null, error);
}

/// <param name="Languages">"auto", or a list such as "en", "ar+en", "ara+eng".</param>
public sealed record OcrRequest(string Languages = "auto", OcrEnginePreference Engine = OcrEnginePreference.Auto);

/// <summary>
/// Image in, <see cref="OcrResult"/> out. Prepares the image once, asks <see cref="OcrPlanner"/> which engine fits, runs it and
/// maps the rectangles back to the upright source image. Throws <see cref="OcrUnavailableException"/> for expected failures.
/// </summary>
public sealed class OcrService
{
    private readonly OcrLanguageService _languages;
    private readonly IWindowsOcr _windows;
    private readonly ITesseractOcr _tesseract;

    public OcrService(IEngineProvider engines, OcrLanguageService? languages = null, IWindowsOcr? windows = null, ITesseractOcr? tesseract = null)
    {
        _languages = languages ?? new OcrLanguageService();
        _windows = windows ?? new WindowsOcr();
        _tesseract = tesseract ?? new TesseractOcr(engines);
    }

    public OcrLanguageService Languages => _languages;

    public async Task<OcrCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken)
    {
        var tess = _tesseract.IsInstalled ? await _tesseract.LanguagesAsync(cancellationToken).ConfigureAwait(false) : [];
        return new OcrCapabilities(_languages.InstalledTags(), _tesseract.IsInstalled, tess);
    }

    /// <summary>Resolves the request without running anything (the UI uses it to show what will happen).</summary>
    public async Task<OcrPlanResult> PlanAsync(OcrRequest request, CancellationToken cancellationToken)
    {
        var requested = OcrLanguages.ParseList(request.Languages, out var unknown);
        if (unknown.Count > 0)
            return new OcrPlanResult(null, $"Unknown OCR language: {string.Join(", ", unknown)}.");
        return OcrPlanner.Plan(requested, request.Engine, await GetCapabilitiesAsync(cancellationToken).ConfigureAwait(false));
    }

    public async Task<OcrResult> RecognizeAsync(string imagePath, OcrRequest request, CancellationToken cancellationToken)
    {
        var plan = await PlanAsync(request, cancellationToken).ConfigureAwait(false);
        if (plan.Plan is null) throw new OcrUnavailableException(plan.Error!);

        using var work = WorkDirectory.Create(asciiOnly: true);
        var maxDimension = plan.Plan.Engine == OcrEngineKind.Windows ? (int)Math.Min(Windows.Media.Ocr.OcrEngine.MaxImageDimension, 8000u) : 8000;
        var prepared = OcrImagePreparer.Prepare(imagePath, work.File("input.png"), maxDimension);

        var result = plan.Plan.Engine == OcrEngineKind.Windows
            ? await _windows.RecognizeAsync(prepared.Path, plan.Plan.WindowsTag, cancellationToken).ConfigureAwait(false)
            : await _tesseract.RecognizeAsync(prepared.Path, plan.Plan.TesseractLanguages, cancellationToken).ConfigureAwait(false);

        return Math.Abs(prepared.ScaleToOriginal - 1) < 1e-9
            ? result with { ImageWidth = prepared.OriginalWidth, ImageHeight = prepared.OriginalHeight }
            : result.Scaled(prepared.ScaleToOriginal, prepared.OriginalWidth, prepared.OriginalHeight);
    }
}
