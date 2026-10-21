import { mkdirSync } from "node:fs";
import { join } from "node:path";
import { sleep } from "../cdp.mjs";
import { addFiles, check, clickWedge, freshApp, section, selectTab, waitResults } from "../helpers.mjs";

const hex = (c) => c.trim();
const lum = (h) => {
  let v = h.replace("#", "");
  if (v.length === 3) v = [...v].map((c) => c + c).join("");
  const [r, g, bl] = [0, 2, 4].map((i) => parseInt(v.slice(i, i + 2), 16) / 255).map((c) => (c <= 0.03928 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4));
  return 0.2126 * r + 0.7152 * g + 0.0722 * bl;
};
const ratio = (a, c) => {
  const [x, y] = [lum(a), lum(c)].sort((p, q) => q - p);
  return (x + 0.05) / (y + 0.05);
};

export async function quality({ b, base, out, files }) {
  const shots = join(out, "shots");
  mkdirSync(shots, { recursive: true });

  await section("quality: contrast >= 4.5:1 for every text/background token pair, light and dark", async () => {
    for (const scheme of ["light", "dark"]) {
      await b.setViewport(1280, 900, scheme, false);
      await freshApp(b, base, "en");
      const t = await b.ev(`(() => { const s = getComputedStyle(document.documentElement); const g = (n) => s.getPropertyValue(n).trim(); return Object.fromEntries(['--bg','--surface','--surface-2','--fg','--muted','--primary','--on-primary','--primary-soft','--accent','--danger','--warn','--ok'].map(n => [n, g(n)])); })()`);
      const pairs = [
        ["fg on bg", t["--fg"], t["--bg"]], ["fg on surface", t["--fg"], t["--surface"]], ["fg on surface-2", t["--fg"], t["--surface-2"]],
        ["muted on bg", t["--muted"], t["--bg"]], ["muted on surface", t["--muted"], t["--surface"]], ["muted on surface-2", t["--muted"], t["--surface-2"]],
        ["on-primary on primary", t["--on-primary"], t["--primary"]], ["fg on primary-soft", t["--fg"], t["--primary-soft"]],
        ["accent on bg", t["--accent"], t["--bg"]], ["danger on surface-2", t["--danger"], t["--surface-2"]], ["warn on surface-2", t["--warn"], t["--surface-2"]],
        ["ok on bg", t["--ok"], t["--bg"]], ["primary on bg (links/icons)", t["--primary"], t["--bg"]],
      ];
      const bad = pairs.filter(([, f, c]) => !(ratio(hex(f), hex(c)) >= 4.5)).map(([n, f, c]) => `${n} ${ratio(hex(f), hex(c)).toFixed(2)} (${f} on ${c})`);
      check(`${scheme}: all ${pairs.length} token pairs >= 4.5:1`, bad.length === 0, bad.join("; ") || `min ${Math.min(...pairs.map(([, f, c]) => ratio(hex(f), hex(c)))).toFixed(2)}`);
    }
    await b.setViewport(1280, 900, "dark", false);
  });

  await section("quality: the site stays light (white) even when the visitor's system is dark", async () => {
    for (const path of ["/ar", "/en", "/ar/app", "/ar/privacy"]) {
      await b.setViewport(1280, 900, "dark", false);
      await b.goto(`${base}${path}`, 1200);
      const bg = await b.ev("getComputedStyle(document.body).backgroundColor");
      const [r, g, bl] = (bg.match(/\d+/g) || []).map(Number);
      check(`${path}: light background under a dark system theme`, r > 230 && g > 230 && bl > 230, bg);
    }
  });

  await section("quality: no layout shift, no horizontal overflow (390 / 768 / 1280, ar + en), accessible names, focus ring", async () => {
    await b.send("Page.addScriptToEvaluateOnNewDocument", {
      source: "window.__cls = 0; new PerformanceObserver((l) => { for (const e of l.getEntries()) if (!e.hadRecentInput) window.__cls += e.value; }).observe({ type: 'layout-shift', buffered: true });",
    });
    for (const [locale, width, mobile] of [["en", 1280, false], ["ar", 1280, false], ["en", 768, false], ["ar", 390, true], ["en", 390, true]]) {
      await b.setViewport(width, 900, "dark", mobile);
      await freshApp(b, base, locale);
      await sleep(600);
      // body has overflow-x: clip, so scrollWidth hides overflow: measure every element's right edge instead
      const overflowing = () => b.ev(`(() => { let worst = 0; for (const e of document.querySelectorAll('body *')) { if (e.closest('svg') || e.closest('.sr-only')) continue; if (getComputedStyle(e).position === 'fixed') continue; const r = e.getBoundingClientRect(); if (r.width > 0) worst = Math.max(worst, r.right - ${width}, -r.left); } return Math.round(worst); })()`);
      const empty = await overflowing();
      await addFiles(b, [files.png]);
      await clickWedge(b, "convert:jpg");
      await waitResults(b, 1);
      await sleep(400);
      const withFiles = await overflowing();
      const cls = await b.ev("window.__cls");
      check(`/${locale}/app at ${width}px: no horizontal overflow (empty ${empty}px, with files+result ${withFiles}px)`, empty <= 0 && withFiles <= 0);
      check(`/${locale}/app at ${width}px: layout shift (CLS) ${Number(cls).toFixed(3)} <= 0.05`, cls <= 0.05, String(cls));
    }

    await b.setViewport(1280, 900, "dark", false);
    await freshApp(b, base, "en");
    await addFiles(b, [files.png]);
    await clickWedge(b, "convert:jpg");
    await waitResults(b, 1);
    const unnamed = await b.ev(`(() => {
      const bad = [];
      for (const el of document.querySelectorAll('button, a[href], input, select, textarea, [role=button]')) {
        if (el.type === 'hidden' || el.hidden || el.closest('[hidden]')) continue;
        const name = (el.getAttribute('aria-label') || el.textContent || el.labels?.[0]?.textContent || el.getAttribute('title') || '').trim();
        if (!name) bad.push(el.outerHTML.slice(0, 80));
      }
      return bad;
    })()`);
    check("every interactive element has an accessible name", unnamed.length === 0, unnamed.slice(0, 3).join(" | "));
    check("document language + direction are set (en/ltr)", (await b.ev("document.documentElement.lang + '/' + document.documentElement.dir")) === "en/ltr");
    for (let i = 0; i < 3; i++) await b.key("Tab", { code: "Tab", vk: 9 });
    const ring = await b.ev("(() => { const s = getComputedStyle(document.activeElement); return { tag: document.activeElement.tagName, w: s.outlineWidth, st: s.outlineStyle }; })()");
    check("keyboard focus is visible (outline on :focus-visible)", ring.st !== "none" && parseFloat(ring.w) >= 2, JSON.stringify(ring));
    const targets = await b.ev(`[...document.querySelectorAll('button, a[href], select, [role=button]')].filter(e => e.offsetParent && !e.closest('svg') && !e.classList.contains('sr-only')).map(e => { const r = e.getBoundingClientRect(); return { n: (e.getAttribute('aria-label') || e.textContent).trim().slice(0, 20), w: r.width, h: r.height }; }).filter(t => t.w < 24 || t.h < 24)`);
    check("touch targets >= 24x24 CSS px (WCAG 2.2)", targets.length === 0, JSON.stringify(targets.slice(0, 3)));
  });

  await section("quality: reduced motion is respected", async () => {
    await b.send("Emulation.setEmulatedMedia", { features: [{ name: "prefers-color-scheme", value: "dark" }, { name: "prefers-reduced-motion", value: "reduce" }] });
    await freshApp(b, base, "en");
    await addFiles(b, [files.png]);
    const anim = await b.ev(`(() => { const names = {}; for (const sel of ['.orbit', '.wheel-pop', '.spin']) { const e = document.querySelector(sel); names[sel] = e ? getComputedStyle(e).animationName : 'absent'; } return names; })()`);
    check("with prefers-reduced-motion: orbit / pop animations are off", anim[".orbit"] === "none" && anim[".wheel-pop"] === "none", JSON.stringify(anim));
    await b.send("Emulation.setEmulatedMedia", { features: [{ name: "prefers-color-scheme", value: "dark" }, { name: "prefers-reduced-motion", value: "no-preference" }] });
    await freshApp(b, base, "en");
    await addFiles(b, [files.png]);
    const anim2 = await b.ev(`getComputedStyle(document.querySelector('.orbit')).animationName`);
    check("without it the orbit animation runs (sanity)", anim2 !== "none", anim2);
  });

  await section("quality: screenshots (dark/light x ar/en x desktop/390 mobile)", async () => {
    const combos = [
      ["en-dark-desktop", "en", "dark", 1280, false],
      ["ar-dark-desktop", "ar", "dark", 1280, false],
      ["en-light-desktop", "en", "light", 1280, false],
      ["ar-light-desktop", "ar", "light", 1280, false],
      ["en-dark-mobile", "en", "dark", 390, true],
      ["ar-light-mobile", "ar", "light", 390, true],
    ];
    for (const [name, locale, scheme, width, mobile] of combos) {
      await b.setViewport(width, 900, scheme, mobile);
      await freshApp(b, base, locale);
      await b.shot(join(shots, `${name}-empty.png`));
      await addFiles(b, [files.png, files.pdf3]);
      await selectTab(b, "tools");
      await b.shot(join(shots, `${name}-tools.png`));
      await freshApp(b, base, locale);
      await addFiles(b, [files.png]);
      await clickWedge(b, "convert:jpg");
      await waitResults(b, 1);
      await clickWedge(b, "convert:bmp");
      await waitResults(b, 2);
      await sleep(500);
      await b.shot(join(shots, `${name}-results.png`), { full: true });
    }
    await b.setViewport(1280, 900, "dark", false);
    await freshApp(b, base, "en");
    await addFiles(b, [files.png]);
    await selectTab(b, "tools");
    await clickWedge(b, "img.resize");
    await sleep(300);
    await b.shot(join(shots, "en-dark-desktop-options.png"));
    await b.setViewport(1280, 900, "light", false);
    await freshApp(b, base, "ar");
    await addFiles(b, [files.pdf3]);
    await selectTab(b, "tools");
    await clickWedge(b, "pdf.compress");
    await sleep(300);
    await b.shot(join(shots, "ar-light-desktop-options.png"));
    check("screenshots written", true, shots);
    await b.setViewport(1280, 900, "dark", false);
  });
}
