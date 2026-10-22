using DropFlow.AI.Translation;
using DropFlow.AI.Translation.Formats;
using DropFlow.AI.Translation.Rendering;
using Xunit;

namespace DropFlow.AI.Tests;

/// <summary>The pure parts of translation: script detection, sentence cutting, wrapping, Arabic shaping. No model is needed.</summary>
public class TranslationHelpersTests
{
    // ---- script detection ------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Hello world", TranslationDirection.EnglishToArabic)]
    [InlineData("مرحبا بالعالم", TranslationDirection.ArabicToEnglish)]
    [InlineData("مرحبا بكم في Windows", TranslationDirection.ArabicToEnglish)]    // the majority of the letters decides
    [InlineData("Open the ملف folder please", TranslationDirection.EnglishToArabic)]
    public void The_direction_follows_the_dominant_script(string text, TranslationDirection expected) =>
        Assert.Equal(expected, ScriptDetector.DetectDirection(text));

    [Theory]
    [InlineData("12345")]
    [InlineData("... !!! ---")]
    [InlineData("日本語のテキスト")]
    [InlineData("")]
    public void Text_without_arabic_or_latin_letters_has_no_direction(string text) =>
        Assert.Null(ScriptDetector.DetectDirection(text));

    [Fact]
    public void Strings_already_in_the_target_language_or_without_letters_are_not_sent_to_the_model()
    {
        Assert.True(ScriptDetector.NeedsTranslation("Please wait", TranslationDirection.EnglishToArabic));
        Assert.False(ScriptDetector.NeedsTranslation("الرجاء الانتظار", TranslationDirection.EnglishToArabic));
        Assert.False(ScriptDetector.NeedsTranslation("12:30", TranslationDirection.EnglishToArabic));
        Assert.True(ScriptDetector.NeedsTranslation("الرجاء الانتظار", TranslationDirection.ArabicToEnglish));
        Assert.True(ScriptDetector.NeedsTranslation("افتح الملف report.pdf", TranslationDirection.ArabicToEnglish));
        Assert.False(ScriptDetector.NeedsTranslation("Please wait", TranslationDirection.ArabicToEnglish));
        Assert.False(ScriptDetector.NeedsTranslation("2024", TranslationDirection.ArabicToEnglish));
    }

    [Theory]
    [InlineData("auto", null, true)]
    [InlineData("", null, true)]
    [InlineData(null, null, true)]
    [InlineData("en-ar", TranslationDirection.EnglishToArabic, true)]
    [InlineData("AR-EN", TranslationDirection.ArabicToEnglish, true)]
    [InlineData("fr-en", null, false)]
    public void Direction_options_are_parsed_strictly(string? option, TranslationDirection? expected, bool valid)
    {
        Assert.Equal(valid, TranslationInfo.TryParseDirection(option, out var direction));
        if (valid) Assert.Equal(expected, direction);
    }

    // ---- sentences ---------------------------------------------------------------------------------------------------------

    private static string[] Sentences(string text) =>
        [.. TextSegmenter.Split(text).Where(p => p.IsSentence).Select(p => p.Text)];

    [Fact]
    public void A_paragraph_is_cut_into_sentences_and_the_white_space_between_them_is_kept()
    {
        var text = "First one. Second one?  Third one!\nFourth.";
        Assert.Equal(["First one.", "Second one?", "Third one!", "Fourth."], Sentences(text));
        Assert.Equal(text, string.Concat(TextSegmenter.Split(text).Select(p => p.Text)));     // nothing lost, nothing added
    }

    [Fact]
    public void Abbreviations_initials_and_decimals_do_not_end_a_sentence()
    {
        Assert.Equal(["Dr. Smith met J. Doe at 3.5 percent."], Sentences("Dr. Smith met J. Doe at 3.5 percent."));
        Assert.Equal(["He lives in the U.S.A. now.", "She left."], Sentences("He lives in the U.S.A. now. She left."));
    }

    [Fact]
    public void Arabic_sentence_marks_end_sentences_too()
    {
        Assert.Equal(["كيف حالك؟", "أنا بخير."], Sentences("كيف حالك؟ أنا بخير."));
    }

    [Fact]
    public void Links_addresses_code_and_placeholders_are_kept_as_they_are()
    {
        var pieces = TextSegmenter.Split("Visit https://example.com/a?b=1, or mail me@x.org. Use `dotnet run` or {0} and %s.");
        var kept = pieces.Where(p => !p.IsSentence && p.Text.Trim().Length > 0).Select(p => p.Text).ToList();
        Assert.Contains("https://example.com/a?b=1", kept);                // the trailing comma is not part of the link
        Assert.Contains("me@x.org", kept);
        Assert.Contains("`dotnet run`", kept);
        Assert.Contains("{0}", kept);
        Assert.Contains("%s", kept);
    }

    [Fact]
    public void Times_dates_versions_and_amounts_stay_out_of_the_sentences()
    {
        var kept = TextSegmenter.Split("Open 9:00 to 18:00 on 12/05/2024 for $1,250.50 in v2.3.1 or call 555-1234.")
            .Where(p => !p.IsSentence && p.Text.Trim().Length > 0).Select(p => p.Text).ToList();
        foreach (var token in new[] { "9:00", "18:00", "12/05/2024", "$1,250.50", "2.3.1", "555-1234" }) Assert.Contains(token, kept);
    }

    [Fact]
    public void A_very_long_sentence_is_cut_at_a_comma_into_parts_of_at_most_sixty_words()
    {
        var clause = string.Join(' ', Enumerable.Repeat("word", 40));
        var parts = Sentences($"{clause}, {clause}.");
        Assert.Equal(2, parts.Length);
        Assert.All(parts, p => Assert.True(p.Split(' ').Length <= TextSegmenter.MaxWords));
        Assert.EndsWith(",", parts[0]);
    }

    [Fact]
    public void A_very_long_sentence_without_commas_is_cut_between_words()
    {
        var parts = Sentences(string.Join(' ', Enumerable.Repeat("word", 150)) + ".");
        Assert.True(parts.Length >= 3);
        Assert.All(parts, p => Assert.True(p.Split(' ').Length <= TextSegmenter.MaxWords));
        Assert.Equal(150, parts.Sum(p => p.Split(' ').Length));
    }

    // ---- cleaning a model answer ----------------------------------------------------------------------------------

    [Fact]
    public void Tokenizer_spaces_before_punctuation_and_inside_numbers_are_removed()
    {
        Assert.Equal("Hello, world!", TextSegmenter.Tidy("Hello , world !"));
        Assert.Equal("at 9:30 (today)", TextSegmenter.Tidy("at 9: 30 ( today )"));
        Assert.Equal("كيف حالك؟", TextSegmenter.Tidy("كيف حالك ؟"));
    }

    [Fact]
    public void A_dropped_final_mark_is_restored_but_a_fragment_does_not_gain_one()
    {
        Assert.Equal("أين المحطة؟", TextSegmenter.Finish("Where is the station?", "أين المحطة", TranslationDirection.EnglishToArabic));
        Assert.Equal("Where is the station?", TextSegmenter.Finish("أين المحطة؟", "Where is the station", TranslationDirection.ArabicToEnglish));
        Assert.Equal("شكرا.", TextSegmenter.Finish("Thanks.", "شكرا", TranslationDirection.EnglishToArabic));
        Assert.Equal("الاجتماع في", TextSegmenter.Finish("The meeting is at", "الاجتماع في.", TranslationDirection.EnglishToArabic));
    }

    [Fact]
    public void Scattered_vowel_marks_are_removed_from_arabic_answers_but_tanween_and_shadda_stay()
    {
        Assert.Equal("نفتح كلّ يوم", TextSegmenter.Finish("We open every day", "نَفْتحُ كُلّ يوم", TranslationDirection.EnglishToArabic));
        Assert.Equal("شكراً", TextSegmenter.Finish("Thanks", "شكراً", TranslationDirection.EnglishToArabic));
    }

    // ---- wrapping ------------------------------------------------------------------------------------------------------

    [Fact]
    public void Greedy_wrapping_respects_the_width_the_line_prefix_and_the_first_line_offset()
    {
        Assert.Equal("one two\n> three four\n> five six", TextWrap.Greedy("one two three four five six", 12, "> "));
        Assert.Equal("aaa\nbbb ccc", TextWrap.Greedy("aaa bbb ccc", 10, "", firstLineOffset: 4));      // 4 characters (a list marker) already used
        Assert.Equal("unbreakable-long-word", TextWrap.Greedy("unbreakable-long-word", 5, ""));       // a word longer than the width keeps its own line
    }

    [Fact]
    public void Balanced_wrapping_gives_the_requested_number_of_lines_of_similar_length()
    {
        var wrapped = TextWrap.Balanced("I was thinking we could go to the market before it gets too crowded", 2, "");
        var lines = wrapped.Split('\n');
        Assert.Equal(2, lines.Length);
        Assert.InRange(Math.Abs(lines[0].Length - lines[1].Length), 0, 12);
        Assert.Equal("one\ntwo", TextWrap.Balanced("one two", 3, ""));          // fewer words than lines: no empty lines
    }

    // ---- Arabic shaping (the fallback for ImageMagick builds without Raqm) -------------------------------

    [Fact]
    public void Letters_take_their_joined_forms()
    {
        // "مرحبا": meem initial, reh final, hah initial (reh does not join forward), beh medial, alef final
        Assert.Equal("ﻣﺮﺣﺒﺎ", ArabicShaper.Shape("مرحبا"));
        Assert.Equal("ﺏ", ArabicShaper.Shape("ب"));                       // a lone letter is isolated
        Assert.Equal("ﻻ", ArabicShaper.Shape("لا"));                      // lam + alef is one ligature
        Assert.Equal("ﻼ", ArabicShaper.Shape("بلا")[^1..]);               // joined to the beh before it: the final ligature
        Assert.Equal("a1 b", ArabicShaper.Shape("a1 b"));                       // other text passes through
    }

    [Fact]
    public void A_line_is_put_in_visual_order_with_numbers_and_latin_words_kept_readable()
    {
        var visual = ArabicShaper.Prepare("مرحبا Windows 11");
        Assert.StartsWith("11 Windows ", visual);                               // right-to-left paragraph: the Latin run moves to the left side
        Assert.EndsWith("ﺎﺒﺣﺮﻣ", visual);               // the Arabic word is reversed into its visual order
        Assert.Equal("Hello", ArabicShaper.Prepare("Hello"));                   // no Arabic: untouched
    }

    [Fact]
    public void Brackets_are_mirrored_inside_right_to_left_text()
    {
        // logical "أ (ب)": drawn left to right it must read "(ب) أ" so the brackets still enclose the letter
        Assert.Equal("(ﺏ) ﺃ", ArabicShaper.ToVisual(ArabicShaper.Shape("أ (ب)")));
    }
}
