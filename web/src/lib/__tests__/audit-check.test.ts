import { describe, expect, it } from "vitest";
import { ACCEPTED, problems } from "../../../scripts/audit-check.mjs";

const report = (...ids: string[]) => ({
  vulnerabilities: {
    pkg: { via: ["other", ...ids.map((id) => ({ url: `https://github.com/advisories/${id}`, name: "pkg", title: "t", severity: "high" }))] },
  },
});

describe("audit-check", () => {
  it("passes an accepted advisory before its review date", () => {
    expect(problems(report("GHSA-fc5q-39xc-vjpf"), ACCEPTED, new Date("2026-11-18"))).toEqual([]);
  });

  it("fails on any advisory that is not accepted", () => {
    expect(problems(report("GHSA-new0-0000-0000"), ACCEPTED, new Date("2026-11-18"))[0]).toMatch(/^not accepted: GHSA-new0/);
  });

  it("fails once the review date has passed, so nobody forgets to look again", () => {
    expect(problems(report("GHSA-fc5q-39xc-vjpf"), ACCEPTED, new Date("2027-02-01"))[0]).toMatch(/review date passed/);
  });

  it("every accepted entry has a reason and a review date at most six months away", () => {
    for (const a of Object.values(ACCEPTED) as { reason: string; reviewBy: string }[]) {
      expect(a.reason.length).toBeGreaterThan(10);
      expect(new Date(a.reviewBy).getTime() - new Date("2026-11-18").getTime()).toBeLessThan(183 * 86400_000);
    }
  });
});
