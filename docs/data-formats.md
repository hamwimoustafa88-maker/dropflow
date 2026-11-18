# Data formats

The **Data** family (`data` in `catalog/tools.json`, ar "البيانات") converts structured files through one tree: objects with ordered keys,
arrays, and scalar values (null, true/false, numbers kept exactly as written, strings). Every reader fills the tree and every writer reads it,
so any input works with any output. Everything runs in-process; no program is installed and nothing leaves the device.

| | Desktop (`src/DropFlow.Engines.Data`) | Web (`web/src/engine/converters/data.ts`) |
|---|---|---|
| Inputs | JSON, JSON Lines (`.jsonl`, `.ndjson`), XML, YAML (`.yaml`, `.yml`), CSV, TSV, vCard (`.vcf`), iCalendar (`.ics`) | the same without YAML |
| Outputs | JSON, JSON Lines, XML, YAML, CSV, TSV, XLSX, HTML | JSON, JSON Lines, XML, CSV, TSV |
| Tools | `data.to-vcard`, `data.to-ical` from CSV, TSV or XLSX (first sheet) | the same from CSV or TSV |
| Limit | files over 200 MB are refused | same |

On the desktop, CSV/TSV/JSON to CSV/TSV/JSON/XLSX/HTML stays with the spreadsheet code of the PDF engine (`Office/TabularTools`), so every
pair has one owner; the data engine adds YAML, XML and JSON Lines for those files and everything for the other inputs. A `.json` file gets
the Data wheel (the `data` category comes before `spreadsheet`); CSV and TSV keep the Spreadsheet wheel, where "Make contacts" and
"Make a calendar" appear. CSV/TSV to YAML, XML or JSON Lines is reachable from the CLI and recipes (and, on the web, from the wheel).

Errors are translatable notes (`note.data.*` in `src/DropFlow.Core/Resources/strings.{en,ar}.json`, `app.err.*` on the web).

## Tables (CSV, TSV, XLSX, HTML; also JSON Lines records)

- **Finding the records.** An object with a single key is opened (`{"people": [...]}`, or the XML shape `<people><person/>...</people>`),
  and so is an object whose only array holds objects when its other keys are plain values (XML attributes next to the repeated element).
  The array found is the list of records. JSON Lines writes one record per line; a document without such an array is one line.
- **Flattening.** Nested objects become dotted keys (`address.city`), arrays their index (`tags.0`, `tags.1`). Columns are the union of all
  keys in first-seen order; a record without a key has an empty cell. An empty object or array is an empty cell.
- **Arrays of arrays** are read like a CSV file: the first inner array is the header.
- **A single record** (an object that is not a list) becomes one row, flattened from the top so the wrapper stays in the names (`person.name`).
- **Not a table:** a lone value, an empty document, an empty list, or a list that mixes records and plain values. Table targets refuse these
  (`note.data.notTable`); JSON, JSON Lines, XML and YAML still accept them.
- **Types.** CSV and TSV cells become numbers only when plainly numbers (no leading zero, no plus sign, at most 15 digits), so phone numbers
  and codes such as `0123` stay text. In XLSX, numbers and booleans are typed cells; numbers longer than 15 digits are written as text so
  Excel does not round them. A sheet with Arabic text reads right to left.
- **Encoding.** CSV and TSV are written as UTF-8 with a byte order mark and CRLF line ends (Excel needs the mark to show Arabic). Text input
  is read by byte order mark, then strict UTF-8, then Windows-1256 (Arabic) or Windows-1252. A `.csv` file may use `,`, `;` or a tab.

## XML

Reading (both platforms):

- The document is an object with one key, the root element's name.
- An element with only text is that text, as a string (XML has no types). An empty element is `null`.
- Otherwise the element is an object: each attribute is `@name`, each child element a key, and text beside child elements is `#text`.
- A child name that repeats becomes an array, in document order (one child stays a single value).
- Names are local names: namespace prefixes and `xmlns` declarations are dropped. Comments and processing instructions are dropped; CDATA is text.
- **Safety:** a DOCTYPE is refused (`DtdProcessing.Prohibit`, no resolver on the desktop; refused before the browser's `DOMParser` on the web),
  so no entity is expanded and nothing outside the file is ever read. Nesting deeper than 256 levels is refused.

Writing does the reverse: `@name` keys with plain values are attributes, `#text` is text, an array repeats its key's element, an array
without a name (a top-level list, or an array inside an array) uses `<item>`, and a document that is not one keyed object is wrapped in
`<root>`. A key that is not a valid XML name is encoded the .NET way (`first name` becomes `first_x0020_name`, `2024` becomes `_x0032_024`).

## YAML (desktop only)

Read and written through YamlDotNet's event parser and emitter, never its object deserializer (a file cannot name .NET types).

- Plain scalars follow the YAML 1.2 core schema: `null`, `~` or nothing is null; `true`/`false` (any case form) are booleans; decimal,
  `0x` hex and `0o` octal integers and floats are numbers; everything else, and every quoted scalar, is a string. A plain number with a
  leading zero (`0123`) stays text so no digit is lost. `.inf` and `.nan` stay text (JSON has no such numbers).
- Anchors, aliases and `<<` merge keys are expanded (the mapping's own keys win over merged ones). Several documents in one file become an
  array. A file whose aliases would expand past 5,000,000 values, or that nests deeper than 256 levels, is refused.
- Written strings that would read back as something else (`"true"`, `"123"`, `""`, and YAML 1.1 words such as `yes`/`no`/`on`/`off`) are
  quoted; text with line breaks uses a literal block. Arabic is written as it is.

## Contacts: vCard columns

Reading understands vCard 2.1, 3.0 and 4.0: folded lines, quoted-printable values with their `CHARSET` (and soft line breaks), bare 2.1
parameters (`TEL;CELL`) and 4.0 `tel:` URIs. Photos, logos, keys and `X-` properties are not kept. Several values of one column are joined
with `; `. The table always has these columns, in this order:

| Column | vCard property | Notes |
|---|---|---|
| `full_name` | FN | composed from the name parts when FN is missing |
| `given_name`, `family_name`, `middle_name`, `prefix`, `suffix` | N | |
| `nickname` | NICKNAME | |
| `organization`, `department` | ORG | first part; the other parts joined with ` / ` |
| `title` | TITLE | |
| `email` | EMAIL | |
| `mobile` | TEL with TYPE cell/mobile | |
| `home_phone` | TEL with TYPE home | |
| `work_phone` | TEL with TYPE work | |
| `phone` | any other TEL | |
| `fax` | TEL with TYPE fax | |
| `address` | ADR (not work) | parts joined with `, ` |
| `work_address` | ADR with TYPE work | |
| `url` | URL | |
| `birthday` | BDAY | `19900115` becomes `1990-01-15` |
| `note` | NOTE | |

**Making contacts** (`data.to-vcard`) writes vCard 3.0, UTF-8, CRLF, lines folded at 75 octets, one card per row. Headers are matched
without regard to case, spaces or dashes, and these spellings also work: `name`, `display name`, `first name`, `last name`, `surname`,
`company`, `job title`, `e-mail`, `e-mail address`, `mobile phone`, `cell`, `phone number`, `telephone`, `business phone`,
`home address`, `business address`, `website`, `birthdate`, `notes`, and the Arabic headers `الاسم`, `الاسم الكامل`, `الاسم الأول`,
`اسم العائلة`, `الشركة`, `المسمى الوظيفي`, `البريد`, `البريد الإلكتروني`, `الهاتف`, `رقم الهاتف`, `الجوال`, `الموبايل`, `العنوان`,
`ملاحظات`, `تاريخ الميلاد`, `الموقع`. Values separated by `;` become separate e-mail addresses or numbers. A row needs a name,
organisation, e-mail or phone; other rows are skipped and counted in a note. When only `full_name` is given, N is guessed by splitting at
the last space. Properties with non-ASCII text carry `CHARSET=UTF-8`, which older Outlook versions need to show Arabic (other readers ignore it).

## Calendars: iCalendar columns

Only VEVENT is read (alarms inside an event, to-dos and time-zone definitions are skipped). Columns, in this order:

| Column | iCalendar property | Notes |
|---|---|---|
| `summary` | SUMMARY | |
| `start` | DTSTART | `2026-11-18T10:00:00Z` (UTC), `2026-11-18T10:00:00` (with `timezone`) or `2026-11-18` (all day) |
| `end` | DTEND, or DTSTART + DURATION | same forms as `start` |
| `all_day` | DTSTART with a date only | `true` or empty |
| `timezone` | the TZID of DTSTART | an IANA name such as `Asia/Beirut` |
| `location`, `description` | LOCATION, DESCRIPTION | |
| `organizer` | ORGANIZER | `Name <address>` or the address |
| `attendees` | ATTENDEE | joined with `; ` |
| `categories` | CATEGORIES | joined with `, ` |
| `status` | STATUS | |
| `url` | URL | |
| `recurrence` | RRULE | as written, e.g. `FREQ=WEEKLY;BYDAY=WE` |
| `uid` | UID | |

**Making a calendar** (`data.to-ical`) needs a `start` (or `start date`) column; rows whose start cannot be read are skipped and counted.
Accepted forms: `2026-11-18`, `20261118`, `2026/11/18`, `2026-11-18T10:00[:ss]`, `2026-11-18 10:00[:ss]`, `2026-11-18 9:15 AM`, compact
`20261118T100000`, each optionally ending in `Z` or an offset (`+03:00`). Exports of Outlook and Google Calendar work too: `subject`,
`start date` + `start time`, `end date` + `end time`, `all day event`, `location`, `description`; other spellings: `title`, `event`,
`begin`, `from`, `finish`, `to`, `place`, `venue`, `details`, `notes`, `guests`, `participants`, `category`, `rrule`, `repeat`, `link`,
`time zone`, `tz`, and the Arabic `الموضوع`, `الحدث`, `العنوان`, `البداية`, `تاريخ البدء`, `النهاية`, `تاريخ الانتهاء`, `المكان`, `الوصف`.
A time with a time zone the system knows is converted to UTC (so no VTIMEZONE block is needed); without one it stays floating (the
reader's own clock). An all-day event without a later end lasts one day (DTEND is the next day, as iCalendar counts it). Attendees and the
organizer must be e-mail addresses (`Name <a@b.c>` keeps the name as CN). A UID is generated from the row when the table has none.
