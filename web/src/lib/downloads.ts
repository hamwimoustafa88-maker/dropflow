// Every file a visitor can download from the landing page, in ONE table. Each link is GitHub's stable
// `releases/latest/download/<file>` address, so a click downloads the file itself (no GitHub page in between).
// The page never fetches anything from GitHub: these are plain links, and availability is decided HERE, at build time.
import { RELEASE_DOWNLOAD_URL } from "./site";

// ---- Release flags: check these against the assets of the latest release BEFORE deploying -----------------------------
// A platform whose files are not attached to the latest release yet must be `false`: its buttons then show "Soon" instead
// of a link that would answer 404. (Windows x64 Setup, Portable and the CLI zip are in every release since v0.1.2.)
export const WINDOWS_ARM64_RELEASED = true;
export const MACOS_RELEASED = true;
export const LINUX_RELEASED = true;
export const ANDROID_RELEASED = true;

export const PLATFORMS = ["windows", "macos", "linux", "android"] as const;
export type Platform = (typeof PLATFORMS)[number];
/** What the page can highlight for a visitor: a platform with release files, or the in-browser web app. */
export type Target = Platform | "web";

export type Arch = "x64" | "arm64";
/** Keys of messages `download.assets.<key>`. */
export type AssetLabel = "installer" | "portable" | "cli" | "appleSilicon" | "intel" | "deb" | "rpm" | "appImage" | "apk";

export interface DownloadAsset {
  platform: Platform;
  arch: Arch;
  label: AssetLabel;
  /** Exact asset file name on the GitHub release. */
  file: string;
  /** false: rendered as a disabled "Soon" item, never as a link. */
  available: boolean;
}

export const DOWNLOADS: readonly DownloadAsset[] = [
  // The first asset of a platform is what its main button downloads.
  { platform: "windows", arch: "x64", label: "installer", file: "DropFlow-win-Setup.exe", available: true },
  { platform: "windows", arch: "arm64", label: "installer", file: "DropFlow-win-arm64-Setup.exe", available: WINDOWS_ARM64_RELEASED },
  { platform: "windows", arch: "x64", label: "portable", file: "DropFlow-win-Portable.zip", available: true },
  { platform: "windows", arch: "x64", label: "cli", file: "dropflow-cli-win-x64.zip", available: true },
  { platform: "macos", arch: "arm64", label: "appleSilicon", file: "DropFlow-mac-arm64.dmg", available: MACOS_RELEASED },
  { platform: "macos", arch: "x64", label: "intel", file: "DropFlow-mac-x64.dmg", available: MACOS_RELEASED },
  { platform: "linux", arch: "x64", label: "deb", file: "DropFlow-linux-amd64.deb", available: LINUX_RELEASED },
  { platform: "linux", arch: "arm64", label: "deb", file: "DropFlow-linux-arm64.deb", available: LINUX_RELEASED },
  { platform: "linux", arch: "x64", label: "rpm", file: "DropFlow-linux-x86_64.rpm", available: LINUX_RELEASED },
  { platform: "linux", arch: "arm64", label: "rpm", file: "DropFlow-linux-aarch64.rpm", available: LINUX_RELEASED },
  { platform: "linux", arch: "x64", label: "appImage", file: "DropFlow-linux-x86_64.AppImage", available: LINUX_RELEASED },
  { platform: "linux", arch: "arm64", label: "appImage", file: "DropFlow-linux-aarch64.AppImage", available: LINUX_RELEASED },
  { platform: "android", arch: "arm64", label: "apk", file: "DropFlow-android.apk", available: ANDROID_RELEASED },
];

/** The SHA-256 list attached to every release. */
export const CHECKSUMS_FILE = "SHA256SUMS.txt";

export const downloadUrl = (file: string) => `${RELEASE_DOWNLOAD_URL}/${encodeURIComponent(file)}`;

export const assetsOf = (platform: Platform) => DOWNLOADS.filter((a) => a.platform === platform);

export const isReleased = (platform: Platform) => assetsOf(platform).some((a) => a.available);

/** Short technical detail shown next to a label: "x64 · .exe", "ARM64 · AppImage". Not translated (names of formats). */
export function assetDetail(a: DownloadAsset): string {
  const ext = /\.AppImage$/.test(a.file) ? "AppImage" : (a.file.match(/\.[a-z0-9]+$/i)?.[0] ?? "");
  if (a.platform === "android") return ext;
  return `${a.arch === "x64" ? "x64" : "ARM64"} · ${ext}`;
}

/**
 * The visitor's system from the browser's own hints (navigator.userAgentData.platform, else the user agent string).
 * iPhone/iPad and ChromeOS have no DropFlow build: they get the web app. null = unknown.
 */
export function detectTarget(userAgent: string, uaDataPlatform?: string | null): Target | null {
  const p = (uaDataPlatform ?? "").trim().toLowerCase();
  if (p === "android") return "android";
  if (p === "windows") return "windows";
  if (p === "macos") return "macos";
  if (p === "chrome os" || p === "chromeos" || p === "ios") return "web";
  if (p === "linux") return "linux";
  const ua = userAgent;
  if (/android/i.test(ua)) return "android";
  if (/iphone|ipad|ipod/i.test(ua)) return "web";
  if (/windows/i.test(ua)) return "windows";
  if (/macintosh|mac os x/i.test(ua)) return "macos";
  if (/\bcros\b/i.test(ua)) return "web";
  if (/linux|x11/i.test(ua)) return "linux";
  return null;
}

/** Which button to highlight: the visitor's platform when it has files, else the web app. Before detection: Windows. */
export function highlightFor(target: Target | null): Target {
  if (target === null) return "windows";
  if (target === "web") return "web";
  return isReleased(target) ? target : "web";
}

/** Platforms the JSON-LD lists as supported (the released ones, plus the web app). */
export function operatingSystems(): string {
  const os = ["Windows 10", "Windows 11"];
  if (MACOS_RELEASED) os.push("macOS");
  if (LINUX_RELEASED) os.push("Linux");
  if (ANDROID_RELEASED) os.push("Android");
  return [...os, "Web"].join(", ");
}
