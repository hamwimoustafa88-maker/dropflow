// SPDX-License-Identifier: GPL-3.0-or-later
// public/.well-known/security.txt is signed with an OpenPGP cleartext signature (RFC 9116 §2.3) so a researcher can check that
// the contact details really come from DropFlow. Helpers shared by scripts/sign-security-txt.mjs (signs) and
// src/lib/__tests__/security-txt.test.ts (verifies with the public key in public/.well-known/pgp-key.txt).
// Only the PUBLIC key is in the repository. The private key lives outside it (see sign-security-txt.mjs).
import { spawnSync } from "node:child_process";
import { existsSync, mkdtempSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";

/** Fingerprint of DropFlow's security.txt key (Ed25519, created 2026-11-12). Changing it must be a deliberate, reviewed change. */
export const SECURITY_TXT_KEY = "3AF62355A1B5E75A5BBA7E8A644C3E9063738274";
export const PGP_KEY_URL = "https://dropflow.scouthub.dev/.well-known/pgp-key.txt";

/** "3AF6 2355 A1B5 ..." as people usually print a fingerprint. */
export const spacedFingerprint = (fpr) => fpr.match(/.{4}/g).join(" ");

const run = (gpg, args, input) => spawnSync(gpg, args, { input, encoding: "utf8", windowsHide: true });

/** A working gpg: $GPG, gpg on PATH, or the one Git for Windows ships. null when there is none. */
export function findGpg() {
  const candidates = [
    process.env.GPG,
    "gpg",
    ...(process.platform === "win32"
      ? ["C:\\Program Files\\Git\\usr\\bin\\gpg.exe", "C:\\Program Files\\GnuPG\\bin\\gpg.exe", "C:\\Program Files (x86)\\GnuPG\\bin\\gpg.exe"]
      : []),
  ].filter(Boolean);
  for (const c of candidates) {
    if (c !== "gpg" && !existsSync(c)) continue;
    if (run(c, ["--version"]).status === 0) return c;
  }
  return null;
}

const msys = new Map();
/**
 * A path as this gpg reads it. The gpg in Git for Windows is an MSYS program: started from Node it takes C:/x as a RELATIVE
 * path and needs /c/x; Gpg4win's gpg takes C:/x. MSYS builds print a Unix-style "Home: /c/..." in --version.
 */
export function gpgPath(gpg, p) {
  if (!msys.has(gpg)) msys.set(gpg, process.platform === "win32" && /^Home: \//m.test(run(gpg, ["--version"]).stdout ?? ""));
  const fwd = p.replace(/\\/g, "/");
  return msys.get(gpg) ? fwd.replace(/^([A-Za-z]):/, (_, d) => `/${d.toLowerCase()}`) : fwd;
}

/** The text inside a cleartext-signed message (dash-escaping undone), or the input unchanged when it is not signed. */
export function unwrapClearsigned(text) {
  const lines = text.replace(/\r\n/g, "\n").split("\n");
  if (lines[0] !== "-----BEGIN PGP SIGNED MESSAGE-----") return text;
  const start = lines.indexOf("", 1); // armor headers ("Hash: SHA512") end at the first empty line
  const end = lines.indexOf("-----BEGIN PGP SIGNATURE-----");
  if (start < 0 || end < start) throw new Error("security.txt: broken cleartext signature framing");
  return lines.slice(start + 1, end).map((l) => (l.startsWith("- ") ? l.slice(2) : l)).join("\n") + "\n";
}

/**
 * Fields of a security.txt body: { Contact: [...], Expires: [...], ... } (comments and blank lines skipped).
 * @param {string} body
 * @returns {Record<string, string[]>}
 */
export function fields(body) {
  /** @type {Record<string, string[]>} */
  const out = {};
  for (const line of body.split("\n")) {
    const m = /^([A-Za-z-]+):\s*(.+)$/.exec(line);
    if (m) (out[m[1]] ??= []).push(m[2].trim());
  }
  return out;
}

/**
 * Verifies `signed` against `publicKey` alone, in a throw-away keyring (the user's own keyring is never read).
 * Returns { signer, keyExpires } (primary-key fingerprint, Date or null) or throws with gpg's output.
 */
export function verifySigned(gpg, signed, publicKey) {
  const home = mkdtempSync(join(tmpdir(), "dropflow-pgp-"));
  try {
    const base = ["--homedir", gpgPath(gpg, home), "--batch", "--no-autostart", "--no-tty"];
    const imp = run(gpg, [...base, "--import"], publicKey);
    if (imp.status !== 0) throw new Error(`gpg --import failed: ${imp.stderr}`);
    const v = run(gpg, [...base, "--status-fd", "1", "--verify"], signed);
    // VALIDSIG <signing (sub)key fpr> <date> <timestamp> <sig expires> <version> <reserved> <pk algo> <hash algo> <class> <primary fpr>
    const valid = /^\[GNUPG:\] VALIDSIG (\S+)(?: \S+){8} (\S+)$/m.exec(v.stdout);
    if (v.status !== 0 || !valid || /^\[GNUPG:\] (BADSIG|EXPKEYSIG|REVKEYSIG|EXPSIG)/m.test(v.stdout)) {
      throw new Error(`security.txt signature does not verify:\n${v.stdout}\n${v.stderr}`);
    }
    const keys = run(gpg, [...base, "--with-colons", "--list-keys", valid[2]]);
    const pub = keys.stdout.split("\n").find((l) => l.startsWith("pub:"))?.split(":");
    return { signer: valid[2], keyExpires: pub?.[6] ? new Date(Number(pub[6]) * 1000) : null };
  } finally {
    rmSync(home, { recursive: true, force: true, maxRetries: 5, retryDelay: 100 }); // --no-autostart: no agent holds files in it
  }
}
