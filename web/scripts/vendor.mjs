// Copies the heavy runtime assets (ffmpeg.wasm core + worker, pdf.js worker + data, Tesseract OCR, libheif, ONNX Runtime) from node_modules into
// public/vendor so the app loads them from its OWN origin (no CDN at runtime, works offline once cached by the
// service worker). Runs on postinstall / predev / prebuild; idempotent (skips files whose size already matches).
import { createHash } from "node:crypto";
import { cpSync, existsSync, mkdirSync, readFileSync, readdirSync, statSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const root = dirname(dirname(fileURLToPath(import.meta.url)));
const nm = join(root, "node_modules");
const out = join(root, "public", "vendor");

// Single-thread ffmpeg core on purpose: it needs no SharedArrayBuffer, hence no COOP/COEP headers.
const copies = [
  ["@ffmpeg/core/dist/esm/ffmpeg-core.js", "ffmpeg/ffmpeg-core.js"],
  ["@ffmpeg/core/dist/esm/ffmpeg-core.wasm", "ffmpeg/ffmpeg-core.wasm"],
  ["@ffmpeg/ffmpeg/dist/esm/worker.js", "ffmpeg/worker.js"],
  ["@ffmpeg/ffmpeg/dist/esm/const.js", "ffmpeg/const.js"],
  ["@ffmpeg/ffmpeg/dist/esm/errors.js", "ffmpeg/errors.js"],
  ["pdfjs-dist/build/pdf.worker.min.mjs", "pdfjs/pdf.worker.min.mjs"],
  ["pdfjs-dist/cmaps", "pdfjs/cmaps"],
  ["pdfjs-dist/standard_fonts", "pdfjs/standard_fonts"],
  ["pdfjs-dist/wasm", "pdfjs/wasm"],
  ["pdfjs-dist/iccs", "pdfjs/iccs"],
  // OCR (Tesseract.js, Apache-2.0): the worker, the LSTM-only cores (one is picked at runtime by SIMD support) and the
  // Arabic + English "best_int" LSTM models (tessdata, Apache-2.0). Loaded only when OCR is used.
  ["tesseract.js/dist/tesseract.esm.min.js", "tesseract/tesseract.esm.min.js"],
  ["tesseract.js/dist/worker.min.js", "tesseract/worker.min.js"],
  ["tesseract.js-core/tesseract-core-relaxedsimd-lstm.wasm.js", "tesseract/core/tesseract-core-relaxedsimd-lstm.wasm.js"],
  ["tesseract.js-core/tesseract-core-simd-lstm.wasm.js", "tesseract/core/tesseract-core-simd-lstm.wasm.js"],
  ["tesseract.js-core/tesseract-core-lstm.wasm.js", "tesseract/core/tesseract-core-lstm.wasm.js"],
  ["@tesseract.js-data/eng/4.0.0_best_int/eng.traineddata.gz", "tesseract/lang/eng.traineddata.gz"],
  ["@tesseract.js-data/ara/4.0.0_best_int/ara.traineddata.gz", "tesseract/lang/ara.traineddata.gz"],
  // HEIC/HEIF input (libheif + libde265, LGPL-3.0): libheif-js's ES-module build with the wasm inside, kept as a separate,
  // replaceable file (never bundled into our code).
  ["libheif-js/libheif-wasm/libheif-bundle.mjs", "heif/libheif-bundle.mjs"],
  // Background removal (ONNX Runtime Web, MIT): its wasm-only ES module and the single wasm build it loads (SIMD, 1 thread).
  // Loaded at runtime from /vendor (not bundled: the package's bundle would make Turbopack copy the 14 MB wasm into the shell).
  ["onnxruntime-web/dist/ort.wasm.min.mjs", "ort/ort.wasm.min.mjs"],
  ["onnxruntime-web/dist/ort-wasm-simd-threaded.mjs", "ort/ort-wasm-simd-threaded.mjs"],
  ["onnxruntime-web/dist/ort-wasm-simd-threaded.wasm", "ort/ort-wasm-simd-threaded.wasm"],
];

// Files compare by size; directories by file count + total bytes, so an up-to-date tree is not copied again.
function measure(p) {
  const st = statSync(p);
  if (st.isFile()) return { files: 1, bytes: st.size };
  return readdirSync(p).reduce(
    (acc, n) => {
      const m = measure(join(p, n));
      return { files: acc.files + m.files, bytes: acc.bytes + m.bytes };
    },
    { files: 0, bytes: 0 },
  );
}

function same(a, b) {
  try {
    const ma = measure(a);
    const mb = measure(b);
    return ma.files === mb.files && ma.bytes === mb.bytes;
  } catch {
    return false;
  }
}

let copied = 0;
for (const [from, to] of copies) {
  const src = join(nm, from);
  const dst = join(out, to);
  if (!existsSync(src)) {
    console.warn(`vendor: missing ${from} (run npm install)`);
    continue;
  }
  if (same(src, dst)) continue;
  mkdirSync(dirname(dst), { recursive: true });
  cpSync(src, dst, { recursive: true });
  copied++;
}

// The background-removal model is not on npm: it is downloaded ONCE at install/build time from an immutable Hugging Face
// commit and accepted only when its SHA-256 matches (same URL + hash as the desktop, src/DropFlow.AI/Models/ModelCatalog.cs).
// It is then served from the site's own origin like every other engine file; visitors never contact Hugging Face.
// A failed download never fails the build: the tool then reports "Engine failed to load" when used.
const MODELS = [
  {
    file: "models/u2netp.onnx",
    url: "https://huggingface.co/BritishWerewolf/U-2-Netp/resolve/7112208dbac3a3642496c8d54e2f0f9bb3dc1dc8/onnx/model.onnx",
    sha256: "309c8469258dda742793dce0ebea8e6dd393174f89934733ecc8b14c76f4ddd8",
  },
];
const sha256 = (buf) => createHash("sha256").update(buf).digest("hex");
for (const m of MODELS) {
  const dst = join(out, m.file);
  if (existsSync(dst) && sha256(readFileSync(dst)) === m.sha256) continue;
  if (process.env.DROPFLOW_SKIP_MODELS === "1") {
    console.warn(`vendor: skipped ${m.file} (DROPFLOW_SKIP_MODELS=1)`);
    continue;
  }
  try {
    const res = await fetch(m.url, { redirect: "follow" });
    if (!res.ok) throw new Error(`HTTP ${res.status}`);
    const buf = Buffer.from(await res.arrayBuffer());
    const got = sha256(buf);
    if (got !== m.sha256) throw new Error(`SHA-256 mismatch (${got})`);
    mkdirSync(dirname(dst), { recursive: true });
    writeFileSync(dst, buf);
    copied++;
  } catch (e) {
    console.warn(`vendor: could not fetch ${m.file}: ${e instanceof Error ? e.message : e}`);
  }
}

const ver = (name) => JSON.parse(readFileSync(join(nm, name, "package.json"), "utf8")).version;
const versions = {};
for (const n of ["@ffmpeg/core", "@ffmpeg/ffmpeg", "pdfjs-dist", "tesseract.js", "tesseract.js-core", "libheif-js", "onnxruntime-web"]) {
  try {
    versions[n] = ver(n);
  } catch {
    /* not installed yet */
  }
}
mkdirSync(out, { recursive: true });
writeFileSync(join(out, "versions.json"), JSON.stringify(versions, null, 2) + "\n");
writeFileSync(
  join(out, "NOTICE.txt"),
  [
    "DropFlow Web serves these third-party files from its own origin (no CDN):",
    `- ffmpeg/ffmpeg-core.{js,wasm}: @ffmpeg/core ${versions["@ffmpeg/core"] ?? "?"}, GPL-2.0-or-later (FFmpeg + libx264, libvpx, libmp3lame, libopus, libvorbis ...). Source: https://github.com/ffmpegwasm/ffmpeg.wasm and https://ffmpeg.org/`,
    `- ffmpeg/{worker,const,errors}.js: @ffmpeg/ffmpeg ${versions["@ffmpeg/ffmpeg"] ?? "?"}, MIT`,
    `- pdfjs/*: pdfjs-dist ${versions["pdfjs-dist"] ?? "?"}, Apache-2.0`,
    `- tesseract/{tesseract.esm.min,worker.min}.js: tesseract.js ${versions["tesseract.js"] ?? "?"}, Apache-2.0. tesseract/core/*: tesseract.js-core ${versions["tesseract.js-core"] ?? "?"} (Tesseract OCR + Leptonica), Apache-2.0 / BSD-2-Clause`,
    "- tesseract/lang/{ara,eng}.traineddata.gz: tessdata (LSTM best_int, via @tesseract.js-data), Apache-2.0. Source: https://github.com/tesseract-ocr/tessdata",
    `- heif/libheif-bundle.mjs: libheif-js ${versions["libheif-js"] ?? "?"} (libheif + libde265), LGPL-3.0. Source: https://github.com/strukturag/libheif and https://github.com/catdad-experiments/libheif-js`,
    `- ort/*: onnxruntime-web ${versions["onnxruntime-web"] ?? "?"}, MIT. Source: https://github.com/microsoft/onnxruntime`,
    "- models/u2netp.onnx: U-2-Net-P (BritishWerewolf/U-2-Netp ONNX export of xuebinqin/U-2-Net), Apache-2.0",
    "",
  ].join("\n"),
);
console.log(`vendor: ${copied ? `copied ${copied} item(s)` : "up to date"} -> public/vendor`);
