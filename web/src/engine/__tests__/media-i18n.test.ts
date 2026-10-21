import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";
import { LEVELS, TOOL_IDS, toolOptions } from "../lib/ffmpeg-args";
import { appMessages, type Tree } from "./messages-fixture";

const read = (rel: string) => readFileSync(new URL(rel, import.meta.url), "utf8");
const fragment = appMessages as { en: Tree; ar: Tree };

function keys(t: Tree, prefix = ""): string[] {
  return Object.entries(t).flatMap(([k, v]) => (typeof v === "string" ? [`${prefix}${k}`] : keys(v, `${prefix}${k}.`)));
}
function get(t: Tree, path: string): string | Tree | undefined {
  return path.split(".").reduce<string | Tree | undefined>((acc, k) => (acc && typeof acc !== "string" ? acc[k] : undefined), t);
}

describe("media i18n messages", () => {
  it("English and Arabic have identical key trees and no empty strings", () => {
    expect(keys(fragment.ar).sort()).toEqual(keys(fragment.en).sort());
    for (const lang of ["en", "ar"] as const) for (const k of keys(fragment[lang])) expect(get(fragment[lang], k), `${lang}.${k}`).not.toBe("");
  });

  it("labels every tool operation (id with '.' and '-' replaced by '_')", () => {
    for (const lang of ["en", "ar"] as const) {
      for (const id of TOOL_IDS) expect(get(fragment[lang], `ops.${id.replace(/[.-]/g, "_")}`), `${lang} ${id}`).toBeTypeOf("string");
    }
  });

  it("labels every option key, level choice and placeholder-able option", () => {
    const optionKeys = [...new Set(TOOL_IDS.flatMap((id) => toolOptions(id).map((o) => o.key)))];
    expect(optionKeys.sort()).toEqual(["end", "level", "speed", "start", "time"]);
    for (const lang of ["en", "ar"] as const) {
      for (const k of optionKeys) expect(get(fragment[lang], `opt.${k}`), `${lang} opt.${k}`).toBeTypeOf("string");
      for (const v of LEVELS) expect(get(fragment[lang], `choice.level.${v}`), `${lang} level.${v}`).toBeTypeOf("string");
      for (const k of ["start", "end", "time"]) expect(get(fragment[lang], `ph.${k}`), `${lang} ph.${k}`).toBeTypeOf("string");
    }
  });

  it("has a message for every error code the media modules can throw", () => {
    const sources = ["../converters/media.impl.ts", "../converters/media-engine.ts", "../lib/ffmpeg-args.ts", "../lib/media-time.ts"].map(read).join("\n");
    const thrown = new Set<string>();
    for (const m of sources.matchAll(/new ConvertError\(\s*"(\w+)"/g)) thrown.add(m[1]);
    for (const m of sources.matchAll(/\bfail\(\s*"(\w+)"/g)) thrown.add(m[1]);
    // codes returned by classifyFfmpegLog()
    for (const c of ["noAudio", "decodeFailed", "outOfMemory", "encodeFailed"]) thrown.add(c);
    expect(thrown.size).toBeGreaterThan(8);
    for (const lang of ["en", "ar"] as const) {
      for (const code of thrown) expect(get(fragment[lang], `err.${code}`), `${lang} err.${code}`).toBeTypeOf("string");
    }
  });

  it("keeps UI copy short (labels <= 3 words, errors <= 8 words)", () => {
    for (const lang of ["en", "ar"] as const) {
      for (const k of keys(fragment[lang]).filter((x) => /^(ops|opt|choice|err)\./.test(x))) {
        const words = (get(fragment[lang], k) as string).trim().split(/\s+/).length;
        expect(words, `${lang}.${k}`).toBeLessThanOrEqual(k.startsWith("err.") ? 8 : 3);
      }
    }
  });
});
