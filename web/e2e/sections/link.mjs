import { sleep } from "../cdp.mjs";
import { chipNames, check, clearAll, clickWedge, freshApp, section, waitResults } from "../helpers.mjs";

// "Drop a link" on /app: a typed, pasted or dropped http(s) address is downloaded by the browser (GET, no cookies) and
// then treated exactly like a dropped file. A site that refuses cross-origin downloads gets a clear bilingual message.
export async function link({ b, base }) {
  const sameOrigin = `${base}/icons/icon-192.png`;
  const u = new URL(base);
  // Another origin on the same machine (127.0.0.1 vs localhost). It is http:, which the /app CSP does not allow, so the
  // browser refuses it before any request leaves: the same outcome a visitor gets from a site without CORS headers.
  const foreign = `${u.protocol}//${u.hostname === "127.0.0.1" ? "localhost" : "127.0.0.1"}:${u.port}/icons/icon-192.png`;

  await section("link: typed address of a same-origin file -> becomes a file -> converts", async () => {
    await freshApp(b, base, "en");
    const ui = await b.ev(`({ input: !!document.querySelector('[data-testid=link-input]'), ph: document.querySelector('[data-testid=link-input]')?.placeholder, label: document.querySelector('label[for=link-input]')?.textContent })`);
    check("drop zone has an 'or paste a link' field with a label", ui.input && /link/i.test(ui.ph ?? "") && !!ui.label, JSON.stringify(ui));
    await b.ev(`(() => {
      const el = document.querySelector('[data-testid=link-input]');
      Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set.call(el, ${JSON.stringify(sameOrigin)});
      el.dispatchEvent(new Event('input', { bubbles: true }));
    })()`);
    await sleep(100);
    await b.click("[data-testid=link-get]");
    await b.waitFor("document.querySelectorAll('[data-testid=file-chip]').length > 0", 15000, "linked file chip");
    const names = await chipNames(b);
    check("the downloaded file is named from the URL (icon-192.png)", names.includes("icon-192.png"), JSON.stringify(names));
    await clickWedge(b, "convert:jpg");
    const res = await waitResults(b, 1);
    check("the linked PNG converts to JPG like a dropped file", res.some((n) => /icon-192 \(converted\)\.jpg$/.test(n)), JSON.stringify(res));
  });

  await section("link: pasted (Ctrl+V) and dropped (text/uri-list) addresses", async () => {
    await freshApp(b, base, "ar");
    await b.ev(`(() => {
      const dt = new DataTransfer();
      dt.setData('text/plain', ${JSON.stringify(`${base}/icons/icon-512.png`)});
      document.body.dispatchEvent(new ClipboardEvent('paste', { clipboardData: dt, bubbles: true, cancelable: true }));
    })()`);
    await b.waitFor("[...document.querySelectorAll('[data-testid=file-chip] span')].some(s => s.textContent === 'icon-512.png')", 15000, "pasted link");
    check("a pasted link becomes a file (Arabic UI)", true);
    await clearAll(b);
    await sleep(300);
    await b.ev(`(() => {
      const dt = new DataTransfer();
      dt.setData('text/uri-list', ${JSON.stringify(`${base}/icons/apple-touch-icon.png`)});
      for (const type of ['dragenter', 'dragover', 'drop']) window.dispatchEvent(new DragEvent(type, { dataTransfer: dt, bubbles: true, cancelable: true }));
    })()`);
    await b.waitFor("[...document.querySelectorAll('[data-testid=file-chip] span')].some(s => s.textContent === 'apple-touch-icon.png')", 15000, "dropped link");
    check("a dropped link (text/uri-list) becomes a file", true);
  });

  await section("link: a site that does not allow browser downloads -> clear message, nothing added", async () => {
    for (const [loc, re] of [
      ["en", /does not allow downloading from the browser.*Windows app/],
      ["ar", /لا يسمح بالتنزيل من المتصفح.*تطبيق ويندوز/],
    ]) {
      await freshApp(b, base, loc);
      const errorsBefore = b.errors.length;
      const reqsBefore = b.requests.length;
      await b.ev(`(() => {
        const el = document.querySelector('[data-testid=link-input]');
        Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set.call(el, ${JSON.stringify(foreign)});
        el.dispatchEvent(new Event('input', { bubbles: true }));
      })()`);
      await sleep(100);
      await b.click("[data-testid=link-get]");
      await b.waitFor("!!document.querySelector('[data-testid=link-error]')", 15000, "link error");
      const msg = await b.ev("document.querySelector('[data-testid=link-error]').textContent");
      const chips = await b.ev("document.querySelectorAll('[data-testid=file-chip]').length");
      check(`/${loc}: refused cross-origin download shows the message and adds no file`, re.test(msg) && chips === 0, msg);
      // The refusal is expected: drop its console line and the refused request from the run-wide audits.
      b.errors.splice(errorsBefore).filter((e) => !/Content Security Policy|Refused to connect|Failed to fetch|net::ERR/.test(e)).forEach((e) => b.errors.push(e));
      for (let i = b.requests.length - 1; i >= reqsBefore; i--) if (b.requests[i].url === foreign) b.requests.splice(i, 1);
    }
    await freshApp(b, base, "en");
    await b.ev(`(() => {
      const el = document.querySelector('[data-testid=link-input]');
      Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set.call(el, 'javascript:alert(1)');
      el.dispatchEvent(new Event('input', { bubbles: true }));
    })()`);
    await sleep(100);
    await b.click("[data-testid=link-get]");
    await b.waitFor("!!document.querySelector('[data-testid=link-error]')", 5000, "invalid link error");
    check("a non-http address is refused without any request", /not a web address/.test(await b.ev("document.querySelector('[data-testid=link-error]').textContent")));
  });
}
