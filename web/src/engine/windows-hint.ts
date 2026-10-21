// The honest "Windows app" hint: what the desktop app adds for the files in front of you that a browser cannot do.
// Derived from the catalog (platforms) minus what the registry really supports - never a hand-written promise.

import { categoryForExtension, formatLabel, opKey, windowsTools } from "./catalog-web";
import { extensionOf } from "./names";
import type { Registry } from "./registry";

export interface HintItem {
  /** Literal label (format names) - shown as is. */
  label?: string;
  /** Key under `app.ops` for tool names. */
  labelKey?: string;
}

export interface WindowsHint {
  items: HintItem[];
  /** How many more exist beyond `items` (shown as "+N"). */
  more: number;
}

/** Tools worth naming first when the desktop app has them and the browser does not. */
const PRIORITY = ["pdf.ocr", "img.ocr", "vid.transcribe", "aud.transcribe", "pdf.protect", "pdf.unlock", "img.remove-bg", "pdf.sign", "pdf.redact", "vid.encoder", "vid.burn-subs", "vid.join", "aud.merge"];

/** Short names for tools that have no wedge label because they are not implemented on the web. */
export const HINT_TOOL_LABELS: Record<string, string> = {
  "pdf.ocr": "ocr",
  "img.ocr": "ocr",
  "vid.transcribe": "transcribe",
  "aud.transcribe": "transcribe",
  "pdf.protect": "protect",
  "pdf.unlock": "unlock",
  "img.remove-bg": "removeBg",
  "pdf.sign": "sign",
  "pdf.redact": "redact",
  "vid.encoder": "encoder",
  "vid.burn-subs": "burnSubs",
  "vid.join": "join",
  "aud.merge": "join",
};

const MAX_ITEMS = 6;

export function windowsHint(
  files: readonly { name: string }[],
  registry: Pick<Registry, "find">,
  tools = windowsTools,
): WindowsHint | null {
  if (files.length === 0) return null;
  const exts = files.map((f) => extensionOf(f.name));
  const category = categoryForExtension(exts[0]);
  const supportedAll = (op: string) => exts.every((e) => registry.find(op, e));

  const formats: HintItem[] = [];
  if (category && exts.every((e) => categoryForExtension(e)?.id === category.id)) {
    for (const target of category.outputs) {
      if (!supportedAll(`convert:${target}`) && !exts.includes(target)) formats.push({ label: formatLabel(target) });
    }
  }

  const missingTools = tools.filter(
    (t) => (t.cat === category?.id || t.cat === "general") && !supportedAll(t.id),
  );
  const ranked = [...missingTools].sort((a, b) => rank(a.id) - rank(b.id));
  const seen = new Set<string>();
  const toolItems: HintItem[] = [];
  for (const t of ranked) {
    const label = HINT_TOOL_LABELS[t.id];
    const key = label ? `hint.${label}` : `ops.${opKey(t.id)}`;
    if (seen.has(key)) continue;
    seen.add(key);
    // Tools without a short hint label are only counted, not named (their wedge label may not exist on the web).
    if (label) toolItems.push({ labelKey: key });
  }

  const all = [...formats.slice(0, 4), ...toolItems];
  const total = formats.length + missingTools.length;
  if (total === 0) return null;
  const items = all.slice(0, MAX_ITEMS);
  return { items, more: Math.max(0, total - items.length) };
}

function rank(id: string): number {
  const i = PRIORITY.indexOf(id);
  return i === -1 ? 999 : i;
}
