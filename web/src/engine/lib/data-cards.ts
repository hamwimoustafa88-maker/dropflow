// SPDX-License-Identifier: GPL-3.0-or-later
// Contacts (.vcf, vCard 2.1/3.0/4.0) and calendars (.ics, iCalendar VEVENT) as tables with fixed, documented columns, and
// back from a table (docs/data-formats.md). Same columns and rules as the desktop (src/DropFlow.Engines.Data/Formats).

import { NULL, str, textOf, type DValue, type Table } from "./data-tree";

// ---- content lines (shared by vCard and iCalendar) ----------------------------------------------------------------------

interface ContentLine {
  name: string;
  params: [string, string][];
  value: string;
}

function isQuotedPrintable(line: string): boolean {
  const colon = line.indexOf(":");
  return colon > 0 && /QUOTED-PRINTABLE/i.test(line.slice(0, colon));
}

/** Physical lines to logical lines: a leading space/tab continues a line; a quoted-printable value ending in '=' continues too. */
export function unfold(text: string): string[] {
  const out: string[] = [];
  let current: string | null = null;
  for (const line of text.replace(/\r\n?/g, "\n").split("\n")) {
    if (current !== null && current.endsWith("=") && isQuotedPrintable(current)) {
      current = current.slice(0, -1) + line.replace(/^[ \t]+/, "");
      continue;
    }
    if (current !== null && (line.startsWith(" ") || line.startsWith("\t"))) {
      current += line.slice(1);
      continue;
    }
    if (current !== null) out.push(current);
    current = line;
  }
  if (current) out.push(current);
  return out;
}

function splitOutsideQuotes(text: string, sep: string): string[] {
  const parts: string[] = [];
  let cur = "";
  let quoted = false;
  for (const c of text) {
    if (c === '"') quoted = !quoted;
    if (c === sep && !quoted) {
      parts.push(cur);
      cur = "";
    } else cur += c;
  }
  parts.push(cur);
  return parts;
}

function parseLine(line: string): ContentLine | null {
  let colon = -1;
  let quoted = false;
  for (let i = 0; i < line.length; i++) {
    if (line[i] === '"') quoted = !quoted;
    else if (line[i] === ":" && !quoted) {
      colon = i;
      break;
    }
  }
  if (colon <= 0) return null;
  const head = splitOutsideQuotes(line.slice(0, colon), ";");
  let name = head[0].trim();
  const dot = name.lastIndexOf(".");
  if (dot >= 0) name = name.slice(dot + 1);
  const params: [string, string][] = head.slice(1).map((p) => {
    const eq = p.indexOf("=");
    if (eq >= 0) return [p.slice(0, eq).trim().toUpperCase(), p.slice(eq + 1).trim()];
    const bare = p.trim();
    return [/^(QUOTED-PRINTABLE|BASE64|8BIT|7BIT|B)$/i.test(bare) ? "ENCODING" : "TYPE", bare];
  });
  return { name: name.toUpperCase(), params, value: line.slice(colon + 1) };
}

/** All values of a parameter, split on commas (TYPE="work,voice" is a list). */
function param(l: ContentLine, name: string): string[] {
  return l.params
    .filter(([k]) => k === name)
    .flatMap(([, v]) => v.replace(/"/g, "").split(","))
    .map((v) => v.trim())
    .filter(Boolean);
}
const has = (l: ContentLine, name: string, value: string) => param(l, name).some((v) => v.toLowerCase() === value);
/** A parameter's whole value without quotes (CN="Bo, Jr." is one name). */
const whole = (l: ContentLine, name: string) => l.params.find(([k]) => k === name)?.[1].replace(/^"|"$/g, "");

function decodeQuotedPrintable(text: string, charset: string | undefined): string {
  const bytes: number[] = [];
  for (let i = 0; i < text.length; i++) {
    const c = text[i];
    if (c === "=" && /^[0-9A-Fa-f]{2}$/.test(text.slice(i + 1, i + 3))) {
      bytes.push(parseInt(text.slice(i + 1, i + 3), 16));
      i += 2;
    } else if (c === "=" && i === text.length - 1) {
      // soft line break left at the end
    } else bytes.push(...new TextEncoder().encode(c));
  }
  try {
    return new TextDecoder(charset || "utf-8").decode(new Uint8Array(bytes));
  } catch {
    return new TextDecoder("utf-8").decode(new Uint8Array(bytes));
  }
}

function rawText(l: ContentLine): string {
  const enc = param(l, "ENCODING")[0];
  return enc && enc.toUpperCase() === "QUOTED-PRINTABLE" ? decodeQuotedPrintable(l.value, param(l, "CHARSET")[0]) : l.value;
}

export function unescapeText(v: string): string {
  return v.replace(/\\([\s\S])/g, (_, c: string) => (c === "n" || c === "N" ? "\n" : c));
}

export function escapeText(v: string): string {
  return v.replace(/\\/g, "\\\\").replace(/;/g, "\\;").replace(/,/g, "\\,").replace(/\r\n?/g, "\n").replace(/\n/g, "\\n");
}

/** Structured value (N, ADR, ORG): split on unescaped ';', then unescape each part. */
function components(v: string): string[] {
  const parts: string[] = [];
  let cur = "";
  for (let i = 0; i < v.length; i++) {
    if (v[i] === "\\" && i + 1 < v.length) {
      cur += v[i] + v[++i];
      continue;
    }
    if (v[i] === ";") {
      parts.push(unescapeText(cur));
      cur = "";
    } else cur += v[i];
  }
  parts.push(unescapeText(cur));
  return parts;
}

/** Folds one line at 75 octets (UTF-8) without splitting a character, CRLF + space. */
export function fold(line: string): string {
  const enc = new TextEncoder();
  let out = "";
  let octets = 0;
  for (const ch of line) {
    const w = enc.encode(ch).length;
    if (octets + w > 75) {
      out += "\r\n ";
      octets = 1;
    }
    out += ch;
    octets += w;
  }
  return out + "\r\n";
}

/** "E-mail Address" -> "e_mail_address": lower case, runs of spaces, dashes, dots and slashes become '_'. */
export function headerKey(h: string): string {
  return h
    .trim()
    .toLowerCase()
    .split(/[\s\-._/]+/)
    .filter(Boolean)
    .join("_");
}

function mapColumns(t: Table, columnFor: (h: string) => string | undefined): Map<string, number> {
  const map = new Map<string, number>();
  t.columns.forEach((h, i) => {
    const c = columnFor(h);
    if (c && !map.has(c)) map.set(c, i);
  });
  return map;
}

const rowValue = (map: Map<string, number>, row: DValue[]) => (c: string) => {
  const i = map.get(c);
  return i === undefined || i >= row.length ? "" : textOf(row[i]).trim();
};

// ---- vCard -------------------------------------------------------------------------------------------------------------

export const VCARD_COLUMNS = [
  "full_name", "given_name", "family_name", "middle_name", "prefix", "suffix", "nickname",
  "organization", "department", "title",
  "email", "mobile", "home_phone", "work_phone", "phone", "fax",
  "address", "work_address", "url", "birthday", "note",
] as const;

const VCARD_ALIASES: Record<string, string> = {
  name: "full_name", fn: "full_name", display_name: "full_name", fullname: "full_name", contact: "full_name",
  first_name: "given_name", firstname: "given_name", given: "given_name", first: "given_name",
  last_name: "family_name", lastname: "family_name", surname: "family_name", family: "family_name", last: "family_name",
  middle: "middle_name", additional_name: "middle_name",
  company: "organization", org: "organization", organisation: "organization",
  job_title: "title", position: "title",
  e_mail: "email", mail: "email", email_address: "email", e_mail_address: "email",
  mobile_phone: "mobile", cell: "mobile", cell_phone: "mobile", cellphone: "mobile",
  phone_number: "phone", telephone: "phone", tel: "phone",
  home: "home_phone", work: "work_phone", business_phone: "work_phone",
  home_address: "address", business_address: "work_address",
  website: "url", web: "url", web_page: "url",
  birthdate: "birthday", bday: "birthday", date_of_birth: "birthday",
  notes: "note", comment: "note",
  "الاسم": "full_name", "الاسم_الكامل": "full_name", "الاسم_الأول": "given_name", "اسم_العائلة": "family_name",
  "الشركة": "organization", "المسمى_الوظيفي": "title", "البريد": "email", "البريد_الإلكتروني": "email",
  "الهاتف": "phone", "رقم_الهاتف": "phone", "الجوال": "mobile", "الموبايل": "mobile", "العنوان": "address",
  "ملاحظات": "note", "تاريخ_الميلاد": "birthday", "الموقع": "url",
};

const IDENTITY = ["full_name", "given_name", "family_name", "organization", "email", "mobile", "phone", "home_phone", "work_phone"];

export function vcardColumnFor(header: string): string | undefined {
  const k = headerKey(header);
  return (VCARD_COLUMNS as readonly string[]).includes(k) ? k : VCARD_ALIASES[k];
}

const stripScheme = (v: string, scheme: string) => (v.toLowerCase().startsWith(scheme) ? v.slice(scheme.length) : v);

function birthday(v: string): string {
  const t = v.trim();
  if (/^\d{8}$/.test(t)) return `${t.slice(0, 4)}-${t.slice(4, 6)}-${t.slice(6)}`;
  if (/^\d{4}-\d{2}-\d{2}T/.test(t)) return t.slice(0, 10);
  return t;
}

export function readVCard(text: string): Table {
  const rows: DValue[][] = [];
  let card: Map<string, string[]> | null = null;
  let depth = 0;
  for (const raw of unfold(text)) {
    const l = parseLine(raw);
    if (!l) continue;
    if (l.name === "BEGIN" && /^vcard$/i.test(l.value.trim())) {
      if (depth++ === 0) card = new Map();
      continue;
    }
    if (l.name === "END" && /^vcard$/i.test(l.value.trim())) {
      if (--depth === 0 && card) rows.push(cardRow(card));
      if (depth < 0) depth = 0;
      continue;
    }
    if (depth !== 1 || !card) continue;
    const c = card;
    const add = (col: string, value: string) => {
      const v = value.trim();
      if (!v) return;
      const list = c.get(col) ?? [];
      if (!list.includes(v)) list.push(v);
      c.set(col, list);
    };
    const t = rawText(l);
    switch (l.name) {
      case "FN": add("full_name", unescapeText(t)); break;
      case "N": {
        const n = components(t);
        const part = (i: number) => (n[i] ?? "").replace(/,/g, " ").trim();
        add("family_name", part(0)); add("given_name", part(1)); add("middle_name", part(2)); add("prefix", part(3)); add("suffix", part(4));
        break;
      }
      case "NICKNAME": add("nickname", unescapeText(t)); break;
      case "ORG": {
        const o = components(t);
        add("organization", o[0]);
        if (o.length > 1) add("department", o.slice(1).filter((p) => p.trim()).join(" / "));
        break;
      }
      case "TITLE": add("title", unescapeText(t)); break;
      case "EMAIL": add("email", stripScheme(unescapeText(t), "mailto:")); break;
      case "TEL": {
        const num = stripScheme(unescapeText(t), "tel:");
        const col = has(l, "TYPE", "fax") ? "fax"
          : has(l, "TYPE", "cell") || has(l, "TYPE", "mobile") ? "mobile"
          : has(l, "TYPE", "work") ? "work_phone"
          : has(l, "TYPE", "home") ? "home_phone"
          : "phone";
        add(col, num);
        break;
      }
      case "ADR": {
        const parts = components(t).map((p) => p.replace(/\n/g, ", ").trim()).filter(Boolean);
        add(has(l, "TYPE", "work") ? "work_address" : "address", parts.join(", "));
        break;
      }
      case "URL": add("url", unescapeText(t)); break;
      case "BDAY": add("birthday", birthday(unescapeText(t))); break;
      case "NOTE": add("note", unescapeText(t)); break;
    }
  }
  return { columns: [...VCARD_COLUMNS], rows };
}

function cardRow(card: Map<string, string[]>): DValue[] {
  if (!card.has("full_name")) {
    const composed = ["prefix", "given_name", "middle_name", "family_name", "suffix"].map((k) => card.get(k)?.[0] ?? "").filter(Boolean).join(" ");
    if (composed) card.set("full_name", [composed]);
  }
  return VCARD_COLUMNS.map((c) => (card.has(c) ? str(card.get(c)!.join("; ")) : NULL));
}

const nonAscii = (s: string) => Array.from(s).some((c) => c.codePointAt(0)! > 127);
const prop = (nameAndParams: string, plain: string, encoded: string) =>
  nonAscii(plain) ? `${nameAndParams};CHARSET=UTF-8:${encoded}` : `${nameAndParams}:${encoded}`;
const single = (v: string) => v.replace(/[\r\n]+/g, " ");
const many = (v: string) => v.split(";").map((s) => s.trim()).filter(Boolean);

/** vCard 3.0 from a table. Rows with no name, organisation, e-mail or phone are skipped and counted. */
export function writeVCard(t: Table): { text: string; written: number; skipped: number } {
  const map = mapColumns(t, vcardColumnFor);
  let written = 0;
  let skipped = 0;
  let text = "";
  if (!IDENTITY.some((c) => map.has(c))) return { text, written, skipped };
  for (const row of t.rows) {
    const get = rowValue(map, row);
    if (IDENTITY.every((c) => !get(c))) {
      skipped++;
      continue;
    }
    let full = get("full_name") || [get("prefix"), get("given_name"), get("middle_name"), get("family_name"), get("suffix")].filter(Boolean).join(" ");
    if (!full) full = [get("organization"), get("email"), get("mobile"), get("phone"), get("work_phone"), get("home_phone")].find(Boolean) ?? "";
    let given = get("given_name");
    let family = get("family_name");
    if (!given && !family && get("full_name")) {
      const space = full.lastIndexOf(" ");
      if (space > 0) {
        given = full.slice(0, space);
        family = full.slice(space + 1);
      } else given = full;
    }
    const lines = ["BEGIN:VCARD", "VERSION:3.0"];
    const textProp = (name: string, v: string) => v && lines.push(prop(name, v, escapeText(v)));
    lines.push(prop("FN", full, escapeText(full)));
    const n = [family, given, get("middle_name"), get("prefix"), get("suffix")];
    lines.push(prop("N", n.join(""), n.map(escapeText).join(";")));
    textProp("NICKNAME", get("nickname"));
    const org = get("organization");
    const dept = get("department");
    if (org || dept) lines.push(prop("ORG", org + dept, dept ? `${escapeText(org)};${escapeText(dept)}` : escapeText(org)));
    textProp("TITLE", get("title"));
    for (const e of many(get("email"))) lines.push(prop("EMAIL;TYPE=INTERNET", e, single(e)));
    for (const [col, type] of [["mobile", "CELL"], ["home_phone", "HOME,VOICE"], ["work_phone", "WORK,VOICE"], ["phone", "VOICE"], ["fax", "FAX"]])
      for (const p of many(get(col))) lines.push(prop(`TEL;TYPE=${type}`, p, single(p)));
    for (const [col, type] of [["address", "HOME"], ["work_address", "WORK"]])
      for (const a of many(get(col))) lines.push(prop(`ADR;TYPE=${type}`, a, `;;${escapeText(a)};;;;`));
    if (get("url")) lines.push(prop("URL", get("url"), single(get("url"))));
    if (get("birthday")) lines.push(prop("BDAY", get("birthday"), single(get("birthday"))));
    textProp("NOTE", get("note"));
    lines.push("END:VCARD");
    text += lines.map(fold).join("");
    written++;
  }
  return { text, written, skipped };
}

// ---- iCalendar ---------------------------------------------------------------------------------------------------------

export const ICAL_COLUMNS = [
  "summary", "start", "end", "all_day", "timezone", "location", "description",
  "organizer", "attendees", "categories", "status", "url", "recurrence", "uid",
] as const;

const ICAL_EXTRA = ["start_date", "start_time", "end_date", "end_time"];

const ICAL_ALIASES: Record<string, string> = {
  title: "summary", subject: "summary", event: "summary", name: "summary", event_name: "summary",
  begin: "start", from: "start", starts: "start", start_datetime: "start", dtstart: "start",
  date: "start_date", time: "start_time",
  finish: "end", to: "end", ends: "end", end_datetime: "end", dtend: "end",
  all_day_event: "all_day", allday: "all_day",
  tz: "timezone", time_zone: "timezone", tzid: "timezone",
  place: "location", where: "location", venue: "location",
  details: "description", notes: "description", body: "description", note: "description",
  attendee: "attendees", guests: "attendees", participants: "attendees", required_attendees: "attendees",
  category: "categories", rrule: "recurrence", repeat: "recurrence", link: "url", website: "url", id: "uid",
  "الموضوع": "summary", "الحدث": "summary", "العنوان": "summary", "البداية": "start", "تاريخ_البدء": "start",
  "النهاية": "end", "تاريخ_الانتهاء": "end", "المكان": "location", "الوصف": "description", "ملاحظات": "description",
};

export function icalColumnFor(header: string): string | undefined {
  const k = headerKey(header);
  return (ICAL_COLUMNS as readonly string[]).includes(k) || ICAL_EXTRA.includes(k) ? k : ICAL_ALIASES[k];
}

function moment(l: ContentLine): { text: string; allDay: boolean } {
  const v = l.value.trim();
  if (/^\d{8}$/.test(v)) return { text: `${v.slice(0, 4)}-${v.slice(4, 6)}-${v.slice(6)}`, allDay: true };
  const m = /^(\d{4})(\d{2})(\d{2})T(\d{2})(\d{2})(\d{2})(Z)?$/.exec(v);
  if (m) return { text: `${m[1]}-${m[2]}-${m[3]}T${m[4]}:${m[5]}:${m[6]}${m[7] ? "Z" : ""}`, allDay: false };
  return { text: v, allDay: has(l, "VALUE", "date") };
}

const pad2 = (n: number) => String(n).padStart(2, "0");
const isoDate = (d: Date) => `${d.getUTCFullYear()}-${pad2(d.getUTCMonth() + 1)}-${pad2(d.getUTCDate())}`;
const isoTime = (d: Date) => `${isoDate(d)}T${pad2(d.getUTCHours())}:${pad2(d.getUTCMinutes())}:${pad2(d.getUTCSeconds())}`;

function addDuration(start: string, duration: string): string | null {
  const m = /^([+-])?P(?:(\d+)W)?(?:(\d+)D)?(?:T(?:(\d+)H)?(?:(\d+)M)?(?:(\d+)S)?)?$/.exec(duration.trim());
  if (!m) return null;
  const g = (i: number) => (m[i] ? parseInt(m[i], 10) : 0);
  const ms = (((g(2) * 7 + g(3)) * 24 + g(4)) * 60 * 60 + g(5) * 60 + g(6)) * 1000 * (m[1] === "-" ? -1 : 1);
  if (/^\d{4}-\d{2}-\d{2}$/.test(start)) return isoDate(new Date(Date.parse(start + "T00:00:00Z") + ms));
  const t = /^(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2})(Z)?$/.exec(start);
  if (!t) return null;
  return isoTime(new Date(Date.parse(t[1] + "Z") + ms)) + (t[2] ?? "");
}

function person(l: ContentLine): string {
  const address = stripScheme(l.value.trim(), "mailto:");
  const name = whole(l, "CN");
  return name && name.trim() ? `${name} <${address}>` : address;
}

function splitList(v: string): string[] {
  const out: string[] = [];
  let cur = "";
  for (let i = 0; i < v.length; i++) {
    if (v[i] === "\\" && i + 1 < v.length) {
      cur += v[i] + v[++i];
      continue;
    }
    if (v[i] === ",") {
      out.push(unescapeText(cur).trim());
      cur = "";
    } else cur += v[i];
  }
  out.push(unescapeText(cur).trim());
  return out;
}

export function readICal(text: string): Table {
  const rows: DValue[][] = [];
  const stack: string[] = [];
  let ev: Map<string, string[]> | null = null;
  let duration: string | null = null;
  for (const raw of unfold(text)) {
    const l = parseLine(raw);
    if (!l) continue;
    if (l.name === "BEGIN") {
      const comp = l.value.trim().toUpperCase();
      stack.push(comp);
      if (comp === "VEVENT" && stack.length <= 2) {
        ev = new Map();
        duration = null;
      }
      continue;
    }
    if (l.name === "END") {
      const comp = stack.pop() ?? "";
      if (comp === "VEVENT" && ev) {
        const start = ev.get("start")?.[0];
        if (!ev.has("end") && duration && start) {
          const end = addDuration(start, duration);
          if (end) ev.set("end", [end]);
        }
        const e = ev;
        rows.push(ICAL_COLUMNS.map((c) => (e.has(c) ? str(e.get(c)!.join("; ")) : NULL)));
        ev = null;
      }
      continue;
    }
    if (!ev || stack[stack.length - 1] !== "VEVENT") continue;
    const e = ev;
    const add = (col: string, value: string) => {
      const v = value.trim();
      if (!v) return;
      e.set(col, [...(e.get(col) ?? []), v]);
    };
    const v = l.value;
    switch (l.name) {
      case "SUMMARY": add("summary", unescapeText(v)); break;
      case "LOCATION": add("location", unescapeText(v)); break;
      case "DESCRIPTION": add("description", unescapeText(v)); break;
      case "STATUS": add("status", v); break;
      case "URL": add("url", v); break;
      case "UID": add("uid", v); break;
      case "RRULE": add("recurrence", v); break;
      case "CATEGORIES": add("categories", splitList(v).join(", ")); break;
      case "ORGANIZER": add("organizer", person(l)); break;
      case "ATTENDEE": add("attendees", person(l)); break;
      case "DURATION": duration = v.trim(); break;
      case "DTSTART": {
        const s = moment(l);
        add("start", s.text);
        if (s.allDay) e.set("all_day", ["true"]);
        const tz = whole(l, "TZID");
        if (tz && !s.text.endsWith("Z")) add("timezone", tz);
        break;
      }
      case "DTEND": add("end", moment(l).text); break;
    }
  }
  return { columns: [...ICAL_COLUMNS], rows };
}

type TimeKind = "date" | "floating" | "utc";
interface Time {
  /** Wall-clock parts kept in a Date's UTC fields (no local time zone ever applies). */
  value: Date;
  kind: TimeKind;
}

/** Offset of `zone` from UTC (ms) at instant `utc`, or null when the zone is unknown. */
function zoneOffset(utc: number, zone: string): number | null {
  try {
    const parts = new Intl.DateTimeFormat("en-US", {
      timeZone: zone, hourCycle: "h23", year: "numeric", month: "2-digit", day: "2-digit", hour: "2-digit", minute: "2-digit", second: "2-digit",
    }).formatToParts(new Date(utc));
    const p = (t: string) => parseInt(parts.find((x) => x.type === t)!.value, 10);
    return Date.UTC(p("year"), p("month") - 1, p("day"), p("hour"), p("minute"), p("second")) - utc;
  } catch {
    return null;
  }
}

function parseTime(text: string, zone: string): Time | null {
  const t = text.trim();
  if (!t) return null;
  let m = /^(\d{4})[-/]?(\d{2})[-/]?(\d{2})$/.exec(t);
  if (m) return { value: new Date(Date.UTC(+m[1], +m[2] - 1, +m[3])), kind: "date" };
  m = /^(\d{4})[-/]?(\d{2})[-/]?(\d{2})[T ](\d{1,2}):?(\d{2})(?::?(\d{2})(?:\.\d+)?)?\s*([AaPp][Mm])?\s*(Z|[+-]\d{2}:?\d{2})?$/.exec(t);
  if (!m) return null;
  let hour = +m[4];
  if (m[7]) {
    const pm = m[7].toLowerCase() === "pm";
    if (hour === 12) hour = pm ? 12 : 0;
    else if (pm) hour += 12;
  }
  const wall = Date.UTC(+m[1], +m[2] - 1, +m[3], hour, +m[5], m[6] ? +m[6] : 0);
  if (Number.isNaN(wall) || hour > 23) return null;
  if (m[8]) {
    if (m[8] === "Z") return { value: new Date(wall), kind: "utc" };
    const sign = m[8][0] === "-" ? -1 : 1;
    const digits = m[8].replace(/[^0-9]/g, "");
    const offset = sign * (parseInt(digits.slice(0, 2), 10) * 60 + parseInt(digits.slice(2), 10)) * 60_000;
    return { value: new Date(wall - offset), kind: "utc" };
  }
  if (zone) {
    const first = zoneOffset(wall, zone);
    if (first !== null) {
      const guess = wall - first;
      const second = zoneOffset(guess, zone) ?? first;
      return { value: new Date(wall - second), kind: "utc" };
    }
  }
  return { value: new Date(wall), kind: "floating" };
}

const compact = (d: Date, withTime: boolean) =>
  `${d.getUTCFullYear()}${pad2(d.getUTCMonth() + 1)}${pad2(d.getUTCDate())}` +
  (withTime ? `T${pad2(d.getUTCHours())}${pad2(d.getUTCMinutes())}${pad2(d.getUTCSeconds())}` : "");

function formatTime(t: Time): string {
  return t.kind === "date" ? `;VALUE=DATE:${compact(t.value, false)}` : `:${compact(t.value, true)}${t.kind === "utc" ? "Z" : ""}`;
}

function personLine(property: string, raw: string): string | null {
  const p = raw.trim();
  if (!p) return null;
  let name: string | null = null;
  let address = p;
  const lt = p.lastIndexOf("<");
  if (lt >= 0 && p.endsWith(">")) {
    name = p.slice(0, lt).trim().replace(/^"|"$/g, "");
    address = p.slice(lt + 1, -1).trim();
  }
  address = stripScheme(address, "mailto:");
  if (!address.includes("@") || /\s/.test(address)) return null;
  return `${property}${name ? `;CN="${name.replace(/"/g, "'")}"` : ""}:mailto:${address}`;
}

async function uidFor(summary: string, start: string, row: number): Promise<string> {
  const bytes = new TextEncoder().encode(`${summary}|${start}|${row}`);
  const hash = new Uint8Array(await crypto.subtle.digest("SHA-256", bytes));
  return Array.from(hash.slice(0, 12), (b) => b.toString(16).padStart(2, "0")).join("") + "@dropflow";
}

const yes = (v: string) => ["true", "yes", "y", "1", "x", "on", "نعم"].includes(v.toLowerCase());

/** iCalendar from a table; times with a known IANA time zone become UTC. Rows without a readable start are skipped. */
export async function writeICal(t: Table, stamp: Date): Promise<{ text: string; written: number; skipped: number }> {
  const map = mapColumns(t, icalColumnFor);
  let written = 0;
  let skipped = 0;
  if (!map.has("start") && !map.has("start_date")) return { text: "", written, skipped };
  const lines = ["BEGIN:VCALENDAR", "VERSION:2.0", "PRODID:-//DropFlow//Data//EN", "CALSCALE:GREGORIAN", "METHOD:PUBLISH"];
  const stampText = compact(stamp, true) + "Z";
  for (let r = 0; r < t.rows.length; r++) {
    const get = rowValue(map, t.rows[r]);
    const join = (whole: string, date: string, time: string) => whole || (date ? (time ? `${date} ${time}` : date) : "");
    const zone = get("timezone");
    let start = parseTime(join(get("start"), get("start_date"), get("start_time")), zone);
    if (!start) {
      skipped++;
      continue;
    }
    let end = parseTime(join(get("end"), get("end_date"), get("end_time")), zone);
    if (yes(get("all_day")) || start.kind === "date") {
      const day = (d: Date) => new Date(Date.UTC(d.getUTCFullYear(), d.getUTCMonth(), d.getUTCDate()));
      start = { value: day(start.value), kind: "date" };
      end = end ? { value: day(end.value), kind: "date" } : null;
      if (!end || end.value <= start.value) end = { value: new Date(start.value.getTime() + 86_400_000), kind: "date" };
    } else if (end && (end.kind === "date" || end.value < start.value)) end = null;

    const summary = get("summary");
    const uid = get("uid") || (await uidFor(summary, get("start") + get("start_date") + get("start_time"), r));
    lines.push("BEGIN:VEVENT", "UID:" + uid.replace(/[\r\n]/g, ""), "DTSTAMP:" + stampText, "DTSTART" + formatTime(start));
    if (end) lines.push("DTEND" + formatTime(end));
    if (summary) lines.push("SUMMARY:" + escapeText(summary));
    if (get("location")) lines.push("LOCATION:" + escapeText(get("location")));
    if (get("description")) lines.push("DESCRIPTION:" + escapeText(get("description")));
    const organizer = personLine("ORGANIZER", get("organizer").split(";")[0] ?? "");
    if (organizer) lines.push(organizer);
    for (const a of get("attendees").split(";")) {
      const line = personLine("ATTENDEE", a);
      if (line) lines.push(line);
    }
    const categories = get("categories").split(",").map((c) => c.trim()).filter(Boolean);
    if (categories.length) lines.push("CATEGORIES:" + categories.map(escapeText).join(","));
    const status = get("status").toUpperCase();
    if (["TENTATIVE", "CONFIRMED", "CANCELLED"].includes(status)) lines.push("STATUS:" + status);
    if (get("url")) lines.push("URL:" + get("url").replace(/[\r\n]/g, ""));
    const rule = get("recurrence").toUpperCase();
    if (rule && /^(RRULE:)?[A-Z0-9=;,:+-]+$/.test(rule)) lines.push("RRULE:" + rule.replace(/^RRULE:/, ""));
    lines.push("END:VEVENT");
    written++;
  }
  lines.push("END:VCALENDAR");
  return { text: lines.map(fold).join(""), written, skipped };
}
