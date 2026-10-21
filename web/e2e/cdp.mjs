// Tiny DevTools-protocol driver for headless Edge/Chrome (no dependencies; Node 22+ has global WebSocket + fetch).
// It also audits ALL network traffic of the page, its Web Workers and the service worker (flattened auto-attach),
// which is how the e2e proves that no request ever carries user data.
import { spawn } from "node:child_process";
import { mkdtempSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";

export const EDGE = process.env.BROWSER ?? "C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe";
export const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

export async function launch({ width = 1280, height = 900, scheme = "dark", extraArgs = [] } = {}) {
  const profile = mkdtempSync(join(tmpdir(), "df-e2e-"));
  const port = 9300 + Math.floor(Math.random() * 600);
  const proc = spawn(
    EDGE,
    ["--headless=new", "--disable-gpu", "--hide-scrollbars", "--no-first-run", "--autoplay-policy=no-user-gesture-required", `--remote-debugging-port=${port}`, `--user-data-dir=${profile}`, ...extraArgs, "about:blank"],
    { stdio: "ignore" },
  );
  let target;
  for (let i = 0; i < 80 && !target; i++) {
    try {
      target = (await (await fetch(`http://127.0.0.1:${port}/json`)).json()).find((t) => t.type === "page");
    } catch {
      /* browser still starting */
    }
    if (!target) await sleep(200);
  }
  if (!target) {
    proc.kill();
    throw new Error("browser did not start");
  }
  const ws = new WebSocket(target.webSocketDebuggerUrl);
  await new Promise((r) => (ws.onopen = r));

  let id = 0;
  const pending = new Map();
  const listeners = [];
  ws.onmessage = (m) => {
    const msg = JSON.parse(m.data);
    if (msg.id && pending.has(msg.id)) {
      pending.get(msg.id)(msg);
      pending.delete(msg.id);
    } else if (msg.method) {
      for (const l of listeners) l(msg);
    }
  };
  const sendTo = (sessionId, method, params = {}) =>
    new Promise((res) => {
      const n = ++id;
      // A hung DevTools call must never hang the whole run: give up after 90 s with an error result.
      const timer = setTimeout(() => {
        pending.delete(n);
        res({ id: n, error: { message: `CDP timeout: ${method}` }, result: {} });
      }, 90_000);
      pending.set(n, (m) => {
        clearTimeout(timer);
        res(m);
      });
      ws.send(JSON.stringify({ id: n, method, params, ...(sessionId ? { sessionId } : {}) }));
    });
  const send = (method, params = {}) => sendTo(undefined, method, params);

  const b = {
    proc,
    ws,
    send,
    sendTo,
    profile,
    errors: [],
    requests: [],
    onEvent: (fn) => listeners.push(fn),
  };

  // ---- audit: console errors, exceptions, CSP violations, every network request from every context -------------
  b.onEvent((msg) => {
    const p = msg.params ?? {};
    if (msg.method === "Runtime.exceptionThrown") b.errors.push(`exception: ${p.exceptionDetails.text} ${p.exceptionDetails.exception?.description ?? ""}`.slice(0, 400));
    if (msg.method === "Runtime.consoleAPICalled" && p.type === "error") b.errors.push(`console.error: ${p.args.map((a) => a.value ?? a.description).join(" ")}`.slice(0, 400));
    if (msg.method === "Log.entryAdded" && p.entry.level === "error") b.errors.push(`log.error: ${p.entry.text} ${p.entry.url ?? ""}`.slice(0, 400));
    if (msg.method === "Network.requestWillBeSent") {
      b.requests.push({ url: p.request.url, method: p.request.method, hasBody: !!p.request.hasPostData || !!p.request.postData, session: msg.sessionId ?? "page", type: p.type });
    }
    if (msg.method === "Target.attachedToTarget") {
      const s = p.sessionId;
      // Dedicated workers and the service worker get the same auditing as the page.
      void (async () => {
        await sendTo(s, "Network.enable");
        await sendTo(s, "Runtime.enable");
        await sendTo(s, "Log.enable");
        await sendTo(s, "Runtime.runIfWaitingForDebugger");
      })();
    }
  });
  await send("Page.enable");
  await send("Runtime.enable");
  await send("Network.enable");
  await send("Log.enable");
  // Dedicated workers only: attaching (and pausing) the service worker would keep it from ever starting.
  await send("Target.setAutoAttach", { autoAttach: true, waitForDebuggerOnStart: true, flatten: true, filter: [{ type: "worker" }, { exclude: true }] });
  b.initial = { width, height, scheme };
  return b;
}

export function extend(b) {
  b.setViewport = async (width, height, scheme = "dark", mobile = false) => {
    await b.send("Emulation.setDeviceMetricsOverride", { width, height, deviceScaleFactor: mobile ? 2 : 1, mobile });
    await b.send("Emulation.setEmulatedMedia", { features: [{ name: "prefers-color-scheme", value: scheme }] });
  };
  b.ev = async (expression) => {
    const r = await b.send("Runtime.evaluate", { expression, returnByValue: true, awaitPromise: true });
    if (r.result?.exceptionDetails) throw new Error(`eval failed: ${r.result.exceptionDetails.exception?.description ?? r.result.exceptionDetails.text}\n${expression.slice(0, 200)}`);
    return r.result?.result?.value;
  };
  b.goto = async (url, wait = 1500) => {
    await b.send("Page.navigate", { url });
    await sleep(wait);
  };
  b.waitFor = async (expression, timeoutMs = 20000, label = expression) => {
    const t0 = Date.now();
    for (;;) {
      let v;
      try {
        v = await b.ev(expression);
      } catch {
        v = false;
      }
      if (v) return v;
      if (Date.now() - t0 > timeoutMs) throw new Error(`timeout waiting for: ${label}`);
      await sleep(150);
    }
  };
  b.box = async (selector) =>
    b.ev(`(() => { const e = document.querySelector(${JSON.stringify(selector)}); if (!e) return null; const r = e.getBoundingClientRect(); return { x: r.x + r.width / 2, y: r.y + r.height / 2, w: r.width, h: r.height }; })()`);
  b.click = async (selector) => {
    // `instant`: the site uses scroll-behavior: smooth, which would leave the box measurement mid-animation.
    await b.ev(`document.querySelector(${JSON.stringify(selector)})?.scrollIntoView({ block: "center", behavior: "instant" })`);
    await sleep(60);
    const box = await b.box(selector);
    if (!box) throw new Error(`click: no element ${selector}`);
    await b.send("Input.dispatchMouseEvent", { type: "mouseMoved", x: box.x, y: box.y });
    await b.send("Input.dispatchMouseEvent", { type: "mousePressed", x: box.x, y: box.y, button: "left", clickCount: 1 });
    await b.send("Input.dispatchMouseEvent", { type: "mouseReleased", x: box.x, y: box.y, button: "left", clickCount: 1 });
  };
  b.key = async (key, { code = key, vk = 0, modifiers = 0, text } = {}) => {
    await b.send("Input.dispatchKeyEvent", { type: "keyDown", key, code, windowsVirtualKeyCode: vk, modifiers, ...(text ? { text } : {}) });
    await b.send("Input.dispatchKeyEvent", { type: "keyUp", key, code, windowsVirtualKeyCode: vk, modifiers });
  };
  b.setFiles = async (selector, paths) => {
    const doc = await b.send("DOM.getDocument", { depth: -1 });
    const q = await b.send("DOM.querySelector", { nodeId: doc.result.root.nodeId, selector });
    if (!q.result?.nodeId) throw new Error(`setFiles: no element ${selector}`);
    await b.send("DOM.setFileInputFiles", { nodeId: q.result.nodeId, files: paths });
  };
  /** Real OS-style file drag & drop with modifier keys (Alt=1, Ctrl=2, Meta=4, Shift=8). */
  b.dropFiles = async (paths, modifiers = 0, { x = 400, y = 300, hold = 0 } = {}) => {
    const data = { items: [], files: paths, dragOperationsMask: 1 };
    await b.send("Input.dispatchDragEvent", { type: "dragEnter", x, y, data, modifiers });
    await b.send("Input.dispatchDragEvent", { type: "dragOver", x, y, data, modifiers });
    if (hold) await sleep(hold);
    return async () => {
      await b.send("Input.dispatchDragEvent", { type: "drop", x, y, data, modifiers });
    };
  };
  b.shot = async (path, { full = false } = {}) => {
    const r = await b.send("Page.captureScreenshot", { format: "png", captureBeyondViewport: full });
    const { writeFileSync } = await import("node:fs");
    writeFileSync(path, Buffer.from(r.result.data, "base64"));
  };
  b.close = () => {
    try {
      b.ws.close();
    } catch {
      /* ignore */
    }
    b.proc.kill();
    setTimeout(() => {
      try {
        rmSync(b.profile, { recursive: true, force: true });
      } catch {
        /* profile still locked: harmless temp dir */
      }
    }, 1500);
  };
  return b;
}

export async function open(opts) {
  const b = extend(await launch(opts));
  await b.setViewport(b.initial.width, b.initial.height, b.initial.scheme, false);
  return b;
}
