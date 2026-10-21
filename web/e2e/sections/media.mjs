import { writeFileSync } from "node:fs";
import { join } from "node:path";
import { addFiles, check, freshApp, section, selectTab, tabEnabled, wedgeIds } from "../helpers.mjs";

// Audio and video are Windows-app only: the GPL ffmpeg.wasm core was removed from the web app (proprietary licence,
// ADR-0010), so the browser shows the "Windows app" hint tile and only the generic tools instead of a media wheel.
export async function media({ b, base, out, files }) {
  const mp4 = join(out, "inputs", "clip.mp4");
  writeFileSync(mp4, Buffer.concat([Buffer.from([0, 0, 0, 0x18]), Buffer.from("ftypmp42", "latin1"), Buffer.alloc(32)]));

  await section("audio / video: no browser wheel, an honest Windows-app hint; no ffmpeg anywhere", async () => {
    for (const [label, path] of [["WAV", files.wav], ["MP4", mp4]]) {
      await freshApp(b, base, "en");
      await addFiles(b, [path]);
      check(`${label}: the Convert wheel is empty (disabled)`, !(await tabEnabled(b, "convert")));
      await selectTab(b, "tools");
      const ids = await wedgeIds(b);
      check(`${label}: only the generic tools are offered, no aud.* / vid.* tool`, JSON.stringify(ids) === JSON.stringify(["arc.zip", "gen.hash", "gen.base64"]), ids.join(","));
      const hint = await b.ev("document.querySelector('[data-testid=windows-hint]')?.textContent ?? null");
      check(`${label}: the "Windows app" hint tile is shown`, !!hint && /Windows app/.test(hint), String(hint).slice(0, 120));
    }
    await freshApp(b, base, "ar");
    await addFiles(b, [files.wav]);
    check("Arabic: the same hint tile is shown for audio", await b.ev("!!document.querySelector('[data-testid=windows-hint]')"));

    check("no request for anything ffmpeg during the run so far", !b.requests.some((r) => /ffmpeg/i.test(r.url)), b.requests.filter((r) => /ffmpeg/i.test(r.url)).slice(0, 2).map((r) => r.url).join(" | "));
    const errorsBefore = b.errors.length;
    const status = await b.ev("fetch('/vendor/ffmpeg/ffmpeg-core.wasm').then(r => r.status)");
    check("the ffmpeg core is not served any more (/vendor/ffmpeg/... is 404)", status === 404, String(status));
    await new Promise((r) => setTimeout(r, 500));
    b.errors.splice(errorsBefore); // the deliberate 404 above logs one console error
    check("no `Save media offline` button in the app footer", !(await b.ev("!!document.querySelector('[data-testid=save-media]')")));
  });
}
