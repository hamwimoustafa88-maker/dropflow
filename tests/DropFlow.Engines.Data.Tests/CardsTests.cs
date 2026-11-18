// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;
using System.Text.Json;
using ClosedXML.Excel;
using DropFlow.TestSupport;

namespace DropFlow.Engines.Data.Tests;

public sealed class CardsTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private static readonly DateTime Stamp = new(2026, 11, 18, 9, 30, 0, DateTimeKind.Utc);

    private static CardsConverter Cards() => new(Data.DataFiles.DefaultMaxBytes, () => Stamp);

    private string WriteCrlf(string name, string text, Encoding? encoding = null)
    {
        var path = _dir.Combine(name);
        File.WriteAllBytes(path, (encoding ?? new UTF8Encoding(false)).GetBytes(text.ReplaceLineEndings("\r\n")));
        return path;
    }

    /// <summary>Header -> value of each CSV row the converter wrote (simple files: no quoted line breaks).</summary>
    private static List<Dictionary<string, string>> Rows(string csv)
    {
        var lines = Kit.Lines(csv);
        var header = Split(lines[0]);
        return lines.Skip(1).Select(l => header.Zip(Split(l)).ToDictionary(p => p.First, p => p.Second)).ToList();
    }

    private static List<string> Split(string line)
    {
        var cells = new List<string>();
        var sb = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted && c == '"' && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
            else if (c == '"') quoted = !quoted;
            else if (c == ',' && !quoted) { cells.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(c);
        }
        cells.Add(sb.ToString());
        return cells;
    }

    // ---- vCard ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task VCards_2_1_3_0_and_4_0_become_csv_rows_with_the_documented_columns()
    {
        // 2.1 as old Android phones export it: quoted-printable UTF-8 with soft line breaks, bare TYPE parameters.
        var qpName = string.Concat(Encoding.UTF8.GetBytes("علي حسن").Select(b => $"={b:X2}"));
        var vcf = WriteCrlf("contacts.vcf", $"""
            BEGIN:VCARD
            VERSION:2.1
            N;CHARSET=UTF-8;ENCODING=QUOTED-PRINTABLE:{qpName[..30]}=
            {qpName[30..]};;;;
            FN;CHARSET=UTF-8;ENCODING=QUOTED-PRINTABLE:{qpName}
            TEL;CELL:+961 3 123 456
            TEL;HOME;VOICE:01 234 567
            EMAIL;HOME:ali@example.org
            END:VCARD
            BEGIN:VCARD
            VERSION:3.0
            FN:Sam Smith
            N:Smith;Sam;;Dr.;
            ORG:Acme\, Inc.;Research
            TITLE:Chief
            EMAIL;TYPE=INTERNET,WORK:sam@acme.test
            EMAIL;TYPE=INTERNET:sam@home.test
            TEL;TYPE=WORK,VOICE:+1 555 0100
            TEL;TYPE=FAX:+1 555 0199
            ADR;TYPE=WORK:;;1 Main St;Springfield;IL;62701;USA
            NOTE:First line\nsecond\, with comma
            BDAY:19800115
            PHOTO;ENCODING=b;TYPE=JPEG:/9j/4AAQSkZJRgABAQ
             AAAQABAAD
            END:VCARD
            BEGIN:VCARD
            VERSION:4.0
            FN:ليلى
            N:;ليلى;;;
            TEL;VALUE=uri;TYPE="cell,voice":tel:+44-20-7946-0000
            URL:https://example.org/a,b
            NOTE:This is a long note that is folded across two lines because it is longer than seventy-five oc
             tets.
            END:VCARD
            """);

        var csv = await Kit.Convert(vcf, "csv");

        var header = Split(Kit.Lines(csv)[0]);
        Assert.Equal(["full_name", "given_name", "family_name", "middle_name", "prefix", "suffix", "nickname", "organization", "department", "title",
            "email", "mobile", "home_phone", "work_phone", "phone", "fax", "address", "work_address", "url", "birthday", "note"], header);
        var rows = Rows(csv);
        Assert.Equal(3, rows.Count);

        Assert.Equal("علي حسن", rows[0]["full_name"]);
        Assert.Equal("علي حسن", rows[0]["family_name"]);
        Assert.Equal("+961 3 123 456", rows[0]["mobile"]);
        Assert.Equal("01 234 567", rows[0]["home_phone"]);
        Assert.Equal("ali@example.org", rows[0]["email"]);

        Assert.Equal("Sam Smith", rows[1]["full_name"]);
        Assert.Equal("Dr.", rows[1]["prefix"]);
        Assert.Equal("Acme, Inc.", rows[1]["organization"]);
        Assert.Equal("Research", rows[1]["department"]);
        Assert.Equal("sam@acme.test; sam@home.test", rows[1]["email"]);
        Assert.Equal("+1 555 0100", rows[1]["work_phone"]);
        Assert.Equal("+1 555 0199", rows[1]["fax"]);
        Assert.Equal("1 Main St, Springfield, IL, 62701, USA", rows[1]["work_address"]);
        Assert.Equal("1980-01-15", rows[1]["birthday"]);

        Assert.Equal("ليلى", rows[2]["full_name"]);
        Assert.Equal("+44-20-7946-0000", rows[2]["mobile"]);
        Assert.Equal("https://example.org/a,b", rows[2]["url"]);
        Assert.EndsWith("seventy-five octets.", rows[2]["note"]);

        // The note with a line break stays one quoted CSV cell.
        Assert.Contains("\"First line\nsecond, with comma\"", Kit.Read(csv));
    }

    [Fact]
    public async Task VCard_contacts_convert_to_json_and_xlsx_through_the_same_table()
    {
        var vcf = WriteCrlf("one.vcf", "BEGIN:VCARD\nVERSION:3.0\nFN:مريم\nEMAIL:m@example.org\nEND:VCARD\n");

        using var doc = JsonDocument.Parse(File.ReadAllText(await Kit.Convert(vcf, "json")));
        Assert.Equal("مريم", doc.RootElement[0].GetProperty("full_name").GetString());
        Assert.Equal(JsonValueKind.Null, doc.RootElement[0].GetProperty("mobile").ValueKind);

        using var workbook = new XLWorkbook(await Kit.Convert(vcf, "xlsx"));
        Assert.Equal("مريم", workbook.Worksheet(1).Cell(2, 1).GetString());
    }

    [Fact]
    public async Task Csv_to_vcard_to_csv_keeps_arabic_names_and_every_column()
    {
        var csv = WriteCrlf("people.csv", """
            Full Name,First Name,Last Name,Company,E-mail,Mobile,Work Phone,Address,Birthday,Notes
            علي حسن,علي,حسن,شركة النور,ali@example.org; ali2@example.org,+961 3 123 456,+961 1 999 999,"شارع الحمرا، بيروت",1990-01-15,"سطر أول
            سطر ثان; مع فاصلة, وفاصلة"
            ,,,,,,,,,
            ,Sam,Smith,,,+1 555 0100,,,,
            """);

        var vcfResult = await Kit.Run(Cards(), CardsConverter.ToVCard, csv);
        Assert.True(vcfResult.Success, vcfResult.Error);
        Assert.Equal("note.data.rowsSkipped|1", vcfResult.Note);       // the empty row
        var vcf = Assert.Single(vcfResult.Outputs);
        Assert.Equal("people (contacts).vcf", Path.GetFileName(vcf));

        var raw = File.ReadAllText(vcf);
        Assert.StartsWith("BEGIN:VCARD\r\nVERSION:3.0\r\n", raw);
        Assert.Contains("FN;CHARSET=UTF-8:علي حسن\r\n", raw);
        Assert.Contains("N;CHARSET=UTF-8:حسن;علي;;;\r\n", raw);
        Assert.Contains("FN:Sam Smith\r\n", raw);
        Assert.All(raw.Split("\r\n"), line => Assert.True(Encoding.UTF8.GetByteCount(line) <= 75, line));  // folded

        var back = Rows(await Kit.Convert(vcf, "csv"));
        Assert.Equal(2, back.Count);
        Assert.Equal("علي حسن", back[0]["full_name"]);
        Assert.Equal("علي", back[0]["given_name"]);
        Assert.Equal("حسن", back[0]["family_name"]);
        Assert.Equal("شركة النور", back[0]["organization"]);
        Assert.Equal("ali@example.org; ali2@example.org", back[0]["email"]);
        Assert.Equal("+961 3 123 456", back[0]["mobile"]);
        Assert.Equal("+961 1 999 999", back[0]["work_phone"]);
        Assert.Equal("شارع الحمرا، بيروت", back[0]["address"]);
        Assert.Equal("1990-01-15", back[0]["birthday"]);
        Assert.Equal("Sam Smith", back[1]["full_name"]);
        Assert.Equal("+1 555 0100", back[1]["mobile"]);

        using var doc = JsonDocument.Parse(File.ReadAllText(await Kit.Convert(vcf, "json")));
        Assert.Equal("سطر أول\nسطر ثان; مع فاصلة, وفاصلة", doc.RootElement[0].GetProperty("note").GetString());
    }

    [Fact]
    public async Task Arabic_headers_are_understood_when_making_contacts()
    {
        var csv = WriteCrlf("جهات.csv", "الاسم,الجوال,البريد الإلكتروني\nنور,0791234567,noor@example.org\n");
        var vcf = await Kit.Ok(Kit.Run(Cards(), CardsConverter.ToVCard, csv));
        var raw = File.ReadAllText(vcf);
        Assert.Contains("FN;CHARSET=UTF-8:نور\r\n", raw);
        Assert.Contains("TEL;TYPE=CELL:0791234567\r\n", raw);
        Assert.Contains("EMAIL;TYPE=INTERNET:noor@example.org\r\n", raw);
    }

    [Fact]
    public async Task A_table_without_contact_columns_is_refused()
    {
        var csv = WriteCrlf("prices.csv", "item,price\npen,2\n");
        var result = await Kit.Run(Cards(), CardsConverter.ToVCard, csv);
        Assert.False(result.Success);
        Assert.Equal("note.data.noContacts|prices.csv", result.Error);
        Assert.Equal(["prices.csv"], Kit.FilesIn(_dir.Path));
    }

    [Fact]
    public async Task An_xlsx_contact_list_becomes_vcards()
    {
        var path = _dir.Combine("list.xlsx");
        using (var workbook = new XLWorkbook())
        {
            var sheet = workbook.AddWorksheet("People");
            sheet.Cell(1, 1).Value = "Name";
            sheet.Cell(1, 2).Value = "Phone";
            sheet.Cell(2, 1).Value = "يوسف";
            sheet.Cell(2, 2).Value = 5551234;
            workbook.SaveAs(path);
        }

        var raw = File.ReadAllText(await Kit.Ok(Kit.Run(Cards(), CardsConverter.ToVCard, path)));

        Assert.Contains("FN;CHARSET=UTF-8:يوسف\r\n", raw);
        Assert.Contains("TEL;TYPE=VOICE:5551234\r\n", raw);
    }

    // ---- iCalendar ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Ics_events_become_csv_rows_with_the_documented_columns()
    {
        var ics = WriteCrlf("cal.ics", """
            BEGIN:VCALENDAR
            VERSION:2.0
            PRODID:-//Test//EN
            BEGIN:VTIMEZONE
            TZID:Asia/Beirut
            BEGIN:STANDARD
            DTSTART:19701025T000000
            TZOFFSETFROM:+0300
            TZOFFSETTO:+0200
            END:STANDARD
            END:VTIMEZONE
            BEGIN:VEVENT
            UID:1@test
            DTSTART;TZID=Asia/Beirut:20261118T100000
            DTEND;TZID=Asia/Beirut:20261118T113000
            SUMMARY:اجتماع الفريق\, الأسبوعي
            LOCATION:بيروت
            DESCRIPTION:Agenda:\n1. Plans
            ORGANIZER;CN=Ann Lee:mailto:ann@example.org
            ATTENDEE;CN="Bo, Jr.":mailto:bo@example.org
            ATTENDEE:mailto:cy@example.org
            CATEGORIES:Work,Team
            STATUS:CONFIRMED
            RRULE:FREQ=WEEKLY;BYDAY=WE
            BEGIN:VALARM
            ACTION:DISPLAY
            DESCRIPTION:Reminder
            TRIGGER:-PT15M
            END:VALARM
            END:VEVENT
            BEGIN:VEVENT
            UID:2@test
            DTSTART;VALUE=DATE:20261225
            DTEND;VALUE=DATE:20261226
            SUMMARY:Holiday with a summary long enough that a careful writer has to fold this line
              in two
            END:VEVENT
            BEGIN:VEVENT
            UID:3@test
            DTSTART:20261201T080000Z
            DURATION:PT1H30M
            SUMMARY:Call
            END:VEVENT
            BEGIN:VTODO
            SUMMARY:Not an event
            END:VTODO
            END:VCALENDAR
            """);

        var csv = await Kit.Convert(ics, "csv");

        Assert.Equal(["summary", "start", "end", "all_day", "timezone", "location", "description", "organizer", "attendees", "categories", "status", "url", "recurrence", "uid"],
            Split(Kit.Lines(csv)[0]));
        var json = await Kit.Convert(ics, "json");
        using var doc = JsonDocument.Parse(File.ReadAllText(json));
        var events = doc.RootElement.EnumerateArray().ToList();
        Assert.Equal(3, events.Count);

        string S(int i, string key) => events[i].GetProperty(key).GetString() ?? "";
        Assert.Equal("اجتماع الفريق, الأسبوعي", S(0, "summary"));
        Assert.Equal("2026-11-18T10:00:00", S(0, "start"));
        Assert.Equal("2026-11-18T11:30:00", S(0, "end"));
        Assert.Equal("Asia/Beirut", S(0, "timezone"));
        Assert.Equal("Agenda:\n1. Plans", S(0, "description"));     // the alarm's DESCRIPTION is not mixed in
        Assert.Equal("Ann Lee <ann@example.org>", S(0, "organizer"));
        Assert.Equal("Bo, Jr. <bo@example.org>; cy@example.org", S(0, "attendees"));
        Assert.Equal("Work, Team", S(0, "categories"));
        Assert.Equal("FREQ=WEEKLY;BYDAY=WE", S(0, "recurrence"));

        Assert.Equal("2026-12-25", S(1, "start"));
        Assert.Equal("true", S(1, "all_day"));
        Assert.Equal("Holiday with a summary long enough that a careful writer has to fold this line in two", S(1, "summary"));

        Assert.Equal("2026-12-01T08:00:00Z", S(2, "start"));
        Assert.Equal("2026-12-01T09:30:00Z", S(2, "end"));          // from DURATION
    }

    [Fact]
    public async Task Csv_to_ics_to_csv_keeps_arabic_and_turns_zoned_times_into_utc()
    {
        var csv = WriteCrlf("events.csv", """
            summary,start,end,all_day,timezone,location,description,organizer,attendees
            اجتماع الفريق,2026-11-18T10:00:00,2026-11-18T11:30:00,,Asia/Beirut,بيروت,"سطر أول
            سطر ثان",Ann Lee <ann@example.org>,bo@example.org; Not An Address
            عيد,2026-12-25,,true,,,,,
            Call,2026-12-01T08:00:00Z,2026-12-01T09:30:00Z,,,,,,
            Floating,2026-12-02 14:00,,,,,,,
            No start,,,,,,,,
            """);

        var result = await Kit.Run(Cards(), CardsConverter.ToICalendar, csv);
        Assert.True(result.Success, result.Error);
        Assert.Equal("note.data.rowsSkipped|1", result.Note);
        var ics = Assert.Single(result.Outputs);
        Assert.Equal("events (calendar).ics", Path.GetFileName(ics));

        var raw = File.ReadAllText(ics);
        Assert.StartsWith("BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:", raw);
        Assert.Contains("DTSTART:20261118T080000Z\r\n", raw);            // Beirut is UTC+2 in November
        Assert.Contains("DTEND:20261118T093000Z\r\n", raw);
        Assert.Contains("DTSTART;VALUE=DATE:20261225\r\nDTEND;VALUE=DATE:20261226\r\n", raw);
        Assert.Contains("DTSTART:20261202T140000\r\n", raw);
        Assert.Contains("DTSTAMP:20261118T093000Z\r\n", raw);
        Assert.Contains("ORGANIZER;CN=\"Ann Lee\":mailto:ann@example.org\r\n", raw);
        Assert.Contains("ATTENDEE:mailto:bo@example.org\r\n", raw);
        Assert.DoesNotContain("Not An Address", raw);
        Assert.Equal(4, raw.Split("BEGIN:VEVENT").Length - 1);
        Assert.All(raw.Split("\r\n"), line => Assert.True(Encoding.UTF8.GetByteCount(line) <= 75, line));

        using var doc = JsonDocument.Parse(File.ReadAllText(await Kit.Convert(ics, "json")));
        var first = doc.RootElement[0];
        Assert.Equal("اجتماع الفريق", first.GetProperty("summary").GetString());
        Assert.Equal("بيروت", first.GetProperty("location").GetString());
        Assert.Equal("سطر أول\nسطر ثان", first.GetProperty("description").GetString());
        Assert.Equal("2026-11-18T08:00:00Z", first.GetProperty("start").GetString());
        Assert.Equal("Ann Lee <ann@example.org>", first.GetProperty("organizer").GetString());
        Assert.Equal("عيد", doc.RootElement[1].GetProperty("summary").GetString());
        Assert.Equal("true", doc.RootElement[1].GetProperty("all_day").GetString());
    }

    [Fact]
    public async Task Outlook_style_split_date_and_time_columns_are_understood()
    {
        var csv = WriteCrlf("outlook.csv", """
            Subject,Start Date,Start Time,End Date,End Time,All day event,Location
            Review,2026-11-20,09:15,2026-11-20,10:00,False,Room 2
            """);
        var raw = File.ReadAllText(await Kit.Ok(Kit.Run(Cards(), CardsConverter.ToICalendar, csv)));
        Assert.Contains("SUMMARY:Review\r\n", raw);
        Assert.Contains("DTSTART:20261120T091500\r\n", raw);
        Assert.Contains("DTEND:20261120T100000\r\n", raw);
        Assert.Contains("LOCATION:Room 2\r\n", raw);
    }

    [Fact]
    public async Task A_table_without_dates_is_refused_for_a_calendar()
    {
        var csv = WriteCrlf("names.csv", "summary,place\nA,B\n");
        var result = await Kit.Run(Cards(), CardsConverter.ToICalendar, csv);
        Assert.False(result.Success);
        Assert.Equal("note.data.noEvents|names.csv", result.Error);
    }

    [Theory]
    [InlineData(CardsConverter.ToVCard, "csv", true)]
    [InlineData(CardsConverter.ToVCard, "tsv", true)]
    [InlineData(CardsConverter.ToICalendar, "xlsx", true)]
    [InlineData(CardsConverter.ToICalendar, "json", false)]
    [InlineData(CardsConverter.ToVCard, "vcf", false)]
    [InlineData("convert:vcf", "csv", false)]
    public void Cards_supports_tables_only(string operation, string extension, bool expected) =>
        Assert.Equal(expected, new CardsConverter().Supports(operation, extension));
}
