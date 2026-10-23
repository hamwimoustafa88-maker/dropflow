import { check, freshApp, section } from "../helpers.mjs";
import { sleep } from "../cdp.mjs";

const ISSUES = "https://github.com/hamwimoustafa88-maker/dropflow/issues";
const WA = "https://wa.me/96170863903";
const EMAIL = /[\w.+-]+@[\w-]+\.[\w.-]+/;

// Privacy Policy and Terms of Use: four static pages (ar/en x privacy/terms), reachable from the landing footer and the app.
export async function legal({ b, base }) {
  await section("legal pages: 200, right language + direction, honest content, no email address, indexable", async () => {
    await b.goto(`${base}/en`, 300); // a same-origin page for the relative fetch below (this section may run first)
    for (const loc of ["en", "ar"]) {
      for (const doc of ["privacy", "terms"]) {
        const status = await b.ev(`fetch(${JSON.stringify(`/${loc}/${doc}`)}).then(r => r.status)`);
        check(`/${loc}/${doc} answers 200`, status === 200, String(status));
        await b.goto(`${base}/${loc}/${doc}`, 1200);
        const page = await b.ev(`(() => ({
          lang: document.documentElement.lang, dir: document.documentElement.dir,
          h1: document.querySelector('h1')?.textContent ?? '', h2: document.querySelectorAll('main h2').length, h3: document.querySelectorAll('main h3').length,
          text: document.querySelector('main').innerText,
          wa: [...document.querySelectorAll('main a')].filter(a => a.href === ${JSON.stringify(WA)}).length,
          summary: document.querySelectorAll('[data-testid=legal-summary] li').length,
          tocLinks: [...document.querySelectorAll('[data-testid=legal-toc] nav')].map(n => [...n.querySelectorAll('a')].map(a => a.getAttribute('href'))),
          ids: [...document.querySelectorAll('main section[id]')].map(s => '#' + s.id),
          issues: [...document.querySelectorAll('a')].filter(a => a.href === ${JSON.stringify(ISSUES)}).length,
          home: document.querySelector('[data-testid=legal-home]')?.getAttribute('href'),
          other: document.querySelector('[data-testid=legal-other]')?.getAttribute('href'),
          robots: document.querySelector('meta[name=robots]')?.content ?? '',
          title: document.title,
        }))()`);
        check(`/${loc}/${doc}: lang=${loc} dir=${loc === "ar" ? "rtl" : "ltr"}`, page.lang === loc && page.dir === (loc === "ar" ? "rtl" : "ltr"), `${page.lang}/${page.dir}`);
        check(`/${loc}/${doc}: has a heading, 4+ sections (h2), 6+ sub-sections (h3) and the last-updated date`, page.h1.length > 3 && page.h2 >= 4 && page.h3 >= 6 && /2026/.test(page.text), `${page.h2} h2, ${page.h3} h3`);
        check(`/${loc}/${doc}: "in one minute" summary with 4 points`, page.summary === 4, String(page.summary));
        check(`/${loc}/${doc}: both contents lists (mobile + desktop) link to every section anchor`, page.tocLinks.length === 2 && page.tocLinks.every((l) => JSON.stringify(l) === JSON.stringify(page.ids)) && page.ids.length === page.h2, JSON.stringify([page.tocLinks, page.ids]));
        check(`/${loc}/${doc}: the WhatsApp contact button is on the page`, page.wa === 1, String(page.wa));
        check(`/${loc}/${doc}: contact is the GitHub issues page and there is no email address`, page.issues === 1 && !EMAIL.test(page.text), `links ${page.issues}`);
        check(`/${loc}/${doc}: links back to the home page and to the other document`, page.home === `/${loc}` && page.other === `/${loc}/${doc === "privacy" ? "terms" : "privacy"}`, `${page.home} ${page.other}`);
        check(`/${loc}/${doc}: indexable (no noindex: the site is public since the GPL release)`, !/noindex/.test(page.robots), page.robots || "no robots meta");
        check(`/${loc}/${doc}: has its own page title`, new RegExp(doc === "privacy" ? "Privacy|الخصوصية" : "Terms|شروط").test(page.title), page.title);
        if (doc === "privacy") {
          check(`/${loc}/privacy: names Vercel, GitHub and Hugging Face as third parties`, /Vercel/.test(page.text) && /GitHub/.test(page.text) && /Hugging Face/.test(page.text));
        } else {
          check(`/${loc}/terms: credits the ScoutHub teams and the lead developer, and the year`, /ScoutHub/.test(page.text) && /Moustafa Al-Hamwi|مصطفى الحموي/.test(page.text) && !/Moustafa Hamwi|مصطفى حموي/.test(page.text) && /2026/.test(page.text));
          check(`/${loc}/terms: free software under the GPL, never called proprietary`, /GPL-3\.0/.test(page.text) && !/proprietary|برنامج مملوك/i.test(page.text));
        }
      }
    }
  });

  await section("legal pages: no horizontal overflow at 390 / 768 / 1280 px, Arabic and English", async () => {
    for (const w of [390, 768, 1280]) {
      for (const loc of ["ar", "en"]) {
        for (const doc of ["privacy", "terms"]) {
          await b.setViewport(w, 800, "dark", w < 700);
          await b.goto(`${base}/${loc}/${doc}`, 800);
          const worst = await b.ev(`(() => { let worst = 0; for (const e of document.querySelectorAll('body *')) { if (e.closest('svg') || e.closest('.sr-only')) continue; if (getComputedStyle(e).position === 'fixed') continue; const r = e.getBoundingClientRect(); if (r.width > 0) worst = Math.max(worst, r.right - ${w}, -r.left); } return Math.round(worst); })()`);
          check(`/${loc}/${doc} at ${w}px: no horizontal overflow`, worst <= 0, `${worst}px`);
        }
      }
    }
    await b.setViewport(1280, 900, "dark", false);
  });

  await section("legal pages: big headings, bold distinct sub-headings, readable lines, sticky contents list on desktop", async () => {
    for (const loc of ["ar", "en"]) {
      await b.setViewport(1280, 900, "dark", false);
      await b.goto(`${base}/${loc}/terms`, 800);
      const d = await b.ev(`(() => {
        const px = (e, p) => parseFloat(getComputedStyle(e)[p]);
        const h1 = document.querySelector('main h1'); const h2 = document.querySelector('main h2'); const h3 = document.querySelector('main h3');
        const body = document.querySelector('main article p, main article li');
        const aside = document.querySelector('[data-testid=legal-toc]');
        const desktopNav = [...aside.querySelectorAll(':scope > nav')].find((n) => getComputedStyle(n).display !== 'none');
        const mobileDetails = aside.querySelector('details');
        const ph = [...document.querySelectorAll('main article .max-w-176')][0].getBoundingClientRect().width;
        return { h1: px(h1, 'fontSize'), h2: px(h2, 'fontSize'), h2w: px(h2, 'fontWeight'), h3: px(h3, 'fontSize'), h3w: px(h3, 'fontWeight'), body: px(body, 'fontSize'), bodyW: px(body, 'fontWeight'),
          sticky: getComputedStyle(aside).position, hasDesktopNav: !!desktopNav, mobileHidden: getComputedStyle(mobileDetails).display === 'none', measure: Math.round(ph) };
      })()`);
      check(`/${loc}/terms at 1280: h1 >= 60px, h2 >= 36px bold, h3 >= 24px bold and larger than body text`, d.h1 >= 60 && d.h2 >= 36 && d.h2w >= 700 && d.h3 >= 24 && d.h3w >= 700 && d.h3 > d.body && d.bodyW < 600, JSON.stringify(d));
      check(`/${loc}/terms at 1280: the contents list is sticky, the mobile version is hidden, text column <= 704px wide`, d.sticky === "sticky" && d.hasDesktopNav && d.mobileHidden && d.measure <= 704, JSON.stringify(d));
      await b.setViewport(390, 800, "dark", true);
      await b.goto(`${base}/${loc}/terms`, 800);
      const m = await b.ev(`(() => { const a = document.querySelector('[data-testid=legal-toc]'); const det = a.querySelector('details'); return { det: getComputedStyle(det).display !== 'none', open: det.open, desk: [...a.querySelectorAll(':scope > nav')].filter((n) => getComputedStyle(n).display !== 'none').length }; })()`);
      check(`/${loc}/terms at 390: the contents list is a collapsed details, the desktop list is hidden`, m.det && !m.open && m.desk === 0, JSON.stringify(m));
      await b.ev(`document.querySelector('[data-testid=legal-toc] summary').click()`);
      await b.click(`[data-testid=legal-toc] details a[href="#warranty"]`);
      await sleep(1500);
      const hash = await b.ev(`({ hash: location.hash, top: Math.round(document.getElementById('warranty').getBoundingClientRect().top) })`);
      check(`/${loc}/terms: a contents link scrolls its section just below the sticky header`, hash.hash === "#warranty" && hash.top >= 0 && hash.top < 200, JSON.stringify(hash));
    }
    await b.setViewport(1280, 900, "dark", false);
  });

  await section("legal pages: linked from the landing footer and the web app; home link works; language switch keeps the page", async () => {
    for (const [loc, privacyText, termsText] of [["en", "Privacy", "Terms"], ["ar", "الخصوصية", "الشروط"]]) {
      await b.goto(`${base}/${loc}`, 1200);
      const hrefs = await b.ev(`({ p: document.querySelector('[data-testid=footer-privacy]')?.getAttribute('href'), t: document.querySelector('[data-testid=footer-terms]')?.getAttribute('href'), pt: document.querySelector('[data-testid=footer-privacy]')?.textContent.trim(), tt: document.querySelector('[data-testid=footer-terms]')?.textContent.trim() })`);
      check(`/${loc} landing footer links to /${loc}/privacy and /${loc}/terms`, hrefs.p === `/${loc}/privacy` && hrefs.t === `/${loc}/terms` && hrefs.pt === privacyText && hrefs.tt === termsText, JSON.stringify(hrefs));
      await freshApp(b, base, loc);
      const app = await b.ev(`({ p: document.querySelector('[data-testid=app-privacy]')?.getAttribute('href'), t: document.querySelector('[data-testid=app-terms]')?.getAttribute('href') })`);
      check(`/${loc}/app footer links to the privacy policy and the terms`, app.p === `/${loc}/privacy` && app.t === `/${loc}/terms`, JSON.stringify(app));
    }
    await b.goto(`${base}/en`, 1200);
    await b.click("[data-testid=footer-privacy]");
    await b.waitFor("location.pathname === '/en/privacy' && !!document.querySelector('[data-testid=legal-privacy]')", 15000, "open privacy from the footer");
    check("clicking the landing footer link opens the privacy policy", true);
    await b.click("[data-testid=legal-home]");
    await b.waitFor("location.pathname === '/en' && !!document.querySelector('#top')", 15000, "back to home");
    check("the logo link on a legal page returns to the landing page", true);
    await b.goto(`${base}/en/terms`, 1200);
    await b.click("[data-testid=legal-other]");
    await b.waitFor("location.pathname === '/en/privacy'", 15000, "terms -> privacy");
    check("the document switch link goes from the terms to the privacy policy", true);
    await b.click("button[lang=ar]");
    await b.waitFor("location.pathname === '/ar/privacy' && document.documentElement.dir === 'rtl'", 15000, "language switch");
    await sleep(200);
    check("the language switch keeps the same page (/en/privacy -> /ar/privacy, RTL)", true);
  });

  await section("the site sets no cookie of its own on the legal pages", async () => {
    await b.goto(`${base}/en/privacy`, 1200);
    const cookies = await b.send("Network.getCookies", { urls: [base] });
    const names = (cookies.result?.cookies ?? []).map((c) => c.name);
    check("no cookie is set", names.length === 0, names.join(","));
  });
}
