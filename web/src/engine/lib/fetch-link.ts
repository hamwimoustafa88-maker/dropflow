// "Drop a link": downloads ONE file from a web address the user gave (dropped, pasted or typed) so it can be converted
// like a dropped file. This is the ONLY module of the web app allowed to make a network request (the static
// no-upload test enforces it): a plain GET with no credentials, no referrer and no body. Nothing about the user's own
// files is ever sent; the request carries only the address the user typed.

export const MAX_LINK_BYTES = 1024 * 1024 * 1024; // 1 GB

export type LinkErrorCode = "invalid" | "blocked" | "http" | "tooBig" | "empty" | "aborted";

export class LinkError extends Error {
  constructor(
    readonly code: LinkErrorCode,
    readonly status?: number,
  ) {
    super(code);
    this.name = "LinkError";
  }
}

/** The single http(s) URL in a dropped/pasted text (text/uri-list or text/plain), or null. Comments (#) are skipped. */
export function extractUrl(text: string | null | undefined): string | null {
  if (!text) return null;
  const lines = text
    .split(/\r?\n/)
    .map((l) => l.trim())
    .filter((l) => l && !l.startsWith("#"));
  if (lines.length !== 1) return null;
  return parseHttpUrl(lines[0]);
}

/** A normalised http(s) URL, or null for anything else (javascript:, data:, file:, relative text, spaces...). */
export function parseHttpUrl(text: string): string | null {
  const s = text.trim();
  if (!/^https?:\/\/\S+$/i.test(s)) return null;
  try {
    const u = new URL(s);
    return u.protocol === "http:" || u.protocol === "https:" ? u.href : null;
  } catch {
    return null;
  }
}

const EXT_BY_TYPE: Record<string, string> = {
  "image/png": "png",
  "image/jpeg": "jpg",
  "image/webp": "webp",
  "image/gif": "gif",
  "image/avif": "avif",
  "image/bmp": "bmp",
  "image/svg+xml": "svg",
  "image/x-icon": "ico",
  "image/vnd.microsoft.icon": "ico",
  "application/pdf": "pdf",
  "application/zip": "zip",
  "application/gzip": "gz",
  "application/json": "json",
  "text/plain": "txt",
  "text/csv": "csv",
  "text/html": "html",
  "text/markdown": "md",
  "text/vtt": "vtt",
  "application/x-subrip": "srt",
  "audio/mpeg": "mp3",
  "audio/wav": "wav",
  "audio/x-wav": "wav",
  "audio/ogg": "ogg",
  "audio/flac": "flac",
  "audio/mp4": "m4a",
  "video/mp4": "mp4",
  "video/webm": "webm",
  "video/quicktime": "mov",
};

const clean = (name: string) =>
  name
    .replace(/[\\/:*?"<>|\u0000-\u001f]/g, "_")
    .replace(/^\.+/, "")
    .trim()
    .slice(0, 200);

/** File name from Content-Disposition (filename* first), else the URL path, else "download"; adds an extension from Content-Type when missing. */
export function linkFileName(url: string, contentDisposition: string | null, contentType: string | null): string {
  let name = "";
  if (contentDisposition) {
    const star = /filename\*\s*=\s*(?:UTF-8|utf-8)?''([^;]+)/i.exec(contentDisposition);
    const plain = /filename\s*=\s*"([^"]+)"|filename\s*=\s*([^;]+)/i.exec(contentDisposition);
    try {
      if (star) name = decodeURIComponent(star[1].trim().replace(/^"|"$/g, ""));
    } catch {
      name = "";
    }
    if (!name && plain) name = (plain[1] ?? plain[2] ?? "").trim();
  }
  if (!name) {
    try {
      const last = new URL(url).pathname.split("/").filter(Boolean).pop() ?? "";
      name = decodeURIComponent(last);
    } catch {
      name = "";
    }
  }
  name = clean(name) || "download";
  const type = (contentType ?? "").split(";")[0].trim().toLowerCase();
  if (!/\.[a-z0-9]{1,5}$/i.test(name) && EXT_BY_TYPE[type]) name = `${name}.${EXT_BY_TYPE[type]}`;
  return name;
}

export interface FetchLinkOptions {
  signal?: AbortSignal;
  /** received bytes, total bytes (0 when the server does not say) */
  onProgress?: (received: number, total: number) => void;
  maxBytes?: number;
}

/**
 * Downloads `url` (GET, no cookies, no referrer) and returns it as a File. Errors are LinkError:
 * "blocked" = the browser refused (the site does not allow cross-origin downloads, CORS, or the network failed),
 * "http" = the server answered with an error status, "tooBig" = over the cap, "aborted" = the user cancelled.
 */
export async function fetchLink(url: string, { signal, onProgress, maxBytes = MAX_LINK_BYTES }: FetchLinkOptions = {}): Promise<File> {
  const href = parseHttpUrl(url);
  if (!href) throw new LinkError("invalid");
  let res: Response;
  try {
    res = await fetch(href, { method: "GET", credentials: "omit", referrerPolicy: "no-referrer", mode: "cors", cache: "no-store", redirect: "follow", signal });
  } catch (e) {
    if (signal?.aborted || (e instanceof DOMException && e.name === "AbortError")) throw new LinkError("aborted");
    throw new LinkError("blocked");
  }
  if (!res.ok) throw new LinkError("http", res.status);
  const total = Number(res.headers.get("content-length") ?? 0) || 0;
  if (total > maxBytes) {
    void res.body?.cancel();
    throw new LinkError("tooBig");
  }
  const chunks: Uint8Array[] = [];
  let received = 0;
  try {
    if (res.body) {
      const reader = res.body.getReader();
      for (;;) {
        const { done, value } = await reader.read();
        if (done) break;
        received += value.byteLength;
        if (received > maxBytes) {
          await reader.cancel();
          throw new LinkError("tooBig");
        }
        chunks.push(value);
        onProgress?.(received, total);
      }
    } else {
      const buf = new Uint8Array(await res.arrayBuffer());
      received = buf.byteLength;
      if (received > maxBytes) throw new LinkError("tooBig");
      chunks.push(buf);
      onProgress?.(received, total);
    }
  } catch (e) {
    if (e instanceof LinkError) throw e;
    if (signal?.aborted) throw new LinkError("aborted");
    throw new LinkError("blocked");
  }
  if (received === 0) throw new LinkError("empty");
  const type = res.headers.get("content-type") ?? "";
  const name = linkFileName(res.url || href, res.headers.get("content-disposition"), type);
  return new File(chunks as BlobPart[], name, { type: type.split(";")[0].trim(), lastModified: Date.now() });
}
