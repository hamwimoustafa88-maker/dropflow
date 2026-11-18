using System.Text.Json;
using System.Text.RegularExpressions;
using DropFlow.Core.Cli;

namespace DropFlow.Core.Tests;

/// <summary>The website's command-line page (web/src/data/cli.json) lists exactly the commands and exit codes of the real CLI.</summary>
public class CliDocsTests
{
    private static string RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "DropFlow.sln"))) return d.FullName;
        throw new DirectoryNotFoundException("DropFlow.sln not found above the test folder.");
    }

    private static JsonElement Docs() =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "web", "src", "data", "cli.json"))).RootElement;

    [Fact]
    public void Every_command_in_the_usage_is_on_the_website_and_nothing_else()
    {
        var verbs = Regex.Matches(CliParser.Usage, @"^\s*dropflow ([a-z]+)", RegexOptions.Multiline).Select(m => m.Groups[1].Value).ToHashSet();
        var documented = Docs().GetProperty("commands").EnumerateArray().Select(c => c.GetProperty("verb").GetString()!).ToHashSet();

        Assert.Equal(verbs.Order(), documented.Order());
    }

    [Fact]
    public void Every_documented_example_parses_and_every_command_has_both_languages()
    {
        foreach (var c in Docs().GetProperty("commands").EnumerateArray())
        {
            var example = c.GetProperty("example").GetString()!;
            Assert.StartsWith("dropflow ", example);
            var args = example["dropflow ".Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (args[0] is not ("doctor" or "models")) // handled by the dropflow host before the parser
                Assert.True(CliParser.Parse(args).Error is null, $"{example}: {CliParser.Parse(args).Error}");
            Assert.False(string.IsNullOrWhiteSpace(c.GetProperty("en").GetString()));
            Assert.Matches(@"\p{IsArabic}", c.GetProperty("ar").GetString()!);
        }
    }

    [Fact]
    public void The_exit_codes_match_the_usage()
    {
        var codes = Docs().GetProperty("exitCodes").EnumerateArray().Select(e => e.GetProperty("code").GetInt32()).ToList();
        Assert.Equal([0, 1, 2, 3], codes);
        foreach (var code in codes) Assert.Contains($"{code} ", CliParser.Usage);
    }
}
