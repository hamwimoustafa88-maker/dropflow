import { PDFDocument } from "pdf-lib";
import { describe, expect, it, vi } from "vitest";
import { buildImagePdf } from "../lib/image-pdf";
import { ConvertError } from "../types";
import { A4 } from "../lib/image-math";
import { buildJpeg, buildPng, buildWebp, fullTiff, pngChunk } from "./images-fixtures";

const file = (bytes: Uint8Array, name: string, type: string) => new File([bytes as BlobPart], name, { type });
const never = new AbortController().signal;

async function open(bytes: Uint8Array) {
  const doc = await PDFDocument.load(bytes);
  return doc.getPages().map((p) => p.getSize());
}

describe("buildImagePdf", () => {
  it("makes one page per image, page size = image size (fit), PNG embedded without re-encoding", async () => {
    const reencode = vi.fn(async () => buildPng(1, 1));
    const bytes = await buildImagePdf(
      [file(buildPng(40, 30), "a.png", "image/png"), file(buildPng(10, 20), "b.png", "image/png")],
      "fit",
      { reencodePng: reencode },
      never,
    );
    expect(await open(bytes)).toEqual([
      { width: 40, height: 30 },
      { width: 10, height: 20 },
    ]);
    expect(reencode).not.toHaveBeenCalled();
    expect(new TextDecoder().decode(bytes.subarray(0, 5))).toBe("%PDF-");
  });

  it("embeds an upright JPEG byte-for-byte (no recompression)", async () => {
    // Structural JPEG (markers/lengths are valid, the scan is not a real picture): enough for pdf-lib's embedder.
    const { bytes: jpg } = buildJpeg({ width: 64, height: 48, exifTiff: fullTiff(true, { orientation: 1 }) });
    const reencode = vi.fn(async () => buildPng(1, 1));
    const out = await buildImagePdf([file(jpg, "p.jpg", "image/jpeg")], "fit", { reencodePng: reencode }, never);
    expect(await open(out)).toEqual([{ width: 64, height: 48 }]);
    expect(reencode).not.toHaveBeenCalled();
    // the original JPEG bytes appear verbatim inside the PDF stream
    const hay = Buffer.from(out);
    expect(hay.includes(Buffer.from(jpg.subarray(jpg.length - 30)))).toBe(true);
  });

  it("re-encodes rotated JPEGs and PNGs through the canvas hook (orientation baked in)", async () => {
    const rotated = buildJpeg({ width: 64, height: 48, exifTiff: fullTiff(true, { orientation: 6 }) }).bytes;
    const reencode = vi.fn(async () => buildPng(48, 64)); // what the browser would produce: dimensions swapped
    const out = await buildImagePdf([file(rotated, "r.jpg", "image/jpeg")], "fit", { reencodePng: reencode }, never);
    expect(reencode).toHaveBeenCalledTimes(1);
    expect(await open(out)).toEqual([{ width: 48, height: 64 }]);

    const pngWithExif = buildPng(8, 4, [1, 2, 3, 255], [pngChunk("eXIf", fullTiff(false, { orientation: 8 }))]);
    const reencode2 = vi.fn(async () => buildPng(4, 8));
    await buildImagePdf([file(pngWithExif, "e.png", "image/png")], "fit", { reencodePng: reencode2 }, never);
    expect(reencode2).toHaveBeenCalledTimes(1);
  });

  it("re-encodes formats pdf-lib cannot embed (webp, gif, bmp...) and passes the extension on", async () => {
    const seen: string[] = [];
    const out = await buildImagePdf(
      [file(buildWebp(), "w.webp", "image/webp"), file(new Uint8Array([1, 2, 3]), "g.gif", "image/gif")],
      "fit",
      {
        async reencodePng(_f, ext) {
          seen.push(ext);
          return buildPng(5, 5);
        },
      },
      never,
    );
    expect(seen).toEqual(["webp", "gif"]);
    expect((await open(out)).length).toBe(2);
  });

  it("falls back to the canvas when pdf-lib rejects a corrupt JPEG, and fails cleanly when even that is not a PNG", async () => {
    const out = await buildImagePdf(
      [file(new Uint8Array([0xff, 0xd8, 0xff, 0xe0, 0, 0]), "bad.jpg", "image/jpeg")],
      "fit",
      { reencodePng: async () => buildPng(3, 3) },
      never,
    );
    expect(await open(out)).toEqual([{ width: 3, height: 3 }]);
    await expect(
      buildImagePdf([file(new Uint8Array([9, 9, 9]), "bad.gif", "image/gif")], "fit", { reencodePng: async () => new Uint8Array([1, 2, 3]) }, never),
    ).rejects.toBeInstanceOf(ConvertError);
  });

  it("a4 mode: portrait/landscape pages, image centred inside the margin", async () => {
    const out = await buildImagePdf(
      [file(buildPng(100, 200), "tall.png", "image/png"), file(buildPng(200, 100), "wide.png", "image/png")],
      "a4",
      { reencodePng: async () => buildPng(1, 1) },
      never,
    );
    const sizes = await open(out);
    expect(sizes[0].width).toBeCloseTo(A4.width, 1);
    expect(sizes[0].height).toBeCloseTo(A4.height, 1);
    expect(sizes[1].width).toBeCloseTo(A4.height, 1);
    expect(sizes[1].height).toBeCloseTo(A4.width, 1);
  });

  it("sets DropFlow as producer and honours cancellation", async () => {
    const out = await buildImagePdf([file(buildPng(2, 2), "a.png", "image/png")], "fit", { reencodePng: async () => buildPng(1, 1) }, never);
    const doc = await PDFDocument.load(out, { updateMetadata: false }); // load() would otherwise overwrite the producer
    expect(doc.getProducer()).toBe("DropFlow");
    expect(doc.getCreator()).toBe("DropFlow");
    const ctl = new AbortController();
    ctl.abort();
    await expect(
      buildImagePdf([file(buildPng(2, 2), "a.png", "image/png")], "fit", { reencodePng: async () => buildPng(1, 1) }, ctl.signal),
    ).rejects.toMatchObject({ name: "AbortError" });
  });

  it("rejects an empty list", async () => {
    await expect(buildImagePdf([], "fit", { reencodePng: async () => buildPng(1, 1) }, never)).rejects.toBeInstanceOf(ConvertError);
  });
});
