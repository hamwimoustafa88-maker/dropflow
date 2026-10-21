// Minimal types for gifenc 1.0.3 (the package ships none). Only what src/engine/lib/gif.ts uses.
declare module "gifenc" {
  export type Palette = number[][];
  export type ColorFormat = "rgb565" | "rgb444" | "rgba4444";
  export function quantize(
    rgba: Uint8Array | Uint8ClampedArray,
    maxColors: number,
    options?: { format?: ColorFormat; oneBitAlpha?: boolean | number; clearAlpha?: boolean; clearAlphaThreshold?: number; clearAlphaColor?: number },
  ): Palette;
  export function applyPalette(rgba: Uint8Array | Uint8ClampedArray, palette: Palette, format?: ColorFormat): Uint8Array;
  export interface Encoder {
    writeFrame(
      index: Uint8Array,
      width: number,
      height: number,
      opts?: { palette?: Palette; first?: boolean; transparent?: boolean; transparentIndex?: number; delay?: number; repeat?: number; dispose?: number },
    ): void;
    finish(): void;
    bytes(): Uint8Array;
    bytesView(): Uint8Array;
  }
  export function GIFEncoder(opts?: { auto?: boolean; initialCapacity?: number }): Encoder;
}
