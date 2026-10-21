// Wheel model (pure, no DOM): decides what each of the three wheels shows for a set of files. Mirrors the desktop
// WheelBuilder: convert wedges come from the category's outputs, tool wedges from the catalog, and a wedge exists ONLY
// when a registered converter really supports it for every selected file.

import { categoryForExtension, formatLabel, opKey, webTools, type CatalogTool } from "./catalog-web";
import { extensionOf } from "./names";
import type { Registry } from "./registry";

export type WheelKind = "convert" | "tools" | "actions";
export const WHEEL_ORDER: readonly WheelKind[] = ["convert", "tools", "actions"];

export interface Wedge {
  /** Stable key, e.g. "convert:png", "img.compress", "web.download". */
  id: string;
  wheel: WheelKind;
  /** Operation id: "convert:png" | a catalog tool id | a web action id ("web.download"). */
  operation: string;
  /** Literal label (format names like "PNG"); wins over labelKey. */
  label?: string;
  /** Key under the `app` message namespace, e.g. "ops.img_compress" or "actions.download". */
  labelKey?: string;
  needsOptions: boolean;
  multi: boolean;
  /** Synthetic "more" wedge that flips to the next page of a long wheel. */
  more?: boolean;
  /** Synthetic "+ Add" wedge (always the last one): opens the list of everything else these files can do. */
  add?: boolean;
  /** The person added this item to this wheel with "+ Add" (it keeps its own family in `wheel`). */
  pinned?: boolean;
}

export interface WheelSet {
  convert: Wedge[];
  tools: Wedge[];
  actions: Wedge[];
}

export interface FileInfo {
  name: string;
  size: number;
}

export interface ActionState {
  /** There are finished results to act on. */
  hasResults: boolean;
  /** navigator.canShare({ files }) is true for the current subject (results, else inputs). */
  canShare: boolean;
  /** At least one text result (or text input) can be copied. */
  hasText: boolean;
}

/**
 * Convert targets the browser adds to a catalog category. They are web-only extras, not desktop formats: a picture
 * becomes text through OCR ("TXT" on the Convert wheel, as the owner asked).
 */
export const WEB_EXTRA_OUTPUTS: Readonly<Record<string, readonly string[]>> = { image: ["txt"] };

/** Catalog `multi` tools that are still useful with a single file (the desktop builder hides every multi tool then). */
const MULTI_SINGLE_OK: ReadonlySet<string> = new Set(["arc.zip"]);

export const WEB_ACTIONS = {
  download: "web.download",
  share: "web.share",
  copy: "web.copy",
} as const;

export function isWebAction(operation: string): boolean {
  return operation.startsWith("web.");
}

export function buildWheels(
  files: readonly FileInfo[],
  registry: Pick<Registry, "find">,
  actions: ActionState,
  catalogTools: readonly CatalogTool[] = webTools,
): WheelSet {
  const exts = files.map((f) => extensionOf(f.name));
  const wheels: WheelSet = { convert: [], tools: [], actions: buildActions(actions) };
  if (files.length === 0) return { convert: [], tools: [], actions: [] };

  // Convert: only when every file belongs to the same catalog category (same rule as desktop).
  const cats = new Set(exts.map((e) => categoryForExtension(e)?.id ?? ""));
  if (cats.size === 1) {
    const category = categoryForExtension(exts[0]);
    if (category) {
      for (const target of new Set([...category.outputs, ...(WEB_EXTRA_OUTPUTS[category.id] ?? [])])) {
        const op = `convert:${target}`;
        const converters = exts.map((e) => registry.find(op, e));
        if (converters.some((c) => !c)) continue;
        wheels.convert.push({
          id: op,
          wheel: "convert",
          operation: op,
          label: formatLabel(target),
          needsOptions: converters[0]!.options(op).length > 0,
          multi: false,
        });
      }
    }
  }

  // Tools: catalog order, filtered by what the registry really supports for every file.
  for (const tool of catalogTools) {
    if (tool.multi && files.length < 2 && !MULTI_SINGLE_OK.has(tool.id)) continue;
    const converters = exts.map((e) => registry.find(tool.id, e));
    if (converters.some((c) => !c)) continue;
    wheels.tools.push({
      id: tool.id,
      wheel: "tools",
      operation: tool.id,
      labelKey: `ops.${opKey(tool.id)}`,
      needsOptions: converters[0]!.options(tool.id).length > 0,
      multi: Boolean(tool.multi),
    });
  }
  return wheels;
}

function buildActions(state: ActionState): Wedge[] {
  const out: Wedge[] = [];
  const add = (operation: string, key: string) =>
    out.push({ id: operation, wheel: "actions", operation, labelKey: `actions.${key}`, needsOptions: false, multi: false });
  if (state.hasResults) add(WEB_ACTIONS.download, "download");
  if (state.canShare) add(WEB_ACTIONS.share, "share");
  if (state.hasText) add(WEB_ACTIONS.copy, "copy");
  return out;
}

// ---- "+ Add": items the person pins to a wheel (mirrors the desktop WheelBuilder.Build with WheelPins) -------------------

/** Id of the "+ Add" wedge. */
export const ADD_ID = "wheel.add";
/** Pin category for mixed selections and files of no known category. */
export const OTHER_CATEGORY = "other";

/** wheel -> input category ("image", "pdf"... or "other") -> wedge ids, oldest first. */
export type Pins = Partial<Record<WheelKind, Record<string, string[]>>>;

export interface PinnedWheels {
  wheels: WheelSet;
  /** What "+ Add" lists for each wheel: every item these files support that is not on that wheel, Convert -> Tools -> Actions. */
  addable: Record<WheelKind, Wedge[]>;
  /** Pinned items shown on each wheel (removable from the "+ Add" list). */
  pinned: Record<WheelKind, Wedge[]>;
}

/** The input category the pins of a selection belong to: the shared catalog category, else "other". */
export function pinCategory(files: readonly FileInfo[]): string {
  const ids = new Set(files.map((f) => categoryForExtension(extensionOf(f.name))?.id ?? OTHER_CATEGORY));
  return ids.size === 1 ? [...ids][0] : OTHER_CATEGORY;
}

/**
 * The wheels as the person sees them: each wheel's own items, then the items pinned to it for this kind of input, then a
 * "+ Add" wedge when anything else applies (or something is pinned, so it can be removed again). A wheel with no item
 * of its own gets no "+ Add" either, so empty wheels stay empty (as before). Pins that do not apply to
 * this selection (no converter, too few files...) are kept in storage but not shown.
 */
export function withPins(own: WheelSet, pins: Pins, category: string): PinnedWheels {
  const everything = new Map<string, Wedge>();
  for (const w of [...own.convert, ...own.tools, ...own.actions]) if (!everything.has(w.id)) everything.set(w.id, w);
  const wheels: WheelSet = { convert: [], tools: [], actions: [] };
  const addable = { convert: [], tools: [], actions: [] } as Record<WheelKind, Wedge[]>;
  const pinned = { convert: [], tools: [], actions: [] } as Record<WheelKind, Wedge[]>;
  if (everything.size === 0) return { wheels, addable, pinned };
  for (const kind of WHEEL_ORDER) {
    const wedges = own[kind].slice();
    for (const id of pins[kind]?.[category] ?? []) {
      const item = everything.get(id);
      if (!item || wedges.some((w) => w.id === id)) continue;
      const p = { ...item, pinned: true };
      wedges.push(p);
      pinned[kind].push(p);
    }
    const onWheel = new Set(wedges.map((w) => w.id));
    addable[kind] = [...everything.values()].filter((w) => !onWheel.has(w.id));
    // A wheel with nothing of its own stays empty (its tab stays disabled, e.g. Convert for mixed files).
    if (wedges.length > 0 && (addable[kind].length > 0 || pinned[kind].length > 0)) {
      wedges.push({ id: ADD_ID, wheel: kind, operation: ADD_ID, labelKey: "add.wedge", needsOptions: false, multi: false, add: true });
    }
    wheels[kind] = wedges;
  }
  return { wheels, addable, pinned };
}

/** Adds a pin at the end (no-op when present). Returns a NEW object. */
export function addPin(pins: Pins, wheel: WheelKind, category: string, id: string): Pins {
  const list = pins[wheel]?.[category] ?? [];
  if (list.includes(id)) return pins;
  return { ...pins, [wheel]: { ...(pins[wheel] ?? {}), [category]: [...list, id] } };
}

/** Removes a pin. Returns a NEW object (empty lists and wheels are dropped). */
export function removePin(pins: Pins, wheel: WheelKind, category: string, id: string): Pins {
  const list = pins[wheel]?.[category];
  if (!list?.includes(id)) return pins;
  const rest = list.filter((x) => x !== id);
  const cats = { ...(pins[wheel] ?? {}) };
  if (rest.length) cats[category] = rest;
  else delete cats[category];
  const out: Pins = { ...pins };
  if (Object.keys(cats).length) out[wheel] = cats;
  else delete out[wheel];
  return out;
}

/** Reads stored pins defensively: unknown wheels, non-string ids and duplicates are dropped (hand-edited storage never crashes). */
export function sanitizePins(raw: unknown): Pins {
  let pins: Pins = {};
  if (!raw || typeof raw !== "object") return pins;
  for (const kind of WHEEL_ORDER) {
    const cats = (raw as Record<string, unknown>)[kind];
    if (!cats || typeof cats !== "object") continue;
    for (const [category, ids] of Object.entries(cats as Record<string, unknown>)) {
      if (!category.trim() || !Array.isArray(ids)) continue;
      for (const id of ids) if (typeof id === "string" && id.trim() && id !== ADD_ID) pins = addPin(pins, kind, category, id);
    }
  }
  return pins;
}

/** Maximum wedges on one ring before the wheel is paged (keeps labels readable, also at 390px). */
export const MAX_PER_PAGE = 8;

/**
 * Splits a long wheel into pages. Up to MAX_PER_PAGE wedges stay on one page; otherwise every page carries
 * MAX_PER_PAGE - 1 wedges plus a synthetic "more" wedge that flips to the next page (wrapping around).
 */
export function pageWedges(wedges: readonly Wedge[], perPage = MAX_PER_PAGE): Wedge[][] {
  if (wedges.length <= perPage) return [wedges.slice()];
  const size = perPage - 1;
  const pages: Wedge[][] = [];
  for (let i = 0; i < wedges.length; i += size) pages.push(wedges.slice(i, i + size));
  return pages.map((p, i) => [
    ...p,
    { id: `more:${i}`, wheel: p[0].wheel, operation: "web.more", labelKey: "more", needsOptions: false, multi: false, more: true },
  ]);
}

/** First wheel that has wedges, preferring `preferred`; null when all are empty. */
export function firstNonEmpty(set: WheelSet, preferred: WheelKind): WheelKind | null {
  if (set[preferred].length > 0) return preferred;
  return WHEEL_ORDER.find((k) => set[k].length > 0) ?? null;
}

/** Maps modifier state of a DragEvent/KeyboardEvent to a wheel: Shift = convert, Ctrl = tools, Alt = actions. */
export function wheelForModifiers(m: { shiftKey?: boolean; ctrlKey?: boolean; altKey?: boolean; metaKey?: boolean }): WheelKind | null {
  if (m.shiftKey) return "convert";
  if (m.ctrlKey || m.metaKey) return "tools";
  if (m.altKey) return "actions";
  return null;
}

/** Maps a pressed modifier KEY (event.key) to a wheel. */
export function wheelForKey(key: string): WheelKind | null {
  switch (key) {
    case "Shift":
      return "convert";
    case "Control":
    case "Meta":
      return "tools";
    case "Alt":
      return "actions";
    default:
      return null;
  }
}
