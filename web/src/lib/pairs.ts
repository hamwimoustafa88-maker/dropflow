import data from "@/data/pairs.json";
import { categories } from "./catalog";

// Popular conversions, one static page each (/ar/convert/heic-to-jpg). Only pairs the catalog really converts are allowed
// (checked by src/lib/__tests__/pairs.test.ts); `web` = the browser app (/app) converts it too (checked against the web
// registry by the same test).

export interface Pair {
  slug: string;
  from: string; // catalog input extension
  to: string; // catalog output extension
  web: boolean;
}

/** Friendly names used in slugs and headings ("word" for docx). Formats without an entry use their extension. */
export const FRIENDLY: Record<string, { slug: string; en: string; ar: string }> = {
  docx: { slug: "word", en: "Word", ar: "وورد" },
  xlsx: { slug: "excel", en: "Excel", ar: "إكسل" },
  pptx: { slug: "powerpoint", en: "PowerPoint", ar: "باوربوينت" },
  txt: { slug: "text", en: "text", ar: "نص" },
};

/** Slug of a pair: friendly names where they exist ("pdf-to-word"). pairs.json stores it so build scripts need no TypeScript. */
export const slugOf = (from: string, to: string) => `${FRIENDLY[from]?.slug ?? from}-to-${FRIENDLY[to]?.slug ?? to}`;

export const PAIRS: readonly Pair[] = data.pairs;

export function pairBySlug(slug: string): Pair | undefined {
  return PAIRS.find((p) => p.slug === slug);
}

/** Display name of a format in a locale: "Word", "وورد", "HEIC". */
export function formatName(ext: string, locale: string): string {
  const f = FRIENDLY[ext];
  if (f) return locale === "ar" ? f.ar : f.en;
  return ext.toUpperCase();
}

/** True if some catalog category takes `from` and produces `to`. */
export function inCatalog(from: string, to: string): boolean {
  return categories.some((c) => c.inputs.includes(from) && c.outputs.includes(to));
}

const categoryOf = (ext: string) => categories.find((c) => c.inputs.includes(ext))?.id;

/** Other popular pages: same input or output first, then the same kind of file (at most `n`). */
export function related(pair: Pair, n = 6): Pair[] {
  const others = PAIRS.filter((p) => p !== pair);
  const close = others.filter((p) => p.from === pair.from || p.to === pair.to);
  const sameKind = others.filter((p) => !close.includes(p) && categoryOf(p.from) === categoryOf(pair.from));
  return [...close, ...sameKind].slice(0, n);
}
