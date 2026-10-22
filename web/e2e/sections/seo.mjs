import { sleep } from "../cdp.mjs";
import { check, section } from "../helpers.mjs";

const REPO = "https://github.com/hamwimoustafa88-maker/dropflow";
const CONTRIBUTING = `${REPO}/blob/main/CONTRIBUTING.md`;
const GPL = "https://www.gnu.org/licenses/gpl-3.0.html";
const LOCALES = ["ar", "en"];
const PATHS = ["", "/app", "/privacy", "/terms"];
const PAGES = PATHS.flatMap((p) => LOCALES.map((l) => `/${l}${p}`));
const RIGHTS = /all rights reserved|proprietary|جميع الحقوق محفوظة|برنامج مملوك/i;

// Search engines and the open-source launch (ADR-0013): indexable pages, robots.txt + sitemap.xml with hreflang,
// canonical/Open Graph/Twitter tags, JSON-LD, GitHub links, GPL wording, semantic basics. Pure reads, same origin only.
export async function seo({ b, base }) {
  await section("seo: robots.txt allows everything and points at the sitemap", async () => {
    await b.goto(`${base}/en`, 1200);
    const r = await b.ev(`fetch('/robots.txt').then(async (r) => ({ s: r.status, t: await r.text() }))`);
    check("/robots.txt answers 200", r.s === 200, String(r.s));
    check("robots.txt: User-Agent * / Allow / and no Disallow", /User-Agent: \*/i.test(r.t) && /^Allow: \/$/m.test(r.t) && !/Disallow/i.test(r.t), r.t.replace(/\n/g, " | "));
    check("robots.txt names an absolute https sitemap.xml", /^Sitemap: https:\/\/[^\s]+\/sitemap\.xml$/m.test(r.t), r.t.replace(/\n/g, " | "));
  });

  await section("seo: sitemap.xml lists all 8 pages with hreflang ar / en / x-default", async () => {
    const xml = await b.ev(`fetch('/sitemap.xml').then(async (r) => ({ s: r.status, type: r.headers.get('content-type'), t: await r.text() }))`);
    check("/sitemap.xml answers 200 as XML", xml.s === 200 && /xml/.test(xml.type ?? ""), `${xml.s} ${xml.type}`);
    const parsed = await b.ev(`(() => {
      const doc = new DOMParser().parseFromString(${JSON.stringify(xml.t)}, 'application/xml');
      if (doc.querySelector('parsererror')) return { error: true };
      return { urls: [...doc.getElementsByTagName('url')].map((u) => ({
        loc: u.getElementsByTagName('loc')[0]?.textContent,
        alt: [...u.getElementsByTagNameNS('http://www.w3.org/1999/xhtml', 'link')].map((l) => [l.getAttribute('hreflang'), l.getAttribute('href')]),
      })) };
    })()`);
    check("sitemap.xml is well-formed XML", !parsed.error);
    const urls = parsed.urls ?? [];
    const paths = urls.map((u) => new URL(u.loc).pathname).sort();
    check("sitemap lists exactly the 8 pages (ar/en x landing, app, privacy, terms)", JSON.stringify(paths) === JSON.stringify([...PAGES].sort()), paths.join(" "));
    const origins = new Set(urls.map((u) => new URL(u.loc).origin));
    check("every sitemap URL is absolute https on one origin", origins.size === 1 && [...origins][0].startsWith("https://"), [...origins].join(" "));
    const bad = urls.filter((u) => {
      const path = new URL(u.loc).pathname.replace(/^\/(ar|en)/, "");
      const m = Object.fromEntries(u.alt.map(([h, href]) => [h, new URL(href).pathname]));
      return m.ar !== `/ar${path}` || m.en !== `/en${path}` || m["x-default"] !== `/ar${path}`;
    });
    check("every sitemap URL has hreflang ar, en and x-default (-> Arabic)", urls.length === 8 && bad.length === 0, bad.map((u) => u.loc).join(" "));
  });

  await section("seo: every page is indexable with canonical, hreflang, Open Graph, Twitter, one h1, lang/dir", async () => {
    for (const path of PAGES) {
      await b.goto(`${base}${path}`, path.endsWith("/app") ? 1600 : 1000);
      const m = await b.ev(`(() => {
        const meta = (sel) => document.querySelector(sel)?.getAttribute('content') ?? null;
        return {
          robots: [...document.querySelectorAll('meta[name=robots], meta[name=googlebot]')].map((e) => e.content),
          canonical: document.querySelector('link[rel=canonical]')?.href ?? null,
          alt: Object.fromEntries([...document.querySelectorAll('link[rel=alternate][hreflang]')].map((l) => [l.hreflang, new URL(l.href).pathname])),
          ogTitle: meta('meta[property="og:title"]'), ogDesc: meta('meta[property="og:description"]'), ogUrl: meta('meta[property="og:url"]'),
          ogLocale: meta('meta[property="og:locale"]'), ogSite: meta('meta[property="og:site_name"]'), ogImage: meta('meta[property="og:image"]'),
          twCard: meta('meta[name="twitter:card"]'), twImage: meta('meta[name="twitter:image"]'),
          desc: meta('meta[name=description]'), title: document.title,
          h1: document.querySelectorAll('h1').length, lang: document.documentElement.lang, dir: document.documentElement.dir,
          imgNoAlt: [...document.querySelectorAll('img')].filter((i) => !i.hasAttribute('alt')).length,
          github: [...document.querySelectorAll('header a[href=${JSON.stringify(REPO)}]')].map((a) => { const r = a.getBoundingClientRect(); return { label: a.getAttribute('aria-label'), w: r.width, h: r.height }; }),
          vague: [...document.querySelectorAll('a[href]')].filter((a) => /^(click here|here|more|اضغط هنا|هنا)$/i.test((a.getAttribute('aria-label') || a.textContent).trim())).length,
        };
      })()`);
      const loc = path.slice(1, 3);
      const rest = path.slice(3);
      check(`${path}: no noindex/nofollow robots meta`, !m.robots.some((c) => /noindex|nofollow/i.test(c)), m.robots.join(",") || "none");
      check(`${path}: canonical points at itself`, m.canonical && new URL(m.canonical).pathname === path && new URL(m.canonical).protocol === "https:", m.canonical);
      check(`${path}: hreflang ar / en / x-default`, m.alt.ar === `/ar${rest}` && m.alt.en === `/en${rest}` && m.alt["x-default"] === `/ar${rest}`, JSON.stringify(m.alt));
      check(
        `${path}: Open Graph (title, description, url, ${loc === "ar" ? "ar_AR" : "en_US"}, site name, image) + Twitter large card`,
        m.ogTitle && m.ogDesc && m.ogUrl && new URL(m.ogUrl).pathname === path && m.ogLocale === (loc === "ar" ? "ar_AR" : "en_US") && m.ogSite === "DropFlow" && m.ogImage && m.twCard === "summary_large_image" && m.twImage,
        JSON.stringify({ og: m.ogUrl, loc: m.ogLocale, img: m.ogImage, tw: m.twCard }),
      );
      check(`${path}: has a title and a description`, m.title.length > 5 && (m.desc ?? "").length > 40, `${m.title} | ${(m.desc ?? "").length}`);
      check(`${path}: exactly one h1`, m.h1 === 1, String(m.h1));
      check(`${path}: lang=${loc} dir=${loc === "ar" ? "rtl" : "ltr"}`, m.lang === loc && m.dir === (loc === "ar" ? "rtl" : "ltr"), `${m.lang}/${m.dir}`);
      check(`${path}: every <img> has alt, no vague link text`, m.imgNoAlt === 0 && m.vague === 0, `${m.imgNoAlt} / ${m.vague}`);
      check(
        `${path}: header links to the GitHub repository (named, >= 44x44)`,
        m.github.length === 1 && /GitHub/.test(m.github[0].label ?? "") && m.github[0].w >= 44 && m.github[0].h >= 44,
        JSON.stringify(m.github),
      );
      if (path === "/ar" || path === "/en") {
        const img = await b.ev(`fetch(new URL(${JSON.stringify(m.ogImage)}).pathname + new URL(${JSON.stringify(m.ogImage)}).search).then((r) => ({ s: r.status, type: r.headers.get('content-type') }))`);
        check(`${path}: the og:image exists on this server (PNG)`, img.s === 200 && img.type === "image/png", JSON.stringify(img));
      }
    }
  });

  await section("seo: JSON-LD on the landing page (SoftwareApplication + SoftwareSourceCode)", async () => {
    for (const loc of LOCALES) {
      await b.goto(`${base}/${loc}`, 1000);
      const ld = await b.ev(`[...document.querySelectorAll('script[type="application/ld+json"]')].map((s) => { try { return JSON.parse(s.textContent); } catch (e) { return { error: String(e) }; } })`);
      check(`/${loc}: one JSON-LD block that parses`, ld.length === 1 && !ld[0].error, JSON.stringify(ld).slice(0, 120));
      const graph = ld[0]?.["@graph"] ?? [];
      const app = graph.find((n) => n["@type"] === "SoftwareApplication") ?? {};
      const src = graph.find((n) => n["@type"] === "SoftwareSourceCode") ?? {};
      check(
        `/${loc}: SoftwareApplication (name, Windows 10/11 ... Web, UtilitiesApplication, free, GPL, download, ar+en)`,
        ld[0]?.["@context"] === "https://schema.org" && app.name === "DropFlow" && /^Windows 10, Windows 11(, macOS)?(, Linux)?(, Android)?, Web$/.test(app.operatingSystem ?? "") && app.applicationCategory === "UtilitiesApplication" &&
          Number(app.offers?.price) === 0 && app.license === GPL && /\/releases\/latest$/.test(app.downloadUrl ?? "") && JSON.stringify(app.inLanguage) === '["ar","en"]',
        JSON.stringify(app).slice(0, 200),
      );
      const faq = graph.find((n) => n["@type"] === "FAQPage") ?? {};
      const shown = await b.ev(`[...document.querySelectorAll('#faq details summary h3')].map((h) => h.textContent.trim())`);
      check(
        `/${loc}: FAQ section (6 questions, h3 under the FAQ h2) matches FAQPage JSON-LD`,
        shown.length === 6 && (faq.mainEntity ?? []).length === 6 && faq.mainEntity.every((q, i) => q["@type"] === "Question" && q.name === shown[i] && q.acceptedAnswer?.text?.length > 5),
        JSON.stringify(shown),
      );
      check(`/${loc}: JSON-LD author/publisher is the ScoutHub organisation`, app.author?.["@type"] === "Organization" && app.author?.name === "ScoutHub" && !/Moustafa Hamwi/.test(JSON.stringify(ld)), JSON.stringify(app.author));
      check(
        `/${loc}: SoftwareSourceCode (repository, C# + TypeScript, GPL)`,
        src.codeRepository === REPO && JSON.stringify(src.programmingLanguage) === '["C#","TypeScript"]' && src.license === GPL,
        JSON.stringify(src).slice(0, 200),
      );
    }
  });

  await section("seo: developer section, footer licence link, and no 'all rights reserved' anywhere", async () => {
    for (const [loc, repoText, contribText] of [["en", "View on GitHub", "Contribute"], ["ar", "تصفّح على GitHub", "ساهم معنا"]]) {
      await b.goto(`${base}/${loc}`, 1000);
      const d = await b.ev(`(() => {
        const s = document.querySelector('#developers');
        const a = (id) => { const e = document.querySelector('[data-testid=' + id + ']'); return e ? { href: e.getAttribute('href'), text: e.textContent.trim(), h: e.getBoundingClientRect().height, rel: e.rel } : null; };
        return { h2: s?.querySelector('h2')?.textContent ?? '', items: s ? s.querySelectorAll('[data-testid=dev-point]').length : 0, repo: a('dev-repo'), contrib: a('dev-contribute'), license: a('dev-license'), issues: a('dev-issues'), security: a('dev-security'), signing: a('dev-signing'),
          nav: !!document.querySelector('header nav a[href="#developers"]') };
      })()`);
      check(`/${loc}: developer section with a heading and 3 points`, d.h2.length > 5 && d.items === 3, `${d.h2} (${d.items})`);
      check(`/${loc}: "${repoText}" links to the repository`, d.repo?.href === REPO && d.repo.text === repoText && d.repo.h >= 44 && /noopener/.test(d.repo.rel), JSON.stringify(d.repo));
      check(`/${loc}: "${contribText}" links to CONTRIBUTING.md`, d.contrib?.href === CONTRIBUTING && d.contrib.text === contribText && d.contrib.h >= 44, JSON.stringify(d.contrib));
      check(`/${loc}: the developer section keeps the licence (GPL LICENSE file), issues, security and code-signing policy links (each >= 44 px tall)`,
        d.license?.href === `${REPO}/blob/main/LICENSE` && /GPL-3\.0/.test(d.license.text) && d.issues?.href === `${REPO}/issues` && d.security?.href === `${REPO}/blob/main/SECURITY.md` &&
          d.signing?.href === `${REPO}/blob/main/docs/code-signing-policy.md` && [d.license, d.issues, d.security, d.signing].every((x) => x.h >= 44 && /noopener/.test(x.rel)), JSON.stringify([d.license, d.issues, d.security, d.signing]));
      check(`/${loc}: header nav links to the developer section`, d.nav);
    }
    for (const path of PAGES) {
      await b.goto(`${base}${path}`, path.endsWith("/app") ? 1600 : 900);
      const text = await b.ev("document.body.innerText");
      check(`${path}: no "all rights reserved" / "proprietary" wording`, !RIGHTS.test(text), (text.match(RIGHTS) ?? [""])[0]);
    }
  });

  await section("seo: header with the GitHub link fits 390 px (ar + en), light and dark", async () => {
    for (const scheme of ["light", "dark"]) {
      for (const path of ["/ar", "/en", "/ar/terms", "/en/app"]) {
        await b.setViewport(390, 800, scheme, true);
        await b.goto(`${base}${path}`, path.endsWith("/app") ? 1600 : 1000);
        const r = await b.ev(`(() => {
          // Content inside a clipping or scrolling box (the format marquee, the format-tab scroller) cannot overflow the page: skip it.
          const clipped = (e) => { for (let p = e.parentElement; p && p !== document.body; p = p.parentElement) if (getComputedStyle(p).overflowX !== 'visible') return true; return false; };
          let worst = 0;
          for (const e of document.querySelectorAll('body *')) { if (e.closest('svg') || e.closest('.sr-only')) continue; if (getComputedStyle(e).position === 'fixed' || clipped(e)) continue; const r = e.getBoundingClientRect(); if (r.width > 0) worst = Math.max(worst, r.right - 390, -r.left); }
          const gh = document.querySelector('[data-testid=header-github]'); const g = gh.getBoundingClientRect(); const cs = getComputedStyle(gh);
          return { worst: Math.round(worst), w: g.width, h: g.height, color: cs.color, bg: getComputedStyle(document.body).backgroundColor };
        })()`);
        check(`${path} at 390px (${scheme}): no horizontal overflow, GitHub link ${Math.round(r.w)}x${Math.round(r.h)} >= 44`, r.worst <= 0 && r.w >= 44 && r.h >= 44 && r.color !== r.bg, JSON.stringify(r));
      }
    }
    await b.setViewport(1280, 900, "dark", false);
  });

  await section("seo: terms page states the GPL with its links; privacy says the code is public", async () => {
    for (const loc of LOCALES) {
      await b.goto(`${base}/${loc}/terms`, 1000);
      const t = await b.ev(`({ text: document.querySelector('main').innerText, links: [...document.querySelectorAll('main a')].map((a) => a.getAttribute('href')) })`);
      check(`/${loc}/terms: says GPL-3.0-or-later, sections 15-16 (no warranty, liability)`, /GPL-3\.0-or-later|GPL-3\.0 أو أحدث/.test(t.text) && /15/.test(t.text) && /16/.test(t.text));
      check(
        `/${loc}/terms: links the GPL text, the LICENSE file, the repository and TRADEMARK.md`,
        [GPL, `${REPO}/blob/main/LICENSE`, REPO, `${REPO}/blob/main/TRADEMARK.md`].every((u) => t.links.includes(u)),
        t.links.join(" "),
      );
      check(`/${loc}/terms: keeps the honest notes (stamp is not a legal signature, redact limits, originals never overwritten)`, /(legal or cryptographic|قانونيًّا أو تشفيريًّا)/.test(t.text) && /(redact|حجب)/i.test(t.text) && /(never overwrites|لا يستبدل)/.test(t.text));
      await b.goto(`${base}/${loc}/privacy`, 1000);
      const p = await b.ev(`({ text: document.querySelector('main').innerText, links: [...document.querySelectorAll('main a')].map((a) => a.getAttribute('href')) })`);
      check(`/${loc}/privacy: says the source code is public and links it`, /(source code is public|الكود المصدري منشور)/.test(p.text) && p.links.includes(REPO));
    }
  });

  await section("security: headers on every route, security.txt, footer contact links, label-in-name, contrast", async () => {
    await b.goto(`${base}/en`, 800);
    for (const path of ["/en", "/ar", "/en/privacy", "/ar/terms", "/en/app", "/robots.txt", "/sitemap.xml", "/icon.svg"]) {
      const h = await b.ev(`fetch(${JSON.stringify(path)}).then((r) => Object.fromEntries(r.headers))`);
      const csp = h["content-security-policy"] ?? "";
      const ok =
        h["x-content-type-options"] === "nosniff" && h["x-frame-options"] === "DENY" && /max-age=63072000; includeSubDomains; preload/.test(h["strict-transport-security"] ?? "") &&
        /camera=\(\)/.test(h["permissions-policy"] ?? "") && h["cross-origin-opener-policy"] === "same-origin" && h["cross-origin-resource-policy"] === "same-origin" &&
        /^(strict-origin-when-cross-origin|no-referrer)$/.test(h["referrer-policy"] ?? "") && h["access-control-allow-origin"] !== "*" && !h["x-powered-by"] && !h["link"] &&
        /default-src 'self'/.test(csp) && /frame-ancestors 'none'/.test(csp) && /object-src 'none'/.test(csp) &&
        // no host is ever named; the only scheme source is https: in the /app connect-src (link downloads)
        !/https?:\/\//.test(csp) && !/http:/.test(csp) && (path.endsWith("/app") ? /connect-src 'self' blob: data: https:(;|$)/.test(csp) : !/https:/.test(csp));
      check(`${path}: CSP + nosniff, DENY, HSTS preload, Permissions-Policy, COOP/CORP, Referrer-Policy, no CORS *, no X-Powered-By`, ok, JSON.stringify({ csp: csp.slice(0, 60), acao: h["access-control-allow-origin"], ref: h["referrer-policy"], link: h["link"] }));
    }
    const appCsp = await b.ev(`fetch('/en/app').then((r) => r.headers.get('content-security-policy'))`);
    check("/app keeps its stricter CSP (form-action 'none', connect-src with blob/data only)", /form-action 'none'/.test(appCsp) && /connect-src 'self' blob: data:/.test(appCsp), appCsp);

    const sec = await b.ev(`fetch('/.well-known/security.txt').then(async (r) => ({ s: r.status, type: r.headers.get('content-type'), t: await r.text() }))`);
    check(
      "/.well-known/security.txt (RFC 9116): Contact, Expires in the future, Preferred-Languages, Canonical, Policy",
      sec.s === 200 && /text\/plain/.test(sec.type ?? "") && /^Contact: https:\/\/github\.com\/hamwimoustafa88-maker\/dropflow\/security\/advisories\/new$/m.test(sec.t) &&
        new Date(/^Expires: (.+)$/m.exec(sec.t)?.[1]) > new Date() && /^Preferred-Languages: ar, en$/m.test(sec.t) &&
        /^Canonical: https:\/\/dropflow\.scouthub\.dev\/\.well-known\/security\.txt$/m.test(sec.t) && /^Policy: https:\/\/github\.com\/.+\/SECURITY\.md$/m.test(sec.t),
      `${sec.s} ${sec.type}`,
    );

    for (const loc of LOCALES) {
      await b.goto(`${base}/${loc}`, 900);
      const f = await b.ev(`[...document.querySelectorAll('[data-testid=footer-nav] a')].map((a) => [a.getAttribute('data-testid'), a.getAttribute('href'), a.getBoundingClientRect().height])`);
      const want = { "footer-privacy": `/${loc}/privacy`, "footer-terms": `/${loc}/terms`, "footer-github": REPO };
      const got = Object.fromEntries(f.map(([id, href]) => [id, href]));
      check(`/${loc}: the footer nav is only Privacy, Terms and ONE open-source link to the repository (each >= 44 px tall)`, JSON.stringify(got) === JSON.stringify(want) && f.every(([, , h]) => h >= 44), JSON.stringify(got));
      const wa = await b.ev(`(() => { const a = document.querySelector('footer [data-testid=footer-whatsapp]'); const r = a.getBoundingClientRect(); return { href: a.getAttribute('href'), target: a.target, rel: a.rel, label: a.getAttribute('aria-label'), text: a.textContent.trim(), glyph: !!a.querySelector('svg[aria-hidden=true] path'), h: r.height, all: document.querySelectorAll('footer a').length }; })()`);
      check(`/${loc}: the footer has ONE prominent WhatsApp button (glyph, name starts with its visible text, new tab, noopener, >= 44 px) and 4 links in total`, wa.href === "https://wa.me/96170863903" && wa.target === "_blank" && /noopener/.test(wa.rel) && wa.glyph && wa.label.startsWith(wa.text) && wa.h >= 44 && wa.all === 4, JSON.stringify(wa));
      const spots = await b.ev(`['footer-whatsapp', 'cta-whatsapp', 'about-whatsapp'].map((id) => { const a = document.querySelector('[data-testid=' + id + ']'); return [id, a?.getAttribute('href'), a?.getAttribute('data-variant')]; })`);
      check(`/${loc}: the WhatsApp button is also in About (solid) and the closing call to action (outline)`, JSON.stringify(spots) === JSON.stringify([["footer-whatsapp", "https://wa.me/96170863903", "solid"], ["cta-whatsapp", "https://wa.me/96170863903", "outline"], ["about-whatsapp", "https://wa.me/96170863903", "solid"]]), JSON.stringify(spots));
      const names = await b.ev(`[...document.querySelectorAll('header [role=group] button')].map((e) => ({ text: e.textContent.trim(), label: e.getAttribute('aria-label') ?? '' }))`);
      check(`/${loc}: language buttons' accessible names start with their visible text (label in name)`, names.length === 2 && names.every((n) => n.label.startsWith(n.text)), JSON.stringify(names));
    }

    // Contrast of the demo's key labels (Shift / Ctrl / Alt) against the button they sit on, both themes, all states.
    const lum = (rgb) => { const [r, g, bl] = rgb.map((c) => c / 255).map((c) => (c <= 0.03928 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4)); return 0.2126 * r + 0.7152 * g + 0.0722 * bl; };
    const ratio = (a, c) => { const [x, y] = [lum(a), lum(c)].sort((p, q) => q - p); return (x + 0.05) / (y + 0.05); };
    for (const scheme of ["light", "dark"]) {
      await b.setViewport(1280, 900, scheme, false);
      await b.goto(`${base}/en`, 900);
      const pairs = await b.ev(`(() => {
        // Resolve any CSS colour (rgb, color(srgb), oklch...) to sRGB bytes through a 1x1 canvas.
        const cv = document.createElement('canvas'); cv.width = cv.height = 1; const cx = cv.getContext('2d', { willReadFrequently: true });
        const rgba = (s) => { cx.clearRect(0, 0, 1, 1); cx.fillStyle = '#000'; cx.fillStyle = s; cx.fillRect(0, 0, 1, 1); return [...cx.getImageData(0, 0, 1, 1).data]; };
        const bg = (e) => { for (; e; e = e.parentElement) { const c = rgba(getComputedStyle(e).backgroundColor); if (c[3] > 128) return c.slice(0, 3); } return rgba(getComputedStyle(document.body).backgroundColor).slice(0, 3); };
        return [...document.querySelectorAll('kbd')].map((k) => { const cs = getComputedStyle(k); return { fg: rgba(cs.color).slice(0, 3), op: +cs.opacity, bg: bg(k) }; });
      })()`);
      const worst = Math.min(...pairs.map((p) => (p.op < 1 ? 0 : ratio(p.fg, p.bg))));
      check(`${scheme}: every <kbd> key label has >= 4.5:1 contrast and no opacity (${pairs.length} labels)`, pairs.length >= 3 && worst >= 4.5, worst.toFixed(2));
    }
    await b.setViewport(1280, 900, "dark", false);
  });

  await section("seo: '/' always opens Arabic (308 -> /ar, whatever Accept-Language says)", async () => {
    for (const lang of ["en-US,en;q=0.9", "ar", "fr"]) {
      const r = await fetch(`${base}/`, { redirect: "manual", headers: { "accept-language": lang } });
      check(`/ with Accept-Language ${lang}: 308 -> /ar`, r.status === 308 && new URL(r.headers.get("location"), base).pathname === "/ar", `${r.status} ${r.headers.get("location")}`);
    }
  });

  await section("demo wheel: no box on click, keyboard ring inside the wedge, green when done", async () => {
    for (const scheme of ["light", "dark"]) {
      await b.setViewport(1280, 900, scheme, false);
      await b.goto(`${base}/en`, 1000);
      await b.click(".wedge-group");
      const during = await b.ev(`(() => { const g = document.activeElement; const cs = getComputedStyle(g); return { tag: g.tagName, outline: cs.outlineStyle, ow: cs.outlineWidth }; })()`);
      check(`${scheme}: clicking a wedge draws no outline box`, during.outline === "none" || parseFloat(during.ow) === 0, JSON.stringify(during));
      await b.waitFor("document.querySelector('[data-testid=demo-status]')?.dataset.done === 'true'", 5000, "demo done");
      await sleep(500); // let the 160 ms fill transition finish
      const done = await b.ev(`(() => {
        const cv = document.createElement('canvas'); cv.width = cv.height = 1; const cx = cv.getContext('2d', { willReadFrequently: true });
        const rgba = (s) => { cx.clearRect(0, 0, 1, 1); cx.fillStyle = s; cx.fillRect(0, 0, 1, 1); return [...cx.getImageData(0, 0, 1, 1).data].slice(0, 3); };
        const root = getComputedStyle(document.documentElement);
        const st = document.querySelector('[data-testid=demo-status]');
        const wedge = document.querySelector('.wedge[data-done=true]');
        const label = wedge?.nextElementSibling;
        let e = st; let bg = [0, 0, 0, 0];
        for (; e; e = e.parentElement) { const c = getComputedStyle(e).backgroundColor; cx.clearRect(0, 0, 1, 1); cx.fillStyle = c; cx.fillRect(0, 0, 1, 1); const d = [...cx.getImageData(0, 0, 1, 1).data]; if (d[3] > 128) { bg = d.slice(0, 3); break; } }
        if (!e) bg = rgba(root.getPropertyValue('--bg'));
        return { text: st.textContent, status: rgba(getComputedStyle(st).color), ok: rgba(root.getPropertyValue('--ok')), statusBg: bg,
          wedge: wedge ? rgba(getComputedStyle(wedge).fill) : null, label: label ? rgba(getComputedStyle(label).fill) : null };
      })()`);
      const lum = (rgb) => { const [r, g, bl] = rgb.map((c) => c / 255).map((c) => (c <= 0.03928 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4)); return 0.2126 * r + 0.7152 * g + 0.0722 * bl; };
      const ratio = (a, c) => { const [x, y] = [lum(a), lum(c)].sort((p, q) => q - p); return (x + 0.05) / (y + 0.05); };
      const same = (a, c) => a && c && a.every((v, i) => Math.abs(v - c[i]) <= 2);
      check(`${scheme}: "Done · ..." status and the finished wedge turn green (success token)`, /^Done · /.test(done.text) && same(done.status, done.ok) && same(done.wedge, done.ok), JSON.stringify(done));
      check(
        `${scheme}: green text >= 4.5:1 on its background, wedge label >= 4.5:1 on green`,
        ratio(done.status, done.statusBg) >= 4.5 && done.label && ratio(done.label, done.wedge) >= 4.5,
        `${ratio(done.status, done.statusBg).toFixed(2)} / ${done.label ? ratio(done.label, done.wedge).toFixed(2) : "-"}`,
      );
      // Keyboard: arrow onto the next wedge -> :focus-visible ring is the wedge's own stroke, no outline box.
      await b.key("ArrowRight", { code: "ArrowRight", vk: 39 });
      await sleep(150);
      const kb = await b.ev(`(() => { const g = document.activeElement; if (!g.classList.contains('wedge-group')) return { tag: g.tagName }; const p = g.querySelector('.wedge'); const cs = getComputedStyle(g); return { outline: cs.outlineStyle, stroke: getComputedStyle(p).stroke, sw: getComputedStyle(p).strokeWidth, fv: g.matches(':focus-visible') }; })()`);
      check(`${scheme}: keyboard focus shows a ring drawn by the wedge itself (no box)`, kb.fv === true && kb.outline === "none" && parseFloat(kb.sw) >= 5, JSON.stringify(kb));
    }
    await b.setViewport(1280, 900, "dark", false);
  });
}

