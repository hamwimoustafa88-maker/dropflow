using System.Text;
using System.Text.Json;
using AngleSharp.Html.Parser;
using DropFlow.Engines.Pdf.Office;
using DropFlow.TestSupport;

namespace DropFlow.Engines.Pdf.Tests;

public class CsvParserTests
{
    private static string Flat(List<List<string>> rows) => string.Join("|", rows.Select(r => string.Join(",", r.Select(f => $"[{f}]"))));

    [Fact]
    public void Simple_rows_and_fields()
    {
        Assert.Equal("[a],[b],[c]|[1],[2],[3]", Flat(Csv.Parse("a,b,c\n1,2,3\n")));
    }

    [Fact]
    public void Quoted_fields_can_hold_the_delimiter_quotes_and_line_breaks()
    {
        var rows = Csv.Parse("name,quote\r\n\"Smith, John\",\"He said \"\"hi\"\"\"\r\n\"multi\r\nline\",\"two\nlines\"\r\n");
        Assert.Equal(3, rows.Count);
        Assert.Equal(["Smith, John", "He said \"hi\""], rows[1]);
        Assert.Equal(["multi\r\nline", "two\nlines"], rows[2]);
    }

    [Fact]
    public void Empty_fields_are_kept_including_a_trailing_one()
    {
        Assert.Equal("[a],[],[c]|[],[],[x]", Flat(Csv.Parse("a,,c\n,,x\n")));
        Assert.Equal("[a],[],[c]", Flat(Csv.Parse("a,,c\n,,\n"))); // a completely empty last record is dropped
        Assert.Equal("[a],[b],[]", Flat(Csv.Parse("a,b,")));
    }

    [Theory]
    [InlineData("a,b\r\n1,2\r\n")]
    [InlineData("a,b\n1,2\n")]
    [InlineData("a,b\r1,2\r")]
    [InlineData("a,b\n1,2")]
    public void All_line_endings_work_and_a_final_one_adds_no_row(string text)
    {
        Assert.Equal("[a],[b]|[1],[2]", Flat(Csv.Parse(text)));
    }

    [Fact]
    public void Blank_lines_in_the_middle_are_rows_but_trailing_blank_rows_are_dropped()
    {
        Assert.Equal("[a]|[]|[b]", Flat(Csv.Parse("a\n\nb\n\n\n")));
    }

    [Fact]
    public void A_quote_inside_an_unquoted_field_is_just_a_character()
    {
        Assert.Equal("[5\" pipe],[x]", Flat(Csv.Parse("5\" pipe,x")));
    }

    [Fact]
    public void Text_after_a_closing_quote_is_kept_leniently()
    {
        Assert.Equal("[abcd],[e]", Flat(Csv.Parse("\"ab\"cd,e")));
    }

    [Fact]
    public void Spaces_are_part_of_the_field()
    {
        Assert.Equal("[ a ],[b ]", Flat(Csv.Parse(" a ,b ")));
    }

    [Fact]
    public void An_empty_file_has_no_rows()
    {
        Assert.Empty(Csv.Parse(""));
        Assert.Empty(Csv.Parse("\n\n"));
    }

    [Fact]
    public void Arabic_text_survives()
    {
        Assert.Equal("[الاسم],[العمر]|[أحمد],[30]", Flat(Csv.Parse("الاسم,العمر\nأحمد,30")));
    }

    [Theory]
    [InlineData("a,b,c\n1,2,3\n4,5,6", ',')]
    [InlineData("a;b;c\n1;2;3\n4;5;6", ';')]
    [InlineData("a\tb\tc\n1\t2\t3", '\t')]
    [InlineData("a|b|c\n1|2|3", '|')]
    [InlineData("single column\nvalue", ',')]
    [InlineData("name;price\napple;1,5\npear;2,5", ';')] // decimal commas must not win over the real separator
    [InlineData("\"a;b\",c\n\"1;2\",3", ',')]            // a semicolon inside quotes is not a separator
    public void The_delimiter_is_detected(string text, char expected)
    {
        Assert.Equal(expected, Csv.DetectDelimiter(text));
    }

    [Fact]
    public void Writer_quotes_only_when_needed_and_uses_crlf()
    {
        var text = Csv.Write([["plain", "has,comma", "has \"quote\"", "line\nbreak", ""], ["x", null, 3L, 1.5, true]]);
        Assert.Equal("plain,\"has,comma\",\"has \"\"quote\"\"\",\"line\nbreak\",\r\nx,,3,1.5,TRUE\r\n", text);
    }

    [Fact]
    public void Writer_formats_numbers_and_dates_the_same_everywhere()
    {
        var text = Csv.Write([[1.0, 0.1 + 0.2, 1234567.891, new DateTime(2026, 10, 21), new DateTime(2026, 10, 21, 13, 5, 9), false]]);
        Assert.Equal("1,0.30000000000000004,1234567.891,2026-10-21,2026-10-21 13:05:09,FALSE\r\n", text);
    }

    [Fact]
    public void Tsv_writer_never_leaves_a_tab_or_line_break_inside_a_value()
    {
        var text = Csv.WriteTsv([["a\tb", "c\r\nd", "e"]]);
        Assert.Equal("a b\tc d\te\r\n", text);
    }

    [Fact]
    public void Writing_then_parsing_returns_the_same_fields_for_awkward_text()
    {
        var rng = new Random(2026);
        var alphabet = new[] { "a", "ب", " ", ",", ";", "\"", "\n", "\r\n", "x y", "é", "\t", "=", "1" };
        for (var round = 0; round < 200; round++)
        {
            var rows = Enumerable.Range(0, rng.Next(1, 5)).Select(_ =>
                Enumerable.Range(0, rng.Next(1, 5)).Select(_ => string.Concat(Enumerable.Range(0, rng.Next(0, 6)).Select(_ => alphabet[rng.Next(alphabet.Length)]))).ToList()).ToList();
            // A row whose fields are all empty cannot be told from a blank line, and trailing ones are dropped by design.
            while (rows.Count > 0 && rows[^1].All(string.IsNullOrEmpty)) rows.RemoveAt(rows.Count - 1);

            var text = Csv.Write(rows.Select(r => (IReadOnlyList<object?>)r.Cast<object?>().ToList()));
            var parsed = Csv.Parse(text);

            Assert.Equal(rows.Select(r => string.Join("\u0001", r)), parsed.Select(r => string.Join("\u0001", r)));
        }
    }
}

public class TextFileTests
{
    [Fact]
    public void Utf8_with_and_without_a_byte_order_mark()
    {
        Assert.Equal("مرحبا", TextFile.Decode([0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("مرحبا")]));
        Assert.Equal("مرحبا", TextFile.Decode(Encoding.UTF8.GetBytes("مرحبا")));
    }

    [Fact]
    public void Utf16_with_a_byte_order_mark()
    {
        Assert.Equal("مرحبا", TextFile.Decode([0xFF, 0xFE, .. Encoding.Unicode.GetBytes("مرحبا")]));
        Assert.Equal("مرحبا", TextFile.Decode([0xFE, 0xFF, .. Encoding.BigEndianUnicode.GetBytes("مرحبا")]));
    }

    [Fact]
    public void Windows_1256_arabic_files_from_older_excel_versions()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var bytes = Encoding.GetEncoding(1256).GetBytes("الاسم;العمر\nأحمد;30");
        Assert.Equal("الاسم;العمر\nأحمد;30", TextFile.Decode(bytes));
    }

    [Fact]
    public void Windows_1252_western_text_is_not_mistaken_for_arabic()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var bytes = Encoding.GetEncoding(1252).GetBytes("café déjà vu");
        Assert.Equal("café déjà vu", TextFile.Decode(bytes));
    }
}

public class TabularConversionTests
{
    // ---- csv / tsv sources ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Csv_to_xlsx_keeps_text_and_numbers_apart_and_handles_arabic()
    {
        using var dir = new TempDir();
        var csv = dir.WriteText("people.csv", "الاسم,العمر,الرمز\nأحمد,30,007\nسارة,25.5,A1\n");

        var result = await Kit.Ok(Kit.RunAsync("convert:xlsx", [csv]));

        Assert.Equal(dir.Combine("people (converted).xlsx"), result.Outputs[0]);
        var sheets = XlsxProbe.Read(result.Outputs[0], out var rtl);
        var sheet = Assert.Single(sheets).Value;
        Assert.Equal(new XlsxProbe.Cell("الاسم", false), sheet["A1"]);
        Assert.Equal(new XlsxProbe.Cell("أحمد", false), sheet["A2"]);
        Assert.Equal(new XlsxProbe.Cell("30", true), sheet["B2"]);
        Assert.Equal(new XlsxProbe.Cell("25.5", true), sheet["B3"]);
        Assert.Equal(new XlsxProbe.Cell("007", false), sheet["C2"]);
        Assert.True(rtl.Values.Single());
    }

    [Fact]
    public async Task A_semicolon_csv_in_windows_1256_is_converted_correctly()
    {
        using var dir = new TempDir();
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var csv = dir.Combine("old.csv");
        File.WriteAllBytes(csv, Encoding.GetEncoding(1256).GetBytes("الاسم;السعر\nتفاح;1,5\n"));

        var result = await Kit.Ok(Kit.RunAsync("convert:json", [csv]));

        using var json = JsonDocument.Parse(File.ReadAllText(result.Outputs[0]));
        var row = json.RootElement[0];
        Assert.Equal("تفاح", row.GetProperty("الاسم").GetString());
        Assert.Equal("1,5", row.GetProperty("السعر").GetString()); // comma decimals are left as text, not guessed
    }

    [Fact]
    public async Task Csv_to_json_makes_typed_objects_from_the_header_row()
    {
        using var dir = new TempDir();
        var csv = dir.WriteText("data.csv", "id,name,score,,name\n1,Ann,9.5,x,dup\n2,Bob,,y,\n");

        var result = await Kit.Ok(Kit.RunAsync("convert:json", [csv]));

        var text = File.ReadAllText(result.Outputs[0]);
        Assert.DoesNotContain("\\u", text); // Arabic and other non-ASCII text is written as is
        using var json = JsonDocument.Parse(text);
        var first = json.RootElement[0];
        Assert.Equal(1, first.GetProperty("id").GetInt32());
        Assert.Equal("Ann", first.GetProperty("name").GetString());
        Assert.Equal(9.5, first.GetProperty("score").GetDouble());
        Assert.Equal("x", first.GetProperty("column4").GetString()); // blank header gets a name
        Assert.Equal("dup", first.GetProperty("name_2").GetString()); // duplicate header is not dropped
        Assert.Equal(2, json.RootElement.GetArrayLength());
        Assert.Equal("", json.RootElement[1].GetProperty("score").GetString());
    }

    [Fact]
    public async Task Csv_to_tsv_and_back_are_lossless_for_awkward_cells()
    {
        using var dir = new TempDir();
        var csv = dir.WriteText("a.csv", "x,y\n\"has,comma\",\"say \"\"hi\"\"\"\n");

        var tsv = (await Kit.Ok(Kit.RunAsync("convert:tsv", [csv]))).Outputs[0];
        Assert.Equal("x\ty\r\nhas,comma\tsay \"hi\"\r\n", File.ReadAllText(tsv).TrimStart('﻿'));
        var back = (await Kit.Ok(Kit.RunAsync("convert:csv", [tsv]))).Outputs[0];

        Assert.Equal("x,y\r\n\"has,comma\",\"say \"\"hi\"\"\"\r\n", File.ReadAllText(back).TrimStart('﻿'));
    }

    [Fact]
    public async Task Text_files_written_for_excel_start_with_a_utf8_byte_order_mark()
    {
        using var dir = new TempDir();
        var xlsx = XlsxFixture.Write(dir.Combine("t.xlsx"), ("S", [["اسم"], ["قيمة"]]));

        var csv = (await Kit.Ok(Kit.RunAsync("convert:csv", [xlsx]))).Outputs[0];

        var bytes = File.ReadAllBytes(csv);
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes.Take(3));
        Assert.Equal("اسم\r\nقيمة\r\n", Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3));
    }

    // ---- xlsx source ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Xlsx_cell_types_are_written_to_csv_the_way_a_person_expects()
    {
        using var dir = new TempDir();
        var xlsx = XlsxFixture.Write(dir.Combine("types.xlsx"),
            ("Data", [["text", "int", "decimal", "date", "bool"], ["مرحبا", 42L, 3.14, new DateTime(2026, 10, 21), true], ["has,comma", -7L, 0.5, new DateTime(2026, 1, 2, 3, 4, 5), false]]));

        var csv = (await Kit.Ok(Kit.RunAsync("convert:csv", [xlsx]))).Outputs[0];

        Assert.Equal(
            "text,int,decimal,date,bool\r\nمرحبا,42,3.14,2026-10-21,TRUE\r\n\"has,comma\",-7,0.5,2026-01-02 03:04:05,FALSE\r\n",
            File.ReadAllText(csv).TrimStart('﻿'));
    }

    [Fact]
    public async Task A_workbook_with_several_sheets_becomes_a_folder_of_csv_files()
    {
        using var dir = new TempDir();
        var xlsx = XlsxFixture.Write(dir.Combine("book.xlsx"), ("First", [["a"], ["1"]]), ("Second<odd>|name", [["b"], ["2"]]));

        var result = await Kit.Ok(Kit.RunAsync("convert:csv", [xlsx]));

        var folder = Assert.Single(result.Outputs);
        Assert.Equal(dir.Combine("book (converted)"), folder);
        Assert.Equal(["book - First.csv", "book - Second_odd__name.csv"], Kit.FilesIn(folder));
        Assert.Equal("b\r\n2\r\n", File.ReadAllText(Path.Combine(folder, "book - Second_odd__name.csv")).TrimStart('﻿'));
    }

    [Fact]
    public async Task A_workbook_with_several_sheets_becomes_one_json_object_keyed_by_sheet()
    {
        using var dir = new TempDir();
        var xlsx = XlsxFixture.Write(dir.Combine("book.xlsx"), ("First", [["a", "b"], [1L, "x"]]), ("Second", [["c"], [true]]));

        var json = (await Kit.Ok(Kit.RunAsync("convert:json", [xlsx]))).Outputs[0];

        using var doc = JsonDocument.Parse(File.ReadAllText(json));
        Assert.Equal(1, doc.RootElement.GetProperty("First")[0].GetProperty("a").GetInt32());
        Assert.Equal("x", doc.RootElement.GetProperty("First")[0].GetProperty("b").GetString());
        Assert.True(doc.RootElement.GetProperty("Second")[0].GetProperty("c").GetBoolean());
    }

    [Fact]
    public async Task Xlsx_to_html_makes_a_table_per_sheet_with_a_header_row()
    {
        using var dir = new TempDir();
        var xlsx = XlsxFixture.Write(dir.Combine("book.xlsx"), ("Prices", [["Item", "Cost"], ["Tea <green>", 2.5], ["شاي", 3L]]));

        var html = (await Kit.Ok(Kit.RunAsync("convert:html", [xlsx]))).Outputs[0];

        var doc = new HtmlParser().ParseDocument(File.ReadAllText(html));
        Assert.Equal(["Item", "Cost"], doc.QuerySelectorAll("thead th").Select(e => e.TextContent));
        Assert.Equal(["Tea <green>", "2.5"], doc.QuerySelectorAll("tbody tr:nth-child(1) td").Select(e => e.TextContent));
        Assert.Equal(["شاي", "3"], doc.QuerySelectorAll("tbody tr:nth-child(2) td").Select(e => e.TextContent));
    }

    [Fact]
    public async Task Hidden_formatting_free_empty_workbook_still_converts()
    {
        using var dir = new TempDir();
        var xlsx = XlsxFixture.Write(dir.Combine("empty.xlsx"), ("Sheet1", []));

        var csv = await Kit.Ok(Kit.RunAsync("convert:csv", [xlsx]));

        Assert.True(File.Exists(csv.Outputs[0]));
    }

    // ---- json source ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Json_array_of_objects_uses_the_union_of_keys_as_columns()
    {
        using var dir = new TempDir();
        var json = dir.WriteText("rows.json", """[{"id":1,"name":"Ann"},{"id":2,"extra":true,"name":"Bob"},{"id":3,"meta":{"a":[1,2]}}]""");

        var csv = (await Kit.Ok(Kit.RunAsync("convert:csv", [json]))).Outputs[0];

        Assert.Equal(
            "id,name,extra,meta\r\n1,Ann,,\r\n2,Bob,TRUE,\r\n3,,,\"{\"\"a\"\":[1,2]}\"\r\n",
            File.ReadAllText(csv).TrimStart('﻿'));
    }

    [Fact]
    public async Task Json_array_of_arrays_and_plain_values()
    {
        using var dir = new TempDir();
        var arrays = dir.WriteText("a.json", "[[\"x\",\"y\"],[1,2.5]]");
        var scalars = dir.WriteText("s.json", "[1,\"two\",null]");

        var a = (await Kit.Ok(Kit.RunAsync("convert:csv", [arrays]))).Outputs[0];
        var s = (await Kit.Ok(Kit.RunAsync("convert:csv", [scalars]))).Outputs[0];

        Assert.Equal("x,y\r\n1,2.5\r\n", File.ReadAllText(a).TrimStart('﻿'));
        Assert.Equal("value\r\n1\r\ntwo\r\n\r\n".TrimEnd('\r', '\n') + "\r\n", File.ReadAllText(s).TrimStart('﻿').TrimEnd('\r', '\n') + "\r\n");
    }

    [Fact]
    public async Task Json_object_of_arrays_becomes_one_sheet_per_key()
    {
        using var dir = new TempDir();
        var json = dir.WriteText("sheets.json", """{"People":[{"n":"Ann"}],"Pets":[{"n":"Rex"},{"n":"Tom"}]}""");

        var xlsx = (await Kit.Ok(Kit.RunAsync("convert:xlsx", [json]))).Outputs[0];

        var sheets = XlsxProbe.Read(xlsx, out _);
        Assert.Equal(["People", "Pets"], sheets.Keys);
        Assert.Equal("Tom", sheets["Pets"]["A3"].Text);
    }

    [Fact]
    public async Task Json_keeps_text_that_looks_like_a_number_as_text_in_the_spreadsheet()
    {
        using var dir = new TempDir();
        var json = dir.WriteText("ids.json", """[{"phone":"0123456789","count":5}]""");

        var xlsx = (await Kit.Ok(Kit.RunAsync("convert:xlsx", [json]))).Outputs[0];

        var cells = XlsxProbe.Read(xlsx, out _).Single().Value;
        Assert.Equal(new XlsxProbe.Cell("0123456789", false), cells["A2"]);
        Assert.Equal(new XlsxProbe.Cell("5", true), cells["B2"]);
    }

    [Fact]
    public async Task Invalid_json_is_reported_by_file_name()
    {
        using var dir = new TempDir();
        var json = dir.WriteText("broken.json", "{ not json");

        var result = await Kit.RunAsync("convert:csv", [json]);

        Assert.False(result.Success);
        Assert.Contains("broken.json", result.Error);
        Assert.Equal(["broken.json"], Kit.FilesIn(dir.Path));
    }

    [Fact]
    public async Task A_corrupt_xlsx_is_reported_by_file_name()
    {
        using var dir = new TempDir();
        var xlsx = dir.WriteText("broken.xlsx", "this is not a zip");

        var result = await Kit.RunAsync("convert:csv", [xlsx]);

        Assert.False(result.Success);
        Assert.Contains("broken.xlsx", result.Error);
        Assert.Equal(["broken.xlsx"], Kit.FilesIn(dir.Path));
    }

    // ---- round trips and housekeeping -----------------------------------------------------------------------------------

    [Fact]
    public async Task Csv_to_xlsx_to_csv_returns_the_original_values()
    {
        using var dir = new TempDir();
        var original = "name,qty,note\r\nAnn,3,\"a, b\"\r\nأحمد,4.5,\"line1\nline2\"\r\n";
        var csv = dir.WriteText("r.csv", original);

        var xlsx = (await Kit.Ok(Kit.RunAsync("convert:xlsx", [csv]))).Outputs[0];
        var back = (await Kit.Ok(Kit.RunAsync("convert:csv", [xlsx]))).Outputs[0];

        Assert.Equal(original.Replace("line1\nline2", "line1\nline2"), File.ReadAllText(back).TrimStart('﻿'));
    }

    [Fact]
    public async Task Json_to_csv_to_json_round_trips_flat_data()
    {
        using var dir = new TempDir();
        var json = dir.WriteText("flat.json", """[{"a":1,"b":"x"},{"a":2,"b":"y"}]""");

        var csv = (await Kit.Ok(Kit.RunAsync("convert:csv", [json]))).Outputs[0];
        var back = (await Kit.Ok(Kit.RunAsync("convert:json", [csv]))).Outputs[0];

        using var doc = JsonDocument.Parse(File.ReadAllText(back));
        Assert.Equal(2, doc.RootElement.GetArrayLength());
        Assert.Equal(2, doc.RootElement[1].GetProperty("a").GetInt32());
        Assert.Equal("y", doc.RootElement[1].GetProperty("b").GetString());
    }

    [Fact]
    public async Task Unicode_paths_collisions_and_the_output_directory()
    {
        using var dir = new TempDir();
        using var outDir = new TempDir();
        var folder = Directory.CreateDirectory(dir.Combine("جداول")).FullName;
        var csv = Path.Combine(folder, "بيانات.csv");
        File.WriteAllText(csv, "a,b\n1,2\n");

        var first = await Kit.Ok(Kit.RunAsync("convert:xlsx", [csv]));
        var second = await Kit.Ok(Kit.RunAsync("convert:xlsx", [csv]));
        var elsewhere = await Kit.Ok(Kit.RunAsync("convert:xlsx", [csv], outputDirectory: outDir.Path));

        Assert.Equal(Path.Combine(folder, "بيانات (converted).xlsx"), first.Outputs[0]);
        Assert.Equal(Path.Combine(folder, "بيانات (converted 2).xlsx"), second.Outputs[0]);
        Assert.Equal(outDir.Combine("بيانات (converted).xlsx"), elsewhere.Outputs[0]);
        Assert.Equal("a,b\n1,2\n", File.ReadAllText(csv)); // the source is untouched
    }

    [Fact]
    public async Task Cancellation_leaves_nothing_behind()
    {
        using var dir = new TempDir();
        var csv = dir.WriteText("c.csv", "a,b\n1,2\n");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Kit.RunAsync("convert:xlsx", [csv], ct: cts.Token));

        Assert.Equal(["c.csv"], Kit.FilesIn(dir.Path));
    }

    [Theory]
    [InlineData("convert:csv", "csv", false)]
    [InlineData("convert:csv", "tsv", true)]
    [InlineData("convert:csv", "xlsx", true)]
    [InlineData("convert:csv", "json", true)]
    [InlineData("convert:html", "csv", true)]
    [InlineData("convert:html", "html", false)]
    [InlineData("convert:xlsx", "xlsx", false)]
    [InlineData("convert:json", "json", false)]
    [InlineData("convert:tsv", "tsv", false)]
    public void Supports_covers_exactly_the_managed_pairs(string operation, string ext, bool expected)
    {
        Assert.Equal(expected, Kit.Converters.Where(c => c.EngineFor(operation) == DropFlow.Core.Operations.EngineId.None).Any(c => c.Supports(operation, ext)));
    }
}
