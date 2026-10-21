using System.Text;
using DropFlow.Engines.Media.Ffmpeg;

namespace DropFlow.Engines.Media.Tests.Ffmpeg;

/// <summary>
/// An independent re-implementation of how FFmpeg reads a filtergraph, ported from libavutil's <c>av_get_token</c> and the way
/// libavfilter's graph parser and <c>av_opt_set_from_string</c> use it. The production escaper is checked against this oracle,
/// so a mistake in our understanding shows up as a failing round trip rather than as a broken burn-in on a user's machine.
/// </summary>
internal static class FilterGraphOracle
{
    private const string Whitespace = " \n\t\r";

    /// <summary>av_get_token: unescape until an unescaped, unquoted terminator. Backslash escapes one char; single quotes are literal.</summary>
    public static string GetToken(string s, ref int pos, string terminators)
    {
        while (pos < s.Length && Whitespace.Contains(s[pos])) pos++;
        var sb = new StringBuilder();
        var end = 0;
        while (pos < s.Length && !terminators.Contains(s[pos]))
        {
            var c = s[pos++];
            if (c == '\\' && pos < s.Length)
            {
                sb.Append(s[pos++]);
                end = sb.Length;
            }
            else if (c == '\'')
            {
                while (pos < s.Length && s[pos] != '\'') sb.Append(s[pos++]);
                if (pos < s.Length)
                {
                    pos++;
                    end = sb.Length;
                }
            }
            else
            {
                sb.Append(c);
            }
        }
        // trailing (unescaped, unquoted) whitespace is dropped
        var result = sb.ToString();
        while (result.Length > end && Whitespace.Contains(result[^1])) result = result[..^1];
        return result;
    }

    public sealed record ParsedFilter(string Name, IReadOnlyList<(string? Key, string Value)> Options);

    /// <summary>Parses a simple chain "name=opts,name2=opts2" the way avfilter_graph_parse does, then splits each option list.</summary>
    public static IReadOnlyList<ParsedFilter> ParseChain(string chain)
    {
        var filters = new List<ParsedFilter>();
        var pos = 0;
        while (pos < chain.Length)
        {
            // Skip labels such as [in] / [out].
            while (pos < chain.Length && chain[pos] == '[')
            {
                pos = chain.IndexOf(']', pos) + 1;
            }
            var name = GetToken(chain, ref pos, "=,;[");
            string opts = "";
            if (pos < chain.Length && chain[pos] == '=')
            {
                pos++;
                opts = GetToken(chain, ref pos, "[],;"); // level 1: the filter's argument string
            }
            filters.Add(new ParsedFilter(name, SplitOptions(opts)));
            while (pos < chain.Length && chain[pos] == '[') pos = chain.IndexOf(']', pos) + 1;
            if (pos < chain.Length && chain[pos] is ',' or ';') pos++;
        }
        return filters;
    }

    /// <summary>Level 2: av_opt_set_from_string with key/value separator '=' and pair separator ':'.</summary>
    public static IReadOnlyList<(string? Key, string Value)> SplitOptions(string opts)
    {
        var result = new List<(string? Key, string Value)>();
        var pos = 0;
        while (pos < opts.Length)
        {
            var first = GetToken(opts, ref pos, "=:");
            if (pos < opts.Length && opts[pos] == '=')
            {
                pos++;
                var value = GetToken(opts, ref pos, ":");
                result.Add((first, value));
            }
            else
            {
                result.Add((null, first));
            }
            if (pos < opts.Length && opts[pos] == ':') pos++;
        }
        return result;
    }
}

public class FilterEscapingTests
{
    public static IEnumerable<object[]> NastyStrings() =>
    [
        [@"C:\Users\Moustafa\Videos\sub.srt"],
        [@"C:\Users\Moustafa's Videos\a,b;c.srt"],
        [@"D:\ملف [1] مهم = 2.srt"],
        [@"\\server\share\dir\a b.srt"],
        ["/home/user/x:y.srt"],
        ["it's:a=b\\c"],
        [" leading and trailing "],
        [""],
        ["'''"],
        ["\\'\\"],
        ["100% {done} #1 $x &y ^z ~ ` \"quoted\""],
        ["a:b:c:d"],
        ["a=b=c"],
        ["[in]x[out]"],
        ["FontName=Arial,FontSize=24,PrimaryColour=&H00FFFFFF"],
        [@"E:\تقرير 2026\الملف النهائي (نسخة 2).ass"],
        ["tab\tinside"],
    ];

    [Theory]
    [MemberData(nameof(NastyStrings))]
    public void Value_round_trips_through_both_ffmpeg_parsing_levels(string value)
    {
        var graph = $"subtitles=filename={FilterEscaping.Value(value)}:fontsdir={FilterEscaping.Value("C:/Windows/Fonts")},scale=1280:720";

        var filters = FilterGraphOracle.ParseChain(graph);

        Assert.Equal(2, filters.Count);
        Assert.Equal("subtitles", filters[0].Name);
        Assert.Equal(2, filters[0].Options.Count);
        Assert.Equal(("filename", value), filters[0].Options[0]);
        Assert.Equal(("fontsdir", "C:/Windows/Fonts"), filters[0].Options[1]);
        Assert.Equal("scale", filters[1].Name);
        Assert.Equal(2, filters[1].Options.Count);
    }

    [Theory]
    [MemberData(nameof(NastyStrings))]
    public void PathValue_turns_backslashes_into_forward_slashes_and_still_round_trips(string path)
    {
        var graph = "subtitles=filename=" + FilterEscaping.PathValue(path);
        var parsed = FilterGraphOracle.ParseChain(graph).Single().Options.Single();
        Assert.Equal(("filename", path.Replace('\\', '/')), parsed);
    }

    [Fact]
    public void A_windows_path_never_leaves_a_bare_drive_colon_or_comma_in_the_graph()
    {
        var v = FilterEscaping.PathValue(@"C:\Users\me\Videos\clip, part 1.srt");
        Assert.Equal(@"'C\:/Users/me/Videos/clip, part 1.srt'", v);
    }

    [Fact]
    public void Quotes_inside_values_are_closed_escaped_and_reopened()
    {
        Assert.Equal(@"'it'\''s'", FilterEscaping.Quote("it's"));
    }

    [Fact]
    public void Without_escaping_the_classic_path_really_does_break_the_parser()
    {
        // Documents the bug the escaper prevents: an unescaped drive colon splits the option list.
        var broken = FilterGraphOracle.ParseChain(@"subtitles=filename=C:/a/b.srt").Single().Options;
        Assert.Equal(2, broken.Count);
        Assert.Equal(("filename", "C"), broken[0]);
    }

    [Fact]
    public void Random_strings_round_trip()
    {
        // Property test over the characters that matter, deterministic seed.
        var rng = new Random(20261021);
        const string alphabet = "ab :=,;'\\[]\"%{}\t-._ملف()&#";
        for (var i = 0; i < 3000; i++)
        {
            var len = rng.Next(0, 24);
            var sb = new StringBuilder();
            for (var k = 0; k < len; k++) sb.Append(alphabet[rng.Next(alphabet.Length)]);
            var value = sb.ToString();
            var other = new string(Enumerable.Range(0, rng.Next(0, 8)).Select(_ => alphabet[rng.Next(alphabet.Length)]).ToArray());

            var graph = $"f=a={FilterEscaping.Value(value)}:b={FilterEscaping.Value(other)},g=1";
            var filters = FilterGraphOracle.ParseChain(graph);

            Assert.True(filters.Count == 2, $"[{value}] [{other}] -> {graph}");
            Assert.Equal(("a", value), filters[0].Options[0]);
            Assert.Equal(("b", other), filters[0].Options[1]);
        }
    }

    [Fact]
    public void Expressions_keep_their_commas_inside_quotes()
    {
        var graph = "volume=volume=" + FilterEscaping.Expression("1-between(t,1.5,2.3)") + ":eval=frame";
        var f = FilterGraphOracle.ParseChain(graph).Single();
        Assert.Equal(("volume", "1-between(t,1.5,2.3)"), f.Options[0]);
        Assert.Equal(("eval", "frame"), f.Options[1]);
    }

    [Theory]
    [InlineData("a:b")]
    [InlineData("it's")]
    [InlineData("x=y")]
    [InlineData("a\\b")]
    public void Expressions_with_characters_that_need_escaping_are_refused(string expression)
    {
        Assert.Throws<ArgumentException>(() => FilterEscaping.Expression(expression));
    }

    [Fact]
    public void Subtitles_filter_survives_a_hostile_windows_path_and_fonts_dir()
    {
        const string file = @"C:\Users\Moustafa's Videos\عطلة, الصيف [نهائي]; v2=final.srt";
        const string fonts = @"C:\Windows\Fonts";
        var filter = ToolFilters.Subtitles(file, fonts, "FontName=Arial,FontSize=24,Alignment=2");

        var parsed = FilterGraphOracle.ParseChain(filter).Single();
        Assert.Equal("subtitles", parsed.Name);
        Assert.Equal(3, parsed.Options.Count);
        Assert.Equal(("filename", file.Replace('\\', '/')), parsed.Options[0]);
        Assert.Equal(("fontsdir", "C:/Windows/Fonts"), parsed.Options[1]);
        Assert.Equal(("force_style", "FontName=Arial,FontSize=24,Alignment=2"), parsed.Options[2]);
    }

    [Fact]
    public void Subtitles_filter_can_be_chained_after_other_filters()
    {
        var chain = "scale=-2:720," + ToolFilters.Subtitles(@"C:\a b\c.srt") + ",format=yuv420p";
        var filters = FilterGraphOracle.ParseChain(chain);
        Assert.Equal(["scale", "subtitles", "format"], filters.Select(f => f.Name));
        Assert.Equal(("filename", "C:/a b/c.srt"), filters[1].Options.Single());
    }

    [Fact]
    public void Burn_style_uses_numpad_alignment()
    {
        Assert.Contains("Alignment=2", ToolFilters.BurnStyle(24, "bottom"));
        Assert.Contains("Alignment=8", ToolFilters.BurnStyle(24, "top"));
        Assert.Contains("Alignment=5", ToolFilters.BurnStyle(24, "middle"));
        Assert.Contains("FontSize=36", ToolFilters.BurnStyle(36, "bottom"));
    }

    [Theory]
    [InlineData("100% done", "100%% done")]
    [InlineData("plain", "plain")]
    [InlineData("%d", "%%d")]
    public void Percent_signs_are_doubled_for_output_patterns(string literal, string expected)
    {
        Assert.Equal(expected, FfmpegCommand.EscapePattern(literal));
    }
}
