// Browser-side actions on results: download, zip, share (Web Share API) and copy text. Nothing here sends data
// anywhere: downloads are object URLs, share hands the files to the OS share sheet, copy writes to the clipboard.

import { zipEntries } from "@/engine/zip-results";
import type { ResultFile } from "@/engine/session";

export function saveUrl(url: string, name: string): void {
  const a = document.createElement("a");
  a.href = url;
  a.download = name;
  a.rel = "noopener";
  a.style.display = "none";
  document.body.appendChild(a);
  a.click();
  a.remove();
}

/** Saves one file, or a ZIP of several. Returns the name that was saved. */
export async function downloadAll(results: readonly ResultFile[], zipName: string): Promise<string> {
  if (results.length === 1) {
    saveUrl(results[0].url, results[0].name);
    return results[0].name;
  }
  const blob = await zipEntries(results.map((r) => ({ path: r.path, file: r.file })));
  const url = URL.createObjectURL(blob);
  saveUrl(url, zipName);
  setTimeout(() => URL.revokeObjectURL(url), 60_000);
  return zipName;
}

export function canShareFiles(files: readonly File[]): boolean {
  if (typeof navigator === "undefined" || typeof navigator.canShare !== "function" || typeof navigator.share !== "function") return false;
  if (files.length === 0) return false;
  try {
    return navigator.canShare({ files: [...files] });
  } catch {
    return false;
  }
}

export async function shareFiles(files: readonly File[]): Promise<boolean> {
  try {
    await navigator.share({ files: [...files] });
    return true;
  } catch (e) {
    if (e instanceof DOMException && e.name === "AbortError") return false; // the user closed the share sheet
    throw e;
  }
}

export async function copyBlobText(blobs: readonly Blob[]): Promise<boolean> {
  const texts = await Promise.all(blobs.map((b) => b.text()));
  const text = texts.join("\n\n");
  try {
    await navigator.clipboard.writeText(text);
    return true;
  } catch {
    // Clipboard API can be blocked (insecure context / no permission): fall back to a temporary textarea.
    const ta = document.createElement("textarea");
    ta.value = text;
    ta.setAttribute("readonly", "");
    ta.style.position = "fixed";
    ta.style.opacity = "0";
    document.body.appendChild(ta);
    ta.select();
    const ok = document.execCommand("copy");
    ta.remove();
    return ok;
  }
}
