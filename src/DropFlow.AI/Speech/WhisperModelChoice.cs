using System.Runtime.InteropServices;

namespace DropFlow.AI.Speech;

/// <summary>What the model choice logic knows about one Whisper model (ids match <see cref="Models.ModelCatalog"/>).</summary>
/// <param name="MinAvailableRamBytes">Free physical memory at which the model runs comfortably on a CPU.</param>
public sealed record WhisperProfile(string ModelId, bool EnglishOnly, bool CanTranslate, long MinAvailableRamBytes);

public sealed record WhisperRecommendation(string ModelId, string Reason, IReadOnlyList<string> Ranking);

/// <summary>Picks a Whisper model for a language and the memory that is free right now.</summary>
public static class WhisperModelChoice
{
    private const long Mb = 1024L * 1024;
    private const long Gb = 1024L * Mb;

    public static IReadOnlyList<WhisperProfile> Profiles { get; } =
    [
        new("ggml-tiny", false, true, 512 * Mb),
        new("ggml-base", false, true, 1 * Gb),
        new("ggml-base.en", true, false, 1 * Gb),
        new("ggml-small", false, true, 2 * Gb),
        new("ggml-small.en", true, false, 2 * Gb),
        new("ggml-medium-q5_0", false, true, 4 * Gb),
        // large-v3-turbo was trained without the translate task: it transcribes well but cannot translate.
        new("ggml-large-v3-turbo-q5_0", false, false, 4 * Gb),
    ];

    public static WhisperProfile? Profile(string modelId) => Profiles.FirstOrDefault(p => p.ModelId == modelId);

    /// <summary>Normalises "AR", "ar-SA", "ar_EG" and "auto"/"" to a lower-case primary language code ("ar") or "auto".</summary>
    public static string NormalizeLanguage(string? language)
    {
        if (string.IsNullOrWhiteSpace(language)) return "auto";
        var code = language.Trim().ToLowerInvariant();
        var cut = code.IndexOfAny(['-', '_']);
        if (cut > 0) code = code[..cut];
        return code == "auto" ? "auto" : code;
    }

    /// <summary>True when <paramref name="modelId"/> can handle this request at all (English-only models cannot do other languages; turbo cannot translate).</summary>
    public static bool IsCompatible(string modelId, string? language, bool translateToEnglish)
    {
        var profile = Profile(modelId);
        if (profile is null) return true; // unknown models are the caller's responsibility
        var lang = NormalizeLanguage(language);
        if (profile.EnglishOnly && lang != "en") return false;
        if (translateToEnglish && lang != "en" && !profile.CanTranslate) return false;
        return true;
    }

    /// <summary>All compatible models, best choice first (ignoring RAM).</summary>
    public static IReadOnlyList<string> Preference(string? language, bool translateToEnglish)
    {
        var lang = NormalizeLanguage(language);
        string[] order = lang == "en"
            ? ["ggml-small.en", "ggml-base.en", "ggml-large-v3-turbo-q5_0", "ggml-medium-q5_0", "ggml-small", "ggml-base", "ggml-tiny"]
            : translateToEnglish
                ? ["ggml-medium-q5_0", "ggml-small", "ggml-base", "ggml-tiny"]
                : ["ggml-large-v3-turbo-q5_0", "ggml-medium-q5_0", "ggml-small", "ggml-base", "ggml-tiny"];
        return order.Where(id => IsCompatible(id, lang, translateToEnglish)).ToList();
    }

    /// <summary>Best model that fits <paramref name="availableRamBytes"/>, whether or not it is installed (the app offers to download it).</summary>
    public static WhisperRecommendation Recommend(string? language, long availableRamBytes, bool translateToEnglish = false)
    {
        var lang = NormalizeLanguage(language);
        var ranking = Preference(lang, translateToEnglish);
        var fitting = ranking.Where(id => Fits(id, availableRamBytes)).ToList();
        var pick = fitting.Count > 0 ? fitting[0] : ranking[^1];

        var reason = lang == "en"
            ? $"English speech: {pick} is the best English model that fits {Describe(availableRamBytes)} of free memory."
            : $"{(lang == "auto" ? "Mixed or unknown language" : $"Language '{lang}'")}: {pick} is the best multilingual model that fits {Describe(availableRamBytes)} of free memory.";
        if (lang != "en" && pick is "ggml-tiny" or "ggml-base")
            reason += " Accuracy for non-English languages (including Arabic) is poor at this size; free some memory to use ggml-small or larger.";
        if (translateToEnglish && lang != "en")
            reason += " Translation to English needs a model that was trained to translate, so large-v3-turbo is not used.";
        return new WhisperRecommendation(pick, reason, ranking);
    }

    /// <summary>
    /// The best already-installed model for this request, or null when none is compatible. Prefers models that fit free memory;
    /// if none fits it still returns the smallest compatible installed one (running slowly beats refusing).
    /// </summary>
    public static string? PickInstalled(IEnumerable<string> installedModelIds, string? language, long availableRamBytes, bool translateToEnglish = false)
    {
        var installed = new HashSet<string>(installedModelIds);
        var candidates = Preference(language, translateToEnglish).Where(installed.Contains).ToList();
        return candidates.FirstOrDefault(id => Fits(id, availableRamBytes)) ?? candidates.LastOrDefault();
    }

    public static bool Fits(string modelId, long availableRamBytes) =>
        Profile(modelId) is not { } p || availableRamBytes >= p.MinAvailableRamBytes;

    private static string Describe(long bytes) => $"{bytes / (double)Gb:0.#} GB";

    /// <summary>Free physical memory right now (including cached memory Windows will give back). Falls back to 4 GB if the query fails.</summary>
    public static long GetAvailableRamBytes()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        return GlobalMemoryStatusEx(ref status) ? (long)status.AvailablePhysical : 4 * Gb;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}
