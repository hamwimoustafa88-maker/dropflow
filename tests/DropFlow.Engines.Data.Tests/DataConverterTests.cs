// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using ClosedXML.Excel;
using DropFlow.TestSupport;
using YamlDotNet.RepresentationModel;

namespace DropFlow.Engines.Data.Tests;

public sealed class DataConverterTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private const string Arabic = "علي حسن";

    // ---- Supports -------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("convert:yaml", "json", true)]
    [InlineData("convert:xml", "json", true)]
    [InlineData("convert:jsonl", "json", true)]
    [InlineData("convert:json", "yaml", true)]
    [InlineData("convert:json", "yml", true)]          // an alias is understood
    [InlineData("convert:yaml", "yml", false)]         // ... and is the same format
    [InlineData("convert:xlsx", "xml", true)]
    [InlineData("convert:csv", "vcf", true)]
    [InlineData("convert:json", "ics", true)]
    [InlineData("convert:yaml", "csv", true)]
    [InlineData("convert:json", "ndjson", true)]
    [InlineData("convert:json", "json", false)]        // never a format to itself
    [InlineData("convert:csv", "json", false)]         // owned by the spreadsheet code of the PDF engine
    [InlineData("convert:xlsx", "csv", false)]
    [InlineData("convert:html", "json", false)]
    [InlineData("convert:json", "csv", false)]
    [InlineData("convert:pdf", "json", false)]
    [InlineData("convert:yaml", "png", false)]
    [InlineData("data.to-vcard", "csv", false)]
    public void Supports_only_what_it_converts(string operation, string extension, bool expected) =>
        Assert.Equal(expected, new DataConverter().Supports(operation, extension));

    // ---- JSON / YAML ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Json_to_yaml_keeps_arabic_types_and_text_that_looks_like_a_number()
    {
        var json = _dir.WriteText("people.json",
            $$$"""{"name":"{{{Arabic}}}","age":30,"ratio":1.10,"ok":true,"none":null,"zip":"01234","code":"123","answer":"yes","tags":["أ","b"],"empty":{}}""");

        var yaml = await Kit.Convert(json, "yaml");

        var text = Kit.Read(yaml);
        Assert.Contains(Arabic, text);                    // written as it is, not escaped
        var root = Load(text);
        Assert.Equal(Arabic, Scalar(root, "name").Value);
        Assert.Equal("30", Scalar(root, "age").Value);
        Assert.Equal(YamlDotNet.Core.ScalarStyle.Plain, Scalar(root, "age").Style);
        Assert.Equal("1.10", Scalar(root, "ratio").Value);
        Assert.Equal("null", Scalar(root, "none").Value);
        Assert.NotEqual(YamlDotNet.Core.ScalarStyle.Plain, Scalar(root, "zip").Style);    // "01234" must not become a number
        Assert.NotEqual(YamlDotNet.Core.ScalarStyle.Plain, Scalar(root, "code").Style);   // nor "123"
        Assert.NotEqual(YamlDotNet.Core.ScalarStyle.Plain, Scalar(root, "answer").Style); // nor "yes" a YAML 1.1 boolean
        Assert.Equal(2, ((YamlSequenceNode)root.Children[new YamlScalarNode("tags")]).Children.Count);
    }

    [Fact]
    public async Task Yaml_to_json_expands_anchors_and_merge_keys_and_types_plain_scalars()
    {
        var yaml = _dir.WriteText("config.yml", """
            # settings
            base: &base
              retries: 3
              host: example.org
            prod:
              <<: *base
              host: prod.example.org
            flags: [true, False, ~, null]
            numbers: [0x1F, 0o17, +5, .5, 1e3]
            code: 0123
            quoted: "42"
            title: مرحبا بالعالم
            note: |
              line one
              line two
            last: end
            """);

        var json = await Kit.Convert(yaml, "json");

        using var doc = JsonDocument.Parse(File.ReadAllText(json));
        var root = doc.RootElement;
        Assert.Equal(3, root.GetProperty("prod").GetProperty("retries").GetInt32());
        Assert.Equal("prod.example.org", root.GetProperty("prod").GetProperty("host").GetString());   // own keys win over merged ones
        Assert.Equal(JsonValueKind.True, root.GetProperty("flags")[0].ValueKind);
        Assert.Equal(JsonValueKind.False, root.GetProperty("flags")[1].ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("flags")[2].ValueKind);
        Assert.Equal([31, 15, 5], root.GetProperty("numbers").EnumerateArray().Take(3).Select(e => e.GetInt32()));
        Assert.Equal(0.5, root.GetProperty("numbers")[3].GetDouble());
        Assert.Equal(1000, root.GetProperty("numbers")[4].GetDouble());
        Assert.Equal("0123", root.GetProperty("code").GetString());
        Assert.Equal("42", root.GetProperty("quoted").GetString());
        Assert.Equal("مرحبا بالعالم", root.GetProperty("title").GetString());
        Assert.Equal("line one\nline two\n", root.GetProperty("note").GetString());
        Assert.Contains("مرحبا بالعالم", File.ReadAllText(json)); // not \u escaped
    }

    [Fact]
    public async Task Json_to_yaml_to_json_round_trips()
    {
        var original = $$"""{"a":[1,2.5,-3e2],"b":{"c":"{{Arabic}}","d":"line1\nline2","e":"","f":"true","g":": colon"},"h":null,"i":false}""";
        var json = _dir.WriteText("round.json", original);

        var yaml = await Kit.Convert(json, "yaml");
        var back = await Kit.Convert(yaml, "json");

        Assert.Equal(Normalize(original), Normalize(File.ReadAllText(back)));
    }

    [Fact]
    public async Task Several_yaml_documents_become_an_array()
    {
        var yaml = _dir.WriteText("docs.yaml", "name: a\n---\nname: b\n");
        using var doc = JsonDocument.Parse(File.ReadAllText(await Kit.Convert(yaml, "json")));
        Assert.Equal(["a", "b"], doc.RootElement.EnumerateArray().Select(e => e.GetProperty("name").GetString()));
    }

    [Fact]
    public async Task A_yaml_alias_bomb_is_refused()
    {
        var sb = new StringBuilder("a: &a [x, x, x, x, x, x, x, x, x, x]\n");
        for (var i = 1; i < 9; i++) sb.Append($"{(char)('a' + i)}: &{(char)('a' + i)} [*{(char)('a' + i - 1)}, *{(char)('a' + i - 1)}, *{(char)('a' + i - 1)}, *{(char)('a' + i - 1)}, *{(char)('a' + i - 1)}, *{(char)('a' + i - 1)}, *{(char)('a' + i - 1)}, *{(char)('a' + i - 1)}, *{(char)('a' + i - 1)}, *{(char)('a' + i - 1)}]\n");
        var yaml = _dir.WriteText("bomb.yaml", sb.ToString());

        var result = await Kit.Run(new DataConverter(), "convert:json", yaml);

        Assert.False(result.Success);
        Assert.StartsWith("note.data.invalid|bomb.yaml|", result.Error);
        Assert.Equal(["bomb.yaml"], Kit.FilesIn(_dir.Path));
    }

    [Fact]
    public async Task Deep_nesting_is_refused_instead_of_crashing()
    {
        var yaml = _dir.WriteText("deep.yaml", new string('[', 5000) + new string(']', 5000));
        var xml = _dir.WriteText("deep.xml", string.Concat(Enumerable.Repeat("<a>", 5000)) + string.Concat(Enumerable.Repeat("</a>", 5000)));
        var json = _dir.WriteText("deep.json", new string('[', 5000) + new string(']', 5000));

        foreach (var (file, target) in new[] { (yaml, "json"), (xml, "json"), (json, "yaml") })
        {
            var result = await Kit.Run(new DataConverter(), "convert:" + target, file);
            Assert.False(result.Success);
            Assert.StartsWith("note.data.invalid|", result.Error);
        }
    }

    // ---- XML ------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Xml_to_json_maps_attributes_repeated_elements_and_text()
    {
        var xml = _dir.WriteText("library.xml", """
            <?xml version="1.0" encoding="utf-8"?>
            <library xmlns:x="urn:x" version="2">
              <!-- a comment -->
              <book id="1" lang="ar"><title>ألف ليلة وليلة</title><year>1704</year></book>
              <book id="2"><title lang="en">Dune</title><x:year>1965</x:year><note/></book>
              <owner>Ann</owner>
              <motto>Read <![CDATA[<more>]]></motto>
            </library>
            """);

        using var doc = JsonDocument.Parse(File.ReadAllText(await Kit.Convert(xml, "json")));

        var library = doc.RootElement.GetProperty("library");
        Assert.Equal("2", library.GetProperty("@version").GetString());
        Assert.False(library.TryGetProperty("@x", out _));                       // namespace declarations are dropped
        var books = library.GetProperty("book");
        Assert.Equal(JsonValueKind.Array, books.ValueKind);                       // repeated -> array
        Assert.Equal("1", books[0].GetProperty("@id").GetString());
        Assert.Equal("ألف ليلة وليلة", books[0].GetProperty("title").GetString());
        Assert.Equal("1704", books[0].GetProperty("year").GetString());          // XML has no types
        Assert.Equal("Dune", books[1].GetProperty("title").GetProperty("#text").GetString());
        Assert.Equal("en", books[1].GetProperty("title").GetProperty("@lang").GetString());
        Assert.Equal("1965", books[1].GetProperty("year").GetString());          // local name, prefix dropped
        Assert.Equal(JsonValueKind.Null, books[1].GetProperty("note").ValueKind); // empty element
        Assert.Equal("Ann", library.GetProperty("owner").GetString());
        Assert.Equal("Read <more>", library.GetProperty("motto").GetString());
    }

    [Fact]
    public async Task Json_to_xml_writes_attributes_text_and_repeated_elements_and_reads_back_the_same()
    {
        var json = _dir.WriteText("shop.json",
            $$$$"""{"shop":{"@city":"بيروت","item":[{"@id":"1","name":"{{{{Arabic}}}}"},{"@id":"2","name":"B & C <d>"}],"first name":"x","note":{"#text":"hi","@lang":"en"}}}""");

        var xml = await Kit.Convert(json, "xml");

        var root = XDocument.Load(xml).Root!;                                   // a different reader than the writer
        Assert.Equal("shop", root.Name.LocalName);
        Assert.Equal("بيروت", (string?)root.Attribute("city"));
        Assert.Equal(["1", "2"], root.Elements("item").Select(e => (string?)e.Attribute("id")));
        Assert.Equal(Arabic, (string?)root.Elements("item").First().Element("name"));
        Assert.Equal("B & C <d>", (string?)root.Elements("item").Last().Element("name"));
        Assert.Equal("x", (string?)root.Element("first_x0020_name"));
        Assert.Equal("hi", root.Element("note")!.Value);
        Assert.Equal("en", (string?)root.Element("note")!.Attribute("lang"));

        using var back = JsonDocument.Parse(File.ReadAllText(await Kit.Convert(xml, "json")));
        Assert.Equal(Arabic, back.RootElement.GetProperty("shop").GetProperty("item")[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task Json_without_a_single_root_key_is_wrapped_in_root_and_items()
    {
        var json = _dir.WriteText("list.json", """[1, "two", {"a": null}]""");
        var root = XDocument.Load(await Kit.Convert(json, "xml")).Root!;
        Assert.Equal("root", root.Name.LocalName);
        Assert.Equal(["1", "two", ""], root.Elements("item").Select(e => e.Value));
    }

    [Fact]
    public async Task Xml_with_a_dtd_or_an_external_entity_is_refused_and_nothing_is_read_from_disk()
    {
        var secret = _dir.WriteText("secret.txt", "TOP-SECRET");
        var xxe = _dir.WriteText("xxe.xml", $"""
            <?xml version="1.0"?>
            <!DOCTYPE r [<!ENTITY s SYSTEM "file:///{secret.Replace('\\', '/')}">]>
            <r>&s;</r>
            """);
        var bomb = _dir.WriteText("laughs.xml", """
            <?xml version="1.0"?>
            <!DOCTYPE l [<!ENTITY a "aaaaaaaaaa"><!ENTITY b "&a;&a;&a;&a;&a;&a;&a;&a;&a;&a;">]>
            <l>&b;</l>
            """);

        foreach (var file in new[] { xxe, bomb })
        {
            var result = await Kit.Run(new DataConverter(), "convert:json", file);
            Assert.False(result.Success);
            Assert.StartsWith($"note.data.invalid|{Path.GetFileName(file)}|", result.Error);
            Assert.DoesNotContain("TOP-SECRET", result.Error);
        }
        Assert.Equal(["laughs.xml", "secret.txt", "xxe.xml"], Kit.FilesIn(_dir.Path));
    }

    // ---- tables ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Jsonl_to_csv_flattens_nested_values_as_dotted_keys_in_utf8_with_bom()
    {
        var jsonl = _dir.WriteText("rows.ndjson",
            $$$"""
            {"id":1,"name":"{{{Arabic}}}","address":{"city":"بيروت","zip":"01234"},"tags":["a","b"]}

            {"id":2,"name":"Sam, Jr.","note":"say \"hi\"\nbye","address":{"city":"Paris"}}
            """);

        var csv = await Kit.Convert(jsonl, "csv");

        var bytes = File.ReadAllBytes(csv);
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
        var text = Kit.Read(csv);
        Assert.StartsWith("id,name,address.city,address.zip,tags.0,tags.1,note\r\n", text);
        Assert.Contains($"1,{Arabic},بيروت,01234,a,b,\r\n", text);
        Assert.Contains("2,\"Sam, Jr.\",Paris,,,,\"say \"\"hi\"\"\nbye\"\r\n", text);
    }

    [Fact]
    public async Task Xml_records_become_an_xlsx_sheet_with_a_header_row()
    {
        var xml = _dir.WriteText("staff.xml", $"""
            <staff><person id="7"><name>{Arabic}</name><age>41</age></person><person id="8"><name>Bo</name><age>9</age></person></staff>
            """);

        var xlsx = await Kit.Convert(xml, "xlsx");

        using var workbook = new XLWorkbook(xlsx);
        var sheet = workbook.Worksheet(1);
        Assert.Equal(["@id", "name", "age"], Enumerable.Range(1, 3).Select(c => sheet.Cell(1, c).GetString()));
        Assert.Equal(Arabic, sheet.Cell(2, 2).GetString());
        Assert.Equal("8", sheet.Cell(3, 1).GetString());
        Assert.True(sheet.RightToLeft);                    // Arabic content reads right to left
    }

    [Fact]
    public async Task Yaml_numbers_and_booleans_are_typed_cells_in_xlsx_and_long_ids_stay_text()
    {
        var yaml = _dir.WriteText("items.yaml", "- {n: 12, price: 2.5, ok: true, id: 12345678901234567890}\n- {n: 13, price: 3, ok: false, id: 1}\n");
        using var workbook = new XLWorkbook(await Kit.Convert(yaml, "xlsx"));
        var sheet = workbook.Worksheet(1);
        Assert.Equal(12, sheet.Cell(2, 1).GetDouble());
        Assert.Equal(2.5, sheet.Cell(2, 2).GetDouble());
        Assert.True(sheet.Cell(2, 3).GetBoolean());
        Assert.Equal("12345678901234567890", sheet.Cell(2, 4).GetString());
        Assert.Equal(XLDataType.Text, sheet.Cell(2, 4).DataType);
    }

    [Fact]
    public async Task Csv_to_yaml_and_xml_read_semicolons_and_type_plain_numbers()
    {
        var csv = _dir.WriteText("prices.csv", $"name;price;code\n{Arabic};12.5;007\nB;3;x\n");

        using var doc = JsonDocument.Parse(File.ReadAllText(await Kit.Convert(await Kit.Convert(csv, "yaml"), "json")));
        Assert.Equal(Arabic, doc.RootElement[0].GetProperty("name").GetString());
        Assert.Equal(12.5, doc.RootElement[0].GetProperty("price").GetDouble());
        Assert.Equal("007", doc.RootElement[0].GetProperty("code").GetString());

        var root = XDocument.Load(await Kit.Convert(csv, "xml")).Root!;
        Assert.Equal(2, root.Elements("item").Count());
    }

    [Fact]
    public async Task A_windows_1256_csv_is_read_as_arabic()
    {
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        var path = _dir.Combine("old.csv");
        File.WriteAllBytes(path, Encoding.GetEncoding(1256).GetBytes($"name,city\r\n{Arabic},بيروت\r\n"));

        using var doc = JsonDocument.Parse(File.ReadAllText(await Kit.Convert(path, "jsonl")));
        Assert.Equal(Arabic, doc.RootElement.GetProperty("name").GetString());
    }

    [Fact]
    public async Task Json_to_tsv_through_yaml_and_html_escapes_and_marks_direction()
    {
        var yaml = _dir.WriteText("t.yaml", $"- a: \"x\\ty\"\n  b: {Arabic}\n- a: <b>\n  b: z\n");

        var tsv = Kit.Lines(await Kit.Convert(yaml, "tsv"));
        Assert.Equal(["a\tb", $"\"x\ty\"\t{Arabic}", "<b>\tz"], tsv);

        var html = Kit.Read(await Kit.Convert(yaml, "html"));
        Assert.Contains("<meta charset=\"utf-8\">", html);
        Assert.Contains($"<td dir=\"auto\">{Arabic}</td>", html);
        Assert.Contains("&lt;b&gt;", html);
    }

    [Fact]
    public async Task An_array_of_arrays_uses_the_first_row_as_header()
    {
        var yaml = _dir.WriteText("grid.yaml", "- [name, city]\n- [Ali, Beirut]\n- [Sam]\n");
        Assert.Equal(["name,city", "Ali,Beirut", "Sam,"], Kit.Lines(await Kit.Convert(yaml, "csv")));
    }

    [Fact]
    public async Task Wrapped_records_are_found_and_a_single_record_keeps_its_wrapper()
    {
        var wrapped = _dir.WriteText("wrapped.yaml", "data:\n  total: 2\n  rows:\n    - {a: 1}\n    - {a: 2}\n");
        Assert.Equal(["a", "1", "2"], Kit.Lines(await Kit.Convert(wrapped, "csv")));

        var single = _dir.WriteText("single.yaml", "person:\n  name: Ann\n  langs: [ar, en]\n");
        Assert.Equal(["person.name,person.langs.0,person.langs.1", "Ann,ar,en"], Kit.Lines(await Kit.Convert(single, "csv")));

        Assert.Equal(["{\"a\":1}", "{\"a\":2}"], Kit.Read(await Kit.Convert(wrapped, "jsonl")).Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.Equal(["{\"person\":{\"name\":\"Ann\",\"langs\":[\"ar\",\"en\"]}}"], Kit.Read(await Kit.Convert(single, "jsonl")).Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    [Theory]
    [InlineData("scalar.yaml", "just text\n", "csv")]
    [InlineData("empty.yaml", "", "xlsx")]
    [InlineData("mixed.yaml", "- {a: 1}\n- 2\n", "tsv")]
    [InlineData("list.yaml", "[]\n", "html")]
    public async Task Input_that_is_not_a_table_is_refused_for_table_targets(string name, string content, string target)
    {
        var file = _dir.WriteText(name, content);

        var result = await Kit.Run(new DataConverter(), "convert:" + target, file);

        Assert.False(result.Success);
        Assert.Equal($"note.data.notTable|{name}", result.Error);
        Assert.Equal([name], Kit.FilesIn(_dir.Path));
    }

    [Fact]
    public async Task A_lone_scalar_is_still_fine_for_tree_targets()
    {
        var file = _dir.WriteText("scalar.yaml", "just text\n");
        Assert.Equal("\"just text\"", Kit.Read(await Kit.Convert(file, "json")).Trim());
    }

    // ---- errors, limits, names ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Files_over_the_limit_are_refused_with_a_translatable_note()
    {
        var file = _dir.WriteText("big.json", "[" + string.Join(",", Enumerable.Repeat("1", 100)) + "]");

        var result = await Kit.Run(new DataConverter(maxBytes: 50), "convert:yaml", file);

        Assert.False(result.Success);
        Assert.Equal("note.data.tooLarge|big.json|1", result.Error);
        Assert.Equal(["big.json"], Kit.FilesIn(_dir.Path));
    }

    [Fact]
    public void The_default_limit_is_200_megabytes() => Assert.Equal(200L * 1024 * 1024, DataFiles.DefaultMaxBytes);

    [Theory]
    [InlineData("broken.json", "{ not json", "yaml")]
    [InlineData("broken.xml", "<a><b></a>", "json")]
    [InlineData("broken.yaml", "a: [1, 2\nb: c", "json")]
    public async Task Corrupt_input_is_reported_by_file_name(string name, string content, string target)
    {
        var file = _dir.WriteText(name, content);
        var result = await Kit.Run(new DataConverter(), "convert:" + target, file);
        Assert.False(result.Success);
        Assert.StartsWith($"note.data.invalid|{name}|", result.Error);
        Assert.Equal([name], Kit.FilesIn(_dir.Path));
    }

    [Fact]
    public async Task A_bad_json_line_is_reported_with_its_number()
    {
        var file = _dir.WriteText("log.jsonl", "{\"a\":1}\n{\"a\":2}\n{oops}\n");
        var result = await Kit.Run(new DataConverter(), "convert:csv", file);
        Assert.False(result.Success);
        Assert.StartsWith("note.data.invalid|log.jsonl|line 3:", result.Error);
    }

    [Fact]
    public async Task Results_never_overwrite_and_inputs_are_untouched()
    {
        var file = _dir.WriteText("a.json", """{"x":1}""");
        var before = File.ReadAllBytes(file);

        var first = await Kit.Convert(file, "yaml");
        var second = await Kit.Convert(file, "yaml");

        Assert.Equal("a (converted).yaml", Path.GetFileName(first));
        Assert.Equal("a (converted 2).yaml", Path.GetFileName(second));
        Assert.Equal(before, File.ReadAllBytes(file));
    }

    [Fact]
    public async Task Arabic_folder_and_file_names_work_and_the_output_folder_is_honoured()
    {
        var folder = Directory.CreateDirectory(_dir.Combine("بيانات عربية")).FullName;
        var file = Path.Combine(folder, "ملف البيانات.json");
        File.WriteAllText(file, """[{"الاسم":"علي"}]""");
        var outDir = _dir.Combine("نتائج");

        var output = await Kit.Ok(Kit.Run(new DataConverter(), "convert:xml", file, outDir));

        Assert.Equal(Path.Combine(outDir, "ملف البيانات (converted).xml"), output);
        Assert.Equal("علي", XDocument.Load(output).Root!.Element("item")!.Element("الاسم")!.Value);
    }

    [Fact]
    public async Task Cancellation_stops_the_job_and_leaves_no_output()
    {
        var file = _dir.WriteText("c.json", """{"x":1}""");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Kit.Run(new DataConverter(), "convert:yaml", file, null, cts.Token));
        Assert.Equal(["c.json"], Kit.FilesIn(_dir.Path));
    }

    [Theory]
    [InlineData("note.data.tooLarge|big.json|200")]
    [InlineData("note.data.invalid|a.xml|DTD is prohibited")]
    [InlineData("note.data.notTable|a.yaml")]
    [InlineData("note.data.noContacts|a.csv")]
    [InlineData("note.data.noEvents|a.csv")]
    [InlineData("note.data.rowsSkipped|3")]
    public void Every_message_is_translated_in_english_and_arabic(string note)
    {
        var en = Core.Localization.NoteText.Localize(note, Core.Localization.JsonLocalizer.Load("en"))!;
        var ar = Core.Localization.NoteText.Localize(note, Core.Localization.JsonLocalizer.Load("ar"))!;
        Assert.DoesNotContain("note.", en);
        Assert.DoesNotContain("note.", ar);
        Assert.NotEqual(en, ar);
        Assert.Contains(note.Split('|')[1], ar);
    }

    [Fact]
    public void The_module_exposes_both_converters() =>
        Assert.Equal(["CardsConverter", "DataConverter"], DataModule.Create(TestSupport.Engines.Locator).Select(c => c.GetType().Name).Order());

    // ---- helpers --------------------------------------------------------------------------------------------------------

    private static YamlMappingNode Load(string text)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(text));
        return (YamlMappingNode)stream.Documents[0].RootNode;
    }

    private static YamlScalarNode Scalar(YamlMappingNode map, string key) => (YamlScalarNode)map.Children[new YamlScalarNode(key)];

    private static string Normalize(string json) => JsonSerializer.Serialize(JsonDocument.Parse(json).RootElement);
}
