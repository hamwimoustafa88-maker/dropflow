import { describe, expect, it } from "vitest";
import { registry } from "../registry";
import { windowsHint } from "../windows-hint";

describe("Windows app hint", () => {
  it("pdf: lists Office formats and OCR-type tools the browser cannot do", () => {
    const h = windowsHint([{ name: "a.pdf" }], registry)!;
    const labels = h.items.map((i) => i.label ?? i.labelKey);
    expect(labels).toEqual(expect.arrayContaining(["DOCX", "PPTX", "XLSX"]));
    expect(labels).toContain("hint.ocr");
    expect(h.items.length).toBeLessThanOrEqual(6);
    expect(h.more).toBeGreaterThan(0);
  });

  it("never lists something the web build really does", () => {
    const labels = windowsHint([{ name: "a.pdf" }], registry)!.items.map((i) => i.label);
    expect(labels).not.toContain("PNG");
    expect(labels).not.toContain("JPG");
    expect(labels).not.toContain("TXT");
  });

  it("docx (nothing converts in the browser): offers the desktop formats", () => {
    const h = windowsHint([{ name: "letter.docx" }], registry)!;
    expect(h.items.map((i) => i.label)).toEqual(expect.arrayContaining(["PDF", "TXT", "MD"]));
  });

  it("images: does not claim formats the browser already writes", () => {
    const h = windowsHint([{ name: "a.png" }], registry)!;
    const labels = h.items.map((i) => i.label);
    expect(labels).not.toContain("JPG");
    expect(labels).not.toContain("PDF");
    expect(labels).toEqual(expect.arrayContaining(["HEIC"]));
  });

  it("returns null without files", () => {
    expect(windowsHint([], registry)).toBeNull();
  });
});
