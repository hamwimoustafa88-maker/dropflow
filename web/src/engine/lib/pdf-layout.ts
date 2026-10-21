// Pure page geometry for stamping (watermark, page numbers). Node-compatible; no pdf-lib import.
// Coordinates are PDF user space (origin bottom-left, y up). `rotation` is the page's /Rotate (clockwise, multiple of 90).

export type PageNumberPosition = "bottom-center" | "bottom-right" | "bottom-left" | "top-center" | "top-right" | "top-left";

export const PAGE_NUMBER_POSITIONS: readonly PageNumberPosition[] = [
  "bottom-center",
  "bottom-right",
  "bottom-left",
  "top-center",
  "top-right",
  "top-left",
];

export interface Box {
  x: number;
  y: number;
  width: number;
  height: number;
}

export function normalizeRotation(deg: number): 0 | 90 | 180 | 270 {
  const r = (((Math.round(deg / 90) * 90) % 360) + 360) % 360;
  return r as 0 | 90 | 180 | 270;
}

/** Visual (as displayed) -> user-space point, inside a box of user size W x H, for a page rotated clockwise by `rotation`. */
export function visualToUser(vx: number, vy: number, W: number, H: number, rotation: number): { x: number; y: number } {
  switch (normalizeRotation(rotation)) {
    case 90:
      return { x: W - vy, y: vx };
    case 180:
      return { x: W - vx, y: H - vy };
    case 270:
      return { x: vy, y: H - vx };
    default:
      return { x: vx, y: vy };
  }
}

/** User-space -> visual point (inverse of visualToUser). */
export function userToVisual(x: number, y: number, W: number, H: number, rotation: number): { x: number; y: number } {
  switch (normalizeRotation(rotation)) {
    case 90:
      return { x: y, y: W - x };
    case 180:
      return { x: W - x, y: H - y };
    case 270:
      return { x: H - y, y: x };
    default:
      return { x, y };
  }
}

export interface PlacementInput {
  box: Box;
  rotation: number;
  position: PageNumberPosition;
  textWidth: number;
  fontSize: number;
  margin?: number;
}

export interface Placement {
  /** Baseline start of the text in user space (includes the box offset). */
  x: number;
  y: number;
  /** Text rotation in degrees, counter-clockwise, so the text reads upright on the displayed page. */
  angle: number;
}

/** Ascent used to keep top-aligned text inside the margin. */
const ASCENT = 0.8;

export function pageNumberPlacement(input: PlacementInput): Placement {
  const margin = input.margin ?? 24;
  const rot = normalizeRotation(input.rotation);
  const { width: W, height: H } = input.box;
  const vw = rot === 90 || rot === 270 ? H : W;
  const vh = rot === 90 || rot === 270 ? W : H;
  const [vert, horiz] = input.position.split("-") as ["top" | "bottom", "left" | "center" | "right"];
  const vx = horiz === "left" ? margin : horiz === "right" ? vw - margin - input.textWidth : (vw - input.textWidth) / 2;
  const vy = vert === "bottom" ? margin : vh - margin - input.fontSize * ASCENT;
  const u = visualToUser(vx, vy, W, H, rot);
  return { x: input.box.x + u.x, y: input.box.y + u.y, angle: rot };
}

export interface WatermarkInput {
  box: Box;
  rotation: number;
  /** Natural size of the rendered text image (any unit). */
  imageWidth: number;
  imageHeight: number;
  /** Target width as a fraction of the page's shorter side. */
  widthFraction?: number;
  /** Visual angle, counter-clockwise. */
  visualAngle?: number;
}

export interface WatermarkPlacement {
  /** Bottom-left origin of the (rotated) image, user space. */
  x: number;
  y: number;
  width: number;
  height: number;
  /** Rotation to give drawImage, counter-clockwise degrees (visualAngle + page /Rotate). */
  angle: number;
}

/** Centres the text image on the page, rotated `visualAngle` (default 45 deg) as displayed, width ~60% of the shorter side. */
export function watermarkPlacement(input: WatermarkInput): WatermarkPlacement {
  const frac = input.widthFraction ?? 0.6;
  const visual = input.visualAngle ?? 45;
  const rot = normalizeRotation(input.rotation);
  const { width: W, height: H } = input.box;
  const width = Math.min(W, H) * frac;
  const height = (width * input.imageHeight) / Math.max(1, input.imageWidth);
  const angle = (visual + rot) % 360;
  const rad = (angle * Math.PI) / 180;
  const cos = Math.cos(rad);
  const sin = Math.sin(rad);
  // pdf-lib rotates around the image's bottom-left origin: move the origin so the image CENTRE lands on the page centre.
  const cx = input.box.x + W / 2;
  const cy = input.box.y + H / 2;
  const x = cx - ((width / 2) * cos - (height / 2) * sin);
  const y = cy - ((width / 2) * sin + (height / 2) * cos);
  return { x, y, width, height, angle };
}
