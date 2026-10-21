// Shared helpers for the PDF tests: build real PDFs with pdf-lib and read them back with an INDEPENDENT reader
// (the pdf.js legacy build). pdf.js runs in a small child Node process (JSON lines over stdio): importing the 2 MB
// pdf.js bundle inside Vitest's module graph takes over a minute, a plain Node process loads it in about a second.
// If the reader cannot boot, `bootPdfjs()` returns null plus a visible reason and the tests that need it are skipped.

import { PDFDocument, StandardFonts, degrees } from "pdf-lib";
import { spawn, type ChildProcessWithoutNullStreams } from "node:child_process";
import { mkdtempSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import { pathToFileURL } from "node:url";

export interface MakePdfOptions {
  /** Text drawn on each page ("PAGE-1" by default); one entry per page. */
  texts?: string[];
  size?: [number, number];
  rotations?: number[];
}

export async function makePdf(pageCount: number, opts: MakePdfOptions = {}): Promise<Uint8Array> {
  const doc = await PDFDocument.create({ updateMetadata: false });
  const font = await doc.embedFont(StandardFonts.Helvetica);
  for (let i = 0; i < pageCount; i++) {
    const page = doc.addPage(opts.size ?? [300, 400]);
    page.drawText(opts.texts?.[i] ?? `PAGE-${i + 1}`, { x: 40, y: 200, size: 18, font });
    const r = opts.rotations?.[i];
    if (r) page.setRotation(degrees(r));
  }
  return doc.save();
}

export interface Positioned {
  str: string;
  /** Baseline start on the DISPLAYED page (origin top-left, y down). */
  x: number;
  y: number;
  /** True when the text reads upright on screen. */
  upright: boolean;
}

export interface PdfjsHandle {
  texts(bytes: Uint8Array): Promise<string[]>;
  positioned(bytes: Uint8Array): Promise<Positioned[][]>;
  numPages(bytes: Uint8Array): Promise<number>;
  close(): void;
}

const CHILD = `
import { createInterface } from "node:readline";
import Module from "node:module";
// pdf.js eagerly loads the 27 MB native @napi-rs/canvas (about 11 s here); text extraction does not need it.
const origLoad = Module._load;
Module._load = function (request, ...rest) {
  if (request === "@napi-rs/canvas") throw new Error("stubbed out for speed");
  return origLoad.call(this, request, ...rest);
};
const [libUrl, workerUrl, fontsUrl] = process.argv.slice(2);
const lib = await import(libUrl);
lib.GlobalWorkerOptions.workerSrc = workerUrl;
async function open(b64) {
  const task = lib.getDocument({ data: new Uint8Array(Buffer.from(b64, "base64")), standardFontDataUrl: fontsUrl, useWorkerFetch: false, verbosity: 0 });
  return { task, doc: await task.promise };
}
async function handle(req) {
  const { task, doc } = await open(req.b64);
  try {
    if (req.kind === "numPages") return doc.numPages;
    const out = [];
    for (let i = 1; i <= doc.numPages; i++) {
      const page = await doc.getPage(i);
      const tc = await page.getTextContent();
      if (req.kind === "texts") {
        out.push(tc.items.map((it) => ("str" in it ? it.str : "")).join(" ").trim());
      } else {
        const vp = page.getViewport({ scale: 1 });
        out.push(tc.items.filter((it) => "str" in it && it.str.trim() !== "").map((it) => {
          const t = it.transform;
          const [x, y] = vp.convertToViewportPoint(t[4], t[5]);
          const m = lib.Util.transform(vp.transform, t);
          return { str: it.str, x, y, upright: m[0] > 0 && Math.abs(m[1]) < 1e-3 && m[3] < 0 };
        }));
      }
    }
    return out;
  } finally {
    await task.destroy();
  }
}
console.log(JSON.stringify({ ready: true }));
const rl = createInterface({ input: process.stdin });
for await (const line of rl) {
  const req = JSON.parse(line);
  try {
    console.log(JSON.stringify({ id: req.id, ok: true, res: await handle(req) }));
  } catch (e) {
    console.log(JSON.stringify({ id: req.id, ok: false, error: String(e && e.message || e) }));
  }
}
`;

export async function bootPdfjs(): Promise<{ pdfjs: PdfjsHandle | null; reason: string }> {
  let child: ChildProcessWithoutNullStreams | null = null;
  try {
    const root = path.resolve(process.cwd(), "node_modules/pdfjs-dist");
    const dir = mkdtempSync(path.join(tmpdir(), "df-pdfjs-"));
    const script = path.join(dir, "reader.mjs");
    writeFileSync(script, CHILD);
    child = spawn(
      process.execPath,
      [
        script,
        pathToFileURL(path.join(root, "legacy/build/pdf.mjs")).href,
        pathToFileURL(path.join(root, "legacy/build/pdf.worker.mjs")).href,
        root.split(path.sep).join("/") + "/standard_fonts/",
      ],
      { stdio: ["pipe", "pipe", "pipe"] },
    );
    const proc = child;
    let nextId = 1;
    const waiting = new Map<number, { resolve: (v: unknown) => void; reject: (e: Error) => void }>();
    let buffer = "";
    let stderr = "";
    let onReady: (() => void) | null = null;
    const ready = new Promise<void>((resolve) => (onReady = resolve));
    proc.stderr.on("data", (d: Buffer) => (stderr += d.toString()));
    proc.stdout.on("data", (d: Buffer) => {
      buffer += d.toString();
      let nl: number;
      while ((nl = buffer.indexOf("\n")) >= 0) {
        const line = buffer.slice(0, nl).trim();
        buffer = buffer.slice(nl + 1);
        if (!line.startsWith("{")) continue; // pdf.js prints "Warning: ..." lines on stdout
        const msg = JSON.parse(line) as { ready?: boolean; id?: number; ok?: boolean; res?: unknown; error?: string };
        if (msg.ready) onReady?.();
        else if (msg.id !== undefined) {
          const w = waiting.get(msg.id);
          waiting.delete(msg.id);
          if (msg.ok) w?.resolve(msg.res);
          else w?.reject(new Error(msg.error ?? "pdf.js reader failed"));
        }
      }
    });
    const exited = new Promise<never>((_, reject) =>
      proc.on("exit", (code) => {
        for (const w of waiting.values()) w.reject(new Error(`reader exited (${code})`));
        reject(new Error(`pdf.js reader exited with ${code}: ${stderr.slice(0, 300)}`));
      }),
    );
    exited.catch(() => undefined);
    await Promise.race([ready, exited, new Promise((_, rej) => setTimeout(() => rej(new Error("reader boot timeout")), 60_000))]);
    proc.unref();

    const call = (kind: string, bytes: Uint8Array) =>
      new Promise<unknown>((resolve, reject) => {
        const id = nextId++;
        waiting.set(id, { resolve, reject });
        proc.stdin.write(JSON.stringify({ id, kind, b64: Buffer.from(bytes).toString("base64") }) + "\n");
      });
    const handle: PdfjsHandle = {
      numPages: (b) => call("numPages", b) as Promise<number>,
      texts: (b) => call("texts", b) as Promise<string[]>,
      positioned: (b) => call("positioned", b) as Promise<Positioned[][]>,
      close: () => {
        proc.stdin.end();
        proc.kill();
      },
    };
    // Smoke test so a broken boot is reported once, here, instead of inside many tests.
    await handle.texts(await makePdf(1));
    return { pdfjs: handle, reason: "" };
  } catch (e) {
    child?.kill();
    return { pdfjs: null, reason: `pdf.js reader did not boot: ${e instanceof Error ? e.message : String(e)}` };
  }
}

/** Minimal valid 1x1 PNG (transparent), used as the stub watermark image. */
export const TINY_PNG = Uint8Array.from(
  Buffer.from("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==", "base64"),
);

export function latin1(bytes: Uint8Array): string {
  return Buffer.from(bytes).toString("latin1");
}

/** A real 16x12 JPEG (865 bytes) so pdf-lib can embed it. */
export const TINY_JPEG = Uint8Array.from(Buffer.from("/9j/4AAQSkZJRgABAQAAAQABAAD/4gHYSUNDX1BST0ZJTEUAAQEAAAHIAAAAAAQwAABtbnRyUkdCIFhZWiAH4AABAAEAAAAAAABhY3NwAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAQAA9tYAAQAAAADTLQAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAlkZXNjAAAA8AAAACRyWFlaAAABFAAAABRnWFlaAAABKAAAABRiWFlaAAABPAAAABR3dHB0AAABUAAAABRyVFJDAAABZAAAAChnVFJDAAABZAAAAChiVFJDAAABZAAAAChjcHJ0AAABjAAAADxtbHVjAAAAAAAAAAEAAAAMZW5VUwAAAAgAAAAcAHMAUgBHAEJYWVogAAAAAAAAb6IAADj1AAADkFhZWiAAAAAAAABimQAAt4UAABjaWFlaIAAAAAAAACSgAAAPhAAAts9YWVogAAAAAAAA9tYAAQAAAADTLXBhcmEAAAAAAAQAAAACZmYAAPKnAAANWQAAE9AAAApbAAAAAAAAAABtbHVjAAAAAAAAAAEAAAAMZW5VUwAAACAAAAAcAEcAbwBvAGcAbABlACAASQBuAGMALgAgADIAMAAxADb/2wBDAAoHBwgHBgoICAgLCgoLDhgQDg0NDh0VFhEYIx8lJCIfIiEmKzcvJik0KSEiMEExNDk7Pj4+JS5ESUM8SDc9Pjv/2wBDAQoLCw4NDhwQEBw7KCIoOzs7Ozs7Ozs7Ozs7Ozs7Ozs7Ozs7Ozs7Ozs7Ozs7Ozs7Ozs7Ozs7Ozs7Ozs7Ozs7Ozv/wAARCAAMABADASIAAhEBAxEB/8QAFgABAQEAAAAAAAAAAAAAAAAAAAQG/8QAIRAAAAUEAgMAAAAAAAAAAAAAAQMEERICBhMUADEFIVH/xAAUAQEAAAAAAAAAAAAAAAAAAAAF/8QAGREAAgMBAAAAAAAAAAAAAAAAERIAAiFR/9oADAMBAAIRAxEAPwC2y7YRXHu7hp5evjjhqpB5Sd3AfgcXpbCK3NLTNPM2Mks1VItGLMwB9HmeS+QWoJ6aw9NNpYTaqJN07D77HhV5BavhuLD1MHjmNqri/bOProOLLdyc5C2qgGz/2Q==", "base64"));
