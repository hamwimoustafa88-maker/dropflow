using DropFlow.AI.Models;
using DropFlow.AI.Speech;
using Xunit;

namespace DropFlow.AI.Tests;

public class WhisperModelChoiceTests
{
    private const long Gb = 1024L * 1024 * 1024;

    [Theory]
    [InlineData("ar", 16, "ggml-large-v3-turbo-q5_0")]
    [InlineData("auto", 16, "ggml-large-v3-turbo-q5_0")]
    [InlineData("ar", 3, "ggml-small")]
    [InlineData("ar", 1.5, "ggml-base")]
    [InlineData("ar", 0.6, "ggml-tiny")]
    [InlineData("fr", 8, "ggml-large-v3-turbo-q5_0")]
    [InlineData("en", 16, "ggml-small.en")]
    [InlineData("en", 1.2, "ggml-base.en")]
    [InlineData("en", 0.3, "ggml-tiny")] // nothing fits: fall back to the smallest compatible, not to nothing
    public void Recommends_by_language_and_free_memory(string language, double freeGb, string expected)
    {
        var rec = WhisperModelChoice.Recommend(language, (long)(freeGb * Gb));
        Assert.Equal(expected, rec.ModelId);
        Assert.False(string.IsNullOrWhiteSpace(rec.Reason));
    }

    [Fact]
    public void English_only_models_are_never_chosen_for_other_languages()
    {
        foreach (var lang in new[] { "ar", "fr", "auto", "zh" })
            Assert.DoesNotContain(WhisperModelChoice.Preference(lang, false), id => id.EndsWith(".en", StringComparison.Ordinal));
        Assert.Contains("ggml-small.en", WhisperModelChoice.Preference("en", false));
    }

    [Fact]
    public void Translation_never_uses_turbo_which_cannot_translate()
    {
        Assert.DoesNotContain("ggml-large-v3-turbo-q5_0", WhisperModelChoice.Preference("ar", translateToEnglish: true));
        Assert.Equal("ggml-medium-q5_0", WhisperModelChoice.Recommend("ar", 16 * Gb, translateToEnglish: true).ModelId);
        Assert.False(WhisperModelChoice.IsCompatible("ggml-large-v3-turbo-q5_0", "ar", true));
        Assert.True(WhisperModelChoice.IsCompatible("ggml-large-v3-turbo-q5_0", "ar", false));
    }

    [Fact]
    public void Low_memory_non_english_recommendation_warns_about_quality()
    {
        var rec = WhisperModelChoice.Recommend("ar", (long)(1.2 * Gb));
        Assert.Equal("ggml-base", rec.ModelId);
        Assert.Contains("poor", rec.Reason);
    }

    [Theory]
    [InlineData("AR", "ar")]
    [InlineData("ar-SA", "ar")]
    [InlineData("en_US", "en")]
    [InlineData(" Auto ", "auto")]
    [InlineData("", "auto")]
    [InlineData(null, "auto")]
    public void Languages_are_normalised(string? input, string expected) =>
        Assert.Equal(expected, WhisperModelChoice.NormalizeLanguage(input));

    [Fact]
    public void Picks_the_best_installed_model_that_fits_and_degrades_gracefully()
    {
        string[] installed = ["ggml-tiny", "ggml-small", "ggml-large-v3-turbo-q5_0"];
        Assert.Equal("ggml-large-v3-turbo-q5_0", WhisperModelChoice.PickInstalled(installed, "ar", 16 * Gb));
        Assert.Equal("ggml-small", WhisperModelChoice.PickInstalled(installed, "ar", 3 * Gb));
        // translation: turbo is out, small is the best remaining
        Assert.Equal("ggml-small", WhisperModelChoice.PickInstalled(installed, "ar", 16 * Gb, translateToEnglish: true));
        // nothing fits 100 MB of RAM: still run the smallest installed model instead of refusing
        Assert.Equal("ggml-tiny", WhisperModelChoice.PickInstalled(installed, "ar", 100L * 1024 * 1024));
        // only an English-only model installed, Arabic requested: no compatible model
        Assert.Null(WhisperModelChoice.PickInstalled(["ggml-base.en"], "ar", 16 * Gb));
        Assert.Equal("ggml-base.en", WhisperModelChoice.PickInstalled(["ggml-base.en"], "en", 16 * Gb));
        Assert.Null(WhisperModelChoice.PickInstalled([], "en", 16 * Gb));
    }

    [Fact]
    public void Profile_ids_all_exist_in_the_model_catalog()
    {
        foreach (var p in WhisperModelChoice.Profiles)
            Assert.NotNull(ModelCatalog.Find(p.ModelId));
    }

    [Fact]
    public void Available_ram_is_measured_for_real()
    {
        var ram = WhisperModelChoice.GetAvailableRamBytes();
        Assert.InRange(ram, 64L * 1024 * 1024, 4096L * Gb);
    }

    [Fact]
    public void Linux_free_memory_is_read_from_MemAvailable()
    {
        const string meminfo = "MemTotal:       16314172 kB\nMemFree:          812344 kB\nMemAvailable:    9876543 kB\nBuffers:          123 kB\n";
        Assert.Equal(9876543L * 1024, WhisperModelChoice.ParseMemInfo(meminfo));
        Assert.Null(WhisperModelChoice.ParseMemInfo("MemTotal: 1 kB\n"));
    }

    [Fact]
    public void MacOS_free_memory_counts_free_inactive_and_speculative_pages()
    {
        const string vmStat = """
            Mach Virtual Memory Statistics: (page size of 16384 bytes)
            Pages free:                               10000.
            Pages active:                            200000.
            Pages inactive:                           30000.
            Pages speculative:                         5000.
            Pages wired down:                         90000.
            """;
        Assert.Equal((10000L + 30000 + 5000) * 16384, WhisperModelChoice.ParseVmStat(vmStat));
        Assert.Null(WhisperModelChoice.ParseVmStat("no numbers here"));
    }}
