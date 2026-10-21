// Test helpers (not a test file): a deliberately low-level ZIP and TAR writer so tests can build archives that
// well-behaved libraries would refuse to create (evil names, wrong flags, lying sizes, ZIP64 markers...).
import { deflateSync } from "fflate";
import { crc32 } from "../lib/zip-safe";

export const enc = (s: string) => new TextEncoder().encode(s);

export function concat(parts: readonly Uint8Array[]): Uint8Array {
  const out = new Uint8Array(parts.reduce((n, p) => n + p.length, 0));
  let o = 0;
  for (const p of parts) {
    out.set(p, o);
    o += p.length;
  }
  return out;
}

const le16 = (n: number) => Uint8Array.from([n & 255, (n >> 8) & 255]);
const le32 = (n: number) => Uint8Array.from([n & 255, (n >>> 8) & 255, (n >>> 16) & 255, (n >>> 24) & 255]);
const le64 = (n: number) => concat([le32(n % 4294967296), le32(Math.floor(n / 4294967296))]);

export interface RawZipEntry {
  /** Entry name: string (encoded as UTF-8) or raw bytes. */
  name: string | Uint8Array;
  data?: Uint8Array;
  method?: 0 | 8;
  /** General purpose flags; bit 11 (0x800) is added automatically for string names unless `flags` is given. */
  flags?: number;
  extraLocal?: Uint8Array;
  extraCentral?: Uint8Array;
  externalAttrs?: number;
  madeBy?: number;
  /** Lie about the uncompressed size in the central directory. */
  claimedSize?: number;
  claimedCompressed?: number;
  crc?: number;
  dosTime?: number;
  dosDate?: number;
  /** Write size/offset as ZIP64 extra-field values (central directory carries 0xFFFFFFFF markers). */
  zip64?: boolean;
}

export function buildZip(entries: readonly RawZipEntry[], opts: { zip64Eocd?: boolean } = {}): Uint8Array {
  const locals: Uint8Array[] = [];
  const centrals: Uint8Array[] = [];
  let offset = 0;
  for (const e of entries) {
    const nameBytes = typeof e.name === "string" ? enc(e.name) : e.name;
    const data = e.data ?? new Uint8Array(0);
    const method = e.method ?? 0;
    const body = method === 8 ? deflateSync(data) : data;
    const flags = e.flags ?? (typeof e.name === "string" ? 0x800 : 0);
    const crc = e.crc ?? crc32(data);
    const time = e.dosTime ?? (12 << 11) | (30 << 5) | 15; // 12:30:30
    const date = e.dosDate ?? ((2024 - 1980) << 9) | (5 << 5) | 17; // 2024-05-17
    const extraLocal = e.extraLocal ?? new Uint8Array(0);
    const local = concat([
      le32(0x04034b50),
      le16(20),
      le16(flags),
      le16(method),
      le16(time),
      le16(date),
      le32(crc),
      le32(body.length),
      le32(data.length),
      le16(nameBytes.length),
      le16(extraLocal.length),
      nameBytes,
      extraLocal,
      body,
    ]);
    const size = e.claimedSize ?? data.length;
    const csize = e.claimedCompressed ?? body.length;
    let extraCentral = e.extraCentral ?? new Uint8Array(0);
    let size32 = size;
    let csize32 = csize;
    let offset32 = offset;
    if (e.zip64) {
      extraCentral = concat([extraCentral, le16(1), le16(24), le64(size), le64(csize), le64(offset)]);
      size32 = csize32 = offset32 = 0xffffffff;
    }
    centrals.push(
      concat([
        le32(0x02014b50),
        le16(e.madeBy ?? 20),
        le16(20),
        le16(flags),
        le16(method),
        le16(time),
        le16(date),
        le32(crc),
        le32(csize32),
        le32(size32),
        le16(nameBytes.length),
        le16(extraCentral.length),
        le16(0),
        le16(0),
        le16(0),
        le32(e.externalAttrs ?? 0),
        le32(offset32),
        nameBytes,
        extraCentral,
      ]),
    );
    locals.push(local);
    offset += local.length;
  }
  const cd = concat(centrals);
  const eocd = concat([le32(0x06054b50), le16(0), le16(0), le16(entries.length), le16(entries.length), le32(cd.length), le32(offset), le16(0)]);
  if (!opts.zip64Eocd) return concat([...locals, cd, eocd]);
  const z64Offset = offset + cd.length;
  const z64 = concat([le32(0x06064b50), le64(44), le16(45), le16(45), le32(0), le32(0), le64(entries.length), le64(entries.length), le64(cd.length), le64(offset)]);
  const locator = concat([le32(0x07064b50), le32(0), le64(z64Offset), le32(1)]);
  const eocd64 = concat([le32(0x06054b50), le16(0xffff), le16(0xffff), le16(0xffff), le16(0xffff), le32(0xffffffff), le32(0xffffffff), le16(0)]);
  return concat([...locals, cd, z64, locator, eocd64]);
}

// ---------------------------------------------------------------------------------------------------------------------
// TAR

export interface RawTarEntry {
  name: string;
  data?: Uint8Array;
  /** '0' file, '5' dir, '2' symlink, 'L' GNU long name, 'x' pax header... */
  type?: string;
  /** Write the name in a ustar "prefix" field when it is too long for the 100-byte name field. */
  prefix?: string;
  gnu?: boolean;
  mtime?: number;
}

function octal(n: number, width: number): Uint8Array {
  const s = n.toString(8).padStart(width - 1, "0");
  const out = new Uint8Array(width);
  out.set(enc(s), 0);
  return out;
}

export function tarHeader(e: { name: string; size: number; type: string; prefix?: string; gnu?: boolean; mtime?: number; corruptChecksum?: boolean }): Uint8Array {
  const h = new Uint8Array(512);
  const nameBytes = enc(e.name);
  h.set(nameBytes.subarray(0, 100), 0);
  h.set(octal(0o644, 8), 100);
  h.set(octal(0, 8), 108);
  h.set(octal(0, 8), 116);
  h.set(octal(e.size, 12), 124);
  h.set(octal(e.mtime ?? 1_700_000_000, 12), 136);
  h.fill(0x20, 148, 156);
  h[156] = e.type.charCodeAt(0);
  h.set(enc(e.gnu ? "ustar  " : "ustar\u000000"), 257);
  if (e.prefix) h.set(enc(e.prefix).subarray(0, 155), 345);
  let sum = 0;
  for (let i = 0; i < 512; i++) sum += h[i];
  const chk = enc(sum.toString(8).padStart(6, "0"));
  h.set(chk, 148);
  h[154] = 0;
  h[155] = 0x20;
  if (e.corruptChecksum) h[148] = h[148] === 0x37 ? 0x30 : h[148] + 1;
  return h;
}

function padBlock(data: Uint8Array): Uint8Array {
  const padded = new Uint8Array(Math.ceil(data.length / 512) * 512);
  padded.set(data);
  return padded;
}

export function buildTar(entries: readonly RawTarEntry[], opts: { terminator?: boolean } = {}): Uint8Array {
  const parts: Uint8Array[] = [];
  for (const e of entries) {
    const data = e.data ?? new Uint8Array(0);
    const type = e.type ?? "0";
    parts.push(tarHeader({ name: e.name, size: data.length, type, prefix: e.prefix, gnu: e.gnu, mtime: e.mtime }));
    if (data.length) parts.push(padBlock(data));
  }
  if (opts.terminator !== false) parts.push(new Uint8Array(1024));
  return concat(parts);
}

/** A GNU long-name record ('L') followed by the real entry. */
export function buildTarLongName(longName: string, data: Uint8Array): Uint8Array {
  const nameData = concat([enc(longName), Uint8Array.of(0)]);
  return concat([
    tarHeader({ name: "././@LongLink", size: nameData.length, type: "L", gnu: true }),
    padBlock(nameData),
    tarHeader({ name: longName.slice(0, 99), size: data.length, type: "0", gnu: true }),
    padBlock(data),
    new Uint8Array(1024),
  ]);
}

/** A pax extended header ('x') carrying `path` (and optionally `size`) followed by the real entry. */
export function buildTarPax(path: string, data: Uint8Array): Uint8Array {
  const record = (key: string, value: string) => {
    const body = ` ${key}=${value}\n`;
    const bodyBytes = enc(body).length; // pax lengths count bytes, not characters
    let len = bodyBytes + 1;
    while (String(len).length + bodyBytes !== len) len = String(len).length + bodyBytes;
    return enc(`${len}${body}`);
  };
  const pax = concat([record("path", path), record("mtime", "1700000000.5")]);
  return concat([
    tarHeader({ name: "PaxHeaders/x", size: pax.length, type: "x" }),
    padBlock(pax),
    tarHeader({ name: "short", size: data.length, type: "0" }),
    padBlock(data),
    new Uint8Array(1024),
  ]);
}
