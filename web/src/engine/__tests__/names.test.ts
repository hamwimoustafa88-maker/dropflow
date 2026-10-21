import { describe, expect, it } from "vitest";
import { extOf, formatBytes, keptName, outputName, sanitizeFileName, stemOf, unique } from "../names";

describe("names", () => {
  it("splits extension and stem (Arabic, dots, no extension, dotfiles)", () => {
    expect(extOf("photo.JPG")).toBe("jpg");
    expect(extOf("تقرير.نهائي.pdf")).toBe("pdf");
    expect(stemOf("تقرير.نهائي.pdf")).toBe("تقرير.نهائي");
    expect(extOf("noext")).toBe("");
    expect(extOf(".gitignore")).toBe("");
    expect(stemOf(".gitignore")).toBe(".gitignore");
    expect(extOf("dir/sub/file.tar.gz")).toBe("gz");
    expect(stemOf("C:\\a\\b.png")).toBe("b");
  });

  it("names a single result `name (suffix).ext` and never equals the input name", () => {
    const n = outputName("photo.png", "converted", "png");
    expect(n).toBe("photo (converted).png");
    expect(n).not.toBe("photo.png");
  });

  it("numbers multi-file results with zero padding", () => {
    expect(outputName("doc.pdf", "pages", "png", 0, 12)).toBe("doc (pages 01).png");
    expect(outputName("doc.pdf", "pages", "png", 11, 12)).toBe("doc (pages 12).png");
    expect(outputName("doc.pdf", "split", "pdf", 1, 3)).toBe("doc (split 2).pdf");
  });

  it("makes names unique against earlier results, case-insensitively", () => {
    const taken = new Set(["photo (converted).png"]);
    expect(outputName("Photo.png", "converted", "png", 0, 1, { taken })).toBe("Photo (converted) (2).png");
    expect(unique("a.txt", new Set(["a.txt", "a (2).txt"]))).toBe("a (3).txt");
    expect(keptName("x/y.txt", { taken: new Set(["x/y.txt"]) })).toBe("x/y (2).txt");
  });

  it("sanitises illegal and reserved names", () => {
    expect(sanitizeFileName('a<b>:c"d|e?f*.txt')).toBe("a_b__c_d_e_f_.txt");
    expect(sanitizeFileName("CON.txt")).toBe("_CON.txt");
    expect(sanitizeFileName("...")).toBe("file");
    expect(sanitizeFileName("name. ")).toBe("name");
  });

  it("formats sizes", () => {
    expect(formatBytes(0)).toBe("0 B");
    expect(formatBytes(1536)).toBe("1.50 KB");
    expect(formatBytes(5 * 1024 * 1024)).toBe("5.00 MB");
    expect(formatBytes(150 * 1024 * 1024)).toBe("150 MB");
    expect(formatBytes(-1)).toBe("");
  });
});
