/* DropFlow Web service worker. Hand-written; scripts/build-sw.mjs fills the __PLACEHOLDERS__ and writes public/sw.js.
 *
 * What it does
 *  - install: precaches the /app shell (both locales), every /_next/static asset of the build and the small vendor
 *    files (pdf.js worker + fonts + decoders, ffmpeg glue + core loader), so /app works OFFLINE after the first visit.
 *    Extras (CJK CMaps) are fetched afterwards, on request of the page (CACHE_OPTIONAL), and can never fail the install.
 *  - the 31 MB ffmpeg-core.wasm is NOT precached (audio/video load lazily); it is cached the first time it is used,
 *    or when the user presses "Save media offline" (message CACHE_MEDIA).
 *  - versioned caches + update flow: a new build installs next to the old one and WAITS; the page offers "Update"
 *    and sends SKIP_WAITING; old caches are deleted on activate. The first-ever install activates immediately.
 *  - it only ever answers same-origin GET requests for those assets. It never sees, stores or sends user files:
 *    files are handled in pages and workers as Blobs and never go through fetch.
 */
const VERSION = "__VERSION__";
const VENDOR_VERSION = "__VENDOR_VERSION__";
const SHELL_CACHE = "dropflow-shell-" + VERSION;
const VENDOR_CACHE = "dropflow-vendor-" + VENDOR_VERSION;

const SHELL_URLS = __SHELL_URLS__;
const VENDOR_URLS = __VENDOR_URLS__;
const OPTIONAL_URLS = __OPTIONAL_URLS__;
const MEDIA_CORE = "/vendor/ffmpeg/ffmpeg-core.wasm";
const APP_PAGE = /^\/(ar|en)\/app\/?$/;

self.addEventListener("install", (event) => {
  event.waitUntil(
    (async () => {
      const shell = await caches.open(SHELL_CACHE);
      // `reload` bypasses the HTTP cache so a new version can never precache stale files.
      await shell.addAll(SHELL_URLS.map((u) => new Request(u, { cache: "reload" })));

      const vendor = await caches.open(VENDOR_CACHE);
      const missing = [];
      for (const u of VENDOR_URLS) if (!(await vendor.match(u))) missing.push(u);
      await vendor.addAll(missing.map((u) => new Request(u, { cache: "reload" })));
      // First install: nothing to be careful about, take over now. Later installs wait for the user ("Update").
      if (!self.registration.active) await self.skipWaiting();
    })(),
  );
});

self.addEventListener("activate", (event) => {
  event.waitUntil(
    (async () => {
      const keep = new Set([SHELL_CACHE, VENDOR_CACHE]);
      for (const name of await caches.keys()) {
        if (name.startsWith("dropflow-") && !keep.has(name)) await caches.delete(name);
      }
      await self.clients.claim();
    })(),
  );
});

self.addEventListener("message", (event) => {
  const type = event.data && event.data.type;
  if (type === "SKIP_WAITING") {
    self.skipWaiting();
  } else if (type === "CACHE_MEDIA") {
    event.waitUntil(cacheMedia());
  } else if (type === "CACHE_OPTIONAL") {
    event.waitUntil(cacheOptional());
  }
});

async function broadcast(message) {
  for (const client of await self.clients.matchAll({ includeUncontrolled: true })) client.postMessage(message);
}

// Best-effort extras (CJK CMaps...), fetched in the background AFTER install so they can never block or fail it.
async function cacheOptional() {
  const vendor = await caches.open(VENDOR_CACHE);
  const todo = [];
  for (const u of OPTIONAL_URLS) if (!(await vendor.match(u))) todo.push(u);
  let next = 0;
  async function lane() {
    while (next < todo.length) {
      const u = todo[next++];
      const abort = new AbortController();
      const timer = setTimeout(() => abort.abort(), 15000);
      try {
        const response = await fetch(u, { cache: "reload", signal: abort.signal, redirect: "error" });
        if (response.ok) await vendor.put(u, response);
      } catch {
        /* optional: skip */
      } finally {
        clearTimeout(timer);
      }
    }
  }
  await Promise.all([lane(), lane(), lane(), lane()]);
}

async function cacheMedia() {
  try {
    const vendor = await caches.open(VENDOR_CACHE);
    await vendor.add(new Request(MEDIA_CORE, { cache: "reload" }));
    await broadcast({ type: "MEDIA_CACHED" });
  } catch {
    await broadcast({ type: "MEDIA_FAILED" });
  }
}

self.addEventListener("fetch", (event) => {
  const request = event.request;
  if (request.method !== "GET") return;
  const url = new URL(request.url);
  if (url.origin !== self.location.origin) return; // never touch anything third-party
  const path = url.pathname;

  if (request.mode === "navigate") {
    if (APP_PAGE.test(path)) event.respondWith(appPage(request, path));
    return; // landing page and everything else: plain network, untouched
  }
  if (path.startsWith("/_next/static/")) {
    event.respondWith(cacheFirst(request, SHELL_CACHE));
  } else if (path.startsWith("/vendor/") || path.startsWith("/icons/") || path.startsWith("/manifest") || path === "/icon.svg") {
    event.respondWith(cacheFirst(request, VENDOR_CACHE));
  }
});

async function appPage(request, path) {
  const cache = await caches.open(SHELL_CACHE);
  const key = path.replace(/\/$/, "");
  const hit = await cache.match(key, { ignoreSearch: true });
  if (hit) return sameUrl(hit, request);
  try {
    return await fetch(request);
  } catch (error) {
    // Offline and this locale was never cached: fall back to whichever app page we have.
    const any = (await cache.match("/ar/app")) || (await cache.match("/en/app"));
    if (any) return any;
    throw error;
  }
}

// A Web Worker takes its location (and Turbopack its `?params=` bootstrap config) from the RESPONSE url. A response
// served from the Cache API carries the cached URL (no query), so hand back a copy without a url of its own: the
// worker then keeps the request URL, query and all.
function sameUrl(hit, request) {
  if (!hit || hit.url === request.url) return hit;
  return new Response(hit.body, { status: hit.status, statusText: hit.statusText, headers: hit.headers });
}

async function cacheFirst(request, cacheName) {
  const cache = await caches.open(cacheName);
  const hit = await cache.match(request, { ignoreSearch: true });
  if (hit) return sameUrl(hit, request);
  // Vendor files may have been cached by an older worker version under the other cache name.
  const any = await caches.match(request, { ignoreSearch: true });
  if (any) return sameUrl(any, request);
  const response = await fetch(request);
  if (response.ok && response.status === 200) cache.put(request, response.clone()).catch(() => {});
  return response;
}
