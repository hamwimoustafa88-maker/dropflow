// Runtime detection of what this browser can really encode/decode. Main thread only (uses a DOM canvas / <img>).
// A probe that fails or times out simply counts as "not supported" - we never claim a format we did not verify.

import type { ImageCaps } from "../caps";

export interface CapsProbes {
  /** Does canvas.toBlob(mime) produce a blob of exactly that type? */
  encodes(mime: string): Promise<boolean>;
  /** Can an <img> decode a tiny known AVIF image? */
  decodesAvif(): Promise<boolean>;
}

/** Pure orchestration, testable with stub probes. PNG and JPEG are mandatory in every canvas implementation. */
export async function detectImageCapsWith(p: CapsProbes): Promise<ImageCaps> {
  const [webp, avif, decodeAvif] = await Promise.all([
    p.encodes("image/webp").catch(() => false),
    p.encodes("image/avif").catch(() => false),
    p.decodesAvif().catch(() => false),
  ]);
  return { encode: { png: true, jpg: true, webp, avif }, decodeAvif };
}

// Smallest well-known AVIF test image (a 2x2 still); if this ever fails to decode, AVIF input is simply not offered.
const AVIF_PROBE =
  "data:image/avif;base64,AAAAIGZ0eXBhdmlmAAAAAGF2aWZtaWYxbWlhZk1BMUIAAADybWV0YQAAAAAAAAAoaGRscgAAAAAAAAAAcGljdAAAAAAAAAAAAAAAAGxpYmF2aWYAAAAADnBpdG0AAAAAAAEAAAAeaWxvYwAAAABEAAABAAEAAAABAAABGgAAAB0AAAAoaWluZgAAAAAAAQAAABppbmZlAgAAAAABAABhdjAxQ29sb3IAAAAAamlwcnAAAABLaXBjbwAAABRpc3BlAAAAAAAAAAIAAAACAAAAEHBpeGkAAAAAAwgICAAAAAxhdjFDgQAMAAAAABNjb2xybmNseAACAAIAAYAAAAAXaXBtYQAAAAAAAAABAAEEAQKDBAAAAB9tZGF0EgAKCBgANogQEAwgMg8f8D///8WfhwB8+ErK42A=";

const domProbes: CapsProbes = {
  encodes(mime) {
    return new Promise<boolean>((resolve) => {
      try {
        const canvas = document.createElement("canvas");
        canvas.width = 2;
        canvas.height = 2;
        const ctx = canvas.getContext("2d");
        if (ctx) {
          ctx.fillStyle = "#4338ca";
          ctx.fillRect(0, 0, 2, 2);
        }
        const timer = setTimeout(() => resolve(false), 4000);
        canvas.toBlob(
          (blob) => {
            clearTimeout(timer);
            resolve(!!blob && blob.type === mime);
          },
          mime,
          0.8,
        );
      } catch {
        resolve(false);
      }
    });
  },
  decodesAvif() {
    return new Promise<boolean>((resolve) => {
      try {
        const img = new Image();
        const timer = setTimeout(() => resolve(false), 4000);
        img.onload = () => {
          clearTimeout(timer);
          resolve(img.naturalWidth > 0 && img.naturalHeight > 0);
        };
        img.onerror = () => {
          clearTimeout(timer);
          resolve(false);
        };
        img.src = AVIF_PROBE;
      } catch {
        resolve(false);
      }
    });
  },
};

export function detectImageCaps(): Promise<ImageCaps> {
  return detectImageCapsWith(domProbes);
}
