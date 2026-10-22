import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { describe, expect, it } from "vitest";
import {
  ANDROID_RELEASED,
  CHECKSUMS_FILE,
  DOWNLOADS,
  LINUX_RELEASED,
  MACOS_RELEASED,
  PLATFORMS,
  WINDOWS_ARM64_RELEASED,
  assetDetail,
  assetsOf,
  detectTarget,
  downloadUrl,
  highlightFor,
  operatingSystems,
} from "../downloads";
import { RELEASE_DOWNLOAD_URL, RELEASES_URL } from "../site";

type Tree = { [k: string]: string | Tree | Tree[] };
const messages = (l: "en" | "ar") => JSON.parse(readFileSync(fileURLToPath(new URL(`../../../messages/${l}.json`, import.meta.url)), "utf8")) as Tree;
const get = (t: Tree, path: string) => path.split(".").reduce<unknown>((a, k) => (a && typeof a === "object" ? (a as Tree)[k] : undefined), t);

describe("download table (src/lib/downloads.ts)", () => {
  it("every asset file name is unique and looks like a release file", () => {
    const names = DOWNLOADS.map((a) => a.file);
    expect(new Set(names).size).toBe(names.length);
    for (const n of names) expect(n).toMatch(/^[A-Za-z0-9._-]+\.(exe|zip|dmg|deb|rpm|AppImage|apk)$/);
  });

  it("has the exact asset names the release workflows publish", () => {
    expect(DOWNLOADS.map((a) => a.file)).toEqual([
      "DropFlow-win-Setup.exe",
      "DropFlow-win-arm64-Setup.exe",
      "DropFlow-win-Portable.zip",
      "dropflow-cli-win-x64.zip",
      "DropFlow-mac-arm64.dmg",
      "DropFlow-mac-x64.dmg",
      "DropFlow-linux-amd64.deb",
      "DropFlow-linux-arm64.deb",
      "DropFlow-linux-x86_64.rpm",
      "DropFlow-linux-aarch64.rpm",
      "DropFlow-linux-x86_64.AppImage",
      "DropFlow-linux-aarch64.AppImage",
      "DropFlow-android.apk",
    ]);
  });

  it("every link is a direct latest-release download on GitHub (never the release page, never a 'latest' guess elsewhere)", () => {
    expect(RELEASE_DOWNLOAD_URL).toBe(`${RELEASES_URL}/download`);
    for (const a of [...DOWNLOADS.map((d) => d.file), CHECKSUMS_FILE]) {
      const u = new URL(downloadUrl(a));
      expect(u.origin).toBe("https://github.com");
      expect(u.pathname).toBe(`/hamwimoustafa88-maker/dropflow/releases/latest/download/${a}`);
      expect(u.search + u.hash).toBe("");
    }
  });

  it("Windows x64 Setup comes first (the main button) and is always available, like Portable and the CLI", () => {
    const win = assetsOf("windows");
    expect(win[0]).toMatchObject({ file: "DropFlow-win-Setup.exe", arch: "x64", available: true });
    expect(win.find((a) => a.file === "DropFlow-win-Portable.zip")?.available).toBe(true);
    expect(win.find((a) => a.file === "dropflow-cli-win-x64.zip")?.available).toBe(true);
  });

  it("the other platforms follow their one release flag", () => {
    expect(assetsOf("windows").find((a) => a.arch === "arm64")?.available).toBe(WINDOWS_ARM64_RELEASED);
    for (const a of assetsOf("macos")) expect(a.available).toBe(MACOS_RELEASED);
    for (const a of assetsOf("linux")) expect(a.available).toBe(LINUX_RELEASED);
    for (const a of assetsOf("android")) expect(a.available).toBe(ANDROID_RELEASED);
  });

  it("every platform has files, x64 and ARM64 where both exist, and a short technical detail per file", () => {
    for (const p of PLATFORMS) expect(assetsOf(p).length, p).toBeGreaterThan(0);
    expect(assetsOf("macos").map((a) => a.arch).sort()).toEqual(["arm64", "x64"]);
    expect(assetsOf("linux").filter((a) => a.arch === "arm64")).toHaveLength(3);
    expect(assetDetail(assetsOf("windows")[0])).toBe("x64 · .exe");
    expect(assetDetail(assetsOf("linux").find((a) => a.file.endsWith(".AppImage") && a.arch === "arm64")!)).toBe("ARM64 · AppImage");
    expect(assetDetail(assetsOf("android")[0])).toBe(".apk");
  });

  for (const loc of ["en", "ar"] as const) {
    it(`${loc}: every platform, every asset label and the download strings exist`, () => {
      const m = messages(loc);
      for (const p of [...PLATFORMS, "web"]) expect(get(m, `download.platforms.${p}`), `${loc} platform ${p}`).toEqual(expect.any(String));
      for (const a of DOWNLOADS) expect(get(m, `download.assets.${a.label}`), `${loc} asset ${a.label}`).toEqual(expect.any(String));
      for (const k of ["label", "more", "menu", "yours", "soon", "note"]) expect((get(m, `download.${k}`) as string).length, k).toBeGreaterThan(1);
      expect(get(m, "download.more")).toContain("{platform}");
      expect(get(m, "download.menu")).toContain("{platform}");
    });
  }

  it("the install note covers macOS, Windows SmartScreen and Android in both languages, in one short line", () => {
    const en = get(messages("en"), "download.note") as string;
    const ar = get(messages("ar"), "download.note") as string;
    expect(en).toMatch(/macOS.*Open.*Run anyway.*Android/);
    expect(ar).toMatch(/ماك.*فتح.*تشغيل على أي حال.*أندرويد/);
    for (const s of [en, ar]) expect(s.length).toBeLessThan(130);
  });
});

describe("visitor platform detection", () => {
  const UA = {
    win: "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36 Edg/130.0",
    mac: "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Safari/605.1.15",
    linux: "Mozilla/5.0 (X11; Linux x86_64; rv:131.0) Gecko/20100101 Firefox/131.0",
    android: "Mozilla/5.0 (Linux; Android 15; Pixel 9) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Mobile Safari/537.36",
    iphone: "Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Mobile/15E148 Safari/604.1",
    cros: "Mozilla/5.0 (X11; CrOS x86_64 14541.0.0) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36",
  };

  it("reads the user agent (Android before Linux, iPhone before Mac)", () => {
    expect(detectTarget(UA.win)).toBe("windows");
    expect(detectTarget(UA.mac)).toBe("macos");
    expect(detectTarget(UA.linux)).toBe("linux");
    expect(detectTarget(UA.android)).toBe("android");
    expect(detectTarget(UA.iphone)).toBe("web");
    expect(detectTarget(UA.cros)).toBe("web");
    expect(detectTarget("curl/8")).toBeNull();
  });

  it("prefers navigator.userAgentData.platform when the browser gives it", () => {
    expect(detectTarget(UA.win, "macOS")).toBe("macos");
    expect(detectTarget("", "Windows")).toBe("windows");
    expect(detectTarget("", "Android")).toBe("android");
    expect(detectTarget("", "Linux")).toBe("linux");
    expect(detectTarget("", "Chrome OS")).toBe("web");
    expect(detectTarget(UA.linux, "")).toBe("linux");
  });

  it("highlights Windows before detection, the web app for systems without a build, else the visitor's own platform", () => {
    expect(highlightFor(null)).toBe("windows");
    expect(highlightFor("web")).toBe("web");
    expect(highlightFor("windows")).toBe("windows");
    expect(highlightFor("macos")).toBe(MACOS_RELEASED ? "macos" : "web");
    expect(highlightFor("android")).toBe(ANDROID_RELEASED ? "android" : "web");
  });

  it("the JSON-LD platform list follows the flags", () => {
    const os = operatingSystems();
    expect(os.startsWith("Windows 10, Windows 11")).toBe(true);
    expect(os.endsWith("Web")).toBe(true);
    expect(os.includes("macOS")).toBe(MACOS_RELEASED);
    expect(os.includes("Linux")).toBe(LINUX_RELEASED);
    expect(os.includes("Android")).toBe(ANDROID_RELEASED);
  });
});
