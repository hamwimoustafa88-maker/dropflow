// Turns a drop's DataTransfer into a flat File list. Folders are expanded through the (widely supported)
// webkitGetAsEntry API; everything else falls back to dataTransfer.files. The entries MUST be captured
// synchronously inside the drop handler (they become invalid after the first await).

const MAX_FILES = 2000;

type FsEntry = {
  isFile: boolean;
  isDirectory: boolean;
  name: string;
  file?: (ok: (f: File) => void, fail: (e: unknown) => void) => void;
  createReader?: () => { readEntries: (ok: (e: FsEntry[]) => void, fail: (e: unknown) => void) => void };
};

export function captureDrop(dt: DataTransfer): () => Promise<File[]> {
  const direct = [...dt.files];
  const entries: FsEntry[] = [];
  let hasDirectory = false;
  for (const item of [...dt.items]) {
    if (item.kind !== "file") continue;
    const getEntry = (item as DataTransferItem & { webkitGetAsEntry?: () => FsEntry | null }).webkitGetAsEntry;
    const entry = typeof getEntry === "function" ? getEntry.call(item) : null;
    if (entry) {
      entries.push(entry);
      if (entry.isDirectory) hasDirectory = true;
    }
  }
  return async () => {
    if (!hasDirectory) return direct;
    const out: File[] = [];
    for (const entry of entries) await walk(entry, out);
    return out.length ? out : direct;
  };
}

async function walk(entry: FsEntry, out: File[]): Promise<void> {
  if (out.length >= MAX_FILES) return;
  if (entry.isFile && entry.file) {
    const file = await new Promise<File>((ok, fail) => entry.file!(ok, fail));
    out.push(file);
  } else if (entry.isDirectory && entry.createReader) {
    const reader = entry.createReader();
    // readEntries returns batches; keep reading until it returns an empty batch.
    for (;;) {
      const batch = await new Promise<FsEntry[]>((ok, fail) => reader.readEntries(ok, fail));
      if (batch.length === 0) break;
      for (const child of batch) await walk(child, out);
      if (out.length >= MAX_FILES) break;
    }
  }
}

export function dragHasFiles(dt: DataTransfer | null): boolean {
  return !!dt && [...dt.types].includes("Files");
}

/** Same file picked or dropped twice? (name + size + lastModified) */
export function dedupeFiles(existing: readonly File[], added: readonly File[]): File[] {
  const key = (f: File) => `${f.name}|${f.size}|${f.lastModified}`;
  const seen = new Set(existing.map(key));
  const fresh: File[] = [];
  for (const f of added) {
    const k = key(f);
    if (!seen.has(k)) {
      seen.add(k);
      fresh.push(f);
    }
  }
  return [...existing, ...fresh];
}
