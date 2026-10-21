// Option drafts for the generated options panel: the panel edits strings/booleans, this module validates them and
// produces typed values for `Job.options` (mirrors the desktop ToolOption defaults).

import type { OptionValues, ToolOption } from "./types";

export type Draft = Record<string, string | boolean>;

export function initialDraft(options: readonly ToolOption[]): Draft {
  const d: Draft = {};
  for (const o of options) d[o.key] = o.kind === "bool" ? o.default : String(o.default);
  return d;
}

/** Typed defaults of every option (used when an operation runs without asking). */
export function defaultValues(options: readonly ToolOption[]): OptionValues {
  const out: OptionValues = {};
  for (const o of options) out[o.key] = o.default;
  return out;
}

/** Turns the draft into typed values; returns null when a required or numeric field is invalid or out of range. */
export function readValues(options: readonly ToolOption[], draft: Draft): OptionValues | null {
  const out: OptionValues = {};
  for (const o of options) {
    const raw = draft[o.key];
    switch (o.kind) {
      case "bool":
        out[o.key] = Boolean(raw);
        break;
      case "number":
      case "range": {
        const text = String(raw).trim();
        const n = Number(text);
        if (text === "" || !Number.isFinite(n)) return null;
        if (o.min !== undefined && n < o.min) return null;
        if (o.max !== undefined && n > o.max) return null;
        out[o.key] = n;
        break;
      }
      case "choice":
        out[o.key] = o.choices.includes(String(raw)) ? String(raw) : o.default;
        break;
      case "text":
      case "password":
        if (o.required && String(raw).trim() === "") return null;
        out[o.key] = String(raw);
        break;
    }
  }
  return out;
}
