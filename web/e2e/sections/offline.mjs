import { readFileSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { fileURLToPath } from "node:url";
import { sleep } from "../cdp.mjs";
import {
  addFiles, ascii, check, clickWedge, downloadResult, freshApp, isJpeg, isPng, section, selectTab, setDownloadDir, waitResults,
} from "../helpers.mjs";

/** Always restores the network, even when an assertion inside throws. */
async function whileOffline(b, fn) {
  await b.send("Network.setCacheDisabled", { cacheDisabled: true });
  await setOffline(b, true);
  try {
    await fn();
  } finally {
    await setOffline(b, false);
    await b.send("Network.setCacheDisabled", { cacheDisabled: false });
  }
}

const setOffline = (b, offline) =>
  b.send("Network.emulateNetworkConditions", { offline, latency: 0, downloadThroughput: -1, uploadThroughput: -1 });

export async function offline({ b, base, out, files }) {
  const dl = join(out, "dl-offline");
  await setDownloadDir(b, dl);

  await section("PWA: manifest, service worker registered on /app only, versioned precache", async () => {
    await freshApp(b, base, "en");
    const mf = await b.ev(`fetch(document.querySelector('link[rel=manifest]').href).then(r => r.json())`);
    check("manifest: name, standalone, theme colours from the tokens, 192/512/maskable icons", mf.name === "DropFlow" && mf.display === "standalone" && mf.theme_color === "#070b1c" && mf.icons.some((i) => i.sizes === "192x192") && mf.icons.some((i) => i.sizes === "512x512") && mf.icons.some((i) => i.purpose === "maskable"), JSON.stringify(mf).slice(0, 120));
    const ar = await b.ev(`fetch('/manifest.webmanifest').then(r => r.json())`);
    check("Arabic manifest is RTL with an /ar/app start_url", ar.dir === "rtl" && ar.lang === "ar" && ar.start_url.startsWith("/ar/app"));
    for (const icon of mf.icons.filter((i) => i.type === "image/png")) {
      const ok = await b.ev(`fetch(${JSON.stringify(icon.src)}).then(async r => { const b = new Uint8Array(await r.arrayBuffer()); return r.ok && b[0] === 137 && b[1] === 80; })`);
      check(`icon ${icon.src} is a real PNG`, ok);
    }
    await b.waitFor("navigator.serviceWorker.getRegistration().then(r => !!r?.active)", 30000, "service worker active");
    await b.waitFor("!!document.querySelector('[data-testid=offline-ready]')", 30000, "Works offline chip");
    const state = await b.ev(`(async () => {
      const keys = await caches.keys();
      const shell = keys.find((k) => k.startsWith('dropflow-shell-'));
      const vendor = keys.find((k) => k.startsWith('dropflow-vendor-'));
      const sc = shell ? await (await caches.open(shell)).keys() : [];
      const vc = vendor ? await (await caches.open(vendor)).keys() : [];
      const reg = await navigator.serviceWorker.getRegistration();
      return { keys, shellCount: sc.length, vendorCount: vc.length, shellHas: (p) => 0, scope: reg.scope,
        pages: sc.map((r) => new URL(r.url).pathname).filter((p) => /\\/app$/.test(p)),
        hasPdfWorker: vc.some((r) => r.url.endsWith('/vendor/pdfjs/pdf.worker.min.mjs')),
        hasFfmpegGlue: vc.some((r) => r.url.endsWith('/vendor/ffmpeg/worker.js')),
        hasCoreWasm: vc.some((r) => r.url.endsWith('ffmpeg-core.wasm')),
        chunks: sc.filter((r) => r.url.includes('/_next/static/chunks/')).length };
    })()`);
    check("versioned caches exist (dropflow-shell-<hash>, dropflow-vendor-<hash>)", state.keys.some((k) => /^dropflow-shell-[0-9a-f]{16}$/.test(k)) && state.keys.some((k) => /^dropflow-vendor-[0-9a-f]{16}$/.test(k)), state.keys.join(","));
    check("precached: both /app pages, all JS chunks, pdf.js worker, ffmpeg glue", state.pages.length === 2 && state.chunks >= 20 && state.hasPdfWorker && state.hasFfmpegGlue, JSON.stringify({ pages: state.pages, chunks: state.chunks, vendor: state.vendorCount }));
    // (it may be in the vendor cache if media was already used in this profile: runtime-cached on first use. What
    // matters is that the service worker never PRECACHES it, i.e. it is in none of the install lists.)
    const swSource = await b.ev("fetch('/sw.js').then(r => r.text())");
    const lists = ["SHELL_URLS", "VENDOR_URLS", "OPTIONAL_URLS"].map((n) => swSource.slice(swSource.indexOf("const " + n), swSource.indexOf("];", swSource.indexOf("const " + n))));
    check("the 31 MB ffmpeg core is NOT precached: in none of the install lists (lazy / opt-in)", lists.every((l) => l.length > 20 && !l.includes("ffmpeg-core.wasm")));
  });

  await section("PWA: OFFLINE reload + convert an image and render a PDF without any network", async () => {
    await freshApp(b, base, "en");
    await b.waitFor("navigator.serviceWorker.controller !== null", 20000, "page controlled by SW");
    await whileOffline(b, async () => {
    await b.send("Page.reload");
    await sleep(2500);
    await b.waitFor("!!document.querySelector('[data-testid=dropzone]')", 20000, "app shell offline");
    check("offline reload: the app shell renders from the service worker", true);
    check("offline badge is shown", await b.ev("!!document.querySelector('[data-testid=offline-badge]')"));
    check("navigator.onLine is false", (await b.ev("navigator.onLine")) === false);

    await addFiles(b, [files.png]);
    await clickWedge(b, "convert:jpg");
    await waitResults(b, 1, 30000);
    const jpg = await downloadResult(b, dl, 0);
    check("OFFLINE: PNG -> JPG converted in the worker, valid JPEG", isJpeg(jpg.bytes), `${jpg.size} bytes`);

    await freshApp(b, base, "en");
    await addFiles(b, [files.pdf3]);
    await clickWedge(b, "convert:png");
    await waitResults(b, 3, 60000);
    const png = await downloadResult(b, dl, 0);
    check("OFFLINE: PDF -> PNG pages rendered with pdf.js from cached vendor files", isPng(png.bytes), `${png.size} bytes`);

    await freshApp(b, base, "en");
    await addFiles(b, [files.pdf3, files.pdf2]);
    await selectTab(b, "tools");
    await clickWedge(b, "pdf.merge");
    await waitResults(b, 1);
    const merged = await downloadResult(b, dl, 0);
    check("OFFLINE: PDF merge (pdf-lib in the worker) works", ascii(merged.bytes, 0, 5) === "%PDF-");

    });
    await sleep(500);
  });

  await section("PWA: update flow cleans old caches; opt-in `Save media offline` makes audio/video work offline", async () => {
    await freshApp(b, base, "en");
    await b.ev(`(async () => { await caches.open('dropflow-shell-0000000000000000'); await caches.open('dropflow-vendor-ffffffffffffffff'); const r = await navigator.serviceWorker.getRegistration(); await r.unregister(); })()`);
    await b.goto(`${base}/en/app`, 2500);
    await b.waitFor("navigator.serviceWorker.getRegistration().then(r => !!r?.active)", 30000, "re-registered");
    await sleep(1500);
    const keys = await b.ev("caches.keys()");
    check("on activate the worker deletes caches of other versions", !keys.includes("dropflow-shell-0000000000000000") && !keys.includes("dropflow-vendor-ffffffffffffffff") && keys.some((k) => k.startsWith("dropflow-shell-")), keys.join(","));

    // make the state deterministic: forget the core if media was used earlier in this profile
    await b.ev(`(async () => { for (const k of await caches.keys()) if (k.startsWith('dropflow-vendor-')) await (await caches.open(k)).delete('/vendor/ffmpeg/ffmpeg-core.wasm'); })()`);
    await b.goto(`${base}/en/app`, 2000);
    await b.waitFor("!!document.querySelector('[data-testid=save-media]')", 20000, "Save media offline button");
    await b.click("[data-testid=save-media]");
    await b.waitFor("!!document.querySelector('[data-testid=media-saved]')", 120000, "media saved");
    const cached = await b.ev("caches.match('/vendor/ffmpeg/ffmpeg-core.wasm').then(r => !!r)");
    check("`Save media offline` cached the ffmpeg core on demand", cached);

    await whileOffline(b, async () => {
    await b.ev("localStorage.clear()");
    await b.send("Page.reload");
    await sleep(2500);
    await b.waitFor("!!document.querySelector('[data-testid=dropzone]')", 20000, "offline shell");
    await addFiles(b, [files.wav]);
    await b.waitFor("document.querySelector('[data-wheel=convert]') && !document.querySelector('[data-wheel=convert]').disabled", 120000, "offline media wheel");
    await clickWedge(b, "convert:flac");
    await waitResults(b, 1, 60000);
    const flac = await downloadResult(b, dl, 0);
    check("OFFLINE: WAV -> FLAC through ffmpeg.wasm after saving the core", ascii(flac.bytes, 0, 4) === "fLaC", `${flac.size} bytes`);
    });
  });

  await section("PWA: update flow - a new build installs next to the old one, `Update` activates it, old caches go", async () => {
    // Simulate a new deploy: change the VERSION constant of the generated public/sw.js (restored afterwards).
    const swPath = fileURLToPath(new URL("../../public/sw.js", import.meta.url));
    const original = readFileSync(swPath, "utf8");
    const newVersion = "e2eupdate00000001";
    try {
      await freshApp(b, base, "en");
      await b.waitFor("navigator.serviceWorker.getRegistration().then(r => !!r?.active)", 30000, "sw active");
      await b.waitFor("!!document.querySelector('[data-testid=offline-ready]')", 30000, "ready chip");
      const oldKeys = await b.ev("caches.keys()");
      check("before the update: no Update button, one versioned shell cache", !(await b.ev("!!document.querySelector('[data-testid=update-button]')")) && oldKeys.filter((k) => k.startsWith("dropflow-shell-")).length === 1, oldKeys.join(","));

      writeFileSync(swPath, original.replace(/const VERSION = "[0-9a-f]+";/, `const VERSION = "${newVersion}";`));
      await b.ev("navigator.serviceWorker.getRegistration().then(r => r.update())");
      await b.waitFor("!!document.querySelector('[data-testid=update-button]')", 30000, "Update button");
      check("a new service-worker version waits (installed) and the page offers `Update`", true);
      const during = await b.ev("caches.keys()");
      check("the new version precached into its OWN cache next to the old one (old one still serves the page)", during.includes(`dropflow-shell-${newVersion}`) && during.some((k) => k.startsWith("dropflow-shell-") && !k.includes(newVersion)), during.join(","));

      await b.click("[data-testid=update-button]");
      await b.waitFor(`caches.keys().then(k => k.includes('dropflow-shell-${newVersion}') && k.filter(x => x.startsWith('dropflow-shell-')).length === 1)`, 30000, "old cache removed after activation");
      await sleep(1500);
      await b.waitFor("!!document.querySelector('[data-testid=dropzone]')", 20000, "app after update reload");
      check("after `Update`: the page reloaded under the new worker and the old cache was deleted", (await b.ev("navigator.serviceWorker.controller !== null")) && !(await b.ev("!!document.querySelector('[data-testid=update-button]')")));
      // and the updated app still works
      await addFiles(b, [files.png]);
      await clickWedge(b, "convert:jpg");
      await waitResults(b, 1);
      check("the updated app still converts", true);
    } finally {
      writeFileSync(swPath, original);
    }
  });
}
