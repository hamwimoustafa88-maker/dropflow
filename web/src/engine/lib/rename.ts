// Batch rename (pure, Node-tested): the browser cannot rename files on disk, so DropFlow Web makes renamed COPIES (the
// desktop's default "copy" mode). Same steps and tokens as the desktop RenameSpec (src/DropFlow.Engines.Archive/General/
// Rename/RenameModel.cs), minus what needs the file system ({parent}, created/modified dates, in-place renaming).
//
// Steps, on the name without its extension: find/replace (text or regular expression) -> trim -> case. The result replaces
// {name} in the pattern. Tokens: {name}, {ext} (with its dot; "." + "{ext}" is not doubled), {n} (1-based position),
// {counter} / {counter:000} (start + position, zero-padded), {date} / {date:yyyyMMdd} (today), {original}; {{ and }} are
// literal braces.

import { ConvertError } from "../types";
import { sanitizeFileName } from "../names";

export type CaseMode = "none" | "lower" | "upper" | "title" | "sentence";

export interface RenameSpec {
  pattern: string;
  find: string;
  replace: string;
  regex: boolean;
  ignoreCase: boolean;
  case: CaseMode;
  trim: boolean;
  start: number;
}

export const DEFAULT_RENAME: RenameSpec = { pattern: "{name}{ext}", find: "", replace: "", regex: false, ignoreCase: false, case: "none", trim: false, start: 1 };

function splitName(name: string): { stem: string; ext: string } {
  const i = name.lastIndexOf(".");
  return i > 0 ? { stem: name.slice(0, i), ext: name.slice(i) } : { stem: name, ext: "" };
}

function applyCase(s: string, mode: CaseMode): string {
  switch (mode) {
    case "lower":
      return s.toLocaleLowerCase();
    case "upper":
      return s.toLocaleUpperCase();
    case "title":
      return s.toLocaleLowerCase().replace(/(^|[\s_\-.])(\p{L})/gu, (_, sep: string, c: string) => sep + c.toLocaleUpperCase());
    case "sentence": {
      const lower = s.toLocaleLowerCase();
      return lower.replace(/\p{L}/u, (c) => c.toLocaleUpperCase());
    }
    default:
      return s;
  }
}

function formatDate(d: Date, fmt: string): string {
  const pad = (n: number, w = 2) => String(n).padStart(w, "0");
  return fmt.replace(/yyyy|MM|dd|HH|mm|ss/g, (t) =>
    t === "yyyy" ? String(d.getFullYear()) : t === "MM" ? pad(d.getMonth() + 1) : t === "dd" ? pad(d.getDate()) : t === "HH" ? pad(d.getHours()) : t === "mm" ? pad(d.getMinutes()) : pad(d.getSeconds()),
  );
}

/** The new name of the file at `index` (0-based). Throws ConvertError("invalidPattern") for a bad regular expression or token. */
export function renameOne(original: string, index: number, spec: RenameSpec, now: Date = new Date()): string {
  const { stem, ext } = splitName(original);
  let name = stem;
  if (spec.find) {
    if (spec.regex) {
      let re: RegExp;
      try {
        re = new RegExp(spec.find, spec.ignoreCase ? "giu" : "gu");
      } catch {
        throw new ConvertError("invalidPattern", { value: spec.find });
      }
      name = name.replace(re, spec.replace);
    } else {
      const flags = spec.ignoreCase ? "gi" : "g";
      name = name.replace(new RegExp(spec.find.replace(/[.*+?^${}()|[\]\\]/g, "\\$&"), flags), () => spec.replace);
    }
  }
  if (spec.trim) name = name.trim().replace(/\s{2,}/g, " ");
  name = applyCase(name, spec.case);

  const out = spec.pattern.replace(/\{\{|\}\}|\.?\{(\w+)(?::([^}]*))?\}/g, (m, token: string | undefined, arg: string | undefined) => {
    if (m === "{{") return "{";
    if (m === "}}") return "}";
    const dot = m.startsWith(".") ? "." : "";
    switch (token) {
      case "name":
        return dot + name;
      case "ext":
        return ext; // ".{ext}" is not doubled: the dot is dropped
      case "n":
        return dot + String(index + 1);
      case "counter": {
        const v = spec.start + index;
        return dot + (arg && /^0+$/.test(arg) ? String(v).padStart(arg.length, "0") : String(v));
      }
      case "date":
        return dot + formatDate(now, arg || "yyyy-MM-dd");
      case "original":
        return dot + original;
      default:
        throw new ConvertError("invalidPattern", { value: m.replace(/^\./, "") });
    }
  });
  if (!out.trim()) throw new ConvertError("invalidPattern", { value: spec.pattern });
  return sanitizeFileName(out.trim());
}

/** New names for every file, in order; duplicates get " (2)", " (3)"... (copies must never overwrite each other). */
export function renameAll(names: readonly string[], spec: RenameSpec, now: Date = new Date()): string[] {
  const taken = new Set<string>();
  return names.map((n, i) => {
    const base = renameOne(n, i, spec, now);
    let candidate = base;
    const { stem, ext } = splitName(base);
    for (let k = 2; taken.has(candidate.toLowerCase()); k++) candidate = `${stem} (${k})${ext}`;
    taken.add(candidate.toLowerCase());
    return candidate;
  });
}
