using DropFlow.Core.Sources;

namespace DropFlow.Core.Tests;

public class DropFormatsTests
{
    [Fact]
    public void A_picture_dragged_from_a_page_means_the_picture_not_the_page()
    {
        var html = "Version:0.9\r\nSourceURL:https://news.example/story\r\n<html><body><!--StartFragment--><img src=\"https://cdn.example/photos/cat.webp?w=800&amp;q=80\" alt=x><!--EndFragment--></body></html>";
        var uri = DropFormats.LinkFrom(html: html, urlW: "https://news.example/story");
        Assert.Equal("https://cdn.example/photos/cat.webp?w=800&q=80", uri!.AbsoluteUri);
        Assert.Equal("cat.webp", DropFormats.PlaceholderName(uri));
    }

    [Fact]
    public void Firefox_uri_lists_skip_comments_and_moz_urls_take_the_first_line()
    {
        Assert.Equal("https://a.example/f.pdf", DropFormats.LinkFrom(uriList: "# a comment\r\nhttps://a.example/f.pdf\r\n")!.AbsoluteUri);
        Assert.Equal("https://b.example/clip.mp4", DropFormats.LinkFrom(mozUrl: "https://b.example/clip.mp4\nClip title")!.AbsoluteUri);
    }

    [Theory]
    [InlineData("file:///C:/Windows/win.ini")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ftp://x.example/a.zip")]
    [InlineData("https://user:pass@x.example/a.zip")]
    [InlineData("just some text")]
    public void Anything_that_is_not_a_plain_web_address_is_ignored(string text) =>
        Assert.Null(DropFormats.LinkFrom(text: text, urlW: text, uriList: text));

    [Theory]
    [InlineData("https://x.example/reports/Q3%20results.pdf", "Q3 results.pdf")]
    [InlineData("https://x.example/", "download")]
    [InlineData("https://x.example/watch?v=1", "download")]
    [InlineData("https://x.example/a/..%2F..%2Fevil.exe", "download")]
    public void The_placeholder_name_comes_from_the_path_and_is_cleaned(string url, string expected)
    {
        var name = DropFormats.PlaceholderName(new Uri(url));
        if (expected == "download") Assert.True(name == "download" || !name.Contains('/') && !name.Contains(Path.DirectorySeparatorChar), name);
        else Assert.Equal(expected, name);
    }
}
