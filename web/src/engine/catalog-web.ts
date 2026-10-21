// Read-only view of the shared catalog (src/data/catalog.json, generated from catalog/tools.json) for the web app.
// The catalog says what DropFlow plans per platform; the registry says what REALLY runs in a browser. The wheel is the
// intersection, in catalog order.

import { categories, tools, type CatalogCategory, type CatalogTool } from "@/lib/catalog";
import { normalizeExt } from "./names";

export type { CatalogCategory, CatalogTool };

/** Category whose `inputs` list contains this extension (jpeg -> jpg etc. via normalizeExt). */
export function categoryForExtension(ext: string): CatalogCategory | undefined {
  const e = normalizeExt(ext);
  return categories.find((c) => c.inputs.includes(e));
}

export function categoryById(id: string): CatalogCategory | undefined {
  return categories.find((c) => c.id === id);
}

/** Tools the catalog marks as available on the web platform, in catalog order. */
export const webTools: readonly CatalogTool[] = tools.filter((t) => t.platforms.includes("web"));

/** Tools that exist on desktop (windows) in the catalog. */
export const windowsTools: readonly CatalogTool[] = tools.filter((t) => t.platforms.includes("windows"));

export function toolById(id: string): CatalogTool | undefined {
  return tools.find((t) => t.id === id);
}

/** "img.strip-gps" -> "img_strip_gps": the key used under `app.ops` in the message files (next-intl keys cannot contain dots). */
export function opKey(operation: string): string {
  return operation.replace(/[^A-Za-z0-9]/g, "_");
}

/** "bottom-center" -> "bottom_center", "1:1" -> "1_1": key of a choice value under `app.choice.<option>`. */
export function choiceKey(value: string): string {
  return value.replace(/[^A-Za-z0-9]/g, "_");
}

/** Display label of an output format wedge ("pdfa" -> "PDF/A", "jpg" -> "JPG"). */
export function formatLabel(ext: string): string {
  return ext === "pdfa" ? "PDF/A" : ext.toUpperCase();
}
