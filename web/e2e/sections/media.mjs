import { writeFileSync } from "node:fs";
import { join } from "node:path";
import { sleep } from "../cdp.mjs";
import {
  addFiles, allWedgeIds, ascii, check, clickWedge, decodeInBrowser, downloadResult, freshApp, isPng, jobStates, jobTexts, resultNames, runPanel,
  section, selectTab, setDownloadDir, setOption, waitResults, wedgeIds,
} from "../helpers.mjs";

/** Finds fmt/data chunks properly (ffmpeg adds a LIST chunk, so the data size is not at a fixed offset). */
function wavInfo(buf) {
  let off = 12;
  let rate = 0;
  let channels = 1;
  let bits = 16;
  while (off + 8 <= buf.length) {
    const id = buf.toString("latin1", off, off + 4);
    const size = buf.readUInt32LE(off + 4);
    if (id === "fmt ") {
      channels = buf.readUInt16LE(off + 10);
      rate = buf.readUInt32LE(off + 12);
      bits = buf.readUInt16LE(off + 22);
    }
    if (id === "data") return { rate, seconds: Math.min(size, buf.length - off - 8) / (rate * channels * (bits / 8)) };
    off += 8 + size + (size % 2);
  }
  return { rate, seconds: 0 };
}

/** Records a real WebM (VP8 + Opus) inside the browser with MediaRecorder: a moving rectangle + a 440 Hz tone. */
async function recordWebm(b, ms = 1500) {
  return b.ev(`(async () => {
    const canvas = document.createElement('canvas'); canvas.width = 320; canvas.height = 240;
    const ctx = canvas.getContext('2d');
    const stream = canvas.captureStream(15);
    const ac = new AudioContext(); const osc = ac.createOscillator(); osc.frequency.value = 440; const dest = ac.createMediaStreamDestination(); osc.connect(dest); osc.start();
    dest.stream.getAudioTracks().forEach((t) => stream.addTrack(t));
    const mime = ['video/webm;codecs=vp8,opus', 'video/webm'].find((m) => MediaRecorder.isTypeSupported(m));
    const rec = new MediaRecorder(stream, { mimeType: mime });
    const chunks = []; rec.ondataavailable = (e) => e.data.size && chunks.push(e.data);
    const done = new Promise((r) => (rec.onstop = r));
    rec.start(100);
    const t0 = performance.now();
    await new Promise((resolve) => { (function draw() {
      const t = performance.now() - t0; ctx.fillStyle = '#123'; ctx.fillRect(0, 0, 320, 240); ctx.fillStyle = '#f80'; ctx.fillRect((t / 5) % 260, 80, 60, 60);
      if (t < ${ms}) requestAnimationFrame(draw); else resolve(); })(); });
    rec.stop(); await done; osc.stop(); ac.close();
    const u8 = new Uint8Array(await new Blob(chunks, { type: 'video/webm' }).arrayBuffer());
    let s = ''; for (let i = 0; i < u8.length; i += 8192) s += String.fromCharCode(...u8.subarray(i, i + 8192));
    return btoa(s);
  })()`);
}

const duration = (b, bytes, mime) =>
  b.ev(`(async () => {
    const bin = atob(${JSON.stringify(bytes.toString("base64"))}); const u8 = Uint8Array.from(bin, c => c.charCodeAt(0));
    const v = document.createElement('video'); v.muted = true; v.src = URL.createObjectURL(new Blob([u8], { type: ${JSON.stringify(mime)} }));
    return await new Promise((r) => { v.onloadedmetadata = () => r({ d: v.duration, w: v.videoWidth, h: v.videoHeight }); v.onerror = () => r({ error: true }); setTimeout(() => r({ timeout: true }), 8000); });
  })()`);

export async function media({ b, base, out, files }) {
  const dl = join(out, "dl-media");
  await setDownloadDir(b, dl);

  await section("audio via ffmpeg.wasm: capabilities are probed, then real conversions", async () => {
    await freshApp(b, base, "en");
    await b.ev("localStorage.clear()");
    await b.send("Page.reload");
    await sleep(1500);
    await b.waitFor("!!document.querySelector('[data-testid=dropzone]')");
    const before = await b.ev("performance.getEntriesByType('resource').filter(r => r.name.includes('ffmpeg')).length");
    check("the 31 MB ffmpeg core is NOT loaded before any audio/video file is used", before === 0, String(before));
    await addFiles(b, [files.wav]);
    await b.waitFor("document.querySelector('[data-wheel=convert]') && !document.querySelector('[data-wheel=convert]').disabled", 90000, "convert wheel after engine probe");
    const ids = await allWedgeIds(b);
    const targets = ["mp3", "m4a", "flac", "ogg", "opus", "aac", "aiff", "wma"];
    check("WAV wheel lists exactly what the real core can encode (all 8 audio targets, never WAV->WAV)", targets.every((t) => ids.includes(`convert:${t}`)) && !ids.includes("convert:wav"), ids.join(","));
    const coreReq = b.requests.filter((r) => r.url.includes("ffmpeg-core.wasm"));
    check("the ffmpeg core came from our own origin (/vendor/ffmpeg), fetched by the engine worker, not from a CDN", coreReq.length >= 1 && coreReq.every((r) => new URL(r.url).origin === new URL(base).origin && r.url.includes("/vendor/ffmpeg/")), coreReq.map((r) => r.url).join(","));

    const magic = {
      mp3: (x) => ascii(x, 0, 3) === "ID3" || (x[0] === 0xff && (x[1] & 0xe0) === 0xe0),
      flac: (x) => ascii(x, 0, 4) === "fLaC",
      ogg: (x) => ascii(x, 0, 4) === "OggS",
      opus: (x) => ascii(x, 0, 4) === "OggS" && x.includes(Buffer.from("OpusHead")),
      m4a: (x) => ascii(x, 4, 8) === "ftyp",
      aac: (x) => x[0] === 0xff && (x[1] & 0xf0) === 0xf0,
      aiff: (x) => ascii(x, 0, 4) === "FORM" && ascii(x, 8, 12) === "AIFF",
      wma: (x) => x.subarray(0, 4).equals(Buffer.from([0x30, 0x26, 0xb2, 0x75])),
    };
    let n = 0;
    for (const t of targets) {
      await clickWedge(b, `convert:${t}`);
      await waitResults(b, ++n, 60000);
      const r = await downloadResult(b, dl, n - 1);
      check(`WAV -> ${t.toUpperCase()}: valid container (${r.size} bytes)`, r.size > 500 && magic[t](r.bytes), ascii(r.bytes, 0, 12).replace(/[^\x20-\x7e]/g, "."));
    }
    const names = await resultNames(b);
    check("outputs named `tone (converted).ext`", names[0] === "tone (converted).mp3" && names.every((x) => /^tone \(converted( \d+)?\)\.\w+$/.test(x)), names.join(","));

    // tools
    await selectTab(b, "tools");
    await clickWedge(b, "aud.trim");
    await setOption(b, "start", "0.5");
    await setOption(b, "end", "1.0");
    await runPanel(b);
    await waitResults(b, ++n, 60000);
    const trimmed = await downloadResult(b, dl, n - 1);
    const sec = wavInfo(trimmed.bytes).seconds;
    check("aud.trim 0.5s-1.0s of a 1.5s WAV -> ~0.5s WAV", ascii(trimmed.bytes, 0, 4) === "RIFF" && Math.abs(sec - 0.5) < 0.08, sec.toFixed(3));
    await clickWedge(b, "aud.normalize");
    await waitResults(b, ++n, 60000);
    const norm = await downloadResult(b, dl, n - 1);
    check("aud.normalize -> valid WAV of the same length (1.5 s)", ascii(norm.bytes, 0, 4) === "RIFF" && Math.abs(wavInfo(norm.bytes).seconds - 1.5) < 0.1, JSON.stringify(wavInfo(norm.bytes)));
  });

  await section("video via ffmpeg.wasm: MediaRecorder WebM in, real MP4 / GIF / MP3 / trim / mute / frame / speed / compress out", async () => {
    await freshApp(b, base, "en");
    const webm = Buffer.from(await recordWebm(b, 4000), "base64");
    const clip = join(out, "inputs", "clip.webm");
    writeFileSync(clip, webm);
    check("recorded a real WebM (EBML header, VP8 video + Opus audio)", webm.subarray(0, 4).equals(Buffer.from([0x1a, 0x45, 0xdf, 0xa3])) && webm.includes(Buffer.from("A_OPUS")) && webm.includes(Buffer.from("V_VP8")), `${webm.length} bytes`);

    await addFiles(b, [clip]);
    await b.waitFor("document.querySelector('[data-wheel=convert]') && !document.querySelector('[data-wheel=convert]').disabled", 90000, "convert wheel for video");
    const ids = await allWedgeIds(b);
    check("webm wheel: MP4 MKV MOV AVI WMV GIF + MP3 M4A WAV (never WEBM->WEBM)", ["mp4", "mkv", "mov", "avi", "wmv", "gif", "mp3", "m4a", "wav"].every((t) => ids.includes(`convert:${t}`)) && !ids.includes("convert:webm"), ids.join(","));

    let n = 0;
    await clickWedge(b, "convert:mp4");
    await waitResults(b, ++n, 120000);
    const mp4 = await downloadResult(b, dl, n - 1);
    check("webm -> MP4 is a real MP4 (ftyp) with H.264 (avc1) that plays", ascii(mp4.bytes, 4, 8) === "ftyp" && mp4.bytes.includes(Buffer.from("avc1")), `${mp4.size} bytes`);
    const md = await duration(b, mp4.bytes, "video/mp4");
    check("the MP4 loads in a <video> element: 320x240, ~4 s", md.w === 320 && md.h === 240 && md.d > 3 && md.d < 5, JSON.stringify(md));

    await clickWedge(b, "convert:gif");
    await waitResults(b, ++n, 120000);
    const gif = await downloadResult(b, dl, n - 1);
    check("webm -> GIF is a real GIF89a", ascii(gif.bytes, 0, 6) === "GIF89a" && gif.size > 1000, `${gif.size} bytes`);
    await clickWedge(b, "convert:mp3");
    await waitResults(b, ++n, 120000);
    const mp3 = await downloadResult(b, dl, n - 1);
    check("video -> MP3 extracts the audio", ascii(mp3.bytes, 0, 3) === "ID3" || mp3.bytes[0] === 0xff, `${mp3.size} bytes`);

    await selectTab(b, "tools");
    const tools = await wedgeIds(b);
    check("video tools: trim, mute, snapshot, speed, compress", ["vid.trim", "vid.mute", "vid.snapshot", "vid.speed", "vid.compress"].every((t) => tools.includes(t)), tools.join(","));

    await clickWedge(b, "vid.trim");
    await setOption(b, "start", "1.0");
    await setOption(b, "end", "2.0");
    await runPanel(b);
    await waitResults(b, ++n, 120000);
    const trim = await downloadResult(b, dl, n - 1);
    const td = await duration(b, trim.bytes, "video/webm");
    check("vid.trim 1.0-2.0 s -> ~1 s clip", td.d > 0.8 && td.d < 1.3, JSON.stringify(td));

    await clickWedge(b, "vid.mute");
    await waitResults(b, ++n, 120000);
    const mute = await downloadResult(b, dl, n - 1);
    check("vid.mute: the audio track is gone (no Opus/Vorbis), video stays", !mute.bytes.includes(Buffer.from("A_OPUS")) && !mute.bytes.includes(Buffer.from("A_VORBIS")) && mute.bytes.includes(Buffer.from("V_VP8")), `${mute.size} bytes`);

    await clickWedge(b, "vid.snapshot");
    await setOption(b, "time", "0.5");
    await runPanel(b);
    await waitResults(b, ++n, 120000);
    const snap = await downloadResult(b, dl, n - 1);
    const sd = await decodeInBrowser(b, snap.bytes, "image/png");
    check("vid.snapshot at 0.5 s is a PNG frame of 320x240", isPng(snap.bytes) && sd.w === 320 && sd.h === 240, JSON.stringify(sd));

    await clickWedge(b, "vid.speed");
    await setOption(b, "speed", "2");
    await runPanel(b);
    await waitResults(b, ++n, 120000);
    const fast = await downloadResult(b, dl, n - 1);
    const fd = await duration(b, fast.bytes, "video/webm");
    check("vid.speed 2x halves the duration (~2 s of a 4 s clip)", fd.d > 1.6 && fd.d < 2.6, JSON.stringify(fd));

    await clickWedge(b, "vid.compress");
    await runPanel(b);
    await sleep(500);
    await b.waitFor("document.querySelectorAll('[data-testid=jobs] li').length === 0 || document.querySelectorAll('[data-testid=result]').length > " + n, 120000, "compress done");
    const states = await jobTexts(b);
    const got = (await resultNames(b)).length > n;
    check("vid.compress: smaller output, or the honest `Already small` message", got || states.some((t) => /Already small/.test(t)), JSON.stringify(states));
  });

  await section("media: cancel stops the engine and it recovers", async () => {
    await freshApp(b, base, "en");
    await addFiles(b, [join(out, "inputs", "clip.webm")]);
    await b.waitFor("document.querySelector('[data-wheel=convert]') && !document.querySelector('[data-wheel=convert]').disabled", 90000, "convert wheel");
    await clickWedge(b, "convert:mp4");
    await b.waitFor("document.querySelectorAll('[data-testid=jobs] li').length > 0", 5000, "job row");
    const running = await jobStates(b);
    check("a conversion is running", running.includes("running") || running.includes("waiting"), JSON.stringify(running));
    await b.click("[data-testid=jobs] li button");
    await sleep(1000);
    check("cancelling removes the job without a result", (await resultNames(b)).length === 0 && (await b.ev("document.querySelectorAll('[data-testid=jobs] li[data-state=running]').length")) === 0);
    await selectTab(b, "tools");
    await clickWedge(b, "vid.mute");
    await waitResults(b, 1, 120000);
    check("after the cancel the next job works (engine was re-created)", (await resultNames(b))[0] === "clip (muted).webm", (await resultNames(b))[0]);
  });
}
