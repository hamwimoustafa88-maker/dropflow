// Tiny streaming MD5 (RFC 1321). WebCrypto has no MD5, so this is the one hash implemented by hand.
// MD5 is NOT secure against attackers; it is offered only because many download pages still publish MD5 sums.

const S = [
  7, 12, 17, 22, 7, 12, 17, 22, 7, 12, 17, 22, 7, 12, 17, 22, 5, 9, 14, 20, 5, 9, 14, 20, 5, 9, 14, 20, 5, 9, 14, 20, 4, 11, 16, 23, 4, 11, 16,
  23, 4, 11, 16, 23, 4, 11, 16, 23, 6, 10, 15, 21, 6, 10, 15, 21, 6, 10, 15, 21, 6, 10, 15, 21,
];

const K = Int32Array.from({ length: 64 }, (_, i) => Math.floor(Math.abs(Math.sin(i + 1)) * 4294967296) | 0);

export class Md5 {
  private a = 0x67452301 | 0;
  private b = 0xefcdab89 | 0;
  private c = 0x98badcfe | 0;
  private d = 0x10325476 | 0;
  private readonly buf = new Uint8Array(64);
  private bufLen = 0;
  private total = 0;
  private hex: string | null = null;
  private readonly m = new Int32Array(16);

  update(data: Uint8Array): this {
    if (this.hex !== null) throw new Error("Md5: update after digest");
    this.total += data.length;
    let off = 0;
    if (this.bufLen > 0) {
      const take = Math.min(64 - this.bufLen, data.length);
      this.buf.set(data.subarray(0, take), this.bufLen);
      this.bufLen += take;
      off = take;
      if (this.bufLen === 64) {
        this.block(this.buf, 0);
        this.bufLen = 0;
      }
    }
    while (off + 64 <= data.length) {
      this.block(data, off);
      off += 64;
    }
    if (off < data.length) {
      this.buf.set(data.subarray(off), 0);
      this.bufLen = data.length - off;
    }
    return this;
  }

  /** Lower-case hex digest. Finalises the hash; further update() calls throw. */
  digestHex(): string {
    if (this.hex !== null) return this.hex;
    const bits = this.total;
    const padLen = (this.bufLen < 56 ? 56 : 120) - this.bufLen;
    const tail = new Uint8Array(padLen + 8);
    tail[0] = 0x80;
    const lo = (bits << 3) >>> 0; // low 32 bits of bits*8 (the shift wraps like we want)
    const hi = Math.floor(bits / 0x20000000) >>> 0; // bits*8 / 2^32
    for (let i = 0; i < 4; i++) {
      tail[padLen + i] = (lo >>> (8 * i)) & 0xff;
      tail[padLen + 4 + i] = (hi >>> (8 * i)) & 0xff;
    }
    const savedTotal = this.total;
    this.update(tail);
    this.total = savedTotal;
    let out = "";
    for (const word of [this.a, this.b, this.c, this.d]) {
      for (let i = 0; i < 4; i++) out += ((word >>> (8 * i)) & 0xff).toString(16).padStart(2, "0");
    }
    this.hex = out;
    return out;
  }

  private block(d: Uint8Array, o: number): void {
    const m = this.m;
    for (let i = 0; i < 16; i++) {
      const j = o + i * 4;
      m[i] = d[j] | (d[j + 1] << 8) | (d[j + 2] << 16) | (d[j + 3] << 24);
    }
    let a = this.a;
    let b = this.b;
    let c = this.c;
    let d0 = this.d;
    for (let i = 0; i < 64; i++) {
      let f: number;
      let g: number;
      if (i < 16) {
        f = (b & c) | (~b & d0);
        g = i;
      } else if (i < 32) {
        f = (d0 & b) | (~d0 & c);
        g = (5 * i + 1) & 15;
      } else if (i < 48) {
        f = b ^ c ^ d0;
        g = (3 * i + 5) & 15;
      } else {
        f = c ^ (b | ~d0);
        g = (7 * i) & 15;
      }
      const t = (a + f + K[i] + m[g]) | 0;
      a = d0;
      d0 = c;
      c = b;
      b = (b + ((t << S[i]) | (t >>> (32 - S[i])))) | 0;
    }
    this.a = (this.a + a) | 0;
    this.b = (this.b + b) | 0;
    this.c = (this.c + c) | 0;
    this.d = (this.d + d0) | 0;
  }
}

/** MD5 of a whole buffer as lower-case hex. */
export function md5Hex(data: Uint8Array): string {
  return new Md5().update(data).digestHex();
}
