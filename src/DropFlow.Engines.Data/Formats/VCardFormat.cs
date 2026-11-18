// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;
using DropFlow.Engines.Data.Model;

namespace DropFlow.Engines.Data.Formats;

/// <summary>
/// Contacts (.vcf, vCard 2.1, 3.0 and 4.0) as a table with fixed, documented columns (docs/data-formats.md), and back.
/// Reading understands folded lines, vCard 2.1 quoted-printable values with their CHARSET, bare 2.1 parameters (TEL;CELL) and 4.0
/// tel: URIs. A column can hold several values separated by "; " (two e-mail addresses). Photos, logos and X- properties are not kept.
/// Writing makes vCard 3.0 (what phones, Google, Outlook and Apple all import), UTF-8, CRLF, lines folded at 75 octets; properties with
/// non-ASCII text also carry CHARSET=UTF-8, which older Outlook versions need to show Arabic.
/// </summary>
internal static class VCardFormat
{
    public static readonly IReadOnlyList<string> Columns =
    [
        "full_name", "given_name", "family_name", "middle_name", "prefix", "suffix", "nickname",
        "organization", "department", "title",
        "email", "mobile", "home_phone", "work_phone", "phone", "fax",
        "address", "work_address", "url", "birthday", "note",
    ];

    /// <summary>Other header spellings accepted when making contacts from a table (after lower-casing, spaces and dashes become '_').</summary>
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.Ordinal)
    {
        ["name"] = "full_name", ["fn"] = "full_name", ["display_name"] = "full_name", ["fullname"] = "full_name", ["contact"] = "full_name",
        ["first_name"] = "given_name", ["firstname"] = "given_name", ["given"] = "given_name", ["first"] = "given_name",
        ["last_name"] = "family_name", ["lastname"] = "family_name", ["surname"] = "family_name", ["family"] = "family_name", ["last"] = "family_name",
        ["middle"] = "middle_name", ["additional_name"] = "middle_name",
        ["company"] = "organization", ["org"] = "organization", ["organisation"] = "organization",
        ["job_title"] = "title", ["position"] = "title",
        ["e_mail"] = "email", ["mail"] = "email", ["email_address"] = "email", ["e_mail_address"] = "email",
        ["mobile_phone"] = "mobile", ["cell"] = "mobile", ["cell_phone"] = "mobile", ["cellphone"] = "mobile",
        ["phone_number"] = "phone", ["telephone"] = "phone", ["tel"] = "phone",
        ["home"] = "home_phone", ["work"] = "work_phone", ["business_phone"] = "work_phone",
        ["home_address"] = "address", ["business_address"] = "work_address",
        ["website"] = "url", ["web"] = "url", ["web_page"] = "url",
        ["birthdate"] = "birthday", ["bday"] = "birthday", ["date_of_birth"] = "birthday",
        ["notes"] = "note", ["comment"] = "note",
        // Arabic headers people type in their own sheets
        ["الاسم"] = "full_name", ["الاسم_الكامل"] = "full_name", ["الاسم_الأول"] = "given_name", ["اسم_العائلة"] = "family_name",
        ["الشركة"] = "organization", ["المسمى_الوظيفي"] = "title", ["البريد"] = "email", ["البريد_الإلكتروني"] = "email",
        ["الهاتف"] = "phone", ["رقم_الهاتف"] = "phone", ["الجوال"] = "mobile", ["الموبايل"] = "mobile", ["العنوان"] = "address",
        ["ملاحظات"] = "note", ["تاريخ_الميلاد"] = "birthday", ["الموقع"] = "url",
    };

    private static readonly string[] Identity = ["full_name", "given_name", "family_name", "organization", "email", "mobile", "phone", "home_phone", "work_phone"];

    // ---- reading --------------------------------------------------------------------------------------------------------

    public static Table Read(string text)
    {
        var rows = new List<DataValue[]>();
        Dictionary<string, List<string>>? card = null;
        var depth = 0;
        foreach (var raw in ContentLines.Unfold(text))
        {
            var line = ContentLines.Parse(raw);
            if (line is null) continue;
            if (line.Name == "BEGIN" && line.Value.Trim().Equals("VCARD", StringComparison.OrdinalIgnoreCase))
            {
                if (depth++ == 0) card = [];
                continue;
            }
            if (line.Name == "END" && line.Value.Trim().Equals("VCARD", StringComparison.OrdinalIgnoreCase))
            {
                if (--depth == 0 && card is not null) rows.Add(ToRow(card));
                if (depth < 0) depth = 0;
                continue;
            }
            if (depth == 1 && card is not null) Take(card, line);
        }
        return new Table(Columns, rows);
    }

    private static void Take(Dictionary<string, List<string>> card, ContentLine line)
    {
        void Add(string column, string value)
        {
            value = value.Trim();
            if (value.Length == 0) return;
            if (!card.TryGetValue(column, out var list)) card[column] = list = [];
            if (!list.Contains(value)) list.Add(value);
        }

        var text = ContentLines.RawText(line);
        switch (line.Name)
        {
            case "FN": Add("full_name", ContentLines.Unescape(text)); break;
            case "N":
                var n = ContentLines.Components(text);
                string Part(int i) => i < n.Count ? n[i].Replace(",", " ").Trim() : "";
                Add("family_name", Part(0)); Add("given_name", Part(1)); Add("middle_name", Part(2)); Add("prefix", Part(3)); Add("suffix", Part(4));
                break;
            case "NICKNAME": Add("nickname", ContentLines.Unescape(text)); break;
            case "ORG":
                var org = ContentLines.Components(text);
                Add("organization", org[0]);
                if (org.Count > 1) Add("department", string.Join(" / ", org.Skip(1).Where(p => p.Trim().Length > 0)));
                break;
            case "TITLE": Add("title", ContentLines.Unescape(text)); break;
            case "EMAIL": Add("email", StripScheme(ContentLines.Unescape(text), "mailto:")); break;
            case "TEL":
                var number = StripScheme(ContentLines.Unescape(text), "tel:");
                Add(line.Has("TYPE", "fax") ? "fax"
                    : line.Has("TYPE", "cell") || line.Has("TYPE", "mobile") ? "mobile"
                    : line.Has("TYPE", "work") ? "work_phone"
                    : line.Has("TYPE", "home") ? "home_phone"
                    : "phone", number);
                break;
            case "ADR":
                var parts = ContentLines.Components(text).Select(p => p.Replace("\n", ", ").Trim()).Where(p => p.Length > 0);
                Add(line.Has("TYPE", "work") ? "work_address" : "address", string.Join(", ", parts));
                break;
            case "URL": Add("url", ContentLines.Unescape(text)); break;
            case "BDAY": Add("birthday", Birthday(ContentLines.Unescape(text))); break;
            case "NOTE": Add("note", ContentLines.Unescape(text)); break;
        }
    }

    private static DataValue[] ToRow(Dictionary<string, List<string>> card)
    {
        if (!card.ContainsKey("full_name"))
        {
            var composed = string.Join(" ", new[] { "prefix", "given_name", "middle_name", "family_name", "suffix" }
                .Select(k => card.TryGetValue(k, out var v) ? v[0] : "").Where(s => s.Length > 0));
            if (composed.Length > 0) card["full_name"] = [composed];
        }
        return Columns.Select(c => card.TryGetValue(c, out var v) ? DataValue.String(string.Join("; ", v)) : DataValue.Null).ToArray();
    }

    private static string StripScheme(string value, string scheme) =>
        value.StartsWith(scheme, StringComparison.OrdinalIgnoreCase) ? value[scheme.Length..] : value;

    // 19900115 -> 1990-01-15; 1990-01-15T00:00:00Z -> 1990-01-15; anything else as written (--0115 has no year).
    private static string Birthday(string value)
    {
        var v = value.Trim();
        if (v.Length == 8 && v.All(char.IsAsciiDigit)) return $"{v[..4]}-{v[4..6]}-{v[6..]}";
        if (v.Length > 10 && v[4] == '-' && v[7] == '-' && v[10] == 'T') return v[..10];
        return v;
    }

    // ---- writing --------------------------------------------------------------------------------------------------------

    /// <summary>The canonical column a table header stands for, or null ("E-mail Address" -> email, "الجوال" -> mobile).</summary>
    public static string? ColumnFor(string header)
    {
        var key = Cards.HeaderKey(header);
        if (Columns.Contains(key)) return key;
        return Aliases.GetValueOrDefault(key);
    }

    /// <summary>A .vcf file from a table. Rows with no name, organisation, e-mail or phone are skipped and counted.</summary>
    public static string Write(Table table, out int written, out int skipped)
    {
        var map = Cards.Map(table, ColumnFor);
        written = 0;
        skipped = 0;
        var sb = new StringBuilder();
        if (!map.Keys.Any(Identity.Contains)) return "";

        foreach (var row in table.Rows)
        {
            string Get(string column) => map.TryGetValue(column, out var i) && i < row.Length ? (row[i].Text ?? "").Trim() : "";
            if (Identity.All(c => Get(c).Length == 0))
            {
                skipped++;
                continue;
            }

            var fullName = Get("full_name");
            if (fullName.Length == 0)
                fullName = string.Join(" ", new[] { Get("prefix"), Get("given_name"), Get("middle_name"), Get("family_name"), Get("suffix") }.Where(s => s.Length > 0));
            if (fullName.Length == 0) fullName = new[] { Get("organization"), Get("email"), Get("mobile"), Get("phone"), Get("work_phone"), Get("home_phone") }.First(s => s.Length > 0);

            var given = Get("given_name");
            var family = Get("family_name");
            if (given.Length == 0 && family.Length == 0 && Get("full_name").Length > 0)
            {
                // N is required in 3.0; a plain "Ali Hassan" is split at the last space, the usual guess.
                var space = fullName.LastIndexOf(' ');
                if (space > 0) { given = fullName[..space]; family = fullName[(space + 1)..]; }
                else given = fullName;
            }

            var lines = new List<string> { "BEGIN:VCARD", "VERSION:3.0" };
            void Text(string name, string value) { if (value.Length > 0) lines.Add(Prop(name, value, ContentLines.Escape(value))); }
            lines.Add(Prop("FN", fullName, ContentLines.Escape(fullName)));
            var nParts = new[] { family, given, Get("middle_name"), Get("prefix"), Get("suffix") };
            lines.Add(Prop("N", string.Concat(nParts), string.Join(";", nParts.Select(ContentLines.Escape))));
            Text("NICKNAME", Get("nickname"));
            var org = Get("organization");
            var dept = Get("department");
            if (org.Length > 0 || dept.Length > 0)
                lines.Add(Prop("ORG", org + dept, dept.Length > 0 ? $"{ContentLines.Escape(org)};{ContentLines.Escape(dept)}" : ContentLines.Escape(org)));
            Text("TITLE", Get("title"));
            foreach (var email in Many(Get("email"))) lines.Add(Prop("EMAIL;TYPE=INTERNET", email, Single(email)));
            foreach (var (column, type) in new[] { ("mobile", "CELL"), ("home_phone", "HOME,VOICE"), ("work_phone", "WORK,VOICE"), ("phone", "VOICE"), ("fax", "FAX") })
                foreach (var number in Many(Get(column))) lines.Add(Prop($"TEL;TYPE={type}", number, Single(number)));
            foreach (var (column, type) in new[] { ("address", "HOME"), ("work_address", "WORK") })
                foreach (var address in Many(Get(column))) lines.Add(Prop($"ADR;TYPE={type}", address, $";;{ContentLines.Escape(address)};;;;"));
            var url = Get("url");
            if (url.Length > 0) lines.Add(Prop("URL", url, Single(url)));
            var birthday = Get("birthday");
            if (birthday.Length > 0) lines.Add(Prop("BDAY", birthday, Single(birthday)));
            Text("NOTE", Get("note"));
            lines.Add("END:VCARD");

            foreach (var line in lines) ContentLines.AppendFolded(sb, line);
            written++;
        }
        return sb.ToString();
    }

    private static string Prop(string nameAndParams, string plain, string encoded) =>
        plain.Any(c => c > 127) ? $"{nameAndParams};CHARSET=UTF-8:{encoded}" : $"{nameAndParams}:{encoded}";

    private static string Single(string value) => value.Replace("\r", " ").Replace("\n", " ");

    private static IEnumerable<string> Many(string value) =>
        value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
