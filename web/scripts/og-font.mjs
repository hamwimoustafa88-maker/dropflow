// Fetches the site's own Arabic font (IBM Plex Sans Arabic Bold, SIL OFL 1.1, the face next/font already self-hosts)
// as TTF for the share images: next/og (satori) cannot read the WOFF2 files that next/font produces.
// Build-time only (prebuild), from the same Google Fonts service next/font uses during the build. The file is cached in
// .og-cache/ (gitignored) and never served to visitors. If the download fails, the share image falls back to Latin text.
import { existsSync, mkdirSync, statSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const root = dirname(dirname(fileURLToPath(import.meta.url)));
const out = join(root, ".og-cache", "IBMPlexSansArabic-Bold.ttf");

if (existsSync(out) && statSync(out).size > 50_000) {
  console.log("og-font: cached");
} else {
  try {
    const css = await (await fetch("https://fonts.googleapis.com/css2?family=IBM+Plex+Sans+Arabic:wght@700")).text();
    const url = /url\((https:\/\/fonts\.gstatic\.com\/[^)]+\.ttf)\)/.exec(css)?.[1];
    if (!url) throw new Error("no TTF in the Google Fonts response");
    const res = await fetch(url);
    if (!res.ok) throw new Error(`HTTP ${res.status}`);
    const buf = Buffer.from(await res.arrayBuffer());
    if (buf.readUInt32BE(0) !== 0x00010000) throw new Error("not a TrueType font");
    mkdirSync(dirname(out), { recursive: true });
    writeFileSync(out, buf);
    console.log(`og-font: ${Math.round(buf.length / 1024)} KB`);
  } catch (e) {
    console.warn(`og-font: skipped (${e instanceof Error ? e.message : e}); share images use Latin text only`);
  }
}
