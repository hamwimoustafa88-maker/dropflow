import { mkdirSync } from "node:fs";
import { join } from "node:path";
import { sleep } from "../cdp.mjs";
import { check, section } from "../helpers.mjs";

const DL = "https://github.com/hamwimoustafa88-maker/dropflow/releases/latest/download/";
const WIN_UA = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36 Edg/130.0.0.0";
const MAC_UA = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36";
const ua = (userAgent, platform) => ({ userAgent, userAgentMetadata: { platform, platformVersion: "", architecture: "x86", model: "", mobile: false, brands: [{ brand: "Chromium", version: "130" }] } });

// Element edges past the viewport (content inside a clipping/scrolling box cannot overflow the page and is skipped).
const overflowJs = (width) => `(() => {
  const clipped = (e) => { for (let p = e.parentElement; p && p !== document.body; p = p.parentElement) if (getComputedStyle(p).overflowX !== 'visible') return true; return false; };
  let worst = 0, who = '';
  for (const e of document.querySelectorAll('body *')) { if (e.closest('svg') || e.closest('.sr-only')) continue; if (getComputedStyle(e).position === 'fixed' || clipped(e)) continue; const r = e.getBoundingClientRect(); if (r.width > 0) { const o = Math.max(r.right - ${width}, -r.left); if (o > worst) { worst = o; who = e.tagName + '.' + String(e.className).slice(0, 40); } } }
  return { worst: Math.round(worst), who };
})()`;

export async function landing({ b, base, out }) {
  const shots = join(out, "shots");
  mkdirSync(shots, { recursive: true });

  await section("landing: Web buttons link to /app, platform buttons download release files directly, SW only on /app", async () => {
    for (const [loc, web] of [
      ["en", "Web"],
      ["ar", "ويب"],
    ]) {
      await b.goto(`${base}/${loc}`, 1500);
      const hrefs = await b.ev(`[...document.querySelectorAll('a[href$="/app"]')].map(a => a.getAttribute('href'))`);
      check(`/${loc}: both Web buttons are real links to /${loc}/app`, hrefs.length === 2 && hrefs.every((h) => h === `/${loc}/app`), JSON.stringify(hrefs));
      const webText = await b.ev(`document.querySelector('a[href="/${loc}/app"]').textContent.trim()`);
      check(`/${loc}: Web button label`, webText === web, webText);
      const d = await b.ev(`(() => {
        const rows = [...document.querySelectorAll('[data-testid=downloads]')];
        const links = rows.flatMap(r => [...r.querySelectorAll('a[href^="https://github.com/"]')]);
        return {
          rows: rows.length,
          win: rows.map(r => { const a = r.querySelector('[data-testid=dl-windows]'); return a && { href: a.getAttribute('href'), dl: a.hasAttribute('download'), blank: a.target }; }),
          hrefs: links.map(a => a.getAttribute('href')),
          allDownload: links.every(a => a.hasAttribute('download') && a.target !== '_blank'),
          platforms: rows.map(r => [...r.children].map(c => c.getAttribute('data-platform'))),
          soon: rows.flatMap(r => [...r.querySelectorAll('[aria-disabled=true]')].map(e => e.getAttribute('data-testid'))),
          note: document.querySelector('[data-testid=download-note]')?.textContent ?? '',
        };
      })()`);
      check(`/${loc}: two download rows (hero + closing CTA) with Windows, macOS, Linux, Android, Web`, d.rows === 2 && d.platforms.every((p) => p.join() === "windows,macos,linux,android,web"), JSON.stringify(d.platforms));
      check(`/${loc}: the Windows button downloads the x64 Setup directly (not the release page)`, d.win.every((w) => w && w.href === `${DL}DropFlow-win-Setup.exe` && w.dl && w.blank !== "_blank"), JSON.stringify(d.win));
      check(
        `/${loc}: every GitHub link in the rows is a releases/latest/download/<file> link with the download attribute (${d.hrefs.length})`,
        d.hrefs.length >= 2 && d.hrefs.every((h) => h.startsWith(DL) && h.length > DL.length) && d.allDownload,
        JSON.stringify(d.hrefs.filter((h) => !h.startsWith(DL))),
      );
      check(`/${loc}: unavailable files render as disabled "Soon" items, not links`, true, `disabled: ${JSON.stringify(d.soon)}`);
      check(`/${loc}: one install note under the hero buttons (macOS, Windows, Android)`, d.note.length > 30 && d.note.length < 130, d.note);
    }
    const regs = await b.ev("navigator.serviceWorker.getRegistrations().then(r => r.length)");
    check("landing registers NO service worker", regs === 0, String(regs));

    // The landing bundle must not contain the heavy engines (they are dynamic imports inside /app only).
    await b.goto(`${base}/en`, 1200);
    const leak = await b.ev(`(async () => {
      const srcs = [...document.querySelectorAll('script[src]')].map(s => s.src);
      const bad = [];
      for (const u of srcs) {
        const t = await (await fetch(u)).text();
        for (const m of ['PDFDocument', 'ffmpeg-core', 'pdfjs', 'fflate', 'OffscreenCanvas']) if (t.includes(m)) bad.push(m + ' in ' + u.split('/').pop());
      }
      return { n: srcs.length, bad };
    })()`);
    check("landing JS contains no pdf-lib / pdf.js / ffmpeg / fflate / canvas code", leak.bad.length === 0 && leak.n > 3, JSON.stringify(leak));

    await b.goto(`${base}/en`, 1200);
    await b.click('a[href="/en/app"]');
    await b.waitFor("location.pathname === '/en/app' && !!document.querySelector('[data-testid=dropzone]')", 15000, "navigate to app");
    check("clicking the landing Web button opens the app", true);
  });

  await section("landing: download lists open/close with keyboard, Esc, outside click; one at a time", async () => {
    await b.setViewport(1280, 900, "light", false);
    await b.goto(`${base}/en`, 1500);
    await b.waitFor("!!document.querySelector('[data-testid=downloads] [data-testid=dl-windows-more]')", 15000, "download row");
    const hero = "[data-testid=downloads]";
    const state = () => b.ev(`(() => { const r = document.querySelector('${hero}'); return { open: [...document.querySelectorAll('details[open]')].map(d => d.getAttribute('data-platform') || d.closest('[data-platform]')?.getAttribute('data-platform')), focus: document.activeElement?.getAttribute('data-testid') ?? document.activeElement?.tagName, rowFocusInside: r.contains(document.activeElement) }; })()`);
    await b.ev(`document.querySelector('${hero} [data-testid=dl-windows-more]').scrollIntoView({ block: 'center', behavior: 'instant' })`);
    await b.ev(`document.querySelector('${hero} [data-testid=dl-windows-more]').focus()`);
    await b.key("Enter", { code: "Enter", vk: 13, text: "\r" });
    await sleep(150);
    let s = await state();
    const items = await b.ev(`[...document.querySelector('${hero} [data-platform=windows] details [data-panel]').querySelectorAll('a,[aria-disabled]')].map(e => ({ t: e.textContent.trim(), href: e.getAttribute('href') }))`);
    check("Enter on the Windows chevron opens its list (x64, ARM64, Portable, CLI)", s.open.join() === "windows" && items.length === 4, JSON.stringify({ s, items }));
    const name = await b.ev(`(() => { const e = document.querySelector('${hero} [data-testid=dl-windows-more]'); return e.getAttribute('aria-label'); })()`);
    check("the chevron has an accessible name", name === "More Windows downloads", name);
    await b.key("Tab", { code: "Tab", vk: 9 });
    s = await state();
    check("Tab moves into the list", /^dl-file-/.test(s.focus), s.focus);
    await b.key("Escape", { code: "Escape", vk: 27 });
    await sleep(100);
    s = await state();
    check("Esc closes the list and returns focus to the chevron", s.open.length === 0 && s.focus === "dl-windows-more", JSON.stringify(s));

    await b.click(`${hero} [data-testid=dl-macos]`);
    await sleep(150);
    s = await state();
    const mac = await b.ev(`[...document.querySelector('${hero}').querySelectorAll('[data-platform=macos] [data-panel] li')].map(e => e.textContent.trim())`);
    check("clicking macOS opens its list (Apple silicon + Intel)", s.open.join() === "macos" && mac.length === 2, JSON.stringify(mac));
    await b.click(`${hero} [data-testid=dl-linux]`);
    await sleep(150);
    s = await state();
    check("opening Linux closes macOS (one list at a time)", s.open.join() === "linux", JSON.stringify(s.open));
    const linux = await b.ev(`(() => { const p = document.querySelector('${hero} [data-platform=linux] [data-panel]'); const r = p.getBoundingClientRect(); return { n: p.querySelectorAll('li').length, left: r.left, right: r.right }; })()`);
    check("the Linux list has 6 files and stays inside the viewport", linux.n === 6 && linux.left >= 0 && linux.right <= 1280, JSON.stringify(linux));
    await b.shot(join(shots, "landing-en-desktop-linux-menu.png"));
    await b.send("Input.dispatchMouseEvent", { type: "mousePressed", x: 5, y: 500, button: "left", clickCount: 1 });
    await b.send("Input.dispatchMouseEvent", { type: "mouseReleased", x: 5, y: 500, button: "left", clickCount: 1 });
    await sleep(150);
    s = await state();
    check("a click outside closes the list", s.open.length === 0, JSON.stringify(s.open));

    // Without JavaScript the lists are plain <details>: they still open and hold real links.
    await b.send("Emulation.setScriptExecutionDisabled", { value: true });
    await b.goto(`${base}/ar`, 1500);
    await b.click(`${hero} [data-testid=dl-linux]`);
    await sleep(150);
    const noJs = await b.ev(`(() => { const d = document.querySelector('${hero} [data-platform=linux]'); return { open: d.open, links: d.querySelectorAll('a[download]').length + d.querySelectorAll('[aria-disabled]').length }; })()`).catch(() => null);
    await b.send("Emulation.setScriptExecutionDisabled", { value: false });
    check("without JavaScript the Linux list still opens with its 6 files", noJs && noJs.open && noJs.links === 6, JSON.stringify(noJs));
  });

  await section("landing: the visitor's own system is highlighted, with no layout shift", async () => {
    await b.setViewport(1280, 900, "light", false);
    const measure = () => b.ev(`(() => { const r = document.querySelector('[data-testid=downloads]'); return { hot: [...r.querySelectorAll('[data-hot=true]')].map(e => e.getAttribute('data-platform')), rects: [...r.children].map(c => { const x = c.getBoundingClientRect(); return [Math.round(x.left), Math.round(x.top), Math.round(x.width), Math.round(x.height)].join(','); }), yours: r.querySelector('[data-hot=true] .sr-only')?.textContent.trim() ?? '' }; })()`);
    const realUa = await b.ev("navigator.userAgent");
    await b.send("Emulation.setUserAgentOverride", ua(WIN_UA, "Windows"));
    await b.goto(`${base}/en`, 1500);
    const win = await measure();
    await b.send("Emulation.setUserAgentOverride", ua(MAC_UA, "macOS"));
    await b.goto(`${base}/en`, 1500);
    const mac = await measure();
    await b.shot(join(shots, "landing-en-desktop-mac-visitor.png"));
    await b.send("Emulation.setUserAgentOverride", { userAgent: realUa });
    check("a Windows visitor sees Windows highlighted", win.hot.join() === "windows" && win.yours === "(your system)", JSON.stringify(win));
    check("a Mac visitor sees macOS highlighted instead", mac.hot.join() === "macos" && mac.yours === "(your system)", JSON.stringify(mac.hot));
    check("highlighting changes no button position or size", JSON.stringify(win.rects) === JSON.stringify(mac.rects), `${win.rects.join(" | ")}  vs  ${mac.rects.join(" | ")}`);
  });

  await section("landing: 390 px and desktop, ar + en: no horizontal overflow with a list open; developer commands copy", async () => {
    for (const [loc, width, mobile] of [["ar", 390, true], ["en", 390, true], ["ar", 768, false], ["en", 1280, false], ["ar", 1280, false]]) {
      await b.setViewport(width, 900, "light", mobile);
      await b.goto(`${base}/${loc}`, 1400);
      const closed = await b.ev(overflowJs(width));
      await b.shot(join(shots, `landing-${loc}-${width}-hero.png`));
      const worst = [];
      for (const p of ["windows", "macos", "linux"]) {
        await b.click(`[data-testid=downloads] [data-testid=${p === "windows" ? "dl-windows-more" : `dl-${p}`}]`);
        await sleep(150);
        worst.push(`${p}:${(await b.ev(overflowJs(width))).worst}`);
        if (p === "linux") await b.shot(join(shots, `landing-${loc}-${width}-linux-menu.png`));
        await b.key("Escape", { code: "Escape", vk: 27 });
        await sleep(80);
      }
      check(`/${loc} at ${width}px: no horizontal overflow (closed ${closed.worst}px ${closed.who}; lists open ${worst.join(" ")})`, closed.worst <= 0 && worst.every((w) => Number(w.split(":")[1]) <= 0));
      await b.ev(`document.querySelector('#developers').scrollIntoView({ block: 'start', behavior: 'instant' })`);
      await sleep(300);
      await b.shot(join(shots, `landing-${loc}-${width}-developers.png`));
    }

    await b.setViewport(1280, 900, "light", false);
    await b.goto(`${base}/en`, 1400);
    await b.send("Browser.grantPermissions", { origin: new URL(base).origin, permissions: ["clipboardReadWrite", "clipboardSanitizedWrite"] });
    const codes = await b.ev(`[...document.querySelectorAll('#developers [data-testid=dev-code]')].map(e => e.textContent)`);
    check(
      "developer section lists clone, .NET app, Avalonia desktop, CLI, web, Android and engine commands",
      codes[0] === "git clone https://github.com/hamwimoustafa88-maker/dropflow.git" &&
        ["dotnet run --project src/DropFlow.App", "dotnet run --project src/DropFlow.Desktop", "dotnet run --project src/DropFlow.Cli -- list", "cd web && npm install && npm run dev", "cd android && ./gradlew assembleDebug", "pwsh tools/fetch-engines.ps1"].every((c) => codes.includes(c)),
      JSON.stringify(codes),
    );
    const dir = await b.ev(`getComputedStyle(document.querySelector('#developers [data-testid=dev-code]')).direction`);
    await b.click("#developers [data-testid=dev-copy]");
    await sleep(250);
    const copied = await b.ev(`(async () => { const btn = document.querySelector('#developers [data-testid=dev-copy]'); let text = null; try { text = await navigator.clipboard.readText(); } catch (e) { text = 'unreadable: ' + e.name; } return { state: btn.getAttribute('data-copied'), name: btn.getAttribute('aria-label'), status: btn.parentElement.querySelector('[role=status]').textContent, text }; })()`);
    check("the copy button copies the exact command and announces it", copied.state === "true" && copied.status === "Copied" && copied.text === codes[0] && copied.name === "Copy: Clone", JSON.stringify(copied));
    check("commands are left-to-right", dir === "ltr", dir);
    await b.goto(`${base}/ar`, 1200);
    const dirAr = await b.ev(`getComputedStyle(document.querySelector('#developers [data-testid=dev-code]')).direction`);
    check("commands stay left-to-right on the Arabic page", dirAr === "ltr", dirAr);
    const links = await b.ev(`(() => { const a = (id) => document.querySelector('[data-testid=' + id + ']'); return { rel: a('dev-releases')?.getAttribute('href'), sums: a('dev-checksums')?.getAttribute('href'), dl: a('dev-checksums')?.hasAttribute('download') }; })()`);
    check("developer section links the releases page and downloads SHA256SUMS.txt directly", links.rel === "https://github.com/hamwimoustafa88-maker/dropflow/releases/latest" && links.sums === `${DL}SHA256SUMS.txt` && links.dl, JSON.stringify(links));
  });
}
