using DropFlow.Core.Cli;

namespace DropFlow.Core.Tests;

public class CliParserTests
{
    private static CliCommand Ok(params string[] args)
    {
        var r = CliParser.Parse(args);
        Assert.Null(r.Error);
        return r.Command!;
    }

    private static string Err(params string[] args)
    {
        var r = CliParser.Parse(args);
        Assert.Null(r.Command);
        return r.Error!;
    }

    [Fact]
    public void No_arguments_shows_the_usage() => Assert.Equal(CliKind.Help, Ok().Kind);

    [Theory]
    [InlineData("help")]
    [InlineData("--help")]
    [InlineData("-h")]
    public void Help_flags_show_the_usage(string flag) => Assert.Equal(CliKind.Help, Ok(flag).Kind);

    [Fact]
    public void Convert_takes_files_a_target_options_and_an_output_folder()
    {
        var c = Ok("convert", "a.heic", @"C:\p ics\b.heic", "--to", "JPEG", "--opt", "quality=80", "--opt", "stripMetadata=true", "--out", @"D:\out", "--json");

        Assert.Equal(CliKind.Convert, c.Kind);
        Assert.Equal("convert:jpg", c.Operation);                  // JPEG normalised to jpg
        Assert.Equal(["a.heic", @"C:\p ics\b.heic"], c.Files);
        Assert.Equal(80d, c.Options!["quality"]);
        Assert.Equal(true, c.Options["stripMetadata"]);
        Assert.Equal(@"D:\out", c.OutputDirectory);
        Assert.True(c.Json);
    }

    [Fact]
    public void Run_takes_a_tool_id_first()
    {
        var c = Ok("run", "pdf.merge", "a.pdf", "b.pdf");
        Assert.Equal(CliKind.Run, c.Kind);
        Assert.Equal("pdf.merge", c.Operation);
        Assert.Equal(2, c.Files!.Count);
    }

    [Fact]
    public void Recipes_can_be_listed_and_run()
    {
        Assert.Equal(CliKind.RecipeList, Ok("recipe", "list").Kind);
        var c = Ok("recipe", "run", "web-photo", "p.jpg");
        Assert.Equal("web-photo", c.Recipe);
        Assert.Equal(CliKind.RecipeRun, c.Kind);
    }

    [Fact]
    public void List_and_engines_accept_their_flags()
    {
        Assert.Equal("pdf", Ok("list", "--category", "pdf").Category);
        Assert.True(Ok("engines", "--json").Json);
    }

    [Theory]
    [InlineData("convert", "a.png")]                      // missing --to
    [InlineData("convert", "--to", "png")]                // missing files
    [InlineData("run")]
    [InlineData("run", "img.crop")]                       // tool but no files
    [InlineData("recipe")]
    [InlineData("recipe", "run", "x")]
    [InlineData("bogus")]
    [InlineData("convert", "a.png", "--to", "jpg", "--opt", "novalue")]
    [InlineData("convert", "a.png", "--to")]
    [InlineData("convert", "a.png", "--to", "jpg", "--surprise")]
    [InlineData("list", "extra")]
    public void Bad_command_lines_give_a_clear_error_not_a_crash(params string[] args) => Assert.False(string.IsNullOrWhiteSpace(Err(args)));

    [Fact]
    public void Option_values_are_typed()
    {
        Assert.Equal(80d, CliParser.ParseValue("80"));
        Assert.Equal(-1.5, CliParser.ParseValue("-1.5"));
        Assert.Equal(true, CliParser.ParseValue("true"));
        Assert.Equal(false, CliParser.ParseValue("False"));
        Assert.Equal("fast", CliParser.ParseValue("fast"));
        Assert.Equal("#ff0000", CliParser.ParseValue("#ff0000"));
    }
}
