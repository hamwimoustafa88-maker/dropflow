// SVG geometry of the wheel for any number of wedges (the landing demo is always 6). Pure, shared by the component
// and its tests. Coordinates are rounded so server and client render byte-identical paths (no hydration mismatch).

export const CX = 200;
export const CY = 200;
export const R_OUT = 178;
export const R_IN = 72;

const round = (n: number) => Math.round(n * 100) / 100;

export function polar(r: number, deg: number): readonly [number, number] {
  const a = (deg * Math.PI) / 180;
  return [round(CX + r * Math.cos(a)), round(CY + r * Math.sin(a))] as const;
}

/** Start angle of wedge `i` of `n`: wedge 0 is centred at the top (-90 degrees), going clockwise. */
export function wedgeStart(n: number, i: number): number {
  const step = 360 / n;
  return -90 - step / 2 + i * step;
}

/** Centre angle of wedge `i` of `n` in degrees. */
export function wedgeMid(n: number, i: number): number {
  return wedgeStart(n, i) + 180 / n;
}

/** Path of one annular wedge. A single wedge is drawn as a full ring (two half arcs). */
export function wedgePath(n: number, i: number): string {
  if (n <= 1) {
    const [ox0, oy0] = polar(R_OUT, -90);
    const [ox1, oy1] = polar(R_OUT, 90);
    const [ix0, iy0] = polar(R_IN, -90);
    const [ix1, iy1] = polar(R_IN, 90);
    return `M ${ox0} ${oy0} A ${R_OUT} ${R_OUT} 0 1 1 ${ox1} ${oy1} A ${R_OUT} ${R_OUT} 0 1 1 ${ox0} ${oy0} Z M ${ix0} ${iy0} A ${R_IN} ${R_IN} 0 1 0 ${ix1} ${iy1} A ${R_IN} ${R_IN} 0 1 0 ${ix0} ${iy0} Z`;
  }
  const step = 360 / n;
  const a0 = wedgeStart(n, i);
  const a1 = a0 + step;
  const large = step > 180 ? 1 : 0;
  const [x0, y0] = polar(R_OUT, a0);
  const [x1, y1] = polar(R_OUT, a1);
  const [x2, y2] = polar(R_IN, a1);
  const [x3, y3] = polar(R_IN, a0);
  return `M ${x0} ${y0} A ${R_OUT} ${R_OUT} 0 ${large} 1 ${x1} ${y1} L ${x2} ${y2} A ${R_IN} ${R_IN} 0 ${large} 0 ${x3} ${y3} Z`;
}

/** Where the label of wedge `i` is centred. */
export function labelPos(n: number, i: number): readonly [number, number] {
  return polar((R_OUT + R_IN) / 2, n <= 1 ? -90 : wedgeMid(n, i));
}

/** Roughly how many characters fit in a wedge label at 14px (arc length at mid radius, minus padding). */
export function maxLabelChars(n: number): number {
  const mid = (R_OUT + R_IN) / 2;
  const arc = n <= 1 ? 2 * Math.PI * mid : (2 * Math.PI * mid) / n;
  const radialSpace = R_OUT - R_IN - 12;
  return Math.max(3, Math.floor(Math.min(Math.max(arc, 0) - 14, radialSpace + 40) / 8.2));
}

/** Shortens a label that would overflow its wedge ("Page numbers" -> "Page num…"). */
export function fitLabel(label: string, n: number): string {
  const max = maxLabelChars(n);
  const chars = [...label];
  return chars.length <= max ? label : chars.slice(0, Math.max(1, max - 1)).join("") + "…";
}

/** Next focus index for arrow keys on a ring of `n` wedges; null when the key is not a navigation key. */
export function nextIndex(n: number, current: number, key: string): number | null {
  if (n <= 0) return null;
  switch (key) {
    case "ArrowRight":
    case "ArrowDown":
      return (current + 1) % n;
    case "ArrowLeft":
    case "ArrowUp":
      return (current - 1 + n) % n;
    case "Home":
      return 0;
    case "End":
      return n - 1;
    default:
      return null;
  }
}
