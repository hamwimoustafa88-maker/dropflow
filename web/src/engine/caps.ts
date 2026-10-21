// Runtime capability store. Things the browser/engine can only tell us at runtime (canvas encoders...)
// are detected once and kept here; converters read them inside `supports()` so the wheel never offers something that
// does not work. Plain module state + a tiny subscription so the UI can re-derive the wheel.

export type ImageEncodeFormat = "png" | "jpg" | "webp" | "avif";

export interface ImageCaps {
  /** Which formats `canvas.toBlob` really encodes in this browser (detected, never assumed). */
  encode: Record<ImageEncodeFormat, boolean>;
  /** Whether this browser can decode AVIF (createImageBitmap on a known AVIF image). */
  decodeAvif: boolean;
}

/** Conservative defaults used until detection finishes: PNG/JPEG always work, WebP/AVIF are unknown. */
export const DEFAULT_IMAGE_CAPS: ImageCaps = {
  encode: { png: true, jpg: true, webp: false, avif: false },
  decodeAvif: false,
};

let imageCaps: ImageCaps = DEFAULT_IMAGE_CAPS;
let version = 0;
const listeners = new Set<() => void>();

function bump(): void {
  version++;
  listeners.forEach((l) => l());
}

export function getImageCaps(): ImageCaps {
  return imageCaps;
}
export function setImageCaps(c: ImageCaps): void {
  imageCaps = c;
  bump();
}

export function capsVersion(): number {
  return version;
}
export function subscribeCaps(fn: () => void): () => void {
  listeners.add(fn);
  return () => {
    listeners.delete(fn);
  };
}
