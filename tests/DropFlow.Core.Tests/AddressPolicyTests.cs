using System.Net;
using DropFlow.Core.Operations;
using DropFlow.Core.Queue;
using DropFlow.Core.Registry;
using DropFlow.Core.Sources;

namespace DropFlow.Core.Tests;

public class AddressPolicyTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.1.2.3")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.1.1")]
    [InlineData("169.254.169.254")]   // cloud metadata
    [InlineData("100.64.0.1")]        // carrier-grade NAT
    [InlineData("0.0.0.0")]
    [InlineData("224.0.0.1")]
    [InlineData("255.255.255.255")]
    [InlineData("::1")]
    [InlineData("fe80::1")]
    [InlineData("fd00::1")]
    [InlineData("::ffff:192.168.1.1")] // IPv4 mapped
    [InlineData("64:ff9b::a00:1")]     // NAT64 of 10.0.0.1
    [InlineData("2002:c0a8:0101::1")]  // 6to4 of 192.168.1.1
    public void Local_and_private_addresses_are_refused(string address) =>
        Assert.False(AddressPolicy.IsPublic(IPAddress.Parse(address)));

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("172.32.0.1")]
    [InlineData("1.1.1.1")]
    [InlineData("2606:4700:4700::1111")]
    [InlineData("64:ff9b::808:808")]   // NAT64 of 8.8.8.8
    public void Public_addresses_are_allowed(string address) =>
        Assert.True(AddressPolicy.IsPublic(IPAddress.Parse(address)));

    [Fact]
    public async Task The_default_downloader_refuses_a_link_to_this_computer()
    {
        using var dir = new TempDir();
        using var downloader = new UrlDownloader(dir.Path);
        var ex = await Assert.ThrowsAsync<UrlDownloadException>(() => downloader.DownloadAsync("http://127.0.0.1:9/secret.txt"));
        Assert.Equal(UrlDownloadError.Blocked, ex.Kind);
        Assert.Empty(Directory.GetFiles(dir.Path));
    }

    [Fact]
    public async Task A_conversion_of_a_downloaded_file_keeps_the_mark_of_the_web()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var dir = new TempDir();
        var input = dir.File("from-web.png");
        MarkOfTheWeb.Write(input, new Uri("https://example.com/a.png?token=secret"));
        Assert.Contains("ZoneId=3", MarkOfTheWeb.Read(input));
        Assert.DoesNotContain("secret", MarkOfTheWeb.Read(input));

        var output = Path.Combine(dir.Path, "from-web (converted).jpg");
        var registry = new ToolRegistry().Register(new FakeConverter(["convert:jpg"], run: (_, _, _) => { File.WriteAllText(output, "x"); return Task.FromResult(ConversionResult.Ok([output])); }));
        var queue = new JobQueue(registry, Catalog.ToolCatalog.LoadEmbedded(), 1);
        await queue.Enqueue(new ConversionJob([input], "convert:jpg")).Completion;

        Assert.Contains("ZoneId=3", MarkOfTheWeb.Read(output));
    }
}
