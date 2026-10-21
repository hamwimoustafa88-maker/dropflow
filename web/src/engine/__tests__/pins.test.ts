import { afterEach, beforeEach, describe, expect, it } from "vitest";
import { DEFAULT_IMAGE_CAPS, setImageCaps } from "../caps";
import { PINS_STORAGE_KEY, getPins, pin, pinsVersion, resetPinsForTests, subscribePins, unpin } from "../pins-store";
import { registry } from "../registry";
import { ADD_ID, addPin, buildWheels, pageWedges, pinCategory, removePin, sanitizePins, withPins, type WheelSet } from "../wheel";

const NO_ACTIONS = { hasResults: false, canShare: false, hasText: false };
const f = (name: string) => ({ name, size: 10 });
const ids = (w: { id: string }[]) => w.map((x) => x.id);

describe("+ Add: wheels with pins (mirrors the desktop WheelBuilder + WheelPins)", () => {
  beforeEach(() => setImageCaps({ encode: { png: true, jpg: true, webp: true, avif: false }, decodeAvif: false }));
  afterEach(() => setImageCaps(DEFAULT_IMAGE_CAPS));

  it("pin category: the shared catalog category, else 'other'", () => {
    expect(pinCategory([f("a.png"), f("b.JPG")])).toBe("image");
    expect(pinCategory([f("a.png"), f("b.pdf")])).toBe("other");
    expect(pinCategory([f("noext")])).toBe("other");
  });

  it("every wheel ends with + Add listing what is on the OTHER wheels; TXT is on the image Convert wheel itself", () => {
    const own = buildWheels([f("photo.png")], registry, NO_ACTIONS);
    expect(ids(own.convert)).toContain("convert:txt");
    const { wheels, addable } = withPins(own, {}, "image");
    for (const k of ["convert", "tools"] as const) expect(wheels[k].at(-1)).toMatchObject({ id: ADD_ID, add: true, wheel: k, labelKey: "add.wedge" });
    expect(ids(addable.convert)).toEqual(expect.arrayContaining(["img.ocr", "img.remove-bg", "img.compress"]));
    expect(ids(addable.convert)).not.toContain("convert:txt");
    expect(ids(addable.tools)).toEqual(expect.arrayContaining(["convert:txt", "convert:jpg"]));
    // a wheel with nothing of its own (Actions before any result) stays empty: no lonely "+ Add"
    expect(wheels.actions).toEqual([]);
    const done = withPins(buildWheels([f("photo.png")], registry, { hasResults: true, canShare: false, hasText: false }), {}, "image");
    expect(done.wheels.actions.map((w) => w.id)).toEqual(["web.download", ADD_ID]);
  });

  it("a pinned item appears on that wheel (marked, keeping its family), leaves the + Add list, and only for that category", () => {
    const own = buildWheels([f("photo.png")], registry, NO_ACTIONS);
    const pins = addPin({}, "tools", "image", "convert:txt");
    const { wheels, addable, pinned } = withPins(own, pins, "image");
    const added = wheels.tools.find((w) => w.id === "convert:txt");
    expect(added).toMatchObject({ pinned: true, wheel: "convert", operation: "convert:txt", label: "TXT" });
    expect(wheels.tools.at(-1)?.id).toBe(ADD_ID); // + Add stays last
    expect(ids(pinned.tools)).toEqual(["convert:txt"]);
    expect(ids(addable.tools)).not.toContain("convert:txt");
    // a PDF selection does not show the image pin
    const pdf = withPins(buildWheels([f("a.pdf")], registry, NO_ACTIONS), pins, "pdf");
    expect(ids(pdf.wheels.tools)).not.toContain("convert:txt");
  });

  it("pins that do not apply (no converter for these files) are kept but not shown", () => {
    const own = buildWheels([f("photo.png")], registry, NO_ACTIONS);
    const pins = addPin(addPin({}, "convert", "image", "vid.trim"), "convert", "image", "img.ocr");
    const { wheels } = withPins(own, pins, "image");
    expect(ids(wheels.convert)).toContain("img.ocr");
    expect(ids(wheels.convert)).not.toContain("vid.trim");
  });

  it("no files: no wheels and no + Add", () => {
    const empty: WheelSet = { convert: [], tools: [], actions: [] };
    expect(withPins(empty, {}, "other").wheels).toEqual(empty);
  });

  it("paging keeps + Add as the last wedge of the last page", () => {
    const own = buildWheels([f("photo.png")], registry, NO_ACTIONS);
    const pages = pageWedges(withPins(own, {}, "image").wheels.tools);
    expect(pages.length).toBeGreaterThan(1);
    const last = pages.at(-1)!;
    expect(last.filter((w) => !w.more).at(-1)?.id).toBe(ADD_ID);
  });

  it("add/remove return new objects, ignore duplicates and drop empty lists; stored data is sanitised", () => {
    const a = addPin({}, "convert", "image", "img.ocr");
    expect(addPin(a, "convert", "image", "img.ocr")).toBe(a);
    const b = addPin(a, "convert", "image", "img.qr");
    expect(b.convert?.image).toEqual(["img.ocr", "img.qr"]);
    expect(removePin(removePin(b, "convert", "image", "img.ocr"), "convert", "image", "img.qr")).toEqual({});
    expect(sanitizePins({ convert: { image: ["img.ocr", "img.ocr", 3, "", ADD_ID] }, nope: { image: ["x"] }, tools: "bad" })).toEqual({ convert: { image: ["img.ocr"] } });
    expect(sanitizePins(null)).toEqual({});
    expect(sanitizePins("[]")).toEqual({});
  });
});

describe("pins store (localStorage, with an in-memory fallback)", () => {
  const store = new Map<string, string>();
  afterEach(() => {
    store.clear();
    resetPinsForTests();
    delete (globalThis as { localStorage?: unknown }).localStorage;
  });

  it("persists to localStorage and reads it back after a 'reload'", () => {
    (globalThis as { localStorage?: unknown }).localStorage = {
      getItem: (k: string) => store.get(k) ?? null,
      setItem: (k: string, v: string) => void store.set(k, v),
    };
    let calls = 0;
    const off = subscribePins(() => calls++);
    pin("tools", "image", "convert:txt");
    expect(JSON.parse(store.get(PINS_STORAGE_KEY)!)).toEqual({ tools: { image: ["convert:txt"] } });
    expect(calls).toBe(1);
    expect(pinsVersion()).toBe(1);
    resetPinsForTests(); // a new page load
    expect(getPins()).toEqual({ tools: { image: ["convert:txt"] } });
    unpin("tools", "image", "convert:txt");
    expect(JSON.parse(store.get(PINS_STORAGE_KEY)!)).toEqual({});
    off();
  });

  it("works for the session when storage is blocked or holds garbage", () => {
    (globalThis as { localStorage?: unknown }).localStorage = {
      getItem: () => "{not json",
      setItem: () => {
        throw new Error("QuotaExceeded");
      },
    };
    expect(getPins()).toEqual({});
    pin("convert", "image", "img.ocr");
    expect(getPins()).toEqual({ convert: { image: ["img.ocr"] } });
  });
});
