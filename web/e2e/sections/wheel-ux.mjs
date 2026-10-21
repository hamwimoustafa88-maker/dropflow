import { writeFileSync } from "node:fs";
import { join } from "node:path";
import { sleep } from "../cdp.mjs";
import {
  activeTab, addFiles, check, chipNames, clickWedge, downloadResult, freshApp, hasPanel, resultNames, runPanel, section, selectTab,
  setDownloadDir, tabEnabled, waitResults, wedgeIds,
} from "../helpers.mjs";

const MOD = { alt: 1, ctrl: 2, shift: 8 };

export async function wheelUx({ b, base, out, files }) {
  const dl = join(out, "dl-wheel");
  await setDownloadDir(b, dl);

  await section("wheel: Shift / Ctrl / Alt held at DROP time pick Convert / Tools / Actions", async () => {
    for (const [mod, want, file, label] of [
      ["shift", "convert", files.png, "Shift -> Convert"],
      ["ctrl", "tools", files.png, "Ctrl -> Tools"],
      ["alt", "actions", files.txt, "Alt -> Actions (text file: `Copy text` exists)"],
    ]) {
      await freshApp(b, base, "en");
      const drop = await b.dropFiles([file], MOD[mod]);
      await sleep(200);
      const overlay = await b.ev("document.querySelector('[data-testid=drop-overlay]')?.getAttribute('data-kind')");
      check(`while dragging with ${mod}: the overlay highlights the ${want} wheel`, overlay === want, String(overlay));
      await drop();
      await b.waitFor("document.querySelectorAll('[data-testid=file-chip]').length === 1", 8000, "chip after drop");
      await sleep(250);
      check(`dropped with ${mod}: the ${want} wheel opens (${label})`, (await activeTab(b)) === want, String(await activeTab(b)));
      check("overlay is gone after the drop", (await b.ev("!!document.querySelector('[data-testid=drop-overlay]')")) === false);
    }
    // Plain drop = Convert; dragCancel hides the overlay
    await freshApp(b, base, "en");
    await b.dropFiles([files.png], 0);
    await sleep(150);
    check("dragging a file over the page shows the full-page overlay", await b.ev("!!document.querySelector('[data-testid=drop-overlay]')"));
    await b.send("Input.dispatchDragEvent", { type: "dragCancel", x: 10, y: 10, data: { items: [], files: [], dragOperationsMask: 1 } });
    await b.ev("window.dispatchEvent(new DragEvent('dragleave', { bubbles: true, dataTransfer: (() => { const d = new DataTransfer(); d.items.add(new File(['x'], 'x.png')); return d; })() }))");
    await sleep(200);
    check("leaving the window hides the overlay", (await b.ev("!!document.querySelector('[data-testid=drop-overlay]')")) === false);
  });

  await section("wheel: keyboard (arrows, Enter, Esc, tap Shift/Ctrl/Alt) and tab buttons", async () => {
    await freshApp(b, base, "en");
    const drop = await b.dropFiles([files.png], 0);
    await drop();
    await b.waitFor("document.querySelectorAll('[data-testid=file-chip]').length === 1");
    await sleep(400);
    const ids = await wedgeIds(b);
    check("after a drop the keyboard focus is already on the first wedge", (await b.ev("document.activeElement?.getAttribute('data-wedge')")) === ids[0], String(await b.ev("document.activeElement?.getAttribute('data-wedge')")));
    await b.key("ArrowRight", { code: "ArrowRight", vk: 39 });
    check("ArrowRight moves focus to the next wedge", (await b.ev("document.activeElement?.getAttribute('data-wedge')")) === ids[1]);
    await b.key("ArrowLeft", { code: "ArrowLeft", vk: 37 });
    await b.key("ArrowLeft", { code: "ArrowLeft", vk: 37 });
    check("ArrowLeft wraps around to the last wedge", (await b.ev("document.activeElement?.getAttribute('data-wedge')")) === ids[ids.length - 1]);
    await b.key("Home", { code: "Home", vk: 36 });
    check("Home jumps to the first wedge", (await b.ev("document.activeElement?.getAttribute('data-wedge')")) === ids[0]);

    await b.key("Enter", { code: "Enter", vk: 13 });
    const names = await waitResults(b, 1);
    check("Enter on the focused wedge runs it (first convert target)", names.length === 1 && /\(converted\)/.test(names[0]), names[0]);

    // tap Ctrl -> Tools, tap Alt -> Actions (has Download now), tap Shift -> Convert
    await b.send("Input.dispatchKeyEvent", { type: "rawKeyDown", key: "Control", code: "ControlLeft", windowsVirtualKeyCode: 17, modifiers: 2 });
    await b.send("Input.dispatchKeyEvent", { type: "keyUp", key: "Control", code: "ControlLeft", windowsVirtualKeyCode: 17 });
    await sleep(250);
    check("tapping Ctrl while the wheel is open switches to Tools", (await activeTab(b)) === "tools", String(await activeTab(b)));
    await b.send("Input.dispatchKeyEvent", { type: "rawKeyDown", key: "Alt", code: "AltLeft", windowsVirtualKeyCode: 18, modifiers: 1 });
    await b.send("Input.dispatchKeyEvent", { type: "keyUp", key: "Alt", code: "AltLeft", windowsVirtualKeyCode: 18 });
    await sleep(250);
    check("tapping Alt switches to Actions (Download exists once there is a result)", (await activeTab(b)) === "actions" && (await wedgeIds(b)).includes("web.download"), `${await activeTab(b)} ${(await wedgeIds(b)).join(",")}`);
    await b.send("Input.dispatchKeyEvent", { type: "rawKeyDown", key: "Shift", code: "ShiftLeft", windowsVirtualKeyCode: 16, modifiers: 8 });
    await b.send("Input.dispatchKeyEvent", { type: "keyUp", key: "Shift", code: "ShiftLeft", windowsVirtualKeyCode: 16 });
    await sleep(250);
    check("tapping Shift switches back to Convert", (await activeTab(b)) === "convert");
    // Shift used as a modifier (Shift+Tab) must NOT switch wheels
    await b.send("Input.dispatchKeyEvent", { type: "rawKeyDown", key: "Shift", code: "ShiftLeft", windowsVirtualKeyCode: 16, modifiers: 8 });
    await b.send("Input.dispatchKeyEvent", { type: "keyDown", key: "Tab", code: "Tab", windowsVirtualKeyCode: 9, modifiers: 8 });
    await b.send("Input.dispatchKeyEvent", { type: "keyUp", key: "Tab", code: "Tab", windowsVirtualKeyCode: 9, modifiers: 8 });
    await b.send("Input.dispatchKeyEvent", { type: "keyUp", key: "Shift", code: "ShiftLeft", windowsVirtualKeyCode: 16 });
    await selectTab(b, "tools");
    await b.send("Input.dispatchKeyEvent", { type: "rawKeyDown", key: "Shift", code: "ShiftLeft", windowsVirtualKeyCode: 16, modifiers: 8 });
    await b.send("Input.dispatchKeyEvent", { type: "keyDown", key: "Tab", code: "Tab", windowsVirtualKeyCode: 9, modifiers: 8 });
    await b.send("Input.dispatchKeyEvent", { type: "keyUp", key: "Tab", code: "Tab", windowsVirtualKeyCode: 9, modifiers: 8 });
    await b.send("Input.dispatchKeyEvent", { type: "keyUp", key: "Shift", code: "ShiftLeft", windowsVirtualKeyCode: 16 });
    check("Shift+Tab (Shift used as a modifier) does not hijack the wheel", (await activeTab(b)) === "tools");

    // Esc: options panel first, then the wheel
    await selectTab(b, "tools");
    await clickWedge(b, "img.resize");
    check("resize panel is open", await hasPanel(b));
    await b.key("Escape", { code: "Escape", vk: 27 });
    await sleep(200);
    check("Esc closes the options panel and brings the wheel back", !(await hasPanel(b)) && (await wedgeIds(b)).length > 0);
    await b.key("Escape", { code: "Escape", vk: 27 });
    await sleep(200);
    check("second Esc closes the wheel (an `Open wheel` button remains)", (await b.ev("!!document.querySelector('[data-testid=open-wheel]')")) && (await wedgeIds(b)).length === 0);
    await b.click("[data-testid=open-wheel]");
    await sleep(200);
    check("Open wheel brings it back", (await wedgeIds(b)).length > 0);

    // tab buttons
    await selectTab(b, "tools");
    check("tab buttons switch wheels", (await activeTab(b)) === "tools");
  });

  await section("wheel: Actions on the web (Download all, Copy text; Share only where supported)", async () => {
    await freshApp(b, base, "en");
    await addFiles(b, [files.txt]);
    await selectTab(b, "actions");
    const before = await wedgeIds(b);
    check("before any result the Actions wheel offers only things that exist (Copy text for a text file)", before.includes("web.copy") && !before.includes("web.download"), before.join(","));
    await clickWedge(b, "web.copy");
    await sleep(500);
    const live = await b.ev("document.querySelector('[data-testid=live]').textContent");
    check("`Copy text` copied (toast announced)", live === "Copied", live);
    await selectTab(b, "tools");
    await clickWedge(b, "gen.hash");
    await runPanel(b);
    await waitResults(b, 1);
    await selectTab(b, "actions");
    const after = await wedgeIds(b);
    check("with a result: Download appears (and Copy text for the text result)", after.includes("web.download") && after.includes("web.copy"), after.join(","));
    const canShare = await b.ev("typeof navigator.canShare === 'function' && typeof navigator.share === 'function'");
    check(`Share wedge exists only if Web Share exists in this browser (${canShare})`, after.includes("web.share") === (canShare && (await b.ev("navigator.canShare({ files: [new File(['x'], 'x.txt', { type: 'text/plain' })] })"))));
    await clickWedge(b, "web.download");
    await sleep(900);
    check("Download action saved the result", (await import("node:fs")).readdirSync(dl).some((f) => /hash/.test(f)));
  });

  await section("chips, remove, clear, big-file guard", async () => {
    await freshApp(b, base, "en");
    await addFiles(b, [files.png, files.png2, files.txt]);
    check("three chips", (await chipNames(b)).length === 3);
    await b.click("[data-testid=file-chip]:nth-child(2) button");
    await sleep(200);
    check("removing a chip removes only that file", (await chipNames(b)).join(",") === "photo.png,note.txt", (await chipNames(b)).join(","));
    check("mixed files: no Convert wheel (different categories), tools still work", (await tabEnabled(b, "convert")) === false);

    // big-file guard: 60 MB file, limit select = 50 MB -> clear short warning; Off -> gone
    const big = join(out, "inputs", "big-video.bin");
    writeFileSync(big, Buffer.alloc(60 * 1024 * 1024, 1));
    await freshApp(b, base, "en");
    await b.ev("localStorage.removeItem('dropflow.bigMb')");
    await addFiles(b, [big]);
    check("below the default limit (100 MB): no warning", (await b.ev("!!document.querySelector('[data-testid=big-warning]')")) === false);
    await b.ev(`(() => { const s = document.querySelector('[data-testid=limit-select]'); s.value = '50'; s.dispatchEvent(new Event('change', { bubbles: true })); })()`);
    await sleep(300);
    const warn = await b.ev("document.querySelector('[data-testid=big-warning]')?.innerText.replace(/\\s+/g, ' ')");
    check("limit 50 MB: a clear short warning above the wheel names the file and size", /big-video\.bin/.test(warn ?? "") && /60\.0 MB/.test(warn ?? "") && /slow/i.test(warn ?? ""), String(warn));
    await b.ev(`(() => { const s = document.querySelector('[data-testid=limit-select]'); s.value = '0'; s.dispatchEvent(new Event('change', { bubbles: true })); })()`);
    await sleep(300);
    check("limit Off: warning disappears; the limit persists in localStorage", (await b.ev("!!document.querySelector('[data-testid=big-warning]')")) === false && (await b.ev("localStorage.getItem('dropflow.bigMb')")) === "0");
    await b.ev("localStorage.removeItem('dropflow.bigMb')");
    // hash the big file to prove huge inputs are still processed (pure MD5 streaming + SubtleCrypto)
    await selectTab(b, "tools");
    await clickWedge(b, "gen.hash");
    await runPanel(b);
    await waitResults(b, 1, 60000);
    check("60 MB file is hashed in the worker without freezing the page", (await resultNames(b))[0] === "big-video (hash).txt");
    const hashRes = await downloadResult(b, dl, 0);
    check("hash of the 60 MB file is correct", hashRes.bytes.toString().startsWith((await import("node:crypto")).createHash("sha256").update(Buffer.alloc(60 * 1024 * 1024, 1)).digest("hex")));

    await b.ev("[...document.querySelectorAll('button')].find(x => x.textContent.trim() === 'Clear')?.click()");
    await sleep(300);
    check("Clear files returns to the drop zone", await b.ev("!!document.querySelector('[data-testid=dropzone]')"));
  });
}
