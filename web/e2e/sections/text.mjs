import { createHash } from "node:crypto";
import { readFileSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { unzipSync } from "fflate";
import { sleep } from "../cdp.mjs";
import {
  activeTab, addFiles, check, clickWedge, downloadResult, freshApp, jobTexts, resultNames, runPanel, section, selectTab,
  setDownloadDir, setOption, tabEnabled, waitResults, wedgeIds,
} from "../helpers.mjs";

export async function textTools({ b, base, out, files }) {
  const dl = join(out, "dl-text");
  await setDownloadDir(b, dl);

  await section("subtitles: Arabic SRT (BOM + CRLF) <-> VTT / TXT", async () => {
    await freshApp(b, base, "ar");
    await addFiles(b, [files.srt]);
    const ids = await wedgeIds(b);
    check("srt convert wheel = VTT, TXT (no SRT->SRT, no ASS writer)", JSON.stringify(ids) === JSON.stringify(["convert:vtt", "convert:txt"]), ids.join(","));
    await clickWedge(b, "convert:vtt");
    await clickWedge(b, "convert:txt");
    const names = await waitResults(b, 2);
    check("Arabic names kept: `حلقة (converted).vtt` and `.txt`", names.join(",") === "حلقة (converted).vtt,حلقة (converted).txt", names.join(","));
    const vtt = (await downloadResult(b, dl, 0)).bytes.toString("utf8");
    check("VTT: WEBVTT header, dot milliseconds, Arabic text byte-exact, BOM gone", vtt.startsWith("WEBVTT") && vtt.includes("00:00:01.000 --> 00:00:02.500") && vtt.includes("مرحباً بالعالم") && !vtt.includes("﻿"), JSON.stringify(vtt.slice(0, 90)));
    const txt = (await downloadResult(b, dl, 1)).bytes.toString("utf8");
    check("TXT: just the texts, no timestamps", txt.includes("مرحباً بالعالم") && txt.includes("DropFlow سريع وخاص") && !txt.includes("-->"), JSON.stringify(txt));

    // round trip: feed the VTT back and convert to SRT
    const vttPath = join(out, "حلقة2.vtt");
    writeFileSync(vttPath, vtt);
    await freshApp(b, base, "ar");
    await addFiles(b, [vttPath]);
    await clickWedge(b, "convert:srt");
    await waitResults(b, 1);
    const srt = (await downloadResult(b, dl, 0)).bytes.toString("utf8");
    check("VTT -> SRT: CRLF, comma milliseconds, index lines", srt.includes("1\r\n00:00:01,000 --> 00:00:02,500\r\n") && srt.includes("2\r\n00:00:03,000 --> 00:00:04,000"), JSON.stringify(srt.slice(0, 80)));

    // legacy windows-1256 encoded subtitle (very common for Arabic) must decode correctly
    const cp1256 = Buffer.from([0x31, 0x0d, 0x0a, ...Buffer.from("00:00:01,000 --> 00:00:02,000\r\n"), 0xe3, 0xd1, 0xcd, 0xc8, 0xc7, 0x0d, 0x0a]); // "مرحبا"
    const legacy = join(out, "قديم.srt");
    writeFileSync(legacy, cp1256);
    await freshApp(b, base, "ar");
    await addFiles(b, [legacy]);
    await clickWedge(b, "convert:txt");
    await waitResults(b, 1);
    check("windows-1256 SRT is decoded to real Arabic (مرحبا)", (await downloadResult(b, dl, 0)).bytes.toString("utf8").includes("مرحبا"));
  });

  await section("subtitles: malformed input is an error, not a crash", async () => {
    const bad = join(out, "bad.srt");
    writeFileSync(bad, "this is not a subtitle file\nat all\n");
    await freshApp(b, base, "en");
    await addFiles(b, [bad]);
    await clickWedge(b, "convert:vtt");
    await sleep(1200);
    const jobs = await jobTexts(b);
    check("no cues -> `No subtitles found`", jobs.some((t) => /No subtitles found/.test(t)), JSON.stringify(jobs));
  });

  await section("archives: extract (zip-slip safe names), download-all as ZIP, create ZIP", async () => {
    await freshApp(b, base, "en");
    await addFiles(b, [files.zip]);
    check("zip: Convert wheel is empty (disabled) and the Tools wheel opens by itself", (await tabEnabled(b, "convert")) === false && (await activeTab(b)) === "tools");
    const tools = await wedgeIds(b);
    check("zip tools: Extract + Zip + Hash + Base64", ["arc.extract", "arc.zip", "gen.hash", "gen.base64"].every((t) => tools.includes(t)), tools.join(","));
    await clickWedge(b, "arc.extract");
    const names = await waitResults(b, 3);
    check("extracted 3 files, names flattened for display (hello.txt, تقرير.txt, a.bin)", [...names].sort().join(",") === ["a.bin", "hello.txt", "تقرير.txt"].sort().join(","), names.join(","));
    check("results offer `Download all · ZIP`", /ZIP/.test(await b.ev("document.querySelector('[data-testid=download-all]').textContent")));
    await b.click("[data-testid=download-all]");
    await sleep(800);
    const zipName = (await import("node:fs")).readdirSync(dl).find((f) => f.endsWith(".zip"));
    const z = unzipSync(new Uint8Array(readFileSync(join(dl, zipName))));
    check("downloaded ZIP keeps folder structure and contents (docs/hello.txt, Arabic name)", Buffer.from(z["docs/hello.txt"] ?? []).toString() === "hello zip" && Buffer.from(z["تقرير.txt"] ?? []).toString() === "مرحبا" && Object.keys(z).length === 3, Object.keys(z).join(","));

    await freshApp(b, base, "en");
    await addFiles(b, [files.png, files.txt]);
    await selectTab(b, "tools");
    await clickWedge(b, "arc.zip");
    const n2 = await waitResults(b, 1);
    check("zip of 2 files -> `photo (archive).zip`", n2[0] === "photo (archive).zip", n2[0]);
    const made = await downloadResult(b, dl, 0);
    const mz = unzipSync(new Uint8Array(made.bytes));
    check("created ZIP contains both originals byte-for-byte", Buffer.compare(Buffer.from(mz["photo.png"]), readFileSync(files.png)) === 0 && Buffer.from(mz["note.txt"]).toString() === "hello DropFlow\n", Object.keys(mz).join(","));
  });

  await section("general: hash (SubtleCrypto + pure MD5) and base64", async () => {
    const note = readFileSync(files.txt);
    await freshApp(b, base, "en");
    await addFiles(b, [files.txt]);
    await selectTab(b, "tools");
    await clickWedge(b, "gen.hash");
    await runPanel(b);
    await waitResults(b, 1);
    const h = (await downloadResult(b, dl, 0)).bytes.toString("utf8").trim();
    check("SHA-256 matches node:crypto, sha256sum format", h === `${createHash("sha256").update(note).digest("hex")}  note.txt`, h);

    await clickWedge(b, "gen.hash");
    await setOption(b, "algorithm", "md5");
    await runPanel(b);
    await waitResults(b, 2);
    const m = (await downloadResult(b, dl, 1)).bytes.toString("utf8").trim();
    check("MD5 (our pure implementation) matches node:crypto", m === `${createHash("md5").update(note).digest("hex")}  note.txt`, m);

    await clickWedge(b, "gen.base64");
    await runPanel(b);
    await waitResults(b, 3);
    const e = (await downloadResult(b, dl, 2)).bytes.toString("utf8").trim();
    check("Base64 encode matches", e === note.toString("base64"), e);

    const b64path = join(out, "payload.txt");
    writeFileSync(b64path, note.toString("base64") + "\n");
    await freshApp(b, base, "en");
    await addFiles(b, [b64path]);
    await selectTab(b, "tools");
    await clickWedge(b, "gen.base64");
    await setOption(b, "mode", "Decode");
    await runPanel(b);
    await waitResults(b, 1);
    const names = await resultNames(b);
    const d = await downloadResult(b, dl, 0);
    check("Base64 decode restores the bytes and sniffs the type (.txt)", d.bytes.equals(note) && names[0].endsWith(".txt"), names[0]);
  });

  await section("not-in-browser: a .docx gets only generic tools and the Windows app hint", async () => {
    await freshApp(b, base, "en");
    await addFiles(b, [files.docx]);
    check("docx: Convert tab disabled; tools = zip/hash/base64 only", (await tabEnabled(b, "convert")) === false && JSON.stringify(await wedgeIds(b)) === JSON.stringify(["arc.zip", "gen.hash", "gen.base64"]));
    const hint = await b.ev("document.querySelector('[data-testid=windows-hint]')?.innerText.replace(/\\s+/g, ' ')");
    check("hint lists what the desktop app is for (PDF, TXT...) with the Soon badge", /Windows app/.test(hint) && /PDF/.test(hint) && /Soon/.test(hint), hint);
  });
}
