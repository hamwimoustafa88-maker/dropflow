// Copies the heavy runtime assets (ffmpeg.wasm core, ffmpeg worker, pdf.js worker + data) from node_modules into
// public/vendor so the app loads them from its OWN origin (no CDN at runtime, works offline once cached by the
// service worker). Runs on postinstall / predev / prebuild; idempotent (skips files whose size already matches).
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

const ver = (name) => JSON.parse(readFileSync(join(nm, name, "package.json"), "utf8")).version;
const versions = {};
for (const n of ["@ffmpeg/core", "@ffmpeg/ffmpeg", "pdfjs-dist"]) {
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
    "",
  ].join("\n"),
);
console.log(`vendor: ${copied ? `copied ${copied} item(s)` : "up to date"} -> public/vendor`);
