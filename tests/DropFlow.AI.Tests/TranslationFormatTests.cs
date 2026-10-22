using DropFlow.AI.Translation;
using DropFlow.AI.Translation.Formats;
using Xunit;

namespace DropFlow.AI.Tests;

/// <summary>
/// The file-format layer: what is cut out for translation and what is kept. The key property is that rendering a file with its own
/// strings (an "identity translation") gives the same file back, so nothing but the words can ever change.
/// </summary>
public class TranslationFormatTests
{
    private static string Identity(TranslationTemplate t) => t.Render([.. t.Units]);

    /// <summary>Replaces every unit by "[n:unit]" so the output shows exactly which strings were sent.</summary>
    private static string Marked(TranslationTemplate t) => t.Render([.. t.Units.Select((u, i) => $"[{i}:{u}]")]);

    // ---- SRT and VTT -------------------------------------------------------------------------------------------------

    private const string Srt =
        "1\n00:00:01,000 --> 00:00:03,500\nHello there\n\n" +
        "2\n00:00:04,000 --> 00:00:07,200\nFirst line of the cue\nsecond line of the cue\n\n" +
        "3\n00:00:08,000 --> 00:00:09,800\n<i>Italic cue</i>\n\n" +
        "4\n00:00:10,000 --> 00:00:12,500\n- Where are you going?\n- To the station.\n\n" +
        "5\n00:00:13,000 --> 00:00:15,000\n{\\an8}Top cue\n\n" +
        "6\n00:00:16,000 --> 00:00:18,000\nSome <b>bold</b> word in the middle\n";

    [Fact]
    public void Subtitle_files_survive_an_identity_translation_byte_for_byte()
    {
        Assert.Equal(Srt, Identity(SubtitleParser.Parse(Srt)));
        Assert.Equal(Srt.TrimEnd('\n'), Identity(SubtitleParser.Parse(Srt.TrimEnd('\n'))));      // no final line break
        Assert.Equal(Srt + "\n\n", Identity(SubtitleParser.Parse(Srt + "\n\n")));                // extra blank lines at the end
    }

    [Fact]
    public void Only_the_words_of_each_cue_are_sent_for_translation()
    {
        var t = SubtitleParser.Parse(Srt);
        Assert.Equal(
        [
            "Hello there",
            "First line of the cue second line of the cue",      // the lines of a cue are one sentence
            "Italic cue",                                          // the tags around it are kept
            "Where are you going?", "To the station.",             // two speakers: one unit per line
            "Top cue",                                             // {\an8} is kept
            "Some ", "bold", " word in the middle",                // markup inside the text splits it
        ], t.Units);
    }

    [Fact]
    public void Numbers_timings_and_tags_are_never_touched_and_cue_line_counts_are_kept()
    {
        var output = Marked(SubtitleParser.Parse(Srt));
        foreach (var line in new[] { "1\n00:00:01,000 --> 00:00:03,500", "00:00:04,000 --> 00:00:07,200", "<i>[2:Italic cue]</i>", "{\\an8}[5:Top cue]", "- [3:Where are you going?]\n- [4:To the station.]" })
            Assert.Contains(line, output);

        // the two-line cue stays two lines
        var cue2 = output.Split("\n\n")[1].Split('\n');
        Assert.Equal(4, cue2.Length);                              // number, timing, two text lines
        Assert.Equal("[1:First line of the cue second line of the cue]", string.Join(' ', cue2.Skip(2)));
    }

    [Fact]
    public void A_translation_longer_or_shorter_than_the_cue_is_spread_over_the_same_number_of_lines()
    {
        var t = SubtitleParser.Parse("1\n00:00:01,000 --> 00:00:03,000\nline one\nline two\n");
        var output = t.Render(["I was thinking we could go to the market before it gets too crowded"]);
        var text = output.Split('\n').Skip(2).Where(l => l.Length > 0).ToList();
        Assert.Equal(2, text.Count);
        Assert.Equal("I was thinking we could go to the market before it gets too crowded", string.Join(' ', text));
    }

    [Fact]
    public void WebVtt_headers_notes_styles_and_cue_identifiers_are_kept()
    {
        const string vtt = "WEBVTT - a title\n\nNOTE this is a comment\nwith two lines\n\nSTYLE\n::cue { color: red }\n\nintro\n00:00:01.000 --> 00:00:03.000 align:start position:0%\n<v Alice>Hello there</v>\n\n00:00:04.000 --> 00:00:05.000\nBye\n";
        var t = SubtitleParser.Parse(vtt);
        Assert.Equal(vtt, Identity(t));
        Assert.Equal(["Hello there", "Bye"], t.Units);
        var output = Marked(t);
        Assert.Contains("WEBVTT - a title", output);
        Assert.Contains("NOTE this is a comment\nwith two lines", output);
        Assert.Contains("::cue { color: red }", output);
        Assert.Contains("intro\n00:00:01.000 --> 00:00:03.000 align:start position:0%\n<v Alice>[0:Hello there]</v>", output);
    }

    // ---- Markdown ---------------------------------------------------------------------------------------------------------

    private const string Markdown =
        "---\ntitle: A guide\ntags: [a, b]\n---\n\n" +
        "# Getting started\n\n" +
        "Plain text with **bold words**, `inline code` and a [link label](https://example.com/page \"Title\").\n\n" +
        "1. First step\n2. Second step\n\n" +
        "- Item one\n  - Nested item\n\n" +
        "> A quote line\n\n" +
        "```bash\ndotnet run --project x\n```\n\n" +
        "| Name | Value |\n|---|---|\n| Alpha | One |\n\n" +
        "![Alt text](images/pic.png)\n\n" +
        "Setext heading\n==============\n\n" +
        "[ref]: https://example.com/ref\n\n" +
        "Visit https://example.com/x for more. Ends with a break.  \nNext line.\n";

    [Fact]
    public void Markdown_files_survive_an_identity_translation_byte_for_byte()
    {
        Assert.Equal(Markdown, Identity(MarkdownParser.Parse(Markdown)));
    }

    [Fact]
    public void Markdown_syntax_code_and_links_are_not_sent_for_translation()
    {
        var units = MarkdownParser.Parse(Markdown).Units;
        // words that ARE translated
        foreach (var expected in new[] { "Getting started", "Plain text with ", "bold words", "link label", "First step", "Item one", "A quote line", "Alt text", "Alpha", "Setext heading" })
            Assert.Contains(units, u => u.Contains(expected));
        // syntax that must not be
        foreach (var text in units)
        {
            Assert.DoesNotContain("dotnet run", text);                  // code block
            Assert.DoesNotContain("inline code", text);                 // code span
            Assert.DoesNotContain("example.com", text);                 // link targets and addresses
            Assert.DoesNotContain("title: A guide", text);              // front matter
            Assert.DoesNotContain("images/pic.png", text);
            Assert.DoesNotContain("---", text);
            Assert.DoesNotContain("====", text);
            Assert.DoesNotContain("**", text);
        }
    }

    [Fact]
    public void Markdown_markers_stay_around_the_marked_translations()
    {
        var output = Marked(MarkdownParser.Parse("# Title\n\n- item\n\n> quote\n\n1. one\n"));
        Assert.Equal("# [0:Title]\n\n- [1:item]\n\n> [2:quote]\n\n1. [3:one]\n", output);
    }

    [Fact]
    public void A_soft_wrapped_markdown_paragraph_is_one_unit_and_a_hard_break_is_kept()
    {
        var t = MarkdownParser.Parse("First line of text\ncontinues here.\n\nHard  \nbreak\n");
        Assert.Equal(["First line of text continues here.", "Hard", "break"], t.Units);
        Assert.Equal("[0:First line of text continues here.]\n\n[1:Hard]  \n[2:break]\n", Marked(t));
    }

    [Fact]
    public void Fenced_code_with_markdown_like_content_is_left_alone_even_when_unclosed()
    {
        var t = MarkdownParser.Parse("Intro\n\n~~~\n# not a heading\n- not a list\n");
        Assert.Equal(["Intro"], t.Units);
    }

    [Fact]
    public void Table_structure_and_cell_text_are_separated()
    {
        var t = MarkdownParser.Parse("| Feature | Description |\n|---|---|\n| Convert | Change the format |\n");
        Assert.Equal(["Feature", "Description", "Convert", "Change the format"], t.Units.Select(u => u.Trim()));
        Assert.Contains("|---|---|", Marked(t));
    }

    // ---- HTML ------------------------------------------------------------------------------------------------------------

    private const string Html =
        "<!DOCTYPE html>\n<html lang=\"en\">\n<head>\n<meta charset=\"windows-1252\">\n<title>Our company</title>\n<style>p { color: red }</style>\n</head>\n<body>\n" +
        "<h1>About us</h1>\n<p>We build <b>simple</b> tools &amp; more. Contact <a href=\"https://example.com?a=1&amp;b=2\">the team</a>.</p>\n" +
        "<pre>do_not_translate(this)</pre>\n<code>also_code()</code>\n<script>var s = \"Hello world\";</script>\n<!-- a comment -->\n</body>\n</html>\n";

    [Fact]
    public void Html_text_is_translated_and_tags_attributes_scripts_and_code_are_not()
    {
        var t = HtmlParser.Parse(Html);
        Assert.Equal(["Our company", "About us", "We build", "simple", "tools & more. Contact", "the team", "."], t.Units);
        var output = Marked(t);
        Assert.Contains("<a href=\"https://example.com?a=1&amp;b=2\">[5:the team]</a>", output);
        Assert.Contains("<pre>do_not_translate(this)</pre>", output);
        Assert.Contains("<code>also_code()</code>", output);
        Assert.Contains("<script>var s = \"Hello world\";</script>", output);
        Assert.Contains("<style>p { color: red }</style>", output);
        Assert.Contains("<!-- a comment -->", output);
        Assert.Contains("[4:tools &amp; more. Contact]", output);                 // the ampersand of a translation is escaped again
    }

    [Fact]
    public void An_html_document_gets_the_target_language_direction_and_utf8()
    {
        var toArabic = HtmlParser.FixDocument(Html, TranslationDirection.EnglishToArabic);
        Assert.Contains("<html lang=\"ar\" dir=\"rtl\">", toArabic);
        Assert.Contains("<meta charset=\"utf-8\">", toArabic);
        var toEnglish = HtmlParser.FixDocument("<html lang=\"ar\" dir=\"rtl\" class=\"x\">", TranslationDirection.ArabicToEnglish);
        Assert.Equal("<html class=\"x\" lang=\"en\" dir=\"ltr\">", toEnglish);
    }

    // ---- plain text ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void Plain_text_keeps_blank_lines_indentation_and_list_markers()
    {
        const string text = "Title line\n\n  indented line\n- first\n* second\n1) numbered\n\n\nlast line\n";
        var t = PlainTextParser.Parse(text);
        Assert.Equal(text, Identity(t));
        Assert.Equal(["Title line", "indented line", "first", "second", "numbered", "last line"], t.Units);
    }

    [Fact]
    public void A_hard_wrapped_paragraph_is_joined_for_translation_and_wrapped_again_to_the_same_width()
    {
        const string text = "We are happy to announce that our new store will open next\nMonday at nine in the morning. The store has a large selection\nof books and games for children of all ages.\n\nShort line\nanother short line\n";
        var t = PlainTextParser.Parse(text);
        Assert.Equal(text, Identity(t));
        Assert.Equal(
        [
            "We are happy to announce that our new store will open next Monday at nine in the morning. The store has a large selection of books and games for children of all ages.",
            "Short line", "another short line",                      // not wrapped text: kept line by line
        ], t.Units);

        var wrapped = t.Render(["word " + string.Join(' ', Enumerable.Repeat("translated", 30)), "x", "y"]);
        var lines = wrapped.Split('\n').TakeWhile(l => l.Length > 0).ToList();
        Assert.True(lines.Count > 2);
        Assert.All(lines, l => Assert.True(l.Length <= 62, l));
    }
}
