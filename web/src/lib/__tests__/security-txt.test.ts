// SPDX-License-Identifier: GPL-3.0-or-later
// public/.well-known/security.txt must stay signed with DropFlow's key, valid, and not about to expire.
// Fix a failure with `node scripts/sign-security-txt.mjs [--renew]` on the machine that holds the key.
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { describe, expect, it } from "vitest";
import { PGP_KEY_URL, SECURITY_TXT_KEY, fields, findGpg, unwrapClearsigned, verifySigned } from "../../../scripts/security-txt.mjs";

const read = (name: string) => readFileSync(fileURLToPath(new URL(`../../../public/.well-known/${name}`, import.meta.url)), "utf8");
const signed = read("security.txt");
const publicKey = read("pgp-key.txt");
const f = fields(unwrapClearsigned(signed));
const DAY = 86_400_000;
const gpg = findGpg();
if (!gpg) console.warn("security-txt.test: gpg not found, the signature itself is NOT checked here (CI checks it)");

describe("security.txt (RFC 9116)", () => {
  it("is an OpenPGP cleartext-signed message", () => {
    const lines = signed.replace(/\r\n/g, "\n").split("\n");
    expect(lines[0]).toBe("-----BEGIN PGP SIGNED MESSAGE-----");
    expect(lines[1]).toBe("Hash: SHA512");
    expect(lines).toContain("-----BEGIN PGP SIGNATURE-----");
    expect(lines.at(-2)).toBe("-----END PGP SIGNATURE-----");
  });

  it("names the contact, policy, languages, its own address and the key", () => {
    expect(f.Contact).toEqual(["https://github.com/hamwimoustafa88-maker/dropflow/security/advisories/new"]);
    expect(f.Canonical).toEqual(["https://dropflow.scouthub.dev/.well-known/security.txt"]);
    expect(f.Policy).toEqual(["https://github.com/hamwimoustafa88-maker/dropflow/blob/main/SECURITY.md"]);
    expect(f["Preferred-Languages"]).toEqual(["ar, en"]);
    expect(f.Encryption).toEqual([PGP_KEY_URL]);
  });

  it("expires once, more than 30 days and at most a year from now", () => {
    expect(f.Expires).toHaveLength(1);
    const left = (Date.parse(f.Expires[0]) - Date.now()) / DAY;
    expect(left, "renew it: cd web && node scripts/sign-security-txt.mjs --renew").toBeGreaterThan(30);
    expect(left).toBeLessThanOrEqual(366);
  });

  it("publishes only a public key", () => {
    expect(publicKey.startsWith("-----BEGIN PGP PUBLIC KEY BLOCK-----")).toBe(true);
    expect(publicKey).not.toMatch(/PRIVATE KEY/);
  });

  it.skipIf(!gpg)("verifies with that public key alone, made by DropFlow's key, which is not about to expire", () => {
    const { signer, keyExpires } = verifySigned(gpg!, signed, publicKey);
    expect(signer).toBe(SECURITY_TXT_KEY);
    expect(keyExpires === null || keyExpires.getTime() - Date.now() > 60 * DAY, "extend the key: see scripts/sign-security-txt.mjs").toBe(true);
  });

  it.skipIf(!gpg)("stops verifying when a field is changed", () => {
    const forged = signed.replace(/^Contact: .+$/m, "Contact: mailto:attacker@example.com");
    expect(forged).not.toBe(signed);
    expect(() => verifySigned(gpg!, forged, publicKey)).toThrow(/does not verify/);
  });
});
