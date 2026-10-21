import { check, freshApp, section } from "../helpers.mjs";
import { sleep } from "../cdp.mjs";

const ISSUES = "https://github.com/hamwimoustafa88-maker/dropflow/issues";
const EMAIL = /[\w.+-]+@[\w-]+\.[\w.-]+/;

// Privacy Policy and Terms of Use: four static pages (ar/en x privacy/terms), reachable from the landing footer and the app.
export async function legal({ b, base }) {
  await section("legal pages: 200, right language + direction, honest content, no email address, noindex", async () => {
    for (const loc of ["en", "ar"]) {
      for (const doc of ["privacy", "terms"]) {
        const status = await b.ev(`fetch(${JSON.stringify(`/${loc}/${doc}`)}).then(r => r.status)`);
        check(`/${loc}/${doc} answers 200`, status === 200, String(status));
        await b.goto(`${base}/${loc}/${doc}`, 1200);
        const page = await b.ev(`(() => ({
          lang: document.documentElement.lang, dir: document.documentElement.dir,
          h1: document.querySelector('h1')?.textContent ?? '', h2: document.querySelectorAll('main h2').length,
          text: document.querySelector('main').innerText,
          issues: [...document.querySelectorAll('a')].filter(a => a.href === ${JSON.stringify(ISSUES)}).length,
          home: document.querySelector('[data-testid=legal-home]')?.getAttribute('href'),
          other: document.querySelector('[data-testid=legal-other]')?.getAttribute('href'),
          robots: document.querySelector('meta[name=robots]')?.content ?? '',
          title: document.title,
        }))()`);
        check(`/${loc}/${doc}: lang=${loc} dir=${loc === "ar" ? "rtl" : "ltr"}`, page.lang === loc && page.dir === (loc === "ar" ? "rtl" : "ltr"), `${page.lang}/${page.dir}`);
        check(`/${loc}/${doc}: has a heading, 8+ sections and the effective date`, page.h1.length > 3 && page.h2 >= 8 && /2026/.test(page.text), `${page.h2} sections`);
        check(`/${loc}/${doc}: contact is the GitHub issues page and there is no email address`, page.issues === 1 && !EMAIL.test(page.text), `links ${page.issues}`);
        check(`/${loc}/${doc}: links back to the home page and to the other document`, page.home === `/${loc}` && page.other === `/${loc}/${doc === "privacy" ? "terms" : "privacy"}`, `${page.home} ${page.other}`);
        check(`/${loc}/${doc}: stays noindex (the site is not public yet)`, /noindex/.test(page.robots), page.robots);
        check(`/${loc}/${doc}: has its own page title`, new RegExp(doc === "privacy" ? "Privacy|الخصوصية" : "Terms|شروط").test(page.title), page.title);
        if (doc === "privacy") {
          check(`/${loc}/privacy: names Vercel, GitHub and Hugging Face as third parties`, /Vercel/.test(page.text) && /GitHub/.test(page.text) && /Hugging Face/.test(page.text));
        } else {
          check(`/${loc}/terms: names the owner and the year`, /Moustafa Hamwi|مصطفى حموي/.test(page.text) && /2026/.test(page.text));
        }
      }
    }
  });

  await section("legal pages: mobile layout (390px) without horizontal overflow, Arabic and English", async () => {
    for (const loc of ["ar", "en"]) {
      for (const doc of ["privacy", "terms"]) {
        await b.setViewport(390, 800, "dark", true);
        await b.goto(`${base}/${loc}/${doc}`, 1000);
        const worst = await b.ev(`(() => { let worst = 0; for (const e of document.querySelectorAll('body *')) { if (e.closest('svg') || e.closest('.sr-only')) continue; if (getComputedStyle(e).position === 'fixed') continue; const r = e.getBoundingClientRect(); if (r.width > 0) worst = Math.max(worst, r.right - 390, -r.left); } return Math.round(worst); })()`);
        check(`/${loc}/${doc} at 390px: no horizontal overflow`, worst <= 0, `${worst}px`);
      }
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
