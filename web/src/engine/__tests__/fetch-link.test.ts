import { afterEach, describe, expect, it, vi } from "vitest";
import { LinkError, extractUrl, fetchLink, linkFileName, parseHttpUrl } from "../lib/fetch-link";

afterEach(() => {
  vi.unstubAllGlobals();
});

describe("drop a link: address parsing", () => {
  it("accepts exactly one http(s) address from a drop or paste", () => {
    expect(extractUrl("https://example.com/a.pdf")).toBe("https://example.com/a.pdf");
    expect(extractUrl("# comment\r\nhttp://example.com/x.png\r\n")).toBe("http://example.com/x.png");
    expect(extractUrl("  https://example.com/a b.png ")).toBeNull(); // a space: not one address
    expect(extractUrl("https://a.com/1\nhttps://b.com/2")).toBeNull();
    expect(extractUrl("hello")).toBeNull();
    expect(extractUrl("")).toBeNull();
    expect(extractUrl(null)).toBeNull();
  });

  it("rejects every non-http(s) scheme", () => {
    for (const bad of ["javascript:alert(1)", "data:text/plain,hi", "file:///C:/x.pdf", "ftp://h/x", "blob:https://x/y", "//example.com/x"]) {
      expect(parseHttpUrl(bad), bad).toBeNull();
    }
    expect(parseHttpUrl("HTTPS://Example.com/A.PDF")).toBe("https://example.com/A.PDF");
  });
});

describe("drop a link: file name", () => {
  it("prefers Content-Disposition (filename* first), then the URL path, then 'download' + type", () => {
    expect(linkFileName("https://x.com/get?id=1", "attachment; filename*=UTF-8''%D8%AA%D9%82%D8%B1%D9%8A%D8%B1.pdf", null)).toBe("تقرير.pdf");
    expect(linkFileName("https://x.com/get", 'attachment; filename="report 2026.pdf"', null)).toBe("report 2026.pdf");
    expect(linkFileName("https://x.com/files/photo%20one.jpg?x=1", null, "image/jpeg")).toBe("photo one.jpg");
    expect(linkFileName("https://x.com/", null, "image/png")).toBe("download.png");
    expect(linkFileName("https://x.com/data", null, "application/pdf; charset=binary")).toBe("data.pdf");
    expect(linkFileName("https://x.com/", null, null)).toBe("download");
  });

  it("never returns a path or a hidden/unsafe name", () => {
    expect(linkFileName("https://x.com/a", 'attachment; filename="../../etc/passwd"', null)).not.toMatch(/[\\/]/);
    expect(linkFileName("https://x.com/a", 'attachment; filename="..hidden.txt"', null)).toBe("hidden.txt");
    expect(linkFileName("https://x.com/a", 'attachment; filename="a<b>:c|d?.txt"', null)).toBe("a_b__c_d_.txt");
  });
});

function stream(parts: Uint8Array[]): ReadableStream<Uint8Array> {
  return new ReadableStream({
    start(c) {
      for (const p of parts) c.enqueue(p);
      c.close();
    },
  });
}

describe("drop a link: download", () => {
  it("is a GET with no credentials, no referrer and no body; returns a named File with progress", async () => {
    const calls: [string, RequestInit][] = [];
    vi.stubGlobal("fetch", async (url: string, init: RequestInit) => {
      calls.push([url, init]);
      return new Response(stream([new Uint8Array([1, 2]), new Uint8Array([3])]), { status: 200, headers: { "content-type": "image/png", "content-length": "3" } });
    });
    const progress: [number, number][] = [];
    const f = await fetchLink("https://example.com/pics/cat", { onProgress: (r, t) => progress.push([r, t]) });
    expect(f.name).toBe("cat.png");
    expect(f.type).toBe("image/png");
    expect(new Uint8Array(await f.arrayBuffer())).toEqual(new Uint8Array([1, 2, 3]));
    expect(progress).toEqual([[2, 3], [3, 3]]);
    expect(calls).toHaveLength(1);
    const [, init] = calls[0];
    expect(init.method).toBe("GET");
    expect(init.credentials).toBe("omit");
    expect(init.referrerPolicy).toBe("no-referrer");
    expect(init.body).toBeUndefined();
    expect(init.headers).toBeUndefined();
  });

  it("maps a refused cross-origin request (TypeError) to 'blocked', an error status to 'http'", async () => {
    vi.stubGlobal("fetch", async () => {
      throw new TypeError("Failed to fetch");
    });
    await expect(fetchLink("https://no-cors.example/x.pdf")).rejects.toMatchObject({ code: "blocked" });
    vi.stubGlobal("fetch", async () => new Response("nope", { status: 404 }));
    await expect(fetchLink("https://example.com/x.pdf")).rejects.toMatchObject({ code: "http", status: 404 });
  });

  it("refuses invalid addresses without any request", async () => {
    const spy = vi.fn();
    vi.stubGlobal("fetch", spy);
    await expect(fetchLink("javascript:alert(1)")).rejects.toBeInstanceOf(LinkError);
    expect(spy).not.toHaveBeenCalled();
  });

  it("stops at the size cap (declared or streamed) and on an empty body", async () => {
    vi.stubGlobal("fetch", async () => new Response(stream([new Uint8Array(10)]), { headers: { "content-length": "999999" } }));
    await expect(fetchLink("https://example.com/big.bin", { maxBytes: 100 })).rejects.toMatchObject({ code: "tooBig" });
    vi.stubGlobal("fetch", async () => new Response(stream([new Uint8Array(60), new Uint8Array(60)])));
    await expect(fetchLink("https://example.com/big.bin", { maxBytes: 100 })).rejects.toMatchObject({ code: "tooBig" });
    vi.stubGlobal("fetch", async () => new Response(stream([])));
    await expect(fetchLink("https://example.com/empty.txt")).rejects.toMatchObject({ code: "empty" });
  });

  it("cancel aborts the download", async () => {
    const ctl = new AbortController();
    vi.stubGlobal("fetch", async (_u: string, init: RequestInit) => {
      ctl.abort();
      if (init.signal?.aborted) throw new DOMException("aborted", "AbortError");
      return new Response("x");
    });
    await expect(fetchLink("https://example.com/x.txt", { signal: ctl.signal })).rejects.toMatchObject({ code: "aborted" });
  });
});
