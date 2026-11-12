// OCR with Tesseract.js (Apache-2.0) on the device. Everything it loads comes from the site's own origin under
// /vendor/tesseract (copied from node_modules by scripts/vendor.mjs): the library, its worker, the LSTM core (wasm inside)
// and the Arabic + English models. Tesseract runs in its OWN Web Worker; nothing it reads or writes leaves the device.
//
// One worker is kept warm between jobs (loading the models takes a moment) and closed after a minute of rest.

import type * as TesseractNS from "tesseract.js";
import { importVendor } from "./vendor-import";

export const TESS_BASE = "/vendor/tesseract/";

type TesseractModule = typeof TesseractNS;
type TessWorker = TesseractNS.Worker;

export type OcrLang = "auto" | "ara" | "eng";

/** Tesseract language list. English first: it reads digits and Latin better, and Arabic is still found (verified). */
export function langsFor(lang: string): string[] {
  if (lang === "ara") return ["ara"];
  if (lang === "eng") return ["eng"];
  return ["eng", "ara"];
}

export interface OcrConfig {
  /** Tests: import tesseract.js from node_modules. */
  importer?: () => Promise<{ default: TesseractModule } | TesseractModule>;
  /** Paths the Tesseract worker loads from; undefined = the library's Node defaults (tests only). */
  workerPath?: string;
  corePath?: string;
  langPath: string;
}

export const BROWSER_OCR: OcrConfig = {
  workerPath: `${TESS_BASE}worker.min.js`,
  corePath: `${TESS_BASE}core/`,
  langPath: `${TESS_BASE}lang`,
};

const IDLE_MS = 60_000;

interface Warm {
  key: string;
  worker: Promise<TessWorker>;
}

let warm: Warm | null = null;
let idleTimer: ReturnType<typeof setTimeout> | null = null;
let progressSink: ((fraction: number) => void) | null = null;
let chain: Promise<unknown> = Promise.resolve();

function dropWorker(): void {
  const w = warm;
  warm = null;
  if (idleTimer) clearTimeout(idleTimer);
  idleTimer = null;
  void w?.worker.then((x) => x.terminate()).catch(() => {});
}

async function workerFor(langs: string[], config: OcrConfig): Promise<TessWorker> {
  const key = `${langs.join("+")}|${config.langPath}`;
  if (warm && warm.key !== key) dropWorker();
  if (!warm) {
    const worker = (async () => {
      const mod = await (config.importer ? config.importer() : importVendor<{ default: TesseractModule }>(`${TESS_BASE}tesseract.esm.min.js`));
      const T = ("default" in mod && mod.default ? mod.default : mod) as TesseractModule;
      const options: Partial<TesseractNS.WorkerOptions> = {
        langPath: config.langPath,
        cacheMethod: "none", // the service worker / HTTP cache keeps the files; nothing in IndexedDB
        gzip: true,
        workerBlobURL: false, // a plain same-origin worker script, no blob: bootstrap
        logger: (m: TesseractNS.LoggerMessage) => {
          if (m.status === "recognizing text" && typeof m.progress === "number") progressSink?.(m.progress);
        },
        errorHandler: () => {},
      };
      if (config.workerPath) options.workerPath = config.workerPath;
      if (config.corePath) options.corePath = config.corePath;
      return T.createWorker(langs, T.OEM.LSTM_ONLY, options);
    })();
    warm = { key, worker };
    worker.catch(() => {
      if (warm?.worker === worker) warm = null;
    });
  }
  return warm.worker;
}

/**
 * Recognises the text in one image (PNG/JPEG bytes or Blob). Jobs run one after another on the warm worker. Cancelling
 * terminates the worker (Tesseract cannot stop a page half-way) - the next job starts a fresh one.
 */
export function recognize(
  image: Blob | Uint8Array,
  lang: string,
  onProgress: (fraction: number) => void,
  signal: AbortSignal,
  config: OcrConfig = BROWSER_OCR,
): Promise<string> {
  const run = async (): Promise<string> => {
    if (signal.aborted) throw new DOMException("Cancelled", "AbortError");
    if (idleTimer) clearTimeout(idleTimer);
    const worker = await workerFor(langsFor(lang), config);
    progressSink = onProgress;
    let onAbort: (() => void) | null = null;
    const aborted = new Promise<never>((_, reject) => {
      onAbort = () => {
        dropWorker();
        reject(new DOMException("Cancelled", "AbortError"));
      };
      signal.addEventListener("abort", onAbort, { once: true });
    });
    try {
      const input = image instanceof Uint8Array ? (image as unknown as TesseractNS.ImageLike) : image;
      const result = await Promise.race([worker.recognize(input, {}, { text: true, blocks: true }), aborted]);
      return cleanOcrText(confidentText(result.data));
    } finally {
      progressSink = null;
      if (onAbort) signal.removeEventListener("abort", onAbort);
      if (warm) idleTimer = setTimeout(dropWorker, IDLE_MS);
    }
  };
  const next = chain.then(run, run);
  chain = next.catch(() => undefined); // one failed job never blocks the next
  return next;
}

/** Closes the warm worker now (tests, page teardown). */
export function closeOcr(): Promise<void> {
  const w = warm;
  warm = null;
  if (idleTimer) clearTimeout(idleTimer);
  idleTimer = null;
  return w ? w.worker.then((x) => x.terminate()).then(() => undefined, () => undefined) : Promise.resolve();
}

/**
 * Tidies Tesseract's text: LF line ends, no trailing spaces, at most one empty line in a row, a final newline. The
 * direction marks Tesseract puts around right-to-left lines (U+200F ... U+200E) are KEPT: they make Arabic lines display
 * in the right order in editors that do not detect the direction themselves.
 */
export function cleanOcrText(text: string): string {
  const t = text
    .replace(/\r\n?/g, "\n")
    .replace(/[ \t ]+\n/g, "\n")
    .replace(/\n{3,}/g, "\n\n")
    .trim();
  return t.length ? `${t}\n` : "";
}

/**
 * Lines Tesseract is sure of. On a picture without text (a photo, a QR code, a gradient) Tesseract still "reads" lines such as
 * "ل اناس a 0" or "= LJ _J M". Measured with the site's models (eng+ara, LSTM): real printed lines score 90-96, those invented
 * lines 0-55 (one stray "|" scored 70 but has no letter). A line is kept when it scores at least {@link MIN_LINE_CONFIDENCE} and
 * holds at least two letters or digits; paragraphs stay separated by an empty line. Without block data (older engines) the plain
 * text is used as before.
 */
export const MIN_LINE_CONFIDENCE = 60;

interface OcrLineLike { text: string; confidence: number }
interface OcrPageLike { text?: string | null; blocks?: { paragraphs: { lines: OcrLineLike[] }[] }[] | null }

export function confidentText(page: OcrPageLike): string {
  if (!page.blocks) return page.text ?? "";
  const paragraphs: string[] = [];
  for (const block of page.blocks) {
    for (const paragraph of block.paragraphs) {
      const lines = paragraph.lines
        .filter((l) => l.confidence >= MIN_LINE_CONFIDENCE && (l.text.match(/[\p{L}\p{N}]/gu)?.length ?? 0) >= 2)
        .map((l) => l.text.replace(/\s+$/u, ""));
      if (lines.length) paragraphs.push(lines.join("\n"));
    }
  }
  return paragraphs.join("\n\n");
}

/** True when the text holds at least one letter or digit (direction marks and punctuation alone do not count). */
export function hasRealText(text: string): boolean {
  return /[\p{L}\p{N}]/u.test(text);
}
