using DropFlow.AI.Models;

namespace DropFlow.AI.Translation;

/// <summary>The two supported directions. Each has its own model (Opus-MT), downloaded on first use.</summary>
public enum TranslationDirection
{
    EnglishToArabic,
    ArabicToEnglish,
}

/// <summary>Decoding effort: greedy is fastest, beam search is slower and usually a little better.</summary>
public enum TranslationQuality
{
    /// <summary>Greedy decoding (one hypothesis).</summary>
    Fast = 1,

    /// <summary>Beam search with 2 hypotheses.</summary>
    Balanced = 2,

    /// <summary>Beam search with 4 hypotheses (the setting the models were tuned with).</summary>
    Best = 4,
}

/// <summary>Ids, option values and language facts for the translation tools.</summary>
public static class TranslationInfo
{
    public const string EnArModelId = "opus-mt-en-ar";
    public const string ArEnModelId = "opus-mt-ar-en";

    public const string AutoDirection = "auto";
    public const string EnAr = "en-ar";
    public const string ArEn = "ar-en";

    /// <summary>Values of the <c>direction</c> option shared by every translation tool.</summary>
    public static readonly IReadOnlyList<string> DirectionChoices = [AutoDirection, EnAr, ArEn];

    /// <summary>Values of the <c>quality</c> option shared by every translation tool.</summary>
    public static readonly IReadOnlyList<string> QualityChoices = ["fast", "balanced", "best"];

    public static string ModelId(TranslationDirection direction) =>
        direction == TranslationDirection.EnglishToArabic ? EnArModelId : ArEnModelId;

    /// <summary>Reads the <c>direction</c> option: null means "auto" (decide from the text).</summary>
    public static bool TryParseDirection(string? value, out TranslationDirection? direction)
    {
        direction = null;
        switch ((value ?? AutoDirection).Trim().ToLowerInvariant())
        {
            case "" or "auto": return true;
            case "en-ar" or "en2ar" or "en_ar": direction = TranslationDirection.EnglishToArabic; return true;
            case "ar-en" or "ar2en" or "ar_en": direction = TranslationDirection.ArabicToEnglish; return true;
            default: return false;
        }
    }

    public static TranslationQuality ParseQuality(string? value) => (value ?? "balanced").Trim().ToLowerInvariant() switch
    {
        "fast" => TranslationQuality.Fast,
        "best" => TranslationQuality.Best,
        _ => TranslationQuality.Balanced,
    };

    /// <summary>The model entries a direction needs (for tests and the model manager UI).</summary>
    public static ModelInfo? Model(TranslationDirection direction) => ModelCatalog.Find(ModelId(direction));
}

/// <summary>
/// Raised for expected problems (a model is not installed or is damaged). The message is the text the app shows; for a missing model it
/// is the standard "Model 'id' is not installed" line that makes the app offer the download.
/// </summary>
public sealed class TranslationUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Translates text locally. Implementations never touch the network.</summary>
public interface ITextTranslator
{
    /// <summary>The model id still to be installed for this direction, or null when it is ready.</summary>
    string? MissingModel(TranslationDirection direction);

    /// <summary>
    /// The sentence to show the user when this direction cannot run yet, or null when it can. For a missing model it is the standard
    /// "Model 'id' is not installed" line that makes the app offer the download.
    /// </summary>
    string? NotReadyMessage(TranslationDirection direction);

    /// <summary>
    /// Translates every string and returns the results in the same order. A string may be a sentence, a paragraph or a line; long ones
    /// are split into sentences internally and joined again, leading and trailing white space is kept, and URLs, e-mail addresses,
    /// code spans and strings without letters are returned as they are. Throws <see cref="TranslationUnavailableException"/> when the model is missing.
    /// </summary>
    Task<IReadOnlyList<string>> TranslateAsync(
        TranslationDirection direction,
        IReadOnlyList<string> texts,
        TranslationQuality quality,
        IProgress<double>? progress,
        CancellationToken cancellationToken);
}
