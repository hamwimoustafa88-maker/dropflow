// SPDX-License-Identifier: GPL-3.0-or-later
import { describe, expect, it } from "vitest";
import { dataConverter } from "../converters/data";
import { convertData, readData, runData, type XmlParse } from "../converters/data.impl";
import { readICal, readVCard, writeVCard } from "../lib/data-cards";
import { parseDelimited, parseJson, toTable, writeJson, xmlName, type XmlDocumentLike, type XmlNodeLike } from "../lib/data-tree";
import { registry } from "../registry";
import { buildWheels } from "../wheel";
import { ConvertError } from "../types";

const AR = "علي حسن";

// ---- a tiny XML reader for the tests: Node has no DOMParser, the browser's is used in the app ----------------------------

const ENTITIES: Record<string, string> = { amp: "&", lt: "<", gt: ">", quot: '"', apos: "'" };
const decode = (s: string) => s.replace(/&(#x[0-9a-f]+|#\d+|\w+);/gi, (_, e: string) =>
  e[0] === "#" ? String.fromCodePoint(e[1] === "x" ? parseInt(e.slice(2), 16) : parseInt(e.slice(1), 10)) : ENTITIES[e] ?? `&${e};`);

function node(type: number, name: string, value: string | null = null): XmlNodeLike & { childNodes: XmlNodeLike[]; attributes: { name: string; localName: string; prefix: string | null; value: string }[] } {
  return { nodeType: type, nodeName: name, localName: type === 1 ? name.replace(/^.*:/, "") : null, nodeValue: value, childNodes: [], attributes: [] };
}

const testXml: XmlParse = (text) => {
  const doc: XmlDocumentLike & { errors: number } = { doctype: /^\s*(?:<\?[\s\S]*?\?>\s*|<!--[\s\S]*?-->\s*)*<!DOCTYPE/i.test(text) ? {} : null, documentElement: null, errors: 0, getElementsByTagName: () => [] };
  const stack: ReturnType<typeof node>[] = [];
  const re = /<!\[CDATA\[([\s\S]*?)\]\]>|<!--[\s\S]*?-->|<\?[\s\S]*?\?>|<!DOCTYPE[\s\S]*?>|<\/([\w:.-]+)\s*>|<([\w:.-]+)((?:\s+[\w:.-]+\s*=\s*(?:"[^"]*"|'[^']*'))*)\s*(\/?)>|([^<]+)/g;
  for (const m of text.matchAll(re)) {
    const top = stack[stack.length - 1];
    if (m[1] !== undefined) top?.childNodes.push(node(4, "#cdata-section", m[1]));
    else if (m[2]) {
      if (stack.pop()?.nodeName !== m[2]) doc.errors++;
    } else if (m[3]) {
      const el = node(1, m[3]);
      for (const a of (m[4] ?? "").matchAll(/([\w:.-]+)\s*=\s*(?:"([^"]*)"|'([^']*)')/g)) {
        const prefix = a[1].includes(":") ? a[1].split(":")[0] : null;
        el.attributes.push({ name: a[1], localName: a[1].replace(/^.*:/, ""), prefix, value: decode(a[2] ?? a[3] ?? "") });
      }
      if (top) top.childNodes.push(el);
      else doc.documentElement = el;
      if (!m[5]) stack.push(el);
    } else if (m[6] !== undefined && top) top.childNodes.push(node(3, "#text", decode(m[6])));
  }
  if (stack.length || doc.errors) return { doctype: null, documentElement: node(1, "parsererror"), getElementsByTagName: () => [1] };
  return doc;
};

const conv = (text: string, ext: string, op: string) => convertData(text, ext, op, { xml: testXml, now: new Date(Date.UTC(2026, 10, 18, 9, 30)) });
const json = async (text: string, ext: string) => JSON.parse((await conv(text, ext, "convert:json")).text);
const errCode = async (p: Promise<unknown>) => {
  try {
    await p;
  } catch (e) {
    if (e instanceof ConvertError) return e.code;
    throw e;
  }
  return "no error";
};

describe("data converter manifest", () => {
  it.each([
    ["convert:xml", "json", true],
    ["convert:json", "ndjson", true],
    ["convert:csv", "vcf", true],
    ["convert:jsonl", "csv", true],
    ["convert:json", "ics", true],
    ["convert:json", "json", false],
    ["convert:yaml", "json", false], // no YAML in the browser
    ["convert:json", "yml", false],
    ["convert:xlsx", "json", false],
    ["data.to-vcard", "csv", true],
    ["data.to-ical", "tsv", true],
    ["data.to-vcard", "json", false],
    ["convert:json", "png", false],
  ])("%s on .%s -> %s", (op, ext, expected) => expect(dataConverter.supports(op, ext)).toBe(expected));

  it("reads XML on the main thread (DOMParser is not in workers) and the rest in the worker", () => {
    expect(dataConverter.runtime("convert:json", "xml")).toBe("main");
    expect(dataConverter.runtime("convert:xml", "json")).toBe("worker");
  });

  it("is registered and refuses files over 200 MB", () => {
    expect(registry.find("convert:jsonl", "json")?.id).toBe("data");
    expect(dataConverter.maxBytes?.("convert:json")).toBe(200 * 1024 * 1024);
  });

  it("puts the data formats on the wheel of a JSON file, and contacts/calendar tools on a CSV file", () => {
    const actions = { hasResults: false, canShare: false, hasText: false };
    const j = buildWheels([{ name: "people.json", size: 10 }], registry, actions);
    expect(j.convert.map((w) => w.label)).toEqual(["JSONL", "XML", "CSV", "TSV"]);
    const c = buildWheels([{ name: "people.csv", size: 10 }], registry, actions);
    expect(c.convert.map((w) => w.label)).toEqual(expect.arrayContaining(["JSON", "TSV", "JSONL", "XML"]));
    expect(c.tools.map((w) => w.operation)).toEqual(expect.arrayContaining(["data.to-vcard", "data.to-ical"]));
    const v = buildWheels([{ name: "contacts.vcf", size: 10 }], registry, actions);
    expect(v.convert.map((w) => w.label)).toEqual(["JSON", "JSONL", "XML", "CSV", "TSV"]);
  });
});

describe("JSON and JSON Lines", () => {
  it("keeps numbers exactly as written, key order and Arabic", async () => {
    const src = `{"b":1.10,"a":12345678901234567890,"name":"${AR}","nested":{"x":[1,{"y":null}]},"t":true}`;
    expect(writeJson(parseJson(src), 0)).toBe(src);
    const out = (await conv(src, "json", "convert:jsonl")).text;
    expect(out).toBe(src + "\n");
  });

  it("refuses broken JSON, deep nesting and names the bad JSON Lines line", async () => {
    expect(await errCode(conv("{ nope", "json", "convert:xml"))).toBe("dataInvalid");
    expect(await errCode(conv("[".repeat(5000) + "]".repeat(5000), "json", "convert:xml"))).toBe("dataInvalid");
    let error: unknown;
    try {
      await conv('{"a":1}\n\n{oops}\n', "jsonl", "convert:json");
    } catch (e) {
      error = e;
    }
    expect(error).toBeInstanceOf(ConvertError);
    expect((error as ConvertError).code).toBe("dataInvalidLine");
    expect((error as ConvertError).params.line).toBe(3);
  });
});

describe("tables", () => {
  it("flattens records with dotted keys into CSV with a BOM and quotes what needs quoting", async () => {
    const src = `{"id":1,"name":"${AR}","address":{"city":"بيروت","zip":"01234"},"tags":["a","b"]}\n{"id":2,"name":"Sam, Jr.","note":"say \\"hi\\"\\nbye"}\n`;
    const { text } = await conv(src, "jsonl", "convert:csv");
    expect(text.charCodeAt(0)).toBe(0xfeff);
    expect(text.slice(1)).toBe(
      "id,name,address.city,address.zip,tags.0,tags.1,note\r\n" + `1,${AR},بيروت,01234,a,b,\r\n` + '2,"Sam, Jr.",,,,,"say ""hi""\nbye"\r\n',
    );
  });

  it("finds wrapped records, keeps the wrapper of a single record and refuses what is not a table", async () => {
    expect(toTable(parseJson('{"data":{"total":2,"rows":[{"a":1},{"a":2}]}}'))?.rows.length).toBe(2);
    expect(toTable(parseJson('{"person":{"name":"Ann"}}'))?.columns).toEqual(["person.name"]);
    expect(toTable(parseJson('[["name","city"],["Ali","Beirut"]]'))?.columns).toEqual(["name", "city"]);
    for (const src of ['"text"', "[]", '[{"a":1}, 2]'])
      expect(await errCode(conv(src, "json", "convert:tsv"))).toBe("notTable");
  });

  it("reads semicolon CSV and types only plain numbers", async () => {
    const rows = await json(`name;price;code\n${AR};12.5;007\n`, "csv");
    expect(rows).toEqual([{ name: AR, price: 12.5, code: "007" }]);
    expect(parseDelimited("a\tb\n1\t2\n", "\t").columns).toEqual(["a", "b"]);
  });
});

describe("XML", () => {
  const library = `<?xml version="1.0" encoding="utf-8"?>
<library xmlns:x="urn:x" version="2">
  <!-- a comment -->
  <book id="1"><title>ألف ليلة وليلة</title><year>1704</year></book>
  <book id="2"><title lang="en">Dune</title><x:year>1965</x:year><note/></book>
  <motto>Read <![CDATA[<more>]]></motto>
</library>`;

  it("maps attributes to @name, repeated elements to arrays and text to #text", async () => {
    const tree = await json(library, "xml");
    expect(tree.library["@version"]).toBe("2");
    expect(tree.library["@x"]).toBeUndefined();
    expect(tree.library.book).toHaveLength(2);
    expect(tree.library.book[0]).toEqual({ "@id": "1", title: "ألف ليلة وليلة", year: "1704" });
    expect(tree.library.book[1]).toEqual({ "@id": "2", title: { "@lang": "en", "#text": "Dune" }, year: "1965", note: null });
    expect(tree.library.motto).toBe("Read <more>");
  });

  it("writes XML that reads back to the same tree", async () => {
    const src = `{"shop":{"@city":"بيروت","item":[{"@id":"1","name":"${AR}"},{"@id":"2","name":"B & C <d>"}],"first name":"x"}}`;
    const xml = (await conv(src, "json", "convert:xml")).text;
    expect(xml).toContain('<shop city="بيروت">');
    expect(xml).toContain("<name>B &amp; C &lt;d&gt;</name>");
    expect(xml).toContain("<first_x0020_name>x</first_x0020_name>");
    const back = await json(xml, "xml");
    expect(back.shop.item[0].name).toBe(AR);
    expect(back.shop.item[1]["@id"]).toBe("2");
    expect(xmlName("2024")).toBe("_x0032_024");
  });

  it("refuses a DTD before parsing (no entity is ever expanded)", async () => {
    const xxe = `<?xml version="1.0"?>\n<!DOCTYPE r [<!ENTITY s SYSTEM "file:///etc/passwd">]>\n<r>&s;</r>`;
    let parsed = false;
    const spy: XmlParse = (t) => ((parsed = true), testXml(t));
    expect(await errCode(convertData(xxe, "xml", "convert:json", { xml: spy }))).toBe("dataInvalid");
    expect(parsed).toBe(false);
    expect(await errCode(conv("<a><b></a>", "xml", "convert:json"))).toBe("dataInvalid");
    // "<!DOCTYPE" inside the content is not a DTD
    expect((await json("<r><![CDATA[<!DOCTYPE html>]]></r>", "xml")).r).toBe("<!DOCTYPE html>");
  });
});

describe("vCard", () => {
  const qp = Array.from(new TextEncoder().encode(AR), (b) => "=" + b.toString(16).toUpperCase().padStart(2, "0")).join("");
  const cards = [
    "BEGIN:VCARD", "VERSION:2.1", `N;CHARSET=UTF-8;ENCODING=QUOTED-PRINTABLE:${qp.slice(0, 30)}=`, `${qp.slice(30)};;;;`, "TEL;CELL:+961 3 123 456", "END:VCARD",
    "BEGIN:VCARD", "VERSION:3.0", "FN:Sam Smith", "N:Smith;Sam;;Dr.;", "ORG:Acme\\, Inc.;Research", "EMAIL;TYPE=INTERNET:sam@acme.test", "EMAIL:sam@home.test",
    "TEL;TYPE=WORK,VOICE:+1 555 0100", "ADR;TYPE=WORK:;;1 Main St;Springfield;IL;62701;USA", "NOTE:First\\nsecond\\, comma", "BDAY:19800115", "END:VCARD",
    "BEGIN:VCARD", "VERSION:4.0", "FN:ليلى", 'TEL;VALUE=uri;TYPE="cell,voice":tel:+44-20-7946-0000', "NOTE:folded across two lin", " es", "END:VCARD",
  ].join("\r\n");

  it("reads 2.1 quoted-printable, 3.0 and 4.0 cards into the documented columns", () => {
    const t = readVCard(cards);
    const rows = t.rows.map((r) => Object.fromEntries(t.columns.map((c, i) => [c, r[i].k === "null" ? "" : (r[i] as { v: string }).v])));
    expect(t.columns.slice(0, 4)).toEqual(["full_name", "given_name", "family_name", "middle_name"]);
    expect(rows[0]).toMatchObject({ full_name: AR, family_name: AR, mobile: "+961 3 123 456" });
    expect(rows[1]).toMatchObject({
      full_name: "Sam Smith", prefix: "Dr.", organization: "Acme, Inc.", department: "Research", email: "sam@acme.test; sam@home.test",
      work_phone: "+1 555 0100", work_address: "1 Main St, Springfield, IL, 62701, USA", note: "First\nsecond, comma", birthday: "1980-01-15",
    });
    expect(rows[2]).toMatchObject({ full_name: "ليلى", mobile: "+44-20-7946-0000", note: "folded across two lines" });
  });

  it("round-trips Arabic contacts through CSV", async () => {
    const csv = `Full Name,Mobile,E-mail,Company\n${AR},+961 3 123 456,ali@example.org,شركة النور\n,,,\n`;
    const vcf = (await conv(csv, "csv", "data.to-vcard")).text;
    expect(vcf).toContain(`FN;CHARSET=UTF-8:${AR}\r\n`);
    expect(vcf).toContain("N;CHARSET=UTF-8:حسن;علي;;;\r\n");
    for (const line of vcf.split("\r\n")) expect(new TextEncoder().encode(line).length).toBeLessThanOrEqual(75);
    const back = await json(vcf, "vcf");
    expect(back).toHaveLength(1);
    expect(back[0]).toMatchObject({ full_name: AR, given_name: "علي", family_name: "حسن", mobile: "+961 3 123 456", email: "ali@example.org", organization: "شركة النور" });
    expect(writeVCard(parseDelimited("Name,الجوال\nنور,079\n", null)).text).toContain("TEL;TYPE=CELL:079");
  });

  it("refuses a table without contact columns", async () => {
    expect(await errCode(conv("item,price\npen,2\n", "csv", "data.to-vcard"))).toBe("noContacts");
  });
});

describe("iCalendar", () => {
  const ics = [
    "BEGIN:VCALENDAR", "VERSION:2.0",
    "BEGIN:VEVENT", "UID:1@test", "DTSTART;TZID=Asia/Beirut:20261118T100000", "DTEND;TZID=Asia/Beirut:20261118T113000",
    "SUMMARY:اجتماع الفريق\\, الأسبوعي", "ORGANIZER;CN=Ann Lee:mailto:ann@example.org", 'ATTENDEE;CN="Bo, Jr.":mailto:bo@example.org',
    "BEGIN:VALARM", "DESCRIPTION:Reminder", "END:VALARM", "DESCRIPTION:Agenda", "END:VEVENT",
    "BEGIN:VEVENT", "DTSTART;VALUE=DATE:20261225", "SUMMARY:Holiday", "END:VEVENT",
    "BEGIN:VEVENT", "DTSTART:20261201T080000Z", "DURATION:PT1H30M", "SUMMARY:Call", "END:VEVENT",
    "END:VCALENDAR",
  ].join("\r\n");

  it("reads VEVENTs into the documented columns", () => {
    const t = readICal(ics);
    const rows = t.rows.map((r) => Object.fromEntries(t.columns.map((c, i) => [c, r[i].k === "null" ? "" : (r[i] as { v: string }).v])));
    expect(rows).toHaveLength(3);
    expect(rows[0]).toMatchObject({
      summary: "اجتماع الفريق, الأسبوعي", start: "2026-11-18T10:00:00", end: "2026-11-18T11:30:00", timezone: "Asia/Beirut",
      organizer: "Ann Lee <ann@example.org>", attendees: "Bo, Jr. <bo@example.org>", description: "Agenda",
    });
    expect(rows[1]).toMatchObject({ start: "2026-12-25", all_day: "true" });
    expect(rows[2]).toMatchObject({ start: "2026-12-01T08:00:00Z", end: "2026-12-01T09:30:00Z" });
  });

  it("round-trips Arabic events through CSV and turns zoned times into UTC", async () => {
    const csv = [
      "summary,start,end,timezone,location,organizer",
      "اجتماع,2026-11-18T10:00:00,2026-11-18T11:30:00,Asia/Beirut,بيروت,Ann Lee <ann@example.org>",
      "عيد,2026-12-25,,,,",
      "Review,2026-11-20 9:15 AM,,,,",
      "No start,,,,,",
    ].join("\n");
    const out = (await conv(csv, "csv", "data.to-ical")).text;
    expect(out).toContain("DTSTART:20261118T080000Z\r\n");
    expect(out).toContain("DTSTART;VALUE=DATE:20261225\r\nDTEND;VALUE=DATE:20261226\r\n");
    expect(out).toContain("DTSTART:20261120T091500\r\n");
    expect(out).toContain("DTSTAMP:20261118T093000Z\r\n");
    expect(out).toContain('ORGANIZER;CN="Ann Lee":mailto:ann@example.org\r\n');
    expect(out.split("BEGIN:VEVENT")).toHaveLength(4);
    const back = await json(out, "ics");
    expect(back[0]).toMatchObject({ summary: "اجتماع", location: "بيروت", start: "2026-11-18T08:00:00Z" });
    expect(back[1]).toMatchObject({ summary: "عيد", all_day: "true" });
  });

  it("refuses a table without a start column", async () => {
    expect(await errCode(conv("summary,place\nA,B\n", "csv", "data.to-ical"))).toBe("noEvents");
  });
});

describe("runData", () => {
  it("returns a new file named after the input, with the target extension and type", async () => {
    const input = new File([`[{"name":"${AR}"}]`], "people.json", { type: "application/json" });
    const [out] = await runData({ id: "1", operation: "convert:csv", files: [input], options: {} }, () => {}, new AbortController().signal);
    expect(out.name).toBe("people.csv");
    expect(out.type).toBe("text/csv;charset=utf-8");
    const bytes = new Uint8Array(await out.arrayBuffer());
    expect(Array.from(bytes.slice(0, 3))).toEqual([0xef, 0xbb, 0xbf]);
    expect(new TextDecoder().decode(bytes)).toContain(AR);
  });

  it("stops when cancelled", async () => {
    const ctrl = new AbortController();
    ctrl.abort();
    const input = new File(["{}"], "a.json");
    await expect(runData({ id: "1", operation: "convert:xml", files: [input], options: {} }, () => {}, ctrl.signal)).rejects.toThrow(/Cancelled/);
  });

  it("decodes a windows-1256 CSV as Arabic", async () => {
    // "علي" in windows-1256
    const bytes = new Uint8Array([0x6e, 0x61, 0x6d, 0x65, 0x0a, 0xda, 0xe1, 0xed, 0x0a]);
    const [out] = await runData({ id: "1", operation: "convert:json", files: [new File([bytes], "old.csv")], options: {} }, () => {}, new AbortController().signal);
    expect(JSON.parse(await out.text())).toEqual([{ name: "علي" }]);
  });

  it("keeps readData honest about unknown formats", () => {
    expect(() => readData("x", "yaml")).toThrow(ConvertError);
  });
});
