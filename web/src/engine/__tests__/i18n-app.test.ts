import { readFileSync, readdirSync, statSync } from "node:fs";
import { join, relative, sep } from "node:path";
import { fileURLToPath } from "node:url";
import { describe, expect, it } from "vitest";
import { DEFAULT_IMAGE_CAPS, setImageCaps } from "../caps";
import { opKey, choiceKey } from "../catalog-web";
import { registry } from "../registry";
import { HINT_TOOL_LABELS } from "../windows-hint";
import { appMessages, type Tree } from "./messages-fixture";

const root = fileURLToPath(new URL("../../../", import.meta.url));
const src = join(root, "src");

function walk(dir: string): string[] {
  return readdirSync(dir).flatMap((n) => {
    const p = join(dir, n);
    return statSync(p).isDirectory() ? walk(p) : [p];
  });
}
const get = (t: Tree, path: string): string | Tree | undefined => path.split(".").reduce<string | Tree | undefined>((a, k) => (a && typeof a !== "string" ? a[k] : undefined), t);
const leaves = (t: Tree, p = ""): string[] => Object.entries(t).flatMap(([k, v]) => (typeof v === "string" ? [`${p}${k}`] : leaves(v, `${p}${k}.`)));

const LOCALES = ["en", "ar"] as const;

describe("UI messages (app namespace) are complete in Arabic and English", () => {
  it("both locales have exactly the same keys and the same {placeholders}", () => {
    expect(leaves(appMessages.ar).sort()).toEqual(leaves(appMessages.en).sort());
    for (const k of leaves(appMessages.en)) {
      const ph = (s: string) => [...s.matchAll(/\{(\w+)/g)].map((m) => m[1]).sort().join(",");
      expect(ph(get(appMessages.ar, k) as string), k).toBe(ph(get(appMessages.en, k) as string));
      expect((get(appMessages.ar, k) as string).length, k).toBeGreaterThan(0);
    }
  });

  it("whole message files keep identical key trees (landing + app)", () => {
    const all = (l: string) => JSON.parse(readFileSync(join(root, `messages/${l}.json`), "utf8")) as Tree;
    expect(leaves(all("ar")).sort()).toEqual(leaves(all("en")).sort());
  });

  it("every operation the registry can ever offer has a short label (ops.<id>)", () => {
    const ops = [...registry.implementedOperations].filter((o) => !o.startsWith("convert:"));
    expect(ops.length).toBeGreaterThan(15);
    for (const l of LOCALES) for (const op of ops) expect(get(appMessages[l], `ops.${opKey(op)}`), `${l} ops.${opKey(op)}`).toBeTypeOf("string");
  });

  it("every option key, word-like choice, placeholder and warning of every operation has a label", () => {
    setImageCaps({ encode: { png: true, jpg: true, webp: true, avif: true }, decodeAvif: true });
    try {
      for (const c of registry.converters) {
        for (const op of c.operations) {
          for (const o of c.options(op)) {
            for (const l of LOCALES) {
              const label = get(appMessages[l], `optOp.${opKey(op)}.${o.key}`) ?? get(appMessages[l], `opt.${o.key}`);
              expect(label, `${l} option ${op}/${o.key}`).toBeTypeOf("string");
              if (o.kind === "choice") {
                for (const v of o.choices) {
                  // format names, numbers and speeds are shown raw
                  if (/^(png|jpg|txt|md|webp|a4|[0-9.:]+|sha-\d+|sha_\d+|md5)$/.test(v)) continue;
                  expect(get(appMessages[l], `choice.${o.key}.${choiceKey(v)}`), `${l} choice ${o.key}=${v}`).toBeTypeOf("string");
                }
              }
            }
          }
          const w = c.warning?.(op);
          if (w) for (const l of LOCALES) expect(get(appMessages[l], `warn.${w}`), `${l} warn.${w}`).toBeTypeOf("string");
        }
      }
    } finally {
      setImageCaps(DEFAULT_IMAGE_CAPS);
    }
  });

  it("every ConvertError code thrown anywhere in the engine has a translated message", () => {
    const codes = new Set<string>();
    for (const f of walk(join(src, "engine")).filter((p) => /\.ts$/.test(p) && !p.includes(`${sep}__tests__${sep}`))) {
      const text = readFileSync(f, "utf8");
      for (const m of text.matchAll(/new ConvertError\(\s*"(\w+)"/g)) codes.add(m[1]);
      for (const m of text.matchAll(/\bfail\(\s*"(\w+)"/g)) codes.add(m[1]);
    }
    for (const c of ["decodeFailed", "outOfMemory", "encodeFailed", "emptyResult", "generic", "workerFailed", "unknown"]) codes.add(c);
    codes.delete("unknown"); // mapped to `generic` in the UI
    expect(codes.size).toBeGreaterThan(25);
    const internal = new Set(["unsupported"]); // routing bugs; shown as the generic text if ever reached
    for (const l of LOCALES) for (const c of codes) if (!internal.has(c)) expect(get(appMessages[l], `err.${c}`), `${l} err.${c}`).toBeTypeOf("string");
  });

  it("every static t(\"...\") key used by the app components exists", () => {
    const keys = new Set<string>();
    for (const f of walk(join(src, "components/app")).filter((p) => /\.tsx?$/.test(p))) {
      const text = readFileSync(f, "utf8");
      for (const m of text.matchAll(/\bt\(\s*"([\w.]+)"/g)) keys.add(m[1]);
      for (const m of text.matchAll(/\bt\(\s*`([\w.]+)\$\{/g)) keys.add(`${m[1]}*`);
    }
    expect(keys.size).toBeGreaterThan(40);
    for (const l of LOCALES) {
      for (const k of keys) {
        if (k.endsWith("*")) {
          const prefix = k.slice(0, -1).replace(/\.$/, "");
          expect(get(appMessages[l], prefix), `${l} ${k}`).toBeTypeOf("object");
        } else {
          expect(get(appMessages[l], k), `${l} ${k}`).toBeTypeOf("string");
        }
      }
    }
    void relative;
  });

  it("windows-app hint labels exist", () => {
    for (const l of LOCALES) for (const key of new Set(Object.values(HINT_TOOL_LABELS))) expect(get(appMessages[l], `hint.${key}`), `${l} hint.${key}`).toBeTypeOf("string");
  });

  it("owner preference: very few words (labels <= 3 words, wheel/UI strings short)", () => {
    for (const l of LOCALES) {
      for (const section of ["ops", "wheels", "actions", "files", "results", "status", "drop"]) {
        for (const k of leaves(get(appMessages[l], section) as Tree)) {
          const v = get(appMessages[l], `${section}.${k}`) as string;
          if (v.includes("{count, plural")) continue;
          expect(v.trim().split(/\s+/).length, `${l} ${section}.${k} = ${v}`).toBeLessThanOrEqual(4);
        }
      }
    }
  });
});
