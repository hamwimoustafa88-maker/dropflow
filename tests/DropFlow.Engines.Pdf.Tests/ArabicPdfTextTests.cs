using DropFlow.Core.Operations;
using DropFlow.Engines.Pdf.Extraction;
using DropFlow.Engines.Pdf.Rendering;
using DropFlow.TestSupport;

namespace DropFlow.Engines.Pdf.Tests;

/// <summary>
/// Defects the conversion matrix found in Arabic PDFs made by real producers (Edge, LibreOffice): shared heh/yeh glyphs read as the
/// Persian letters, a full stop jumping to the front of an English sentence inside an Arabic page, and a tanween read as "$".
/// </summary>
public class ArabicPdfTextTests
{
    [Theory]
    [InlineData("ھذه الفاتورة", "هذه الفاتورة")]          // initial heh shared with heh doachashmee
    [InlineData("الكمیة", "الكمية")]                       // medial yeh shared with Farsi yeh
    [InlineData("بھا", "بها")]                             // medial heh
    [InlineData("هذه", "هذه")]                             // already right
    [InlineData("فارسی", "فارسی")]                         // Persian final yeh: a real Persian letter, the line is left alone
    [InlineData("کتاب ھمه", "کتاب ھمه")]                   // keheh: Persian/Urdu line, left alone
    [InlineData("ھ", "ھ")]                                  // isolated: never changed
    public void Shared_heh_and_yeh_glyphs_become_arabic_letters_only_in_arabic_text(string extracted, string expected) =>
        Assert.Equal(expected, ArabicGlyphSharing.Fix(extracted));

    [Theory]
    [InlineData(".Invoice number 4821 — DropFlow converts files locally", "Invoice number 4821 — DropFlow converts files locally.")]
    [InlineData("?Is it local", "Is it local?")]
    [InlineData(". 4821 is the number", "4821 is the number.")]
    [InlineData("...", "...")]
    [InlineData("Invoice 4821.", "Invoice 4821.")]
    public void A_full_stop_drawn_on_the_left_goes_back_to_the_end(string visual, string expected) =>
        Assert.Equal(expected, PdfTextExtractor.TrailingPunctuationBack(visual));

    [ChromiumFact]
    public async Task An_arabic_page_printed_by_edge_extracts_with_arabic_letters_and_the_full_stop_at_the_end()
    {
        using var dir = new TempDir();
        var html = dir.WriteText("ar.html", """
            <!doctype html><html lang="ar" dir="rtl"><head><meta charset="utf-8"><style>body{font-family:Tahoma;font-size:16pt}</style></head>
            <body><h1>مرحبا بالعالم</h1><p>هذه الفاتورة رقم 4821. الكمية ثلاثة.</p><p>Invoice number 4821 — DropFlow converts files locally.</p></body></html>
            """);
        var pdf = dir.Combine("ar.pdf");
        Assert.Null(await new ChromiumHtmlPdfRenderer().PrintAsync(html, pdf, new HtmlPdfOptions(), new Progress<JobProgress>(), default));

        var result = await Kit.Ok(Kit.RunAsync("convert:txt", [pdf]));

        var text = File.ReadAllText(result.Outputs[0]);
        Assert.Contains("هذه الفاتورة رقم 4821", text);
        Assert.Contains("الكمية", text);
        Assert.DoesNotContain('ھ', text);
        Assert.DoesNotContain('ی', text);
        Assert.Contains("DropFlow converts files locally.", text);
        Assert.DoesNotContain(".Invoice", text);
    }
}
