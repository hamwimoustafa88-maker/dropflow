using DropFlow.Core.Localization;
using DropFlow.Core.Operations;
using DropFlow.Core.Catalog;

namespace DropFlow.Core.Tests;

public class LocalizationTests
{
    private static readonly IReadOnlyDictionary<string, string> En = JsonLocalizer.ReadEmbedded("en");
    private static readonly IReadOnlyDictionary<string, string> Ar = JsonLocalizer.ReadEmbedded("ar");

    [Fact]
    public void Arabic_and_English_have_exactly_the_same_keys()
    {
        Assert.Empty(En.Keys.Except(Ar.Keys));
        Assert.Empty(Ar.Keys.Except(En.Keys));
    }

    [Fact]
    public void No_translation_is_empty_and_placeholders_match_between_languages()
    {
        foreach (var (key, en) in En)
        {
            Assert.False(string.IsNullOrWhiteSpace(en), key);
            Assert.False(string.IsNullOrWhiteSpace(Ar[key]), key);
            var enHoles = System.Text.RegularExpressions.Regex.Matches(en, @"\{\d+\}").Select(m => m.Value).OrderBy(x => x);
            var arHoles = System.Text.RegularExpressions.Regex.Matches(Ar[key], @"\{\d+\}").Select(m => m.Value).OrderBy(x => x);
            Assert.Equal(enHoles, arHoles);
        }
    }

    [Fact]
    public void Arabic_strings_actually_contain_arabic_script_except_for_brand_and_technical_terms()
    {
        var exempt = new HashSet<string> { "app.name", "settings.language.en", "opt.x", "opt.y", "opt.paper.a4", "opt.paper.letter" };
        foreach (var (key, ar) in Ar.Where(kv => !exempt.Contains(kv.Key) && !kv.Key.StartsWith("engine.name.")))
            Assert.Matches(@"[؀-ۿ]", ar);
    }

    [Fact]
    public void Every_engine_has_a_display_name()
    {
        foreach (var e in Enum.GetValues<EngineId>().Where(e => e != EngineId.None))
            Assert.True(En.ContainsKey($"engine.name.{e}"), e.ToString());
    }

    [Fact]
    public void A_missing_key_returns_the_key_and_arabic_falls_back_to_english()
    {
        var onlyEnglish = new JsonLocalizer("ar", new Dictionary<string, string> { ["a"] = "ا" }, new Dictionary<string, string> { ["b"] = "bee" });
        Assert.Equal("ا", onlyEnglish["a"]);
        Assert.Equal("bee", onlyEnglish["b"]);
        Assert.Equal("zzz", onlyEnglish["zzz"]);
    }

    [Fact]
    public void Language_decides_the_reading_direction_and_Load_picks_the_right_file()
    {
        Assert.True(JsonLocalizer.Load("ar").IsRightToLeft);
        Assert.False(JsonLocalizer.Load("en").IsRightToLeft);
        Assert.Equal("إنهاء", JsonLocalizer.Load("ar")["tray.quit"]);
        Assert.Equal("Quit", JsonLocalizer.Load("en")["tray.quit"]);
        Assert.Equal("Quit", JsonLocalizer.Load("fr")["tray.quit"]);          // unknown language -> English
    }

    [Fact]
    public void Format_fills_placeholders()
    {
        var l = JsonLocalizer.Load("en");
        Assert.Equal("Install FFmpeg?", l.Format("engine.confirm.title", "FFmpeg"));
    }

    [Fact]
    public void Notes_that_are_resource_keys_are_translated_with_their_arguments()
    {
        var ar = JsonLocalizer.Load("ar");
        var en = JsonLocalizer.Load("en");
        Assert.Equal(en["note.img.alreadySmall"], NoteText.Localize("note.img.alreadySmall", en));
        Assert.Equal(ar["note.img.alreadySmall"], NoteText.Localize("note.img.alreadySmall", ar));
        Assert.Contains("412", NoteText.Localize(NoteText.Key("note.img.targetNotReached", 412), ar));
        Assert.Matches(@"[؀-ۿ]", NoteText.Localize(NoteText.Key("note.img.targetNotReached", 412), ar)!);
    }

    [Fact]
    public void Plain_notes_unknown_keys_and_joined_notes_are_handled()
    {
        var en = JsonLocalizer.Load("en");
        Assert.Null(NoteText.Localize(null, en));
        Assert.Equal("Visible stamp only.", NoteText.Localize("Visible stamp only.", en));
        Assert.Equal("note.does.not.exist", NoteText.Localize("note.does.not.exist", en));
        // the job queue joins the notes of several files with "; "
        Assert.Equal($"{en["note.img.alreadySmall"]}; plain", NoteText.Localize("note.img.alreadySmall; plain", en));
    }

    [Fact]
    public void Every_note_key_the_engines_use_exists_in_both_languages()
    {
        foreach (var key in new[] { "note.img.alreadySmall", "note.img.targetNotReached", "note.img.rotatedLossless", "note.media.noEdgeSilence" })
        {
            Assert.True(En.ContainsKey(key), key);
            Assert.True(Ar.ContainsKey(key), key);
        }
    }

    [Fact]
    public void The_resource_text_of_every_catalog_tool_exists_in_both_languages()
    {
        foreach (var tool in ToolCatalog.LoadEmbedded().Tools.Concat(ToolCatalog.LoadEmbedded().Actions))
        {
            Assert.False(string.IsNullOrWhiteSpace(tool.En), tool.Id);
            Assert.Matches(@"[؀-ۿ]", tool.Ar);
        }
    }
}
