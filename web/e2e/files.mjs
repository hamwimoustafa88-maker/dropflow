// Writes every input file the e2e needs (all generated here) and returns their paths.
import { mkdirSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { ARABIC_SRT, addExifGps, makeBigPdf, makePdf, makePng, makeWav, makeZip } from "./fixtures.mjs";

export async function makeFiles(dir) {
  mkdirSync(dir, { recursive: true });
  const w = (name, data) => {
    const p = join(dir, name);
    writeFileSync(p, data);
    return p;
  };
  const png = makePng(96, 64);
  return {
    dir,
    png: w("photo.png", png),
    pngArabic: w("صورة.png", png),
    png2: w("second.png", makePng(40, 40)),
    pdf3: w("report.pdf", await makePdf(3, "Page")),
    pdf2: w("other.pdf", await makePdf(2, "Other")),
    pdfBig: w("drawing.pdf", await makeBigPdf()),
    pdfBroken: w("broken.pdf", Buffer.from("%PDF-1.4\nthis is not a real pdf\n", "latin1")),
    wav: w("tone.wav", makeWav(1.5)),
    srt: w("حلقة.srt", Buffer.from(ARABIC_SRT, "utf8")),
    txt: w("note.txt", Buffer.from("hello DropFlow\n", "utf8")),
    zip: w("pack.zip", makeZip()),
    docx: w("letter.docx", Buffer.from("PK\x03\x04 not a real docx", "latin1")),
    // the JPEG (with EXIF GPS) is produced inside the browser (canvas) and written by the images section
    exifJpegPath: join(dir, "geo.jpg"),
    addExifGps,
  };
}
