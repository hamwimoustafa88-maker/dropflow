using DropFlow.AI.Models;
using DropFlow.AI.Translation;
using DropFlow.Core.Operations;
using DropFlow.TestSupport;
using Xunit;

namespace DropFlow.AI.Tests;

/// <summary>
/// The real Opus-MT models on real sentences. They are skipped, visibly, when a model has not been downloaded
/// (<c>dropflow models download opus-mt-en-ar --yes</c>). Assertions are loose on purpose: a machine-translation model may phrase things
/// differently from run to run of a new model version, so each check only looks for one word a correct translation cannot avoid.
/// </summary>
public class RealTranslationTests : IDisposable
{
    private readonly ModelManager _models = ModelFactAttribute.OpenManager();
    private readonly MarianTextTranslator _translator;

    public RealTranslationTests() => _translator = new MarianTextTranslator(_models);

    public void Dispose()
    {
        _translator.Dispose();
        _models.Dispose();
    }

    private async Task<IReadOnlyList<string>> Translate(TranslationDirection direction, TranslationQuality quality, params string[] texts) =>
        await _translator.TranslateAsync(direction, texts, quality, null, default);

    public static TheoryData<string, string> EnglishToArabicSamples => new()
    {
        { "Hello, how are you today?", "اليوم" },
        { "Where is the nearest hospital?", "مستشفى" },
        { "The weather is beautiful today.", "الطقس" },
        { "Please restart your computer to finish installing the update.", "تشغيل" },
        { "Thank you very much for your help.", "شكر" },
    };

    public static TheoryData<string, string> ArabicToEnglishSamples => new()
    {
        { "أين أقرب مستشفى؟", "hospital" },
        { "الطقس جميل اليوم.", "weather" },
        { "شكراً جزيلاً على مساعدتك.", "thank" },
        { "يرجى إعادة تشغيل الحاسوب لإكمال التثبيت.", "computer" },
        { "الكتاب على الطاولة.", "table" },
    };

    [ModelTheory("opus-mt-en-ar")]
    [MemberData(nameof(EnglishToArabicSamples))]
    public async Task English_sentences_come_out_as_arabic_with_the_expected_word(string source, string keyword)
    {
        var result = (await Translate(TranslationDirection.EnglishToArabic, TranslationQuality.Balanced, source)).Single();
        Assert.Contains(keyword, result);
        Assert.True(ScriptDetector.Count(result).ArabicRatio > 0.9, result);        // really Arabic, not an echo of the English
    }

    [ModelTheory("opus-mt-ar-en")]
    [MemberData(nameof(ArabicToEnglishSamples))]
    public async Task Arabic_sentences_come_out_as_english_with_the_expected_word(string source, string keyword)
    {
        var result = (await Translate(TranslationDirection.ArabicToEnglish, TranslationQuality.Balanced, source)).Single();
        Assert.Contains(keyword, result, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, ScriptDetector.Count(result).Arabic);
    }

    [ModelFact("opus-mt-en-ar")]
    public async Task Greedy_balanced_and_best_all_translate_and_the_result_is_repeatable()
    {
        const string text = "I would like to book a table for two people tonight.";
        var fast = (await Translate(TranslationDirection.EnglishToArabic, TranslationQuality.Fast, text)).Single();
        var best = (await Translate(TranslationDirection.EnglishToArabic, TranslationQuality.Best, text)).Single();
        var best2 = (await Translate(TranslationDirection.EnglishToArabic, TranslationQuality.Best, text)).Single();

        Assert.Contains("طاولة", fast);
        Assert.Contains("طاولة", best);
        Assert.Equal(best, best2);                                    // no randomness: the same input gives the same output
    }

    [ModelFact("opus-mt-en-ar")]
    public async Task A_batch_keeps_its_order_and_a_sentence_gives_the_same_result_alone_or_in_a_batch()
    {
        string[] texts = ["Good morning.", "Where is the station?", "I need a doctor right now, because my son is sick and has a high fever.", "Thank you."];
        var batch = await Translate(TranslationDirection.EnglishToArabic, TranslationQuality.Balanced, texts);
        Assert.Equal(texts.Length, batch.Count);
        Assert.Contains("المحطة", batch[1]);
        Assert.Contains("طبيب", batch[2]);

        var alone = await Translate(TranslationDirection.EnglishToArabic, TranslationQuality.Balanced, texts[1]);
        Assert.Equal(alone[0], batch[1]);                             // padding in a batch must not change a translation
    }

    [ModelFact("opus-mt-en-ar")]
    public async Task Long_text_is_split_into_sentences_and_joined_again_with_links_and_numbers_kept()
    {
        const string text = "Visit https://example.com/help for support. The shop opens at 9:00 and closes at 18:00.\nThe price is $1,250.50 per month.";
        var result = (await Translate(TranslationDirection.EnglishToArabic, TranslationQuality.Fast, text)).Single();

        Assert.Contains("https://example.com/help", result);
        Assert.Contains("9:00", result);
        Assert.Contains("18:00", result);
        Assert.Contains("$1,250.50", result);
        Assert.Contains('\n', result);                                 // the line break between the two paragraphs survives
    }

    [ModelFact("opus-mt-ar-en")]
    public async Task Text_in_the_target_language_and_text_without_letters_are_returned_unchanged()
    {
        var result = await Translate(TranslationDirection.ArabicToEnglish, TranslationQuality.Fast, "Already English text.", "12345", "", "  ");
        Assert.Equal(["Already English text.", "12345", "", "  "], result);
    }

    [ModelFact("opus-mt-en-ar")]
    public async Task Cancelling_before_or_during_translation_stops_it()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _translator.TranslateAsync(TranslationDirection.EnglishToArabic, ["Hello there"], TranslationQuality.Fast, null, cancelled.Token));

        // during: cancel as soon as the first progress report arrives
        using var during = new CancellationTokenSource();
        var texts = Enumerable.Range(1, 60).Select(i => $"This is sentence number {i} of the long document.").ToList();
        var progress = new InlineProgress<double>(p => { if (p > 0 && p < 1) during.Cancel(); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _translator.TranslateAsync(TranslationDirection.EnglishToArabic, texts, TranslationQuality.Fast, progress, during.Token));
    }

    [ModelFact("opus-mt-en-ar")]
    public async Task A_loaded_model_is_released_after_the_idle_time_and_loads_again_on_demand()
    {
        using var translator = new MarianTextTranslator(_models, idleTimeout: TimeSpan.FromMilliseconds(300));
        await translator.TranslateAsync(TranslationDirection.EnglishToArabic, ["Hello"], TranslationQuality.Fast, null, default);
        Assert.Equal(1, translator.LoadedCount);

        for (var i = 0; i < 40 && translator.LoadedCount > 0; i++) await Task.Delay(100);
        Assert.Equal(0, translator.LoadedCount);

        var again = await translator.TranslateAsync(TranslationDirection.EnglishToArabic, ["Hello"], TranslationQuality.Fast, null, default);
        Assert.Equal(1, translator.LoadedCount);
        Assert.Contains('ا', again[0]);
    }

    [ModelFact("opus-mt-en-ar")]
    public void The_tokenizer_reads_both_scripts_and_decodes_back()
    {
        var tokenizer = MarianTokenizer.Load(_models.FilePathOf("opus-mt-en-ar", MarianTextTranslator.SourceSpmFile), _models.FilePathOf("opus-mt-en-ar", MarianTextTranslator.VocabularyFile));
        var tag = tokenizer.IdOf(MarianTextTranslator.ArabicTag);
        Assert.NotNull(tag);

        var ids = tokenizer.Encode("Hello, world!", tag);
        Assert.Equal(tag, (int)ids[0]);                              // the language tag comes first
        Assert.Equal(tokenizer.EosId, (int)ids[^1]);                 // and the end mark last
        Assert.DoesNotContain((long)tokenizer.UnkId, ids);
        Assert.Equal(">>ara<< Hello, world!", tokenizer.Decode(ids));       // the shared vocabulary decodes English pieces too

        // normalisation: full-width letters and ligatures become plain letters before cutting into pieces
        Assert.Equal(tokenizer.Encode("fine ABC"), tokenizer.Encode("ﬁne ＡＢＣ"));
        Assert.Equal([(long)tokenizer.EosId], tokenizer.Encode("   "));
    }

    [ModelFact("opus-mt-ar-en")]
    public void The_arabic_tokenizer_handles_diacritics_digits_and_mixed_text()
    {
        var tokenizer = MarianTokenizer.Load(_models.FilePathOf("opus-mt-ar-en", MarianTextTranslator.SourceSpmFile), _models.FilePathOf("opus-mt-ar-en", MarianTextTranslator.VocabularyFile));
        var ids = tokenizer.Encode("كَتَبَ الطَّالِبُ الدَّرْسَ في 2024 بواسطة Windows");
        Assert.True(ids.Length > 8);
        Assert.True(ids.Count(i => i == tokenizer.UnkId) <= 2, "Arabic with marks must not turn into unknown tokens");
        Assert.Equal("مرحبا بكم", tokenizer.Decode(tokenizer.Encode("مرحبا بكم")));
    }

    // ---- real files through the converter ------------------------------------------------------------------------------

    [ModelFact("opus-mt-en-ar")]
    public async Task A_real_english_subtitle_file_becomes_an_arabic_one_with_the_same_structure()
    {
        using var dir = new TempDir();
        const string source = "1\n00:00:01,000 --> 00:00:03,500\nHello, how are you today?\n\n2\n00:00:04,000 --> 00:00:07,200\nI was thinking we could go to the market\nbefore it gets too crowded.\n\n3\n00:00:08,000 --> 00:00:09,800\n<i>Please be careful with the glass.</i>\n\n4\n00:00:10,000 --> 00:00:12,500\n- Where are you going?\n- To the station.\n";
        var path = dir.WriteText("movie.srt", source);
        using var converter = new TextTranslateConverter(_translator);

        var result = await converter.RunAsync(new ConversionJob([path], "txt.translate"), new Progress<JobProgress>(), default);

        Assert.True(result.Success, result.Error);
        var output = File.ReadAllText(result.Outputs[0]);
        var blocks = output.TrimEnd('\n').Split("\n\n");
        Assert.Equal(4, blocks.Length);
        Assert.Equal(["1", "00:00:01,000 --> 00:00:03,500"], blocks[0].Split('\n')[..2]);
        Assert.Equal(2, blocks[1].Split('\n').Length - 2);                                   // the two-line cue is still two lines
        Assert.StartsWith("<i>", blocks[2].Split('\n')[2]);
        Assert.EndsWith("</i>", blocks[2].Split('\n')[2]);
        Assert.StartsWith("- ", blocks[3].Split('\n')[2]);
        Assert.StartsWith("- ", blocks[3].Split('\n')[3]);
        Assert.Contains("اليوم", blocks[0]);
        Assert.Contains("المحطة", blocks[3]);
        Assert.Equal(source, File.ReadAllText(path));
    }

    [ModelFact("opus-mt-ar-en")]
    public async Task A_real_arabic_subtitle_file_becomes_an_english_one()
    {
        using var dir = new TempDir();
        var path = dir.WriteText("film.srt", "1\n00:00:01,000 --> 00:00:03,500\nمرحباً، كيف حالك اليوم؟\n\n2\n00:00:04,000 --> 00:00:07,200\n- إلى أين أنت ذاهب؟\n- إلى المحطة.\n");
        using var converter = new TextTranslateConverter(_translator);

        var result = await converter.RunAsync(new ConversionJob([path], "txt.translate"), new Progress<JobProgress>(), default);

        Assert.True(result.Success, result.Error);
        var output = File.ReadAllText(result.Outputs[0]);
        Assert.Contains("today", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("station", output, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, ScriptDetector.Count(output).Arabic);
        Assert.Contains("00:00:04,000 --> 00:00:07,200", output);
    }
}

/// <summary>Like <see cref="ModelFactAttribute"/> for a theory.</summary>
public sealed class ModelTheoryAttribute : TheoryAttribute
{
    public ModelTheoryAttribute(string modelId)
    {
        using var manager = ModelFactAttribute.OpenManager();
        if (!manager.IsInstalled(modelId))
            Skip = $"Model '{modelId}' is not downloaded ({manager.PathOf(modelId)}). Run: dropflow models download {modelId} --yes";
    }
}
