using System.Text;
using DropFlow.Core.Operations;
using DropFlow.Engines.Archive.General;
using DropFlow.TestSupport;

namespace DropFlow.Engines.Archive.Tests;

public class Base64Tests
{
    private static Base64Converter Converter => new();

    [Fact]
    public void Supports_every_file_and_exposes_mode_and_width_options()
    {
        Assert.True(Converter.Supports("gen.base64", "png"));
        Assert.True(Converter.Supports("gen.base64", ""));
        Assert.False(Converter.Supports("gen.hash", "png"));

        var options = Converter.OptionsFor("gen.base64");
        var mode = Assert.Single(options, o => o.Key == "mode");
        Assert.Equal(["auto", "encode", "decode"], mode.Choices);
        Assert.Contains(options, o => o.Key == "lineWidth");
    }

    [Fact]
    public async Task Encodes_a_file_to_name_base64_txt()
    {
        using var temp = new TempDir();
        var file = temp.Combine("man.bin");
        File.WriteAllBytes(file, Kit.Bytes("Man"));

        var result = await Kit.Run(Converter, "gen.base64", file, new() { ["mode"] = "encode" });

        Assert.True(result.Success, result.Error);
        var output = Assert.Single(result.Outputs);
        Assert.Equal("man (base64).txt", Path.GetFileName(output));
        Assert.Equal("TWFu", File.ReadAllText(output)); // RFC 4648 test vector
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("f", "Zg==")]
    [InlineData("fo", "Zm8=")]
    [InlineData("foo", "Zm9v")]
    [InlineData("foob", "Zm9vYg==")]
    [InlineData("fooba", "Zm9vYmE=")]
    [InlineData("foobar", "Zm9vYmFy")]
    public async Task Matches_the_rfc4648_vectors(string plain, string encoded)
    {
        using var temp = new TempDir();
        var file = temp.Combine("v.bin");
        File.WriteAllBytes(file, Kit.Bytes(plain));

        var result = await Kit.Run(Converter, "gen.base64", file, new() { ["mode"] = "encode" });

        Assert.Equal(encoded, File.ReadAllText(Assert.Single(result.Outputs)));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(255)]
    [InlineData(49151)]   // one byte short of a full 3*16384 block
    [InlineData(49152)]   // exactly one block
    [InlineData(49153)]   // one byte over
    [InlineData(150_001)]
    public async Task Round_trips_binary_data_of_every_awkward_length(int length)
    {
        using var temp = new TempDir();
        var data = Kit.Noise(length, seed: length);
        var file = temp.Combine("blob.dat");
        File.WriteAllBytes(file, data);

        var encoded = await Kit.Run(Converter, "gen.base64", file, new() { ["mode"] = "encode" });
        Assert.True(encoded.Success, encoded.Error);
        var text = File.ReadAllText(Assert.Single(encoded.Outputs));
        Assert.Equal(Convert.ToBase64String(data), text); // same as the framework's encoder

        var decoded = await Kit.Run(Converter, "gen.base64", Assert.Single(encoded.Outputs), new() { ["mode"] = "decode" });
        Assert.True(decoded.Success, decoded.Error);
        Assert.Equal(data, File.ReadAllBytes(Assert.Single(decoded.Outputs)));
    }

    [Fact]
    public async Task Line_width_wraps_the_output_and_decoding_ignores_the_line_breaks()
    {
        using var temp = new TempDir();
        var data = Kit.Noise(1000);
        var file = temp.Combine("blob.dat");
        File.WriteAllBytes(file, data);

        var encoded = await Kit.Run(Converter, "gen.base64", file, new() { ["mode"] = "encode", ["lineWidth"] = 76 });
        var path = Assert.Single(encoded.Outputs);
        var lines = File.ReadAllText(path).Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.All(lines[..^1], l => Assert.Equal(76, l.Length));
        Assert.InRange(lines[^1].Length, 1, 76);
        Assert.Equal(Convert.ToBase64String(data), string.Concat(lines));

        var decoded = await Kit.Run(Converter, "gen.base64", path, new() { ["mode"] = "decode" });
        Assert.Equal(data, File.ReadAllBytes(Assert.Single(decoded.Outputs)));
    }

    [Fact]
    public async Task Auto_mode_decodes_its_own_output_and_encodes_ordinary_files()
    {
        using var temp = new TempDir();
        var data = Kit.Noise(500);
        var file = temp.Combine("blob.dat");
        File.WriteAllBytes(file, data);

        var first = await Kit.Run(Converter, "gen.base64", file);                     // default mode: auto
        var encodedPath = Assert.Single(first.Outputs);
        Assert.EndsWith("(base64).txt", encodedPath);                                  // a .dat file is encoded

        var second = await Kit.Run(Converter, "gen.base64", encodedPath);              // a .txt full of base64 is decoded
        Assert.Equal(data, File.ReadAllBytes(Assert.Single(second.Outputs)));
    }

    [Fact]
    public async Task Auto_mode_encodes_an_ordinary_text_file_instead_of_decoding_it()
    {
        using var temp = new TempDir();
        var prose = temp.WriteText("notes.txt", "This is just a sentence, not Base64!");
        var word = temp.WriteText("word.txt", "hello");            // letters only: could technically decode, must not
        var longWord = temp.WriteText("long.txt", "internationalization");

        foreach (var file in new[] { prose, word, longWord })
        {
            var result = await Kit.Run(Converter, "gen.base64", file);
            Assert.True(result.Success, result.Error);
            Assert.EndsWith("(base64).txt", Assert.Single(result.Outputs));
            Assert.Equal(Convert.ToBase64String(File.ReadAllBytes(file)), File.ReadAllText(result.Outputs[0]));
        }
    }

    [Fact]
    public async Task Auto_mode_decodes_b64_files_and_distinctive_txt_files()
    {
        using var temp = new TempDir();
        var payload = Kit.Bytes("secret message: 12345");
        var b64 = temp.WriteText("msg.b64", Convert.ToBase64String(payload));
        var txt = temp.WriteText("other.txt", Convert.ToBase64String(payload));        // has digits and '=' so it clearly is Base64

        foreach (var file in new[] { b64, txt })
        {
            var result = await Kit.Run(Converter, "gen.base64", file);
            Assert.True(result.Success, result.Error);
            var output = Assert.Single(result.Outputs);
            Assert.EndsWith("(decoded).txt", output);                                    // recognised as text
            Assert.Equal(payload, File.ReadAllBytes(output));
        }
    }

    [Fact]
    public async Task Decode_mode_forces_decoding_of_any_extension()
    {
        using var temp = new TempDir();
        var file = temp.WriteText("weird.dat", "aGVsbG8gd29ybGQ=");

        var result = await Kit.Run(Converter, "gen.base64", file, new() { ["mode"] = "decode" });

        Assert.True(result.Success, result.Error);
        Assert.Equal("hello world", File.ReadAllText(Assert.Single(result.Outputs)));
    }

    [Fact]
    public async Task Decoded_data_gets_an_extension_recognised_from_its_content()
    {
        using var temp = new TempDir();
        var zipSource = Kit.NewDir(temp, "zsrc");
        File.WriteAllText(Path.Combine(zipSource, "f.txt"), "x");
        var cases = new (string Path, string Ext)[]
        {
            (Samples.Png(temp.Combine("p.png")), "png"),
            (Samples.Pdf(temp.Combine("d.pdf")), "pdf"),
            (Kit.MakeZip(temp.Combine("z.zip"), zipSource), "zip"),
            (Samples.Wav(temp.Combine("w.wav")), "wav"),
        };

        foreach (var (path, ext) in cases)
        {
            var encoded = await Kit.Run(Converter, "gen.base64", path, new() { ["mode"] = "encode" });
            var decoded = await Kit.Run(Converter, "gen.base64", Assert.Single(encoded.Outputs), new() { ["mode"] = "decode" });
            Assert.True(decoded.Success, decoded.Error);
            var output = Assert.Single(decoded.Outputs);
            Assert.EndsWith($"(decoded).{ext}", output);
            Assert.Equal(File.ReadAllBytes(path), File.ReadAllBytes(output));
        }
    }

    [Fact]
    public async Task Unrecognised_binary_becomes_bin_and_never_an_executable_extension()
    {
        using var temp = new TempDir();
        var exeLike = temp.Combine("prog.dat");
        File.WriteAllBytes(exeLike, [0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00, 0x00, 0x00, 0xFF, 0xFE, 0x01]); // "MZ" header
        var encoded = await Kit.Run(Converter, "gen.base64", exeLike, new() { ["mode"] = "encode" });

        var decoded = await Kit.Run(Converter, "gen.base64", Assert.Single(encoded.Outputs), new() { ["mode"] = "decode" });

        Assert.EndsWith("(decoded).bin", Assert.Single(decoded.Outputs));
    }

    [Fact]
    public async Task Decodes_a_data_uri_and_uses_its_media_type()
    {
        using var temp = new TempDir();
        var png = Samples.Png(temp.Combine("p.png"));
        var uri = "data:image/png;base64," + Convert.ToBase64String(File.ReadAllBytes(png));
        var file = temp.WriteText("image-uri.txt", uri);

        var result = await Kit.Run(Converter, "gen.base64", file);   // auto: the data: prefix makes it unmistakable

        Assert.True(result.Success, result.Error);
        var output = Assert.Single(result.Outputs);
        Assert.EndsWith("(decoded).png", output);
        Assert.Equal(File.ReadAllBytes(png), File.ReadAllBytes(output));
    }

    [Fact]
    public async Task Decodes_url_safe_and_unpadded_base64()
    {
        using var temp = new TempDir();
        byte[] data = [0xFB, 0xFF, 0xFE, 0x01, 0x02];                                // produces '+' and '/' in standard Base64
        var standard = Convert.ToBase64String(data);
        Assert.True(standard.Contains('+') && standard.Contains('/'), standard);       // the vector really exercises both special characters
        var urlSafe = standard.TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var file = temp.WriteText("safe.b64", urlSafe);

        var result = await Kit.Run(Converter, "gen.base64", file, new() { ["mode"] = "decode" });

        Assert.True(result.Success, result.Error);
        Assert.Equal(data, File.ReadAllBytes(Assert.Single(result.Outputs)));
    }

    [Fact]
    public async Task Decodes_utf16_text_files_with_a_byte_order_mark()
    {
        using var temp = new TempDir();
        var file = temp.Combine("utf16.txt");
        File.WriteAllText(file, Convert.ToBase64String(Kit.Bytes("arabic: " + Kit.ArabicText)), Encoding.Unicode);

        var result = await Kit.Run(Converter, "gen.base64", file, new() { ["mode"] = "decode" });

        Assert.True(result.Success, result.Error);
        Assert.Equal("arabic: " + Kit.ArabicText, File.ReadAllText(Assert.Single(result.Outputs), Encoding.UTF8));
    }

    [Theory]
    [InlineData("not base64 at all!!")]
    [InlineData("TWFu*")]
    [InlineData("TWFuA")]        // a final group of one character is impossible in Base64
    [InlineData("A")]
    [InlineData("TQ==TQ==")]     // padding in the middle
    [InlineData("TWFu=extra")]
    public async Task Invalid_base64_fails_clearly_and_leaves_no_output(string content)
    {
        using var temp = new TempDir();
        var file = temp.WriteText("bad.txt", content);

        var result = await Kit.Run(Converter, "gen.base64", file, new() { ["mode"] = "decode" });

        Assert.False(result.Success);
        Assert.Contains("Base64", result.Error);
        Assert.Equal(["bad.txt"], Directory.GetFileSystemEntries(temp.Path).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Empty_files_encode_to_an_empty_file_but_cannot_be_decoded()
    {
        using var temp = new TempDir();
        var empty = temp.Combine("empty.bin");
        File.WriteAllBytes(empty, []);

        var encoded = await Kit.Run(Converter, "gen.base64", empty, new() { ["mode"] = "encode" });
        Assert.True(encoded.Success, encoded.Error);
        Assert.Equal(0, new FileInfo(Assert.Single(encoded.Outputs)).Length);

        var decoded = await Kit.Run(Converter, "gen.base64", encoded.Outputs[0], new() { ["mode"] = "decode" });
        Assert.False(decoded.Success);
        Assert.Equal(2, Directory.GetFileSystemEntries(temp.Path).Length);
    }

    [Fact]
    public async Task Arabic_names_round_trip()
    {
        using var temp = new TempDir();
        var file = temp.WriteText($"{Kit.ArabicFile}.txt", Kit.ArabicText);

        var encoded = await Kit.Run(Converter, "gen.base64", file, new() { ["mode"] = "encode" });
        var output = Assert.Single(encoded.Outputs);
        Assert.Equal($"{Kit.ArabicFile} (base64).txt", Path.GetFileName(output));

        var decoded = await Kit.Run(Converter, "gen.base64", output);   // auto: our own output name is recognised
        Assert.Equal($"{Kit.ArabicFile} (base64) (decoded).txt", Path.GetFileName(Assert.Single(decoded.Outputs)));
        Assert.Equal(Kit.ArabicText, File.ReadAllText(decoded.Outputs[0], Encoding.UTF8));
    }

    [Fact]
    public async Task Never_overwrites_existing_files()
    {
        using var temp = new TempDir();
        var file = temp.Combine("a.bin");
        File.WriteAllBytes(file, [1, 2, 3]);
        File.WriteAllText(temp.Combine("a (base64).txt"), "PRECIOUS");

        var result = await Kit.Run(Converter, "gen.base64", file, new() { ["mode"] = "encode" });

        Assert.Equal("a (base64 2).txt", Path.GetFileName(Assert.Single(result.Outputs)));
        Assert.Equal("PRECIOUS", File.ReadAllText(temp.Combine("a (base64).txt")));
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(file));
    }

    [Fact]
    public async Task Bad_options_and_inputs_fail_clearly()
    {
        using var temp = new TempDir();
        var file = temp.WriteText("a.txt", "abc");

        var mode = await Kit.Run(Converter, "gen.base64", file, new() { ["mode"] = "scramble" });
        Assert.False(mode.Success);
        Assert.Contains("scramble", mode.Error);

        var width = await Kit.Run(Converter, "gen.base64", file, new() { ["mode"] = "encode", ["lineWidth"] = "wide" });
        Assert.False(width.Success);

        Assert.False((await Kit.Run(Converter, "gen.base64", temp.Combine("ghost.bin"))).Success);
        Assert.False((await Kit.Run(Converter, "gen.base64", Kit.NewDir(temp, "dir"))).Success);
    }

    [Fact]
    public async Task Cancellation_stops_a_big_encode_and_removes_the_partial_file()
    {
        using var temp = new TempDir();
        var file = temp.Combine("huge.bin");
        using (var fs = File.Create(file)) fs.SetLength(300L * 1024 * 1024);
        using var cts = new CancellationTokenSource();
        var recorder = new Recorder { OnReport = p => { if (p.Fraction > 0.03) cts.Cancel(); } };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Kit.Run(Converter, Kit.Job("gen.base64", [file], new() { ["mode"] = "encode" }), cts.Token, recorder));

        Assert.Equal(["huge.bin"], Directory.GetFileSystemEntries(temp.Path).Select(Path.GetFileName));
    }

    [Fact]
    public void File_type_sniffing_covers_the_safe_set()
    {
        Assert.Equal("png", FileTypeSniffer.Detect([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0]));
        Assert.Equal("jpg", FileTypeSniffer.Detect([0xFF, 0xD8, 0xFF, 0xE0]));
        Assert.Equal("gif", FileTypeSniffer.Detect(Encoding.ASCII.GetBytes("GIF89a....")));
        Assert.Equal("pdf", FileTypeSniffer.Detect(Encoding.ASCII.GetBytes("%PDF-1.7")));
        Assert.Equal("webp", FileTypeSniffer.Detect(Encoding.ASCII.GetBytes("RIFF\0\0\0\0WEBPVP8 ")));
        Assert.Equal("mp4", FileTypeSniffer.Detect([0, 0, 0, 0x18, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'i', (byte)'s', (byte)'o', (byte)'m']));
        Assert.Equal("txt", FileTypeSniffer.Detect(Kit.Bytes("plain text\nwith lines\n")));
        Assert.Equal("txt", FileTypeSniffer.Detect(Kit.Bytes(Kit.ArabicText)));
        Assert.Equal("bin", FileTypeSniffer.Detect([0x00, 0x01, 0x02, 0xFF, 0xFE]));
        Assert.Equal("bin", FileTypeSniffer.Detect([]));
        Assert.Null(FileTypeSniffer.ExtensionForMime("application/x-msdownload"));
        Assert.Equal("png", FileTypeSniffer.ExtensionForMime("image/png; charset=binary"));
    }
}
