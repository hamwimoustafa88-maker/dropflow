import { describe, expect, it } from "vitest";
import { hasNoText, itemsToLines, linesToMarkdown, linesToText, type TextItemLike, type TextLine } from "../lib/pdf-text";

function item(str: string, x: number, y: number, opts: { size?: number; width?: number; dir?: string } = {}): TextItemLike {
  const size = opts.size ?? 12;
  return {
    str,
    dir: opts.dir ?? "ltr",
    transform: [size, 0, 0, size, x, y],
    width: opts.width ?? str.length * size * 0.5,
    height: size,
  };
}

function line(text: string, y: number, size = 12, rtl = false): TextLine {
  return { text, y, x: 0, size, rtl };
}

describe("itemsToLines", () => {
  it("groups items by baseline, top of the page first, and orders left to right", () => {
    const lines = itemsToLines([
      item("world", 80, 700),
      item("Hello", 20, 700),
      item("Second line", 20, 680),
      item("Title", 20, 760, { size: 24 }),
    ]);
    expect(lines.map((l) => l.text)).toEqual(["Title", "Hello world", "Second line"]);
    expect(lines[0].size).toBe(24);
    expect(lines.every((l) => !l.rtl)).toBe(true);
  });

  it("tolerates small baseline jitter inside one line", () => {
    const lines = itemsToLines([item("a", 10, 500), item("b", 40, 500.8), item("c", 70, 499.4)]);
    expect(lines).toHaveLength(1);
    expect(lines[0].text).toBe("a b c");
  });

  it("does not add spaces between touching runs but does between separated ones", () => {
    const lines = itemsToLines([item("Hel", 10, 500, { width: 18 }), item("lo", 28, 500, { width: 12 }), item("there", 60, 500, { width: 30 })]);
    expect(lines[0].text).toBe("Hello there");
  });

  it("orders Arabic (rtl) lines right to left without reversing the text of each run", () => {
    // Visually the first word is the right-most. Stream order is left-to-right here on purpose.
    const lines = itemsToLines([
      item("عالم", 10, 600, { dir: "rtl", width: 40 }),
      item("بالعربية", 100, 600, { dir: "rtl", width: 60 }),
      item("مرحبا", 200, 600, { dir: "rtl", width: 50 }),
    ]);
    expect(lines).toHaveLength(1);
    expect(lines[0].rtl).toBe(true);
    expect(lines[0].text).toBe("مرحبا بالعربية عالم");
    expect(lines[0].x).toBe(250); // right edge of the right-most run
  });

  it("keeps embedded Latin/number runs inside an Arabic line and uses the right edge as x", () => {
    const lines = itemsToLines([
      item("2026", 10, 600, { dir: "ltr", width: 30 }),
      item("عام", 60, 600, { dir: "rtl", width: 30 }),
    ]);
    expect(lines[0].rtl).toBe(true);
    expect(lines[0].text).toBe("عام 2026");
  });

  it("treats a mostly-Latin line with one Arabic word as left to right", () => {
    const lines = itemsToLines([item("The word", 10, 600, { width: 50 }), item("سلام", 80, 600, { dir: "rtl", width: 30 }), item("means peace", 120, 600, { width: 60 })]);
    expect(lines[0].rtl).toBe(false);
    expect(lines[0].text).toBe("The word سلام means peace");
  });

  it("ignores marked-content entries and empty runs; returns [] for nothing", () => {
    expect(itemsToLines([])).toEqual([]);
    const lines = itemsToLines([{ type: "beginMarkedContent" } as never, item("", 10, 10), item("ok", 10, 20)]);
    expect(lines.map((l) => l.text)).toEqual(["ok"]);
  });
});

describe("linesToText", () => {
  it("keeps line breaks, separates paragraphs and pages by a blank line", () => {
    const page1 = [line("First line", 700), line("same paragraph", 686), line("New paragraph", 640)];
    const page2 = [line("Page two", 700)];
    expect(linesToText([page1, page2])).toBe("First line\nsame paragraph\n\nNew paragraph\n\nPage two\n");
  });

  it("returns an empty string for no text", () => {
    expect(linesToText([[], []])).toBe("");
  });

  it("leaves Arabic untouched", () => {
    expect(linesToText([[line("مرحبا بالعالم", 700, 12, true)]])).toBe("مرحبا بالعالم\n");
  });
});

describe("linesToMarkdown", () => {
  it("makes headings from relative font size", () => {
    const md = linesToMarkdown([
      [
        line("Big title", 800, 24),
        line("A subtitle here", 760, 15),
        line("Body text line one", 730, 11),
        line("Body text line two", 717, 11),
        line("Body text line three", 704, 11),
        line("Body text line four", 691, 11),
      ],
    ]);
    expect(md.startsWith("# Big title\n\n## A subtitle here\n\n")).toBe(true);
    expect(md).toContain("Body text line one Body text line two Body text line three Body text line four");
  });

  it("does not turn same-size text into headings", () => {
    const md = linesToMarkdown([[line("one", 700, 12), line("two", 686, 12), line("three", 672, 12)]]);
    expect(md).toBe("one two three\n");
  });

  it("splits paragraphs on big vertical gaps", () => {
    const md = linesToMarkdown([[line("para one a", 700), line("para one b", 686), line("para two", 640)]]);
    expect(md).toBe("para one a para one b\n\npara two\n");
  });

  it("detects bullet and numbered lists (including Arabic-Indic numerals)", () => {
    const md = linesToMarkdown([
      [
        line("• apples", 700),
        line("– pears", 686),
        line("- plums", 672),
        line("1. first", 658),
        line("2) second", 644),
        line("٣. ثالث", 630),
      ],
    ]);
    expect(md).toBe("- apples\n- pears\n- plums\n1. first\n2. second\n3. ثالث\n");
  });

  it("joins a wrapped list item and de-hyphenates", () => {
    const md = linesToMarkdown([[line("• a long item that wraps", 700), line("onto the next line", 686)], [line("exam-", 700), line("ple text", 686)]]);
    expect(md).toBe("- a long item that wraps onto the next line\n\nexample text\n");
  });

  it("separates pages with a blank line and no page markers", () => {
    const md = linesToMarkdown([[line("page one text", 700)], [line("page two text", 700)]]);
    expect(md).toBe("page one text\n\npage two text\n");
  });

  it("keeps Arabic exactly and escapes accidental markdown headers", () => {
    const md = linesToMarkdown([[line("مرحبا بالعالم", 700, 12, true)], [line("#hash start", 700)]]);
    expect(md).toBe("مرحبا بالعالم\n\n\\#hash start\n");
  });

  it("returns an empty string when there is no text", () => {
    expect(linesToMarkdown([[], []])).toBe("");
  });
});

describe("hasNoText", () => {
  it("is true only when every line is blank", () => {
    expect(hasNoText([[], []])).toBe(true);
    expect(hasNoText([[line("  ", 1)]])).toBe(true);
    expect(hasNoText([[line("x", 1)]])).toBe(false);
  });
});
