// SPDX-License-Identifier: GPL-3.0-or-later
// Signs public/.well-known/security.txt (OpenPGP cleartext signature, RFC 9116 §2.3) with DropFlow's security.txt key and
// writes the key's public half to public/.well-known/pgp-key.txt (the file's Encryption link).
//
//   node scripts/sign-security-txt.mjs           re-sign after editing a field (edit the file as it is, signature and all)
//   node scripts/sign-security-txt.mjs --renew   also move Expires to 364 days from today; the web tests fail 30 days before
//                                                Expires, so this is a yearly job
//
// The private key is NOT in the repository. It has a GnuPG folder of its own, %USERPROFILE%\.dropflow\gnupg (or
// $DROPFLOW_GNUPGHOME), unlocked by %USERPROFILE%\.dropflow\security-txt-pgp-passphrase.txt (or $DROPFLOW_PGP_PASSPHRASE_FILE);
// keep an offline copy of both. The key itself expires 2028-11-11; extend it before then, then run this script again:
//   gpg --homedir ~/.dropflow/gnupg --quick-set-expire 3AF62355A1B5E75A5BBA7E8A644C3E9063738274 2y
//   gpg --homedir ~/.dropflow/gnupg --quick-set-expire 3AF62355A1B5E75A5BBA7E8A644C3E9063738274 2y '*'
// Lost key: make a new one (ed25519, see docs/status.md), change SECURITY_TXT_KEY in scripts/security-txt.mjs and SECURITY.md.
import { spawnSync } from "node:child_process";
import { existsSync, readFileSync, writeFileSync } from "node:fs";
import { homedir } from "node:os";
import { join } from "node:path";
import { fileURLToPath } from "node:url";
import { PGP_KEY_URL, SECURITY_TXT_KEY, fields, findGpg, gpgPath, spacedFingerprint, unwrapClearsigned, verifySigned } from "./security-txt.mjs";

const WELL_KNOWN = fileURLToPath(new URL("../public/.well-known/", import.meta.url));
const TXT = join(WELL_KNOWN, "security.txt");
const KEY = join(WELL_KNOWN, "pgp-key.txt");
const HOME = process.env.DROPFLOW_GNUPGHOME ?? join(homedir(), ".dropflow", "gnupg");
const PASS = process.env.DROPFLOW_PGP_PASSPHRASE_FILE ?? join(homedir(), ".dropflow", "security-txt-pgp-passphrase.txt");

function fail(message) {
  console.error(`sign-security-txt: ${message}`);
  process.exit(1);
}

const gpg = findGpg();
if (!gpg) fail("gpg not found: install Git for Windows or GnuPG, or set GPG to its path");
if (!existsSync(HOME) || !existsSync(PASS)) fail(`the signing key is not on this machine (looked for ${HOME} and ${PASS})`);

let body = unwrapClearsigned(readFileSync(TXT, "utf8")).replace(/\r\n/g, "\n").replace(/\n*$/, "\n");
if (process.argv.includes("--renew")) {
  const d = new Date();
  d.setUTCHours(0, 0, 0, 0);
  d.setUTCDate(d.getUTCDate() + 364);
  if (!/^Expires: .+$/m.test(body)) fail("security.txt has no Expires line");
  body = body.replace(/^Expires: .+$/m, `Expires: ${d.toISOString().replace(".000Z", "Z")}`);
}

const f = fields(body);
if (!f.Contact?.length) fail("security.txt needs at least one Contact");
if (f.Expires?.length !== 1) fail("security.txt needs exactly one Expires");
if (!(new Date(f.Expires[0]) > new Date())) fail(`Expires ${f.Expires[0]} has passed: run with --renew`);
if (f.Encryption?.[0] !== PGP_KEY_URL) fail(`Encryption must point at ${PGP_KEY_URL} (the key this script publishes)`);

const base = ["--homedir", gpgPath(gpg, HOME), "--batch", "--no-tty"];
const opts = { encoding: "utf8", windowsHide: true };
const sign = spawnSync(
  gpg,
  [...base, "--pinentry-mode", "loopback", "--passphrase-file", gpgPath(gpg, PASS), "--local-user", `${SECURITY_TXT_KEY}!`, "--digest-algo", "SHA512", "--clearsign"],
  { ...opts, input: body },
);
if (sign.status !== 0) fail(`gpg --clearsign failed:\n${sign.stderr}`);
const exported = spawnSync(gpg, [...base, "--armor", "--export-options", "export-minimal", "--export", SECURITY_TXT_KEY], opts);
if (exported.status !== 0 || !exported.stdout.includes("-----BEGIN PGP PUBLIC KEY BLOCK-----")) fail(`gpg --export failed:\n${exported.stderr}`);
spawnSync(gpg.replace(/gpg(\.exe)?$/i, "gpgconf$1"), ["--homedir", gpgPath(gpg, HOME), "--kill", "gpg-agent"], opts); // signing started one

const signed = sign.stdout.replace(/\r\n/g, "\n");
const publicKey = exported.stdout.replace(/\r\n/g, "\n");
const { signer, keyExpires } = verifySigned(gpg, signed, publicKey); // checked with the public key alone, as a researcher would
if (signer !== SECURITY_TXT_KEY) fail(`signed by ${signer}, expected ${SECURITY_TXT_KEY}`);

writeFileSync(TXT, signed);
writeFileSync(KEY, publicKey);
console.log(`security.txt signed by ${spacedFingerprint(signer)}; Expires ${f.Expires[0]}; key expires ${keyExpires?.toISOString() ?? "never"}`);
if (keyExpires && keyExpires < new Date(f.Expires[0])) {
  console.warn("warning: the key expires before security.txt does; extend it (see the top of this script) and sign again");
}
