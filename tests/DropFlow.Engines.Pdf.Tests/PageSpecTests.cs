using DropFlow.Engines.Pdf.Infrastructure;

namespace DropFlow.Engines.Pdf.Tests;

public class PageSpecTests
{
    private static IReadOnlyList<PageRef> Parse(string spec, int pages = 10)
    {
        Assert.True(PageSpec.TryParse(spec, pages, out var result, out var error), error);
        return result;
    }

    private static string Pages(IReadOnlyList<PageRef> refs) => string.Join(",", refs.Select(r => r.Page));

    [Fact]
    public void Single_pages_keep_the_order_and_duplicates()
    {
        Assert.Equal("3,1,2,2", Pages(Parse("3,1,2,2")));
    }

    [Fact]
    public void Ranges_expand_inclusively()
    {
        Assert.Equal("3,4,5,1", Pages(Parse("3-5,1")));
    }

    [Fact]
    public void Descending_range_reverses_the_pages()
    {
        Assert.Equal("5,4,3", Pages(Parse("5-3")));
    }

    [Fact]
    public void Open_ended_range_runs_to_the_last_page()
    {
        Assert.Equal("8,9,10", Pages(Parse("8-", 10)));
    }

    [Fact]
    public void Keywords_last_first_odd_even_all()
    {
        Assert.Equal("10,1", Pages(Parse("last,first")));
        Assert.Equal("1,3,5", Pages(Parse("odd", 5)));
        Assert.Equal("2,4", Pages(Parse("even", 5)));
        Assert.Equal("1,2,3", Pages(Parse("all", 3)));
    }

    [Fact]
    public void Whitespace_semicolons_and_arabic_comma_separate_tokens()
    {
        Assert.Equal("1,2,5", Pages(Parse(" 1 ; 2،5 ")));
    }

    [Fact]
    public void Arabic_indic_digits_are_accepted()
    {
        Assert.Equal("2,3,4,1", Pages(Parse("٢-٤,١")));
    }

    [Fact]
    public void Rotation_suffix_is_parsed_per_token()
    {
        var refs = Parse("3r90,1,2-3r180");
        Assert.Equal([3, 1, 2, 3], refs.Select(r => r.Page));
        Assert.Equal([90, 0, 180, 180], refs.Select(r => r.Rotation));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("11")]
    [InlineData("abc")]
    [InlineData("1-99")]
    [InlineData("3r45")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(",,")]
    public void Invalid_specs_are_rejected_with_a_message(string spec)
    {
        Assert.False(PageSpec.TryParse(spec, 10, out var result, out var error));
        Assert.Empty(result);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void Blank_spec_means_every_page_when_allowed()
    {
        Assert.True(PageSpec.TryParse(null, 3, out var result, out _, allowBlank: true));
        Assert.Equal("1,2,3", Pages(result));
    }
}
