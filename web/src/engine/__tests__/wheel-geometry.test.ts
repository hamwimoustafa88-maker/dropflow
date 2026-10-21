import { describe, expect, it } from "vitest";
import { CX, CY, R_IN, R_OUT, fitLabel, labelPos, maxLabelChars, nextIndex, wedgeMid, wedgePath, wedgeStart } from "../wheel-geometry";

describe("wheel geometry", () => {
  it("wedge 0 is centred at the top for any wedge count", () => {
    for (const n of [2, 3, 4, 5, 6, 7, 8]) {
      expect(wedgeMid(n, 0)).toBeCloseTo(-90, 6);
      const [x, y] = labelPos(n, 0);
      expect(x).toBeCloseTo(CX, 1);
      expect(y).toBeLessThan(CY);
    }
  });

  it("wedges tile the full circle without gaps or overlap", () => {
    for (const n of [2, 3, 5, 8]) {
      const step = 360 / n;
      for (let i = 0; i < n; i++) expect(wedgeStart(n, i + 1) - wedgeStart(n, i)).toBeCloseTo(step, 6);
      expect(wedgeStart(n, n) - wedgeStart(n, 0)).toBeCloseTo(360, 6);
    }
  });

  it("paths are well-formed, deterministic and stay inside the outer radius", () => {
    for (const n of [1, 2, 6, 8]) {
      for (let i = 0; i < n; i++) {
        const d = wedgePath(n, i);
        expect(d.startsWith("M ")).toBe(true);
        expect(d.endsWith("Z")).toBe(true);
        expect(wedgePath(n, i)).toBe(d);
        for (const m of d.matchAll(/(-?\d+(?:\.\d+)?) (-?\d+(?:\.\d+)?)(?= [AL]| Z|$)/g)) {
          const x = Number(m[1]) - CX;
          const y = Number(m[2]) - CY;
          expect(Math.hypot(x, y)).toBeLessThanOrEqual(R_OUT + 0.5);
          expect(Math.hypot(x, y)).toBeGreaterThanOrEqual(R_IN - 0.5);
        }
      }
    }
  });

  it("labels fit: long labels are shortened with an ellipsis, short ones untouched", () => {
    expect(fitLabel("PNG", 8)).toBe("PNG");
    const long = "A very long wedge label indeed";
    const cut = fitLabel(long, 8);
    expect([...cut].length).toBeLessThanOrEqual(maxLabelChars(8));
    expect(cut.endsWith("…")).toBe(true);
    expect(maxLabelChars(3)).toBeGreaterThan(maxLabelChars(8));
  });

  it("arrow keys cycle, Home/End jump, other keys are ignored", () => {
    expect(nextIndex(5, 4, "ArrowRight")).toBe(0);
    expect(nextIndex(5, 0, "ArrowLeft")).toBe(4);
    expect(nextIndex(5, 2, "ArrowDown")).toBe(3);
    expect(nextIndex(5, 2, "ArrowUp")).toBe(1);
    expect(nextIndex(5, 2, "Home")).toBe(0);
    expect(nextIndex(5, 2, "End")).toBe(4);
    expect(nextIndex(5, 2, "x")).toBeNull();
    expect(nextIndex(0, 0, "ArrowRight")).toBeNull();
  });
});
