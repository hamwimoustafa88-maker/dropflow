using DropFlow.Engines.Pdf.Infrastructure;

namespace DropFlow.Engines.Pdf.Tests;

public class ArabicShaperTests
{
    [Fact]
    public void Latin_text_is_unchanged()
    {
        Assert.Equal("Page 1 of 5", ArabicShaper.ToVisual("Page 1 of 5"));
    }

    [Fact]
    public void A_connected_word_is_shaped_into_joining_forms_and_reversed_for_left_to_right_drawing()
    {
        // marhaba: meem(initial) reh(final; reh never connects forward) hah(initial) beh(medial) alef(final) in logical order;
        // the visual order drawn left to right is the reverse.
        var visual = ArabicShaper.ToVisual("مرحبا");
        Assert.Equal("ﺎﺒﺣﺮﻣ", visual);
    }

    [Fact]
    public void Non_joining_letters_keep_the_following_letter_from_connecting()
    {
        // dal never connects to the next letter: dal(isolated) + beh(isolated... then alef) => logical "دب"
        var visual = ArabicShaper.ToVisual("دب");
        Assert.Equal("ﺏﺩ", visual); // beh isolated, dal isolated (visual order = reverse of logical)
    }

    [Fact]
    public void Lam_alef_becomes_a_ligature()
    {
        var visual = ArabicShaper.ToVisual("لا");
        Assert.Equal("ﻻ", visual);
    }

    [Fact]
    public void Digits_stay_left_to_right_inside_an_arabic_sentence()
    {
        // "صفحة 12" - the word is reversed, the number keeps its digit order, and the paragraph runs right to left,
        // so the number comes first when drawn left to right.
        var visual = ArabicShaper.ToVisual("صفحة 12");
        Assert.Equal("12 ﺔﺤﻔﺻ", visual); // teh marbuta(final) hah(medial) feh(medial) sad(initial)
    }

    [Fact]
    public void Diacritics_are_dropped()
    {
        Assert.Equal(ArabicShaper.ToVisual("مرحبا"), ArabicShaper.ToVisual("مَرْحَبًا"));
    }

    [Fact]
    public void Brackets_are_mirrored_in_right_to_left_text()
    {
        var visual = ArabicShaper.ToVisual("(ا)");
        Assert.Equal("(ﺍ)", visual);
    }

    [Fact]
    public void Empty_text_is_fine()
    {
        Assert.Equal("", ArabicShaper.ToVisual(""));
    }

    [Theory]
    [InlineData("Hello", false)]
    [InlineData("مرحبا", true)]
    [InlineData("Hi مرحبا", true)]
    [InlineData("12345", false)]
    public void ContainsRtl_detects_arabic_letters(string text, bool expected)
    {
        Assert.Equal(expected, ArabicShaper.ContainsRtl(text));
    }
}
