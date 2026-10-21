import { check, section } from "../helpers.mjs";

export async function landing({ b, base }) {
  await section("landing: Web buttons link to /app, Windows buttons link to the GitHub release, SW only on /app", async () => {
    for (const [loc, web] of [
      ["en", "Web"],
      ["ar", "ويب"],
    ]) {
      await b.goto(`${base}/${loc}`, 1500);
      const hrefs = await b.ev(`[...document.querySelectorAll('a[href$="/app"]')].map(a => a.getAttribute('href'))`);
      check(`/${loc}: both Web buttons are real links to /${loc}/app`, hrefs.length === 2 && hrefs.every((h) => h === `/${loc}/app`), JSON.stringify(hrefs));
      const win = await b.ev(`[...document.querySelectorAll('a[href^="https://github.com/hamwimoustafa88-maker/dropflow/releases"]')].map(a => a.getAttribute('href'))`);
      check(`/${loc}: both Windows buttons are real links to the GitHub release`, win.length === 2 && win.every((h) => h.endsWith("/releases/latest")), JSON.stringify(win));
      const webText = await b.ev(`document.querySelector('a[href="/${loc}/app"]').textContent.trim()`);
      check(`/${loc}: Web button label`, webText === web, webText);
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
        for (const m of ['PDFDocument', 'pdfjs', 'fflate', 'OffscreenCanvas']) if (t.includes(m)) bad.push(m + ' in ' + u.split('/').pop());
      }
      return { n: srcs.length, bad };
    })()`);
    check("landing JS contains no pdf-lib / pdf.js / fflate / canvas code", leak.bad.length === 0 && leak.n > 3, JSON.stringify(leak));

    await b.goto(`${base}/en`, 1200);
    await b.click('a[href="/en/app"]');
    await b.waitFor("location.pathname === '/en/app' && !!document.querySelector('[data-testid=dropzone]')", 15000, "navigate to app");
    check("clicking the landing Web button opens the app", true);
  });
}
