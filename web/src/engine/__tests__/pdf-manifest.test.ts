import { afterEach, describe, expect, it, vi } from "vitest";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import catalogJson from "../../data/catalog.json";
import { pdfConverter } from "../converters/pdf";
import { PDFJS_BASE, PDFJS_WORKER_SRC, pdfjsDocParams } from "../lib/pdfjs-setup";
import { appMessages, type Tree } from "./messages-fixture";

const here = (rel: string) => fileURLToPath(new URL(rel, import.meta.url));
const read = (rel: string) => readFileSync(here(rel), "utf8");

const OPS = pdfConverter.operations;
const RENDER = ["convert:png", "convert:jpg", "convert:txt", "convert:md", "pdf.to-images", "pdf.extract-text", "pdf.compress"];
const WORKER = ["pdf.merge", "pdf.split", "pdf.rotate", "pdf.organize", "pdf.metadata", "pdf.watermark", "pdf.page-numbers"];

describe("pdf manifest", () => {
  it("implements exactly the planned operations", () => {
    expect([...OPS].sort()).toEqual([...RENDER, ...WORKER].sort());
    expect(new Set(OPS).size).toBe(OPS.length);
  });

  it("only offers png, jpg, txt, md as conversions (what a browser can truly do from a PDF)", () => {
    const converts = OPS.filter((o) => o.startsWith("convert:")).map((o) => o.slice("convert:".length));
    expect(converts.sort()).toEqual(["jpg", "md", "png", "txt"]);
  });

  it("every tool id is a real catalog tool that is available on the web", () => {
    const tools = new Map((catalogJson as { tools: Array<{ id: string; platforms: string[]; multi?: boolean }> }).tools.map((t) => [t.id, t]));
    for (const op of OPS.filter((o) => !o.startsWith("convert:"))) {
      const tool = tools.get(op);
      expect(tool, op).toBeDefined();
      expect(tool!.platforms, op).toContain("web");
    }
    // pdf.merge is the only multi-input tool of this domain
    expect(tools.get("pdf.merge")!.multi).toBe(true);
    for (const op of OPS.filter((o) => o !== "pdf.merge" && !o.startsWith("convert:"))) expect(tools.get(op)!.multi, op).toBeFalsy();
  });

  it("convert targets are outputs of the catalog's pdf category", () => {
    const cat = (catalogJson as { categories: Array<{ id: string; outputs: string[]; inputs: string[] }> }).categories.find((c) => c.id === "pdf")!;
    expect(cat.inputs).toEqual(["pdf"]);
    for (const op of OPS.filter((o) => o.startsWith("convert:"))) expect(cat.outputs).toContain(op.slice(8));
  });

  it("supports() is true for pdf only, for every operation", () => {
    for (const op of OPS) {
      expect(pdfConverter.supports(op, "pdf"), op).toBe(true);
      for (const ext of ["png", "jpg", "docx", "txt", "pdfa", "", "zip", "PDF"]) expect(pdfConverter.supports(op, ext), `${op} ${ext}`).toBe(false);
    }
    expect(pdfConverter.supports("pdf.protect", "pdf")).toBe(false); // not offered: cannot be done truthfully here
    expect(pdfConverter.supports("pdf.ocr", "pdf")).toBe(false);
    expect(pdfConverter.supports("convert:docx", "pdf")).toBe(false);
    expect(pdfConverter.supports("convert:pdf", "pdf")).toBe(false);
  });

  it("every operation has a suffix, options, runtime, and sane limits", () => {
    for (const op of OPS) {
      expect(pdfConverter.suffix(op), op).toMatch(/^[a-z]+$/);
      expect(Array.isArray(pdfConverter.options(op)), op).toBe(true);
      expect(pdfConverter.runtime(op, "pdf") === "main" || pdfConverter.runtime(op, "pdf") === "worker").toBe(true);
      expect(pdfConverter.maxBytes!(op)).toBeGreaterThanOrEqual(250 * 1024 * 1024);
    }
    expect(pdfConverter.suffix("pdf.merge")).toBe("merged");
    expect(pdfConverter.suffix("pdf.to-images")).toBe("pages");
    expect(pdfConverter.suffix("convert:png")).toBe("pages");
    expect(pdfConverter.suffix("convert:txt")).toBe("text");
    expect(pdfConverter.suffix("pdf.compress")).toBe("compressed");
    expect(pdfConverter.maxBytes!("pdf.merge")).toBe(400 * 1024 * 1024);
    expect(pdfConverter.maxBytes!("pdf.compress")).toBe(250 * 1024 * 1024);
  });

  it("render operations run on the main thread, pure pdf-lib operations in the worker", () => {
    for (const op of RENDER) expect(pdfConverter.runtime(op, "pdf"), op).toBe("main");
    for (const op of WORKER.filter((o) => o !== "pdf.watermark")) expect(pdfConverter.runtime(op, "pdf"), op).toBe("worker");
  });

  describe("watermark runtime", () => {
    afterEach(() => vi.unstubAllGlobals());
    it("is a worker op when OffscreenCanvas exists, otherwise main (Node has none)", () => {
      expect(pdfConverter.runtime("pdf.watermark", "pdf")).toBe("main");
      vi.stubGlobal("OffscreenCanvas", class {});
      expect(pdfConverter.runtime("pdf.watermark", "pdf")).toBe("worker");
    });
  });

  it("only compress warns (text becomes images)", () => {
    for (const op of OPS) expect(pdfConverter.warning!(op), op).toBe(op === "pdf.compress" ? "textToImages" : undefined);
  });

  it("convert:* operations run immediately (no options)", () => {
    for (const op of OPS.filter((o) => o.startsWith("convert:"))) expect(pdfConverter.options(op)).toEqual([]);
    expect(pdfConverter.options("pdf.merge")).toEqual([]);
  });

  it("option schemas are self-consistent", () => {
    for (const op of OPS) {
      const keys = new Set<string>();
      for (const o of pdfConverter.options(op)) {
        expect(keys.has(o.key), `${op}.${o.key} duplicated`).toBe(false);
        keys.add(o.key);
        if (o.kind === "choice") expect(o.choices, `${op}.${o.key}`).toContain(o.default);
        if (o.kind === "range") {
          expect(o.default).toBeGreaterThanOrEqual(o.min);
          expect(o.default).toBeLessThanOrEqual(o.max);
        }
        if (o.kind === "number") expect(o.default).toBeGreaterThanOrEqual(o.min ?? -Infinity);
      }
    }
    const byKey = (op: string, key: string) => pdfConverter.options(op).find((o) => o.key === key)!;
    expect(byKey("pdf.rotate", "angle")).toMatchObject({ kind: "choice", default: "90", choices: ["90", "180", "270"] });
    expect(byKey("pdf.organize", "order")).toMatchObject({ kind: "text", required: true });
    expect(byKey("pdf.watermark", "text")).toMatchObject({ kind: "text", required: true });
    expect(byKey("pdf.watermark", "opacity")).toMatchObject({ kind: "range", min: 5, max: 100, default: 25 });
    expect(byKey("pdf.page-numbers", "position")).toMatchObject({ default: "bottom-center" });
    expect((byKey("pdf.page-numbers", "position") as unknown as { choices: string[] }).choices).toHaveLength(6);
    expect(byKey("pdf.page-numbers", "start")).toMatchObject({ kind: "number", min: 1, default: 1 });
    expect(byKey("pdf.page-numbers", "format")).toMatchObject({ choices: ["n", "n-of-total"] });
    expect(byKey("pdf.to-images", "scale")).toMatchObject({ default: "2", choices: ["1", "1.5", "2", "3"] });
    expect(byKey("pdf.compress", "quality")).toMatchObject({ kind: "range", min: 30, max: 90, default: 60 });
    expect(byKey("pdf.compress", "scale")).toMatchObject({ default: "1.5", choices: ["1", "1.5", "2"] });
    expect(byKey("pdf.metadata", "clear")).toMatchObject({ kind: "bool", default: false });
    expect(byKey("pdf.extract-text", "format")).toMatchObject({ default: "txt", choices: ["txt", "md"] });
  });
});

describe("bundle hygiene and privacy of the PDF domain sources", () => {
  const files = ["../converters/pdf.ts", "../converters/pdf.impl.ts", "../converters/pdf.render.impl.ts", "../lib/ranges.ts", "../lib/pdf-text.ts", "../lib/pdf-canvas.ts", "../lib/pdf-layout.ts", "../lib/pdfjs-setup.ts"];

  it("the manifest imports no heavy library and no implementation statically", () => {
    const src = read("../converters/pdf.ts");
    const statics = [...src.matchAll(/^\s*import\s+(?!type\b)[^;]*?from\s+"([^"]+)"/gm)].map((m) => m[1]);
    for (const spec of statics) {
      expect(spec, spec).not.toMatch(/pdf-lib|pdfjs|\.impl/);
      expect(spec.startsWith("../") || spec.startsWith("./")).toBe(true);
    }
    expect(src).toContain('import("./pdf.impl")');
    expect(src).toContain('import("./pdf.render.impl")');
  });

  it("pdf.js is only imported (as a value) by lib/pdfjs-setup, and lazily", () => {
    for (const f of files) {
      const src = read(f);
      const valueImports = [...src.matchAll(/^\s*import\s+(?!type\b)[^;]*?from\s+"pdfjs-dist[^"]*"/gm)];
      expect(valueImports, f).toHaveLength(0);
    }
    expect(read("../lib/pdfjs-setup.ts")).toContain('import("pdfjs-dist")');
    expect(read("../converters/pdf.impl.ts")).not.toMatch(/pdfjs/);
  });

  it("no code path in the PDF domain can send data anywhere", () => {
    for (const f of files) {
      const src = read(f).replace(/\/\/.*$/gm, "").replace(/\/\*[\s\S]*?\*\//g, "");
      expect(src, f).not.toMatch(/\bfetch\s*\(|XMLHttpRequest|sendBeacon|WebSocket|EventSource|importScripts|postMessage\s*\(/);
      expect(src, f).not.toMatch(/method:\s*["']POST/i);
    }
  });

  it("pdf.js assets are same-origin absolute paths under /vendor/pdfjs", () => {
    const bytes = new Uint8Array([1, 2, 3]);
    const p = pdfjsDocParams(bytes);
    expect(PDFJS_BASE).toBe("/vendor/pdfjs/");
    expect(PDFJS_WORKER_SRC).toBe("/vendor/pdfjs/pdf.worker.min.mjs");
    for (const key of ["cMapUrl", "standardFontDataUrl", "wasmUrl", "iccUrl"] as const) {
      expect(p[key], key).toMatch(/^\/vendor\/pdfjs\/[a-z_]+\/$/);
    }
    expect(p.cMapPacked).toBe(true);
    // a COPY: pdf.js transfers (detaches) the buffer it is given
    expect(p.data).not.toBe(bytes);
    expect(p.data.buffer).not.toBe(bytes.buffer);
    expect([...p.data]).toEqual([1, 2, 3]);
  });
});

describe("i18n messages", () => {
  const it2 = it;
  const frag = appMessages as { en: Tree; ar: Tree };
  const keyOf = (id: string) => id.replace(/[.-]/g, "_");
  const get = (tree: Tree, ...p: string[]): string | Tree | undefined => p.reduce<string | Tree | undefined>((t, k) => (t && typeof t === "object" ? t[k] : undefined), tree);

  function leaves(tree: Tree, prefix = ""): string[] {
    return Object.entries(tree).flatMap(([k, v]) => (typeof v === "string" ? [prefix + k] : leaves(v, `${prefix}${k}.`)));
  }

  it2("en and ar have identical key trees and no empty strings", () => {
    expect(leaves(frag!.ar).sort()).toEqual(leaves(frag!.en).sort());
    for (const lang of ["en", "ar"] as const) for (const k of leaves(frag![lang])) expect(get(frag![lang], ...k.split(".")), `${lang}.${k}`).not.toBe("");
  });

  it2("labels every tool, option, word-like choice, warning and error code", () => {
    for (const lang of ["en", "ar"] as const) {
      const t = frag![lang];
      for (const op of OPS.filter((o) => !o.startsWith("convert:"))) expect(get(t, "ops", keyOf(op)), `${lang} ops.${op}`).toBeTypeOf("string");
      for (const op of OPS) {
        for (const o of pdfConverter.options(op)) {
          expect(get(t, "opt", o.key) ?? get(t, "opt", keyOf(op), o.key), `${lang} opt.${o.key}`).toBeTypeOf("string");
          if (o.kind === "choice") {
            for (const c of o.choices.filter((v) => !/^[0-9.]+$/.test(v))) {
              // 'png'/'jpg'/'txt'/'md' are format names and need no translation
              if (/^(png|jpg|txt|md)$/.test(c)) continue;
              expect(get(t, "choice", o.key, keyOf(c)), `${lang} choice.${o.key}.${c}`).toBeTypeOf("string");
            }
          }
          if (o.kind === "text" && o.placeholder) expect(get(t, "ph", o.key), `${lang} ph.${o.key}`).toBeTypeOf("string");
        }
        const w = pdfConverter.warning!(op);
        if (w) expect(get(t, "warn", w), `${lang} warn.${w}`).toBeTypeOf("string");
      }
      // every ConvertError code thrown anywhere in the PDF domain has a message
      const sources = ["../converters/pdf.impl.ts", "../converters/pdf.render.impl.ts", "../lib/ranges.ts"].map(read).join("\n");
      const codes = new Set([...sources.matchAll(/new ConvertError\(\s*"([A-Za-z]+)"/g)].map((m) => m[1]));
      for (const code of codes) if (code !== "unsupported") expect(get(t, "err", code), `${lang} err.${code}`).toBeTypeOf("string");
      expect(codes.size).toBeGreaterThanOrEqual(8);
    }
  });

  it2("keeps labels short (owner preference: very few words)", () => {
    for (const lang of ["en", "ar"] as const) {
      for (const section of ["ops", "opt"] as const) {
        for (const k of leaves(frag![lang][section] as Tree)) {
          const v = get(frag![lang][section] as Tree, ...k.split(".")) as string;
          expect(v.trim().split(/\s+/).length, `${lang} ${section}.${k} = ${v}`).toBeLessThanOrEqual(3);
        }
      }
      for (const k of leaves(frag![lang].err as Tree)) {
        const v = get(frag![lang].err as Tree, ...k.split(".")) as string;
        expect(v.trim().split(/\s+/).length, `${lang} err.${k} = ${v}`).toBeLessThanOrEqual(8);
      }
    }
  });
});
