// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using DropFlow.Engines.Data.Model;

namespace DropFlow.Engines.Data.Formats;

/// <summary>
/// Calendar events (.ics, iCalendar RFC 5545, VEVENT only) as a table with fixed, documented columns (docs/data-formats.md), and back.
/// Times read as written: <c>2026-11-18T10:00:00Z</c> (UTC), <c>2026-11-18T10:00:00</c> with the TZID in <c>timezone</c>, or a date
/// for all-day events. A DURATION becomes an end time. Alarms, to-dos and time-zone definitions are not kept.
/// Writing turns a time with a known time zone (IANA or Windows name) into UTC, so no VTIMEZONE block is needed; a time without one
/// stays "floating" (the reader's own clock). Rows without a start that can be read are skipped and counted.
/// </summary>
internal static partial class ICalendarFormat
{
    public static readonly IReadOnlyList<string> Columns =
    [
        "summary", "start", "end", "all_day", "timezone", "location", "description",
        "organizer", "attendees", "categories", "status", "url", "recurrence", "uid",
    ];

    // Extra columns understood when making a calendar (exports of Outlook and Google Calendar split date and time).
    private static readonly string[] Extra = ["start_date", "start_time", "end_date", "end_time"];

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.Ordinal)
    {
        ["title"] = "summary", ["subject"] = "summary", ["event"] = "summary", ["name"] = "summary", ["event_name"] = "summary",
        ["begin"] = "start", ["from"] = "start", ["starts"] = "start", ["start_datetime"] = "start", ["dtstart"] = "start",
        ["date"] = "start_date", ["time"] = "start_time",
        ["finish"] = "end", ["to"] = "end", ["ends"] = "end", ["end_datetime"] = "end", ["dtend"] = "end",
        ["all_day_event"] = "all_day", ["allday"] = "all_day",
        ["tz"] = "timezone", ["time_zone"] = "timezone", ["tzid"] = "timezone",
        ["place"] = "location", ["where"] = "location", ["venue"] = "location",
        ["details"] = "description", ["notes"] = "description", ["body"] = "description", ["note"] = "description",
        ["attendee"] = "attendees", ["guests"] = "attendees", ["participants"] = "attendees", ["required_attendees"] = "attendees",
        ["category"] = "categories", ["rrule"] = "recurrence", ["repeat"] = "recurrence", ["link"] = "url", ["website"] = "url",
        ["id"] = "uid",
        ["الموضوع"] = "summary", ["الحدث"] = "summary", ["العنوان"] = "summary", ["البداية"] = "start", ["تاريخ_البدء"] = "start",
        ["النهاية"] = "end", ["تاريخ_الانتهاء"] = "end", ["المكان"] = "location", ["الوصف"] = "description", ["ملاحظات"] = "description",
    };

    // ---- reading --------------------------------------------------------------------------------------------------------

    public static Table Read(string text)
    {
        var rows = new List<DataValue[]>();
        var stack = new Stack<string>();
        Dictionary<string, List<string>>? ev = null;
        string? pendingDuration = null;

        foreach (var raw in ContentLines.Unfold(text))
        {
            var line = ContentLines.Parse(raw);
            if (line is null) continue;
            if (line.Name == "BEGIN")
            {
                var component = line.Value.Trim().ToUpperInvariant();
                stack.Push(component);
                if (component == "VEVENT" && stack.Count <= 2) { ev = []; pendingDuration = null; }
                continue;
            }
            if (line.Name == "END")
            {
                var component = stack.Count > 0 ? stack.Pop() : "";
                if (component == "VEVENT" && ev is not null)
                {
                    if (!ev.ContainsKey("end") && pendingDuration is not null && ev.TryGetValue("start", out var s) && AddDuration(s[0], pendingDuration) is { } end)
                        ev["end"] = [end];
                    rows.Add(Columns.Select(c => ev.TryGetValue(c, out var v) ? DataValue.String(string.Join("; ", v)) : DataValue.Null).ToArray());
                    ev = null;
                }
                continue;
            }
            if (ev is null || stack.Count == 0 || stack.Peek() != "VEVENT") continue;

            void Add(string column, string value)
            {
                value = value.Trim();
                if (value.Length == 0) return;
                if (!ev.TryGetValue(column, out var list)) ev[column] = list = [];
                list.Add(value);
            }

            var value = line.Value;
            switch (line.Name)
            {
                case "SUMMARY": Add("summary", ContentLines.Unescape(value)); break;
                case "LOCATION": Add("location", ContentLines.Unescape(value)); break;
                case "DESCRIPTION": Add("description", ContentLines.Unescape(value)); break;
                case "STATUS": Add("status", value); break;
                case "URL": Add("url", value); break;
                case "UID": Add("uid", value); break;
                case "RRULE": Add("recurrence", value); break;
                case "CATEGORIES": Add("categories", string.Join(", ", SplitList(value))); break;
                case "ORGANIZER": Add("organizer", Person(line)); break;
                case "ATTENDEE": Add("attendees", Person(line)); break;
                case "DURATION": pendingDuration = value.Trim(); break;
                case "DTSTART":
                    var start = When(line);
                    Add("start", start.Text);
                    if (start.AllDay) ev["all_day"] = ["true"];
                    if (line.First("TZID") is { } tz && !start.Text.EndsWith('Z')) Add("timezone", tz);
                    break;
                case "DTEND":
                    Add("end", When(line).Text);
                    break;
            }
        }
        return new Table(Columns, rows);
    }

    private static IEnumerable<string> SplitList(string value)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && i + 1 < value.Length) { sb.Append(value[i]).Append(value[++i]); continue; }
            if (value[i] == ',') { yield return ContentLines.Unescape(sb.ToString()).Trim(); sb.Clear(); continue; }
            sb.Append(value[i]);
        }
        yield return ContentLines.Unescape(sb.ToString()).Trim();
    }

    private static string Person(ContentLine line)
    {
        var address = line.Value.Trim();
        if (address.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)) address = address[7..];
        var name = line.Whole("CN");
        return string.IsNullOrWhiteSpace(name) ? address : $"{name} <{address}>";
    }

    private readonly record struct Moment(string Text, bool AllDay);

    private static Moment When(ContentLine line)
    {
        var v = line.Value.Trim();
        if (v.Length == 8 && v.All(char.IsAsciiDigit)) return new($"{v[..4]}-{v[4..6]}-{v[6..]}", true);
        var m = CompactDateTime().Match(v);
        if (m.Success)
            return new($"{m.Groups[1]}-{m.Groups[2]}-{m.Groups[3]}T{m.Groups[4]}:{m.Groups[5]}:{m.Groups[6]}{(m.Groups[7].Success ? "Z" : "")}", false);
        return new(v, line.Has("VALUE", "DATE"));
    }

    // Adds an iCalendar duration (P1D, PT1H30M, P1W) to a start written by When().
    private static string? AddDuration(string start, string duration)
    {
        var m = Duration().Match(duration);
        if (!m.Success) return null;
        int G(int i) => m.Groups[i].Success ? int.Parse(m.Groups[i].Value, CultureInfo.InvariantCulture) : 0;
        var span = new TimeSpan(G(2) * 7 + G(3), G(4), G(5), G(6));
        if (m.Groups[1].Value == "-") span = -span;

        if (start.Length == 10 && DateTime.TryParseExact(start, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
            return day.Add(span).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var utc = start.EndsWith('Z');
        if (DateTime.TryParseExact(start.TrimEnd('Z'), "yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
            return time.Add(span).ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture) + (utc ? "Z" : "");
        return null;
    }

    [GeneratedRegex(@"^(\d{4})(\d{2})(\d{2})T(\d{2})(\d{2})(\d{2})(Z)?$")]
    private static partial Regex CompactDateTime();

    [GeneratedRegex(@"^([+-])?P(?:(\d+)W)?(?:(\d+)D)?(?:T(?:(\d+)H)?(?:(\d+)M)?(?:(\d+)S)?)?$")]
    private static partial Regex Duration();

    // ---- writing --------------------------------------------------------------------------------------------------------

    public static string? ColumnFor(string header)
    {
        var key = Cards.HeaderKey(header);
        if (Columns.Contains(key) || Extra.Contains(key)) return key;
        return Aliases.GetValueOrDefault(key);
    }

    private enum TimeKind { Date, Floating, Utc }

    private readonly record struct Time(DateTime Value, TimeKind Kind);

    /// <summary>A .ics file from a table. <paramref name="stamp"/> is DTSTAMP (the time the file was made).</summary>
    public static string Write(Table table, DateTime stamp, out int written, out int skipped)
    {
        var map = Cards.Map(table, ColumnFor);
        written = 0;
        skipped = 0;
        if (!map.ContainsKey("start") && !map.ContainsKey("start_date")) return "";

        var lines = new List<string> { "BEGIN:VCALENDAR", "VERSION:2.0", "PRODID:-//DropFlow//Data//EN", "CALSCALE:GREGORIAN", "METHOD:PUBLISH" };
        var stampText = stamp.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        for (var r = 0; r < table.Rows.Count; r++)
        {
            var row = table.Rows[r];
            string Get(string column) => map.TryGetValue(column, out var i) && i < row.Length ? (row[i].Text ?? "").Trim() : "";

            var timezone = Get("timezone");
            var allDay = IsYes(Get("all_day"));
            var start = ParseTime(Join(Get("start"), Get("start_date"), Get("start_time")), timezone);
            if (start is null)
            {
                skipped++;
                continue;
            }
            var end = ParseTime(Join(Get("end"), Get("end_date"), Get("end_time")), timezone);
            if (allDay || start.Value.Kind == TimeKind.Date)
            {
                allDay = true;
                start = new Time(start.Value.Value.Date, TimeKind.Date);
                end = end is { } e ? new Time(e.Value.Date, TimeKind.Date) : null;
                if (end is null || end.Value.Value <= start.Value.Value) end = new Time(start.Value.Value.AddDays(1), TimeKind.Date);
            }
            else if (end is { } e && (e.Kind == TimeKind.Date || ToComparable(e) < ToComparable(start.Value)))
            {
                end = null;
            }

            var summary = Get("summary");
            var uid = Get("uid");
            if (uid.Length == 0) uid = GeneratedUid(summary, Get("start") + Get("start_date") + Get("start_time"), r);

            lines.Add("BEGIN:VEVENT");
            lines.Add("UID:" + Single(uid));
            lines.Add("DTSTAMP:" + stampText);
            lines.Add("DTSTART" + Format(start.Value));
            if (end is { } finish) lines.Add("DTEND" + Format(finish));
            if (summary.Length > 0) lines.Add("SUMMARY:" + ContentLines.Escape(summary));
            if (Get("location") is { Length: > 0 } location) lines.Add("LOCATION:" + ContentLines.Escape(location));
            if (Get("description") is { Length: > 0 } description) lines.Add("DESCRIPTION:" + ContentLines.Escape(description));
            if (PersonLine("ORGANIZER", Get("organizer").Split(';').FirstOrDefault() ?? "") is { } organizer) lines.Add(organizer);
            foreach (var person in Get("attendees").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (PersonLine("ATTENDEE", person) is { } attendee) lines.Add(attendee);
            var categories = Get("categories").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (categories.Length > 0) lines.Add("CATEGORIES:" + string.Join(",", categories.Select(ContentLines.Escape)));
            var status = Get("status").ToUpperInvariant();
            if (status is "TENTATIVE" or "CONFIRMED" or "CANCELLED") lines.Add("STATUS:" + status);
            if (Get("url") is { Length: > 0 } url) lines.Add("URL:" + Single(url));
            if (Get("recurrence").ToUpperInvariant() is { Length: > 0 } rule && RecurrenceRule().IsMatch(rule))
                lines.Add("RRULE:" + (rule.StartsWith("RRULE:", StringComparison.Ordinal) ? rule[6..] : rule));
            lines.Add("END:VEVENT");
            written++;
        }
        lines.Add("END:VCALENDAR");

        var sb = new StringBuilder();
        foreach (var line in lines) ContentLines.AppendFolded(sb, line);
        return sb.ToString();
    }

    private static string Join(string whole, string date, string time) =>
        whole.Length > 0 ? whole : date.Length == 0 ? "" : time.Length == 0 ? date : $"{date} {time}";

    private static bool IsYes(string value) =>
        value.ToLowerInvariant() is "true" or "yes" or "y" or "1" or "x" or "on" or "نعم";

    private static DateTime ToComparable(Time t) => t.Value;

    private static string Format(Time time) => time.Kind switch
    {
        TimeKind.Date => ";VALUE=DATE:" + time.Value.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
        TimeKind.Utc => ":" + time.Value.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture),
        _ => ":" + time.Value.ToString("yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture),
    };

    private static readonly string[] DateFormats = ["yyyy-MM-dd", "yyyyMMdd", "yyyy/MM/dd"];

    private static readonly string[] TimeFormats =
    [
        "yyyy-MM-dd'T'HH:mm", "yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF", "yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss",
        "yyyyMMdd'T'HHmmss", "yyyyMMdd'T'HHmm", "yyyy-MM-dd h:mm tt", "yyyy-MM-dd hh:mm tt", "yyyy-MM-dd h:mm:ss tt", "yyyy/MM/dd HH:mm",
        "yyyy/MM/dd HH:mm:ss",
    ];

    private static Time? ParseTime(string text, string timezone)
    {
        text = text.Trim();
        if (text.Length == 0) return null;
        var c = CultureInfo.InvariantCulture;
        if (DateTime.TryParseExact(text, DateFormats, c, DateTimeStyles.None, out var date)) return new Time(date, TimeKind.Date);

        if (text.EndsWith('Z') || text.EndsWith('z'))
        {
            var bare = text[..^1];
            if (DateTime.TryParseExact(bare, TimeFormats, c, DateTimeStyles.None, out var utc)) return new Time(utc, TimeKind.Utc);
            return null;
        }
        if (OffsetSuffix().IsMatch(text) && DateTimeOffset.TryParse(text, c, DateTimeStyles.None, out var offset))
            return new Time(offset.UtcDateTime, TimeKind.Utc);

        if (!DateTime.TryParseExact(text, TimeFormats, c, DateTimeStyles.None, out var local)) return null;
        if (timezone.Length > 0 && TimeZoneInfo.TryFindSystemTimeZoneById(timezone, out var zone))
        {
            try
            {
                return new Time(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), zone), TimeKind.Utc);
            }
            catch (ArgumentException)
            {
                // a clock time that does not exist in that zone (skipped by a daylight-saving change): keep it floating
            }
        }
        return new Time(local, TimeKind.Floating);
    }

    // "Name <a@b.c>" or "a@b.c". Anything without an address is left out: ORGANIZER and ATTENDEE must be addresses.
    private static string? PersonLine(string property, string person)
    {
        person = person.Trim();
        if (person.Length == 0) return null;
        string? name = null;
        var address = person;
        var lt = person.LastIndexOf('<');
        if (lt >= 0 && person.EndsWith('>'))
        {
            name = person[..lt].Trim().Trim('"');
            address = person[(lt + 1)..^1].Trim();
        }
        if (address.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)) address = address[7..];
        if (!address.Contains('@') || address.Any(char.IsWhiteSpace)) return null;
        var cn = string.IsNullOrEmpty(name) ? "" : $";CN=\"{name.Replace("\"", "'")}\"";
        return $"{property}{cn}:mailto:{address}";
    }

    private static string GeneratedUid(string summary, string start, int row)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{summary}|{start}|{row}"));
        return Convert.ToHexStringLower(hash)[..24] + "@dropflow";
    }

    private static string Single(string value) => value.Replace("\r", "").Replace("\n", "");

    [GeneratedRegex(@"[+-]\d{2}:?\d{2}$")]
    private static partial Regex OffsetSuffix();

    [GeneratedRegex("^(RRULE:)?[A-Z0-9=;,:+-]+$")]
    private static partial Regex RecurrenceRule();
}
