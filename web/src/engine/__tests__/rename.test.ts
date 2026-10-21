import { describe, expect, it } from "vitest";
import { generalConverter } from "../converters/general";
import { runGeneral } from "../converters/general.impl";
import { DEFAULT_RENAME, renameAll, renameOne } from "../lib/rename";
import catalog from "@/data/catalog.json";

const spec = (o: Partial<typeof DEFAULT_RENAME>) => ({ ...DEFAULT_RENAME, ...o });
const NOW = new Date(2026, 9, 21, 8, 5, 3);

describe("batch rename (copies; same tokens as the desktop RenameSpec)", () => {
  it("default pattern keeps the name", () => {
    expect(renameOne("Photo 1.JPG", 0, DEFAULT_RENAME)).toBe("Photo 1.JPG");
  });

  it("tokens: {name} {ext} {n} {counter:000} {date:yyyyMMdd} {original}, literal braces, no doubled dot", () => {
    expect(renameOne("a.png", 4, spec({ pattern: "trip-{counter:000}{ext}", start: 10 }), NOW)).toBe("trip-014.png");
    expect(renameOne("a.png", 0, spec({ pattern: "{date:yyyyMMdd}_{n}.{ext}" }), NOW)).toBe("20261021_1.png");
    expect(renameOne("a.png", 0, spec({ pattern: "{{x}} {original}" }), NOW)).toBe("{x} a.png");
    expect(renameOne("noext", 1, spec({ pattern: "{name}-{n}{ext}" }))).toBe("noext-2");
  });

  it("find/replace as text (special characters are literal) or as a regular expression; case and trim", () => {
    expect(renameOne("IMG_(1).jpg", 0, spec({ find: "_(1)", replace: "-one" }))).toBe("IMG-one.jpg");
    expect(renameOne("img_001.jpg", 0, spec({ find: "IMG", replace: "photo", ignoreCase: true }))).toBe("photo_001.jpg");
    expect(renameOne("img_001.jpg", 0, spec({ find: "_(\\d+)", replace: "-$1", regex: true }))).toBe("img-001.jpg");
    expect(renameOne("my  holiday photo.jpg", 0, spec({ case: "title", trim: true }))).toBe("My Holiday Photo.jpg");
    expect(renameOne("MY FILE.txt", 0, spec({ case: "sentence" }))).toBe("My file.txt");
    expect(renameOne("صورة الرحلة.jpg", 0, spec({ pattern: "{name} {counter:00}{ext}" }))).toBe("صورة الرحلة 01.jpg");
  });

  it("bad patterns are clear errors, never a silent guess", () => {
    expect(() => renameOne("a.png", 0, spec({ pattern: "{nope}" }))).toThrow(expect.objectContaining({ code: "invalidPattern" }));
    expect(() => renameOne("a.png", 0, spec({ find: "(", regex: true }))).toThrow(expect.objectContaining({ code: "invalidPattern" }));
    expect(() => renameOne("a.png", 0, spec({ pattern: "  " }))).toThrow(expect.objectContaining({ code: "invalidPattern" }));
  });

  it("copies never collide: duplicates get (2), (3); illegal characters are replaced", () => {
    expect(renameAll(["a.jpg", "b.jpg", "c.jpg"], spec({ pattern: "same{ext}" }))).toEqual(["same.jpg", "same (2).jpg", "same (3).jpg"]);
    expect(renameAll(["a.jpg"], spec({ pattern: "x:y{ext}" }))).toEqual(["x_y.jpg"]);
  });

  it("runs as a multi-file web tool that keeps the new names and the original bytes", async () => {
    expect((catalog.tools.find((t) => t.id === "gen.rename") as { platforms: string[]; multi?: boolean })).toMatchObject({ multi: true });
    expect(generalConverter.supports("gen.rename", "anything")).toBe(true);
    expect(generalConverter.keepOutputNames?.("gen.rename")).toBe(true);
    const files = [new File(["one"], "b.txt", { type: "text/plain" }), new File(["two"], "a.txt", { type: "text/plain" })];
    const out = await runGeneral(
      { id: "j", operation: "gen.rename", files, options: { pattern: "note-{counter:00}{ext}", start: 1 } },
      () => {},
      new AbortController().signal,
    );
    expect(out.map((f) => f.name)).toEqual(["note-01.txt", "note-02.txt"]);
    expect(await out[1].text()).toBe("two");
    expect(out[0].type).toBe("text/plain");
  });
});
